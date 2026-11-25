<?php

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

function joes_scanner_auth_dir_table( $name ) {
    global $wpdb;
    return $wpdb->prefix . 'js_directory_' . $name;
}

function joes_scanner_auth_dir_error( $message, $status = 400 ) {
    return new WP_Error( 'directory_request_failed', $message, array( 'status' => $status ) );
}

function joes_scanner_auth_dir_db_error() {
    return joes_scanner_auth_dir_error( 'The server directory could not save your request. Please try again.', 503 );
}

function joes_scanner_auth_directory_tables_exist() {
    foreach ( array( 'revisions', 'addresses', 'recipients', 'verification' ) as $name ) {
        if ( ! joes_scanner_auth_table_exists( joes_scanner_auth_dir_table( $name ) ) ) {
            return false;
        }
    }
    return true;
}

function joes_scanner_auth_install_directory() {
    global $wpdb;
    $directory = joes_scanner_auth_table_server_directory();
    $charset = $wpdb->get_charset_collate();
    $revisions = joes_scanner_auth_dir_table( 'revisions' );
    $addresses = joes_scanner_auth_dir_table( 'addresses' );
    $recipients = joes_scanner_auth_dir_table( 'recipients' );
    $verification = joes_scanner_auth_dir_table( 'verification' );

    dbDelta( "CREATE TABLE {$revisions} (
        id BIGINT(20) UNSIGNED NOT NULL AUTO_INCREMENT,
        directory_id BIGINT(20) UNSIGNED NOT NULL,
        owner_user_id BIGINT(20) UNSIGNED NOT NULL,
        actor_user_id BIGINT(20) UNSIGNED NOT NULL,
        reviewer_user_id BIGINT(20) UNSIGNED NULL,
        state VARCHAR(20) NOT NULL,
        changes_json LONGTEXT NOT NULL,
        note TEXT NULL,
        created_at_utc DATETIME NOT NULL,
        decided_at_utc DATETIME NULL,
        PRIMARY KEY (id),
        KEY directory_state (directory_id,state,id)
    ) ENGINE=InnoDB {$charset};" );
    dbDelta( "CREATE TABLE {$addresses} (
        directory_id BIGINT(20) UNSIGNED NOT NULL,
        address_key CHAR(64) NOT NULL,
        PRIMARY KEY (directory_id,address_key),
        KEY address_key (address_key)
    ) ENGINE=InnoDB {$charset};" );
    dbDelta( "CREATE TABLE {$recipients} (
        directory_id BIGINT(20) UNSIGNED NOT NULL,
        user_id BIGINT(20) UNSIGNED NOT NULL,
        PRIMARY KEY (directory_id,user_id),
        KEY user_id (user_id,directory_id)
    ) ENGINE=InnoDB {$charset};" );
    dbDelta( "CREATE TABLE {$verification} (
        token_key CHAR(64) NOT NULL,
        code_hash CHAR(64) NULL,
        email VARCHAR(100) NULL,
        user_id BIGINT(20) UNSIGNED NULL,
        attempts SMALLINT UNSIGNED NOT NULL DEFAULT 0,
        expires_at_utc DATETIME NOT NULL,
        PRIMARY KEY (token_key),
        KEY expires_at_utc (expires_at_utc)
    ) ENGINE=InnoDB {$charset};" );

    if ( ! joes_scanner_auth_directory_tables_exist() ) {
        return false;
    }
    $table_info = $wpdb->get_row( $wpdb->prepare( 'SHOW TABLE STATUS LIKE %s', $wpdb->esc_like( $directory ) ) );
    if ( ! $table_info || ( $table_info->Engine !== 'InnoDB' && $wpdb->query( "ALTER TABLE {$directory} ENGINE=InnoDB" ) === false ) ) {
        return false;
    }

    $legacy_upgrade = version_compare( get_option( JOES_SCANNER_AUTH_DB_VERSION_KEY, '' ), '1.5.10', '<' );
    $rows_ready = joes_scanner_auth_dir_write( static function () use ( $wpdb, $directory, $legacy_upgrade ) {
        // Legacy contact emails were editable. Migrate only numeric account links.
        $custodian = null;
        $rows = $wpdb->get_results( "SELECT * FROM {$directory}" );
        if ( $wpdb->last_error ) {
            return joes_scanner_auth_dir_db_error();
        }
        foreach ( $rows as $row ) {
            $id = (int) $row->id;
            $key = joes_scanner_auth_dir_address_key( $row->server_url );
            $data = array( 'address_key' => $key );
            if ( (int) $row->owner_user_id === 0 ) {
                $owner = 0;
                foreach ( array( $row->submitted_by_user_id, $row->agreement_accepted_user_id, $row->contact_user_id ) as $candidate ) {
                    if ( $candidate && get_userdata( (int) $candidate ) ) {
                        $owner = (int) $candidate;
                        break;
                    }
                }
                if ( ! $owner ) {
                    if ( $custodian === null ) {
                        // A web upgrade can run on plugins_loaded before WP initializes roles.
                        wp_roles();
                        $fallback = get_users( array( 'capability' => 'manage_options', 'fields' => 'ID', 'number' => 1, 'orderby' => 'ID' ) );
                        $custodian = (int) ( $fallback[0] ?? 0 );
                    }
                    if ( ! $custodian ) {
                        return joes_scanner_auth_dir_db_error();
                    }
                }
                $data['owner_user_id'] = $owner ?: $custodian;
                $data['was_public'] = (int) in_array( $row->status, array( 'approved', 'disabled' ), true );
                $data['requires_firewall_auth'] = (int) $row->use_api_firewall_credentials;
            }
            if ( $wpdb->update( $directory, $data, array( 'id' => $id ) ) === false
                || ! joes_scanner_auth_dir_remember_address( $id, $key ) ) {
                return joes_scanner_auth_dir_db_error();
            }
        }

        if ( $legacy_upgrade ) {
            // The old admin log can identify earlier addresses that are still known.
            $events = joes_scanner_auth_table_app_events();
            $ids = array_fill_keys( array_map( 'intval', wp_list_pluck( $rows, 'id' ) ), true );
            $logs = $wpdb->get_col( "SELECT payload_json FROM {$events} WHERE event_type = 'admin'" );
            if ( $wpdb->last_error ) {
                return joes_scanner_auth_dir_db_error();
            }
            foreach ( $logs as $json ) {
                $event = json_decode( $json, true );
                if ( ! in_array( $event['action'] ?? '', array( 'server_directory_edit', 'server_directory_add_default' ), true ) ) {
                    continue;
                }
                $id = (int) ( $event['data']['server_id'] ?? 0 );
                if ( isset( $ids[$id] ) && ! joes_scanner_auth_dir_remember_address(
                    $id,
                    joes_scanner_auth_dir_address_key( $event['data']['server_url'] ?? '' )
                ) ) {
                    return joes_scanner_auth_dir_db_error();
                }
            }
        }
        return true;
    } );
    return ! is_wp_error( $rows_ready );
}

function joes_scanner_auth_dir_write( callable $work ) {
    global $wpdb;
    // Serialize address claims, suspension and publication across listings.
    $lock = 'js-directory-' . substr( hash( 'sha256', DB_NAME . '.' . joes_scanner_auth_table_server_directory() ), 0, 40 );
    $previous = $wpdb->suppress_errors( true );
    $locked = false;
    try {
        $locked = (int) $wpdb->get_var( $wpdb->prepare( 'SELECT GET_LOCK(%s, 5)', $lock ) ) === 1;
        if ( ! $locked || $wpdb->query( 'START TRANSACTION' ) === false ) {
            return joes_scanner_auth_dir_db_error();
        }
        try {
            $result = $work();
            if ( ! is_wp_error( $result ) && $wpdb->query( 'COMMIT' ) !== false ) {
                return $result;
            }
            return is_wp_error( $result ) ? $result : joes_scanner_auth_dir_db_error();
        } finally {
            // Also roll back if a plugin hook throws.
            $wpdb->query( 'ROLLBACK' );
        }
    } finally {
        if ( $locked ) {
            $wpdb->get_var( $wpdb->prepare( 'SELECT RELEASE_LOCK(%s)', $lock ) );
        }
        $wpdb->suppress_errors( $previous );
    }
}

function joes_scanner_auth_dir_url( $value ) {
    if ( ! is_string( $value ) ) {
        return '';
    }
    $value = trim( $value );
    if ( strlen( $value ) > 255 || ! preg_match( '#^https?://(?:\[[a-f0-9:]+\]|[a-z0-9.-]+)(?::[0-9]{1,5})?(?:/|$)#i', $value )
        || preg_match( '/[^\x21-\x7e]/', $value )
        || strpos( $value, '\\' ) !== false || strpbrk( $value, '?#' ) !== false ) {
        return '';
    }
    $parts = wp_parse_url( $value );
    $scheme = strtolower( $parts['scheme'] ?? '' );
    $host = strtolower( rtrim( $parts['host'] ?? '', '.' ) );
    if ( ! in_array( $scheme, array( 'http', 'https' ), true )
        || $host === '' || isset( $parts['user'] ) || isset( $parts['pass'] ) ) {
        return '';
    }

    // Validate the spelling only. Never resolve or fetch a submitted address.
    $literal = trim( $host, '[]' );
    if ( filter_var( $literal, FILTER_VALIDATE_IP ) ) {
        $flags = defined( 'FILTER_FLAG_GLOBAL_RANGE' ) ? FILTER_FLAG_GLOBAL_RANGE : FILTER_FLAG_NO_PRIV_RANGE | FILTER_FLAG_NO_RES_RANGE;
        $packed = inet_pton( $literal );
        $first = ord( $packed[0] );
        if ( ! filter_var( $literal, FILTER_VALIDATE_IP, $flags )
            || ( strlen( $packed ) === 16 && ( $first < 32 || $first >= 64 ) )
            || ( strlen( $packed ) === 4 && ( $first === 0 || $first >= 224
                || ( $first === 100 && ( ord( $packed[1] ) & 192 ) === 64 ) ) ) ) {
            return '';
        }
        $host = strlen( $packed ) === 16 ? '[' . inet_ntop( $packed ) . ']' : inet_ntop( $packed );
    } elseif ( strlen( $host ) > 253
        || ! preg_match( '/^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$/D', $host )
        || preg_match( '/(?:\.(?:localhost|local|localdomain|internal|lan)|\.home\.arpa)$/D', $host ) ) {
        return '';
    }

    $default_port = $scheme === 'https' ? 443 : 80;
    $port = $parts['port'] ?? $default_port;
    if ( $port < 1 || $port > 65535 ) {
        return '';
    }
    $path = $parts['path'] ?? '';
    if ( preg_match( '/%(?![0-9a-f]{2})|%(?:0[0-9a-f]|1[0-9a-f]|2f|3f|23|5c|7f|25)/i', $path )
        || ! preg_match( '#^[a-z0-9/._~!$&()*+,;=:@%+-]*$#iD', $path ) ) {
        return '';
    }
    $path = preg_replace_callback( '/%([0-9a-f]{2})/i', static function ( $match ) {
        $char = chr( hexdec( $match[1] ) );
        return preg_match( '/^[a-z0-9._~-]$/iD', $char ) ? $char : strtoupper( $match[0] );
    }, $path );
    $segments = array();
    foreach ( explode( '/', $path ) as $segment ) {
        if ( $segment === '..' ) {
            array_pop( $segments );
        } elseif ( $segment !== '' && $segment !== '.' ) {
            $segments[] = $segment;
        }
    }
    return $scheme . '://' . $host . ( $port === $default_port ? '' : ':' . $port )
        . ( $segments ? '/' . implode( '/', $segments ) : '' );
}

function joes_scanner_auth_dir_origin( $url ) {
    return (string) preg_replace( '#^(https?://[^/]+).*$#D', '$1', joes_scanner_auth_dir_url( $url ) );
}

function joes_scanner_auth_dir_address_key( $url ) {
    $url = joes_scanner_auth_dir_url( $url );
    // The recorder may serve the same path over HTTP and HTTPS.
    return $url === '' ? '' : hash( 'sha256', strtolower( substr( $url, strpos( $url, '://' ) + 3 ) ) );
}

function joes_scanner_auth_dir_remember_address( $id, $key ) {
    if ( $key === '' ) {
        return true;
    }
    global $wpdb;
    $table = joes_scanner_auth_dir_table( 'addresses' );
    return $wpdb->query( $wpdb->prepare( "INSERT IGNORE INTO {$table} (directory_id,address_key) VALUES (%d,%s)", $id, $key ) ) !== false;
}

// Both public readers and writers consult the same durable suspension records.
function joes_scanner_auth_dir_suspended_address_sql() {
    $directory = joes_scanner_auth_table_server_directory();
    $addresses = joes_scanner_auth_dir_table( 'addresses' );
    return "SELECT a.address_key FROM {$addresses} a JOIN {$directory} s ON s.id = a.directory_id WHERE s.status = 'suspended'";
}

function joes_scanner_auth_dir_shareable_sql( $alias ) {
    $suspended = joes_scanner_auth_dir_suspended_address_sql();
    return "{$alias}.status = 'approved' AND {$alias}.owner_user_id > 0 AND {$alias}.address_key <> ''
        AND {$alias}.address_key NOT IN ({$suspended})";
}

function joes_scanner_auth_dir_validate_address( $url, $visibility, $id ) {
    global $wpdb;
    $table = joes_scanner_auth_table_server_directory();
    $key = joes_scanner_auth_dir_address_key( $url );
    $suspended = joes_scanner_auth_dir_suspended_address_sql();
    if ( $key === '' ) {
        return joes_scanner_auth_dir_error( 'Enter a valid server address.' );
    }
    $blocked = $wpdb->get_var( $wpdb->prepare( "SELECT %s IN ({$suspended})", $key ) );
    if ( $wpdb->last_error ) {
        return joes_scanner_auth_dir_db_error();
    }
    if ( (int) $blocked === 1 ) {
        return joes_scanner_auth_dir_error( 'This address cannot be listed in the server directory.', 409 );
    }
    if ( $visibility === 'public' ) {
        $other = $wpdb->get_var( $wpdb->prepare(
            "SELECT id FROM {$table} WHERE address_key = %s AND id <> %d AND visibility = 'public'
                AND status IN ('pending','approved','disabled') LIMIT 1",
            $key,
            $id
        ) );
        if ( $wpdb->last_error ) {
            return joes_scanner_auth_dir_db_error();
        }
        if ( $other ) {
            return joes_scanner_auth_dir_error( 'A Public listing already uses this address.', 409 );
        }
    }
    return true;
}

function joes_scanner_auth_dir_row( $id, $owner_id = 0 ) {
    global $wpdb;
    $table = joes_scanner_auth_table_server_directory();
    if ( $owner_id ) {
        return $wpdb->get_row( $wpdb->prepare( "SELECT * FROM {$table} WHERE id = %d AND owner_user_id = %d", $id, $owner_id ) );
    }
    return $wpdb->get_row( $wpdb->prepare( "SELECT * FROM {$table} WHERE id = %d", $id ) );
}

function joes_scanner_auth_dir_revision( $id, $state ) {
    global $wpdb;
    $table = joes_scanner_auth_dir_table( 'revisions' );
    return $wpdb->get_row( $wpdb->prepare(
        "SELECT * FROM {$table} WHERE directory_id = %d AND state = %s ORDER BY id DESC LIMIT 1",
        $id,
        $state
    ) );
}

function joes_scanner_auth_dir_record( $id, $owner, $actor, $state, array $changes, $note = '', $reviewer = null ) {
    global $wpdb;
    return $wpdb->insert( joes_scanner_auth_dir_table( 'revisions' ), array(
        'directory_id'     => $id,
        'owner_user_id'    => $owner,
        'actor_user_id'    => $actor,
        'reviewer_user_id' => $reviewer,
        'state'            => $state,
        'changes_json'     => wp_json_encode( $changes ),
        'note'             => $note,
        'created_at_utc'   => gmdate( 'Y-m-d H:i:s' ),
        'decided_at_utc'   => $reviewer ? gmdate( 'Y-m-d H:i:s' ) : null,
    ) ) !== false;
}

function joes_scanner_auth_dir_supersede( $id ) {
    global $wpdb;
    return $wpdb->update(
        joes_scanner_auth_dir_table( 'revisions' ),
        array( 'state' => 'superseded', 'decided_at_utc' => gmdate( 'Y-m-d H:i:s' ) ),
        array( 'directory_id' => $id, 'state' => 'pending' )
    ) !== false;
}

function joes_scanner_auth_dir_fields( $row ) {
    global $wpdb;
    $recipients = joes_scanner_auth_dir_table( 'recipients' );
    $audience = $row ? $wpdb->get_col( $wpdb->prepare(
        "SELECT user_id FROM {$recipients} WHERE directory_id = %d ORDER BY user_id", $row->id
    ) ) : array();
    return array(
        'server_name'             => (string) ( $row->server_name ?? '' ),
        'server_url'              => joes_scanner_auth_dir_url( $row->server_url ?? '' ),
        'info_url'                => joes_scanner_auth_dir_url( $row->info_url ?? '' ),
        'area_label'              => (string) ( $row->area_label ?? '' ),
        'map_anchor'              => (string) ( $row->map_anchor ?? '' ),
        'description'             => (string) ( $row->description ?? '' ),
        'visibility'              => (string) ( $row->visibility ?? 'public' ),
        'requires_scanner_login'  => (int) ( $row->requires_scanner_login ?? 0 ),
        'requires_firewall_auth'  => (int) ( $row->requires_firewall_auth ?? 0 ),
        'audience'                => array_map( 'intval', $audience ),
    );
}

function joes_scanner_auth_dir_set_recipients( $id, array $audience ) {
    global $wpdb;
    $table = joes_scanner_auth_dir_table( 'recipients' );
    if ( $wpdb->delete( $table, array( 'directory_id' => $id ) ) === false ) {
        return false;
    }
    foreach ( $audience as $user_id ) {
        if ( $wpdb->insert( $table, array( 'directory_id' => $id, 'user_id' => $user_id ) ) === false ) {
            return false;
        }
    }
    return true;
}

function joes_scanner_auth_dir_apply( $row, array $fields, $status, array $managed = array() ) {
    $data = array_diff_key( $fields, array( 'audience' => true ) );
    $data['status'] = $status;
    $data['address_key'] = joes_scanner_auth_dir_address_key( $fields['server_url'] );
    $data['updated_at_utc'] = gmdate( 'Y-m-d H:i:s' );
    $data['directory_version'] = (int) ( $row->directory_version ?? 0 ) + 1;
    $data['was_public'] = (int) ( ! empty( $row->was_public ) || ( $status === 'approved' && $fields['visibility'] === 'public' ) );
    if ( $row && ( joes_scanner_auth_dir_origin( $row->server_url ) !== joes_scanner_auth_dir_origin( $fields['server_url'] )
        || ( $row->use_api_firewall_credentials && ! $fields['requires_firewall_auth'] ) ) ) {
        $data['use_api_firewall_credentials'] = 0;
        $data['firewall_username'] = null;
        $data['firewall_password'] = null;
    }
    return array_replace( $data, $managed );
}

function joes_scanner_auth_dir_save( $actor, $id, $version, array $fields, $accepted, $managed = null ) {
    global $wpdb;
    $table = joes_scanner_auth_table_server_directory();
    return joes_scanner_auth_dir_write( static function () use ( $wpdb, $table, $actor, $id, $version, $fields, $accepted, $managed ) {
        $admin = $managed !== null;
        $row = $id ? joes_scanner_auth_dir_row( $id, $admin ? 0 : $actor ) : null;
        if ( $id && ! $row ) {
            return joes_scanner_auth_dir_error( 'Listing not found.', 404 );
        }
        if ( $row && (int) $row->directory_version !== $version ) {
            return joes_scanner_auth_dir_error( 'This listing changed. Reload the page and try again.', 409 );
        }
        if ( $row && $row->status === 'suspended' ) {
            return joes_scanner_auth_dir_error( 'This listing is suspended for security.', 409 );
        }
        $address = joes_scanner_auth_dir_validate_address( $fields['server_url'], $fields['visibility'], $id );
        if ( is_wp_error( $address ) ) {
            return $address;
        }

        $owner = $row ? (int) $row->owner_user_id : $actor;
        $agreement = array();
        if ( $fields['visibility'] === 'public' && ! $admin && empty( $row->agreement_accepted ) ) {
            if ( ! $accepted ) {
                return joes_scanner_auth_dir_error( 'Accept the public listing terms to request Public visibility.' );
            }
            $agreement = array(
                'agreement_accepted' => 1,
                'agreement_text_version' => joes_scanner_auth_server_directory_agreement_version(),
                'agreement_text_snapshot' => joes_scanner_auth_server_directory_agreement_text(),
                'agreement_accepted_at_utc' => gmdate( 'Y-m-d H:i:s' ),
                'agreement_accepted_user_id' => $actor,
            );
        }
        $current = joes_scanner_auth_dir_fields( $row );
        $changes = array();
        foreach ( $fields as $field => $value ) {
            if ( $current[$field] !== $value ) {
                $changes[$field] = $value;
            }
        }
        $active = $row && in_array( $row->status, array( 'approved', 'disabled' ), true );
        $substantial = array_intersect_key( $changes, array_flip(
            array( 'server_url', 'visibility', 'requires_scanner_login', 'requires_firewall_auth', 'audience' )
        ) );
        $stage = $active
            ? ! $admin && $substantial && ( $current['visibility'] === 'public' || $fields['visibility'] !== 'private' )
            : $fields['visibility'] !== 'private';
        $status = $active ? $row->status : ( $stage ? 'pending' : 'approved' );
        $data = $stage && $active
            ? array( 'directory_version' => $version + 1, 'updated_at_utc' => gmdate( 'Y-m-d H:i:s' ) )
            : joes_scanner_auth_dir_apply( $row, $fields, $status, $managed ?? array() );
        $data += $agreement;
        if ( ! $row ) {
            $user = get_userdata( $owner );
            $data += array(
                'created_at_utc'       => gmdate( 'Y-m-d H:i:s' ),
                'owner_user_id'        => $owner,
                'submitted_by_user_id' => $owner,
                'submitted_username'   => $user->user_login,
                'submitted_email'      => $user->user_email,
                'contact_user_id'      => $owner,
                'contact_username'     => $user->user_login,
                'contact_email'        => $user->user_email,
            );
            if ( $wpdb->insert( $table, $data ) === false ) {
                return joes_scanner_auth_dir_db_error();
            }
            $id = (int) $wpdb->insert_id;
        } elseif ( $wpdb->update( $table, $data, array( 'id' => $id ) ) === false ) {
            return joes_scanner_auth_dir_db_error();
        }

        if ( $stage || ( ! $active || $substantial ) ) {
            if ( ! joes_scanner_auth_dir_supersede( $id ) ) {
                return joes_scanner_auth_dir_db_error();
            }
        }
        if ( ! $stage && ! joes_scanner_auth_dir_set_recipients( $id, $fields['audience'] ) ) {
            return joes_scanner_auth_dir_db_error();
        }
        // Remember only applied addresses. A rejected proposal cannot blacklist another address.
        if ( isset( $data['address_key'] ) && ! joes_scanner_auth_dir_remember_address( $id, $data['address_key'] ) ) {
            return joes_scanner_auth_dir_db_error();
        }
        // Pending submissions have no active recipients and need their complete snapshot.
        $record = $stage && ! $active ? $fields : $changes;
        if ( $admin ) {
            foreach ( array_keys( $managed ) as $field ) {
                $record[$field] = true; // Audit the field name without keeping the secret value.
            }
        }
        if ( ! joes_scanner_auth_dir_record( $id, $owner, $actor, $stage ? 'pending' : 'applied', $record, '', $admin ? $actor : null ) ) {
            return joes_scanner_auth_dir_db_error();
        }
        return array( 'id' => $id, 'pending' => (bool) $stage, 'status' => $status );
    } );
}

function joes_scanner_auth_dir_moderate( $id, $actor, $operation, $note, $revision_id, $owner_only = false ) {
    global $wpdb;
    $table = joes_scanner_auth_table_server_directory();
    return joes_scanner_auth_dir_write( static function () use ( $wpdb, $table, $id, $actor, $operation, $note, $revision_id, $owner_only ) {
        $row = joes_scanner_auth_dir_row( $id, $owner_only ? $actor : 0 );
        if ( ! $row ) {
            return joes_scanner_auth_dir_error( 'Listing not found.', 404 );
        }
        $owner = (int) $row->owner_user_id;
        $pending = joes_scanner_auth_dir_revision( $id, 'pending' );
        $data = array(
            'directory_version'   => (int) $row->directory_version + 1,
            'updated_at_utc'      => gmdate( 'Y-m-d H:i:s' ),
        );
        if ( ! $owner_only ) {
            $data['reviewed_by_user_id'] = $actor;
            $data['reviewed_at_utc'] = gmdate( 'Y-m-d H:i:s' );
            $data['review_notes'] = $note;
        }
        $review = in_array( $operation, array( 'approve', 'reject', 'request_changes' ), true );
        $fields = joes_scanner_auth_dir_fields( $row );
        if ( $review ) {
            if ( $row->status === 'suspended' || (int) ( $pending->id ?? 0 ) !== $revision_id
                || ( ! $pending && $row->status !== 'pending' ) ) {
                return joes_scanner_auth_dir_error( 'The proposal changed. Reload the review page.', 409 );
            }
            if ( $operation === 'approve' ) {
                $proposed = $pending ? (array) json_decode( $pending->changes_json, true ) : array();
                $fields = array_replace( $fields, array_intersect_key( $proposed, $fields ) );
                $valid = joes_scanner_auth_dir_validate_address( $fields['server_url'], $fields['visibility'], $id );
                if ( is_wp_error( $valid ) ) {
                    return $valid;
                }
                $status = $row->status === 'disabled' ? 'disabled' : 'approved';
                $data = joes_scanner_auth_dir_apply( $row, $fields, $status ) + $data;
                $data['reject_reason'] = null;
                if ( $fields['visibility'] === 'public' && (int) $row->sort_order === 0 ) {
                    $data['sort_order'] = (int) $wpdb->get_var( "SELECT MAX(sort_order) FROM {$table} WHERE visibility = 'public' AND status = 'approved'" ) + 10;
                }
                if ( ! joes_scanner_auth_dir_remember_address( $id, $data['address_key'] )
                    || ! joes_scanner_auth_dir_set_recipients( $id, $fields['audience'] ) ) {
                    return joes_scanner_auth_dir_db_error();
                }
            } else {
                $data['reject_reason'] = $note;
                if ( $row->status === 'pending' ) {
                    $data['status'] = 'rejected';
                }
            }
        } else {
            $transitions = array(
                'remove'    => 'removed',
                'suspend'   => 'suspended',
                'reinstate' => 'removed',
                'disable'   => 'disabled',
                'enable'    => 'approved',
            );
            if ( ! isset( $transitions[$operation] )
                || ( $owner_only && $operation !== 'remove' )
                || ( ( $row->status === 'suspended' ) !== ( $operation === 'reinstate' ) )
                || ( $operation === 'enable' && $row->status !== 'disabled' )
                || ( $operation === 'disable' && $row->status !== 'approved' ) ) {
                return joes_scanner_auth_dir_error( 'This action is unavailable for the current listing status.', 409 );
            }
            if ( $operation === 'enable' ) {
                $valid = joes_scanner_auth_dir_validate_address( $fields['server_url'], $fields['visibility'], $id );
                if ( is_wp_error( $valid ) ) {
                    return $valid;
                }
                $data['was_public'] = (int) ( $row->was_public || $fields['visibility'] === 'public' );
            }
            $data['status'] = $transitions[$operation];
            if ( in_array( $operation, array( 'remove', 'suspend', 'reinstate' ), true ) ) {
                if ( ! joes_scanner_auth_dir_supersede( $id ) || ! joes_scanner_auth_dir_set_recipients( $id, array() ) ) {
                    return joes_scanner_auth_dir_db_error();
                }
                $data['use_api_firewall_credentials'] = 0;
                $data['firewall_username'] = null;
                $data['firewall_password'] = null;
                $data['reject_reason'] = null;
            }
        }

        if ( $wpdb->update( $table, $data, array( 'id' => $id ) ) === false ) {
            return joes_scanner_auth_dir_db_error();
        }
        if ( $review && $pending ) {
            $state = array( 'approve' => 'approved', 'reject' => 'rejected', 'request_changes' => 'changes_requested' )[$operation];
            $saved = $wpdb->update( joes_scanner_auth_dir_table( 'revisions' ), array(
                'state' => $state,
                'reviewer_user_id' => $actor,
                'decided_at_utc' => gmdate( 'Y-m-d H:i:s' ),
                'note' => $note,
            ), array( 'id' => $revision_id ) ) !== false;
        } else {
            $saved = joes_scanner_auth_dir_record( $id, $owner, $actor, $operation, array(
                'status' => $data['status'] ?? $row->status,
            ), $note, $owner_only ? null : $actor );
        }
        return $saved ? ( $data['status'] ?? $row->status ) : joes_scanner_auth_dir_db_error();
    } );
}
