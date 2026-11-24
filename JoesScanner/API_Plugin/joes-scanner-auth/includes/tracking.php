<?php
/*
//|--------------------------------------------------------------------------//| Connection tracking helpers//|--------------------------------------------------------------------------*/
function joes_scanner_auth_trim_text( $value, $max_len ) {
    $value = is_string( $value ) ? $value : '';
    $value = trim( $value );
    if ( $value === '' ) {
        return null;
    }
    if ( function_exists( 'mb_substr' ) ) {
        return mb_substr( $value, 0, $max_len );
    }
    return substr( $value, 0, $max_len );
}

function joes_scanner_auth_get_param_first( $params, $keys ) {
    if ( ! is_array( $params ) ) {
        return null;
    }
    foreach ( (array) $keys as $k ) {
        if ( array_key_exists( $k, $params ) ) {
            return $params[ $k ];
        }
    }
    return null;
}

function joes_scanner_auth_parse_telemetry_from_params( $params ) {
    $device_platform = joes_scanner_auth_get_param_first( $params, array( 'device_platform', 'platform' ) );
    $device_type     = joes_scanner_auth_get_param_first( $params, array( 'device_type', 'type' ) );
    $device_model    = joes_scanner_auth_get_param_first( $params, array( 'device_model', 'model' ) );
    $app_version     = joes_scanner_auth_get_param_first( $params, array( 'app_version', 'version' ) );

    return array(
        'device_platform' => $device_platform !== null ? joes_scanner_auth_sanitize_device_string( (string) $device_platform, 60 ) : null,
        'device_type'     => $device_type !== null ? joes_scanner_auth_sanitize_device_string( (string) $device_type, 60 ) : null,
        'device_model'    => $device_model !== null ? joes_scanner_auth_sanitize_device_string( (string) $device_model, 120 ) : null,
        'app_version'     => $app_version !== null ? joes_scanner_auth_sanitize_device_string( (string) $app_version, 60 ) : null,
        'session_token'   => null,
        'device_id_hash'  => null,
    );
}

function joes_scanner_auth_find_session_token_by_user_device( $user_id, $device_id_hash ) {
    global $wpdb;

    $table = joes_scanner_auth_table_sessions();

    $user_id = (int) $user_id;
    if ( $user_id <= 0 || ! is_string( $device_id_hash ) || $device_id_hash === '' ) {
        return null;
    }

    $token = $wpdb->get_var(
        $wpdb->prepare(
            "SELECT session_token
             FROM {$table}
             WHERE user_id = %d AND device_id_hash = %s
             ORDER BY last_seen_utc DESC
             LIMIT 1",
            $user_id,
            $device_id_hash
        )
    );

    return $token ? (string) $token : null;
}

function joes_scanner_auth_session_token_exists( $session_token ) {
    global $wpdb;

    $table = joes_scanner_auth_table_sessions();
    $session_token = joes_scanner_auth_sanitize_device_string( (string) $session_token, 36 );
    if ( ! $session_token ) {
        return false;
    }

    $existing = $wpdb->get_var(
        $wpdb->prepare(
            "SELECT session_token FROM {$table} WHERE session_token = %s",
            $session_token
        )
    );

    return (bool) $existing;
}

function joes_scanner_auth_resolve_session_token( $session_token_in, $user_id, $device_id_hash ) {
    $session_token_in = $session_token_in ? joes_scanner_auth_sanitize_device_string( (string) $session_token_in, 36 ) : null;

    // If the client provides a session token, prefer it.
    // The app generates a per-run session token on startup so it can report the same session
    // consistently across auth and telemetry pings.
    if ( $session_token_in ) {
        $row = joes_scanner_auth_get_session_row_by_token( $session_token_in );

        // If the token already exists in the database, accept it when the device matches and:
        // - it already belongs to this user, or
        // - it was previously created while unauthenticated (user_id empty) and can now be upgraded.
        // This prevents one continuous connection from splitting into separate sessions due to ping arriving before auth.
        if ( $row ) {
            $row_user_id     = isset( $row['user_id'] ) ? (int) $row['user_id'] : 0;
            $row_device_hash = isset( $row['device_id_hash'] ) ? (string) $row['device_id_hash'] : '';

            if ( $row_device_hash === (string) $device_id_hash ) {
                if ( $row_user_id === (int) $user_id || $row_user_id === 0 ) {
                    return $session_token_in;
                }
            }

            return joes_scanner_auth_new_session_token();
        }

        // Token is new, use it.
        return $session_token_in;
    }

    // If there is already a recent session for this account and device, reuse it.
    // This prevents the Active and Recently inactive views from splitting one continuous connection into multiple sessions.
    $opts              = joes_scanner_auth_get_options();
    $active_window_min = isset( $opts['active_window_minutes'] ) ? (int) $opts['active_window_minutes'] : 5;
    $within_seconds    = $active_window_min * 60;

    $existing = joes_scanner_auth_get_latest_session_token_for_user_device( (int) $user_id, (string) $device_id_hash, $within_seconds );
    if ( $existing ) {
        return $existing;
    }

    return joes_scanner_auth_new_session_token();
}


function joes_scanner_auth_ensure_user_device_unique_index() {
    joes_scanner_auth_drop_sessions_user_device_unique_index();
}


function joes_scanner_auth_drop_sessions_user_device_unique_index() {
    global $wpdb;

    $sessions_table = joes_scanner_auth_table_sessions();

    $index_exists = (int) $wpdb->get_var(
        $wpdb->prepare(
            "SELECT COUNT(1)
             FROM INFORMATION_SCHEMA.STATISTICS
             WHERE TABLE_SCHEMA = DATABASE()
               AND TABLE_NAME = %s
               AND INDEX_NAME = %s",
            $sessions_table,
            'user_device'
        )
    );

    if ( $index_exists > 0 ) {
        $wpdb->query( "ALTER TABLE {$sessions_table} DROP INDEX user_device" );
    }
}

function joes_scanner_auth_is_json_request( $request ) {
    if ( $request instanceof WP_REST_Request ) {
        $ct = $request->get_header( 'content-type' );
        $ct = is_string( $ct ) ? strtolower( $ct ) : '';
        if ( $ct !== '' && strpos( $ct, 'application/json' ) !== false ) {
            return true;
        }
    }

    if ( isset( $_SERVER['CONTENT_TYPE'] ) ) {
        $ct = strtolower( (string) $_SERVER['CONTENT_TYPE'] );
        if ( strpos( $ct, 'application/json' ) !== false ) {
            return true;
        }
    }

    return false;
}

function joes_scanner_auth_body_too_large( $request, $max_bytes ) {
    $max_bytes = (int) $max_bytes;
    if ( $max_bytes <= 0 ) {
        return false;
    }

    try {
        if ( $request instanceof WP_REST_Request ) {
            $body = $request->get_body();
            if ( is_string( $body ) && strlen( $body ) > $max_bytes ) {
                return true;
            }
        }
    } catch ( \Throwable $e ) {
        // Ignore, do not block on read issues.
    }

    return false;
}

function joes_scanner_auth_rate_limit_allow( $ip, $username, $max_attempts, $window_seconds ) {
    $ip = is_string( $ip ) ? trim( $ip ) : '';
    $username = is_string( $username ) ? trim( strtolower( $username ) ) : '';

    $max_attempts = max( 1, (int) $max_attempts );
    $window_seconds = max( 30, (int) $window_seconds );

    $key_raw = $ip . '|' . $username;
    $key = 'jsauth_rl_' . substr( hash( 'sha256', $key_raw . '|' . wp_salt( 'nonce' ) ), 0, 32 );

    $state = get_transient( $key );
    if ( ! is_array( $state ) ) {
        $state = array( 'count' => 0 );
    }

    if ( (int) $state['count'] >= $max_attempts ) {
        return false;
    }

    return true;
}

function joes_scanner_auth_rate_limit_record_failure( $ip, $username, $max_attempts, $window_seconds ) {
    $ip = is_string( $ip ) ? trim( $ip ) : '';
    $username = is_string( $username ) ? trim( strtolower( $username ) ) : '';

    $max_attempts = max( 1, (int) $max_attempts );
    $window_seconds = max( 30, (int) $window_seconds );

    $key_raw = $ip . '|' . $username;
    $key = 'jsauth_rl_' . substr( hash( 'sha256', $key_raw . '|' . wp_salt( 'nonce' ) ), 0, 32 );

    $state = get_transient( $key );
    if ( ! is_array( $state ) ) {
        $state = array( 'count' => 0 );
    }

    $state['count'] = min( $max_attempts, (int) $state['count'] + 1 );

    set_transient( $key, $state, $window_seconds );
}

function joes_scanner_auth_now_utc_mysql() {
    return gmdate( 'Y-m-d H:i:s' );
}

function joes_scanner_auth_sanitize_device_string( $value, $max_len = 255 ) {
    $max_len = (int) $max_len;
    if ( $max_len <= 0 ) {
        $max_len = 255;
    }
    $value = is_string( $value ) ? $value : '';
    $value = sanitize_text_field( $value );
    $value = trim( $value );
    if ( $value === '' ) {
        return null;
    }
    if ( function_exists( 'mb_substr' ) ) {
        $value = mb_substr( $value, 0, $max_len );
    } else {
        $value = substr( $value, 0, $max_len );
    }
    return $value;
}

function joes_scanner_auth_ensure_device_hash_key() {
    $key = get_option( JOES_SCANNER_AUTH_DEVICE_HASH_KEY_OPTION );

    if ( is_string( $key ) ) {
        $key = trim( $key );
    } else {
        $key = '';
    }

    if ( $key !== '' ) {
        return $key;
    }

    // Generate a stable per-site secret. We keep it in wp_options so it does not change when WP salts rotate.
    try {
        $key = bin2hex( random_bytes( 32 ) );
    } catch ( \Throwable $e ) {
        $key = wp_generate_password( 64, true, true );
    }

    update_option( JOES_SCANNER_AUTH_DEVICE_HASH_KEY_OPTION, $key, false );
    return $key;
}

function joes_scanner_auth_normalize_device_id_for_hash( $device_id ) {
    $device_id = is_string( $device_id ) ? trim( $device_id ) : '';
    if ( $device_id === '' ) {
        return null;
    }

    // Normalize to reduce accidental hash divergence across clients (case, UUID hyphens).
    $device_id = strtolower( $device_id );

    // If it looks like a UUID, remove hyphens to make formatting differences irrelevant.
    if ( preg_match( '/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/', $device_id ) ) {
        $device_id = str_replace( '-', '', $device_id );
    }

    // If it looks like hex with 0x prefix, normalize by removing the prefix.
    if ( preg_match( '/^0x[0-9a-f]+$/', $device_id ) ) {
        $device_id = substr( $device_id, 2 );
    }

    return $device_id;
}

function joes_scanner_auth_hash_device_id_v2( $device_id_normalized ) {
    $device_id_normalized = is_string( $device_id_normalized ) ? trim( $device_id_normalized ) : '';
    if ( $device_id_normalized === '' ) {
        return null;
    }

    $secret = joes_scanner_auth_ensure_device_hash_key();
    return hash_hmac( 'sha256', $device_id_normalized, $secret );
}

function joes_scanner_auth_hash_device_id_legacy( $device_id_normalized ) {
    $device_id_normalized = is_string( $device_id_normalized ) ? trim( $device_id_normalized ) : '';
    if ( $device_id_normalized === '' ) {
        return null;
    }

    return hash_hmac( 'sha256', $device_id_normalized, wp_salt( 'auth' ) );
}

function joes_scanner_auth_migrate_device_hash_if_needed( $legacy_hash, $new_hash ) {
    global $wpdb;

    $legacy_hash = is_string( $legacy_hash ) ? trim( $legacy_hash ) : '';
    $new_hash    = is_string( $new_hash ) ? trim( $new_hash ) : '';

    if ( $legacy_hash === '' || $new_hash === '' || $legacy_hash === $new_hash ) {
        return;
    }

    $devices_table    = joes_scanner_auth_table_devices();
    $sessions_table   = joes_scanner_auth_table_sessions();
    $events_table     = joes_scanner_auth_table_events();
    $settings_table   = joes_scanner_auth_table_device_settings();
    $app_events_table = joes_scanner_auth_table_app_events();

    $legacy_exists = (int) $wpdb->get_var(
        $wpdb->prepare( "SELECT COUNT(1) FROM {$devices_table} WHERE device_id_hash = %s", $legacy_hash )
    );

    if ( $legacy_exists <= 0 ) {
        // If the device row is missing but legacy hashes exist in other tables, still migrate those rows.
        $legacy_exists = (int) $wpdb->get_var(
            $wpdb->prepare( "SELECT COUNT(1) FROM {$sessions_table} WHERE device_id_hash = %s", $legacy_hash )
        );
    }

    if ( $legacy_exists <= 0 ) {
        return;
    }

    $new_exists = (int) $wpdb->get_var(
        $wpdb->prepare( "SELECT COUNT(1) FROM {$devices_table} WHERE device_id_hash = %s", $new_hash )
    );

    // Only auto-migrate when the new hash does not already exist to avoid collisions.
    if ( $new_exists > 0 ) {
        return;
    }

    // Migrate devices row (unique key) first, then all referencing tables.
    $wpdb->query(
        $wpdb->prepare( "UPDATE {$devices_table} SET device_id_hash = %s WHERE device_id_hash = %s", $new_hash, $legacy_hash )
    );

    $wpdb->query(
        $wpdb->prepare( "UPDATE {$sessions_table} SET device_id_hash = %s WHERE device_id_hash = %s", $new_hash, $legacy_hash )
    );

    $wpdb->query(
        $wpdb->prepare( "UPDATE {$events_table} SET device_id_hash = %s WHERE device_id_hash = %s", $new_hash, $legacy_hash )
    );

    $wpdb->query(
        $wpdb->prepare( "UPDATE {$app_events_table} SET device_id_hash = %s WHERE device_id_hash = %s", $new_hash, $legacy_hash )
    );

    $wpdb->query(
        $wpdb->prepare( "UPDATE {$settings_table} SET device_id_hash = %s WHERE device_id_hash = %s", $new_hash, $legacy_hash )
    );
}

function joes_scanner_auth_resolve_device_id_hash( $device_id_raw ) {
    $normalized = joes_scanner_auth_normalize_device_id_for_hash( $device_id_raw );
    if ( ! $normalized ) {
        return null;
    }

    $new_hash    = joes_scanner_auth_hash_device_id_v2( $normalized );
    $legacy_hash = joes_scanner_auth_hash_device_id_legacy( $normalized );

    if ( $legacy_hash && $new_hash ) {
        joes_scanner_auth_migrate_device_hash_if_needed( $legacy_hash, $new_hash );
    }

    return $new_hash;
}

// Backwards compatible wrapper.
function joes_scanner_auth_hash_device_id( $device_id ) {
    return joes_scanner_auth_resolve_device_id_hash( $device_id );
}


function joes_scanner_auth_get_request_ip( $request ) {
    $candidates = array();

    if ( $request instanceof WP_REST_Request ) {
        $cf = $request->get_header( 'cf-connecting-ip' );
        if ( $cf ) {
            $candidates[] = $cf;
        }

        $xff = $request->get_header( 'x-forwarded-for' );
        if ( $xff ) {
            $parts = explode( ',', $xff );
            if ( ! empty( $parts ) ) {
                $candidates[] = trim( $parts[0] );
            }
        }
    }

    if ( isset( $_SERVER['REMOTE_ADDR'] ) ) {
        $candidates[] = $_SERVER['REMOTE_ADDR'];
    }

    foreach ( $candidates as $ip ) {
        $ip = trim( (string) $ip );
        if ( $ip === '' ) {
            continue;
        }
        if ( filter_var( $ip, FILTER_VALIDATE_IP ) ) {
            return $ip;
        }
    }

    return null;
}

function joes_scanner_auth_get_user_agent( $request ) {
    if ( $request instanceof WP_REST_Request ) {
        $ua = $request->get_header( 'user-agent' );
        if ( $ua ) {
            $ua = trim( (string) $ua );
            if ( $ua !== '' ) {
                return $ua;
            }
        }
    }

    if ( isset( $_SERVER['HTTP_USER_AGENT'] ) ) {
        $ua = trim( (string) $_SERVER['HTTP_USER_AGENT'] );
        if ( $ua !== '' ) {
            return $ua;
        }
    }

    return null;
}

function joes_scanner_auth_new_session_token() {
    if ( function_exists( 'wp_generate_uuid4' ) ) {
        return wp_generate_uuid4();
    }

    // Fallback, should be rare.
    $raw = wp_rand() . '|' . microtime( true ) . '|' . wp_salt( 'nonce' );
    return substr( hash( 'sha256', $raw ), 0, 36 );
}

/**
 * Insert a connection event row.
 */
function joes_scanner_auth_log_event( $args ) {
    global $wpdb;

    $table = joes_scanner_auth_table_events();

    $defaults = array(
        'created_at_utc'   => joes_scanner_auth_now_utc_mysql(),
        'user_id'          => null,
        'username'         => null,
        'email'            => null,
        'ip'               => null,
        'user_agent'       => null,
        'device_type'      => null,
        'device_platform'  => null,
        'device_model'     => null,
        'app_version'      => null,
        'device_id_hash'   => null,
        'session_token'    => null,
        'result'           => 'unknown',
        'http_status'      => 0,
        'message'          => null,
    );

    $row = array_merge( $defaults, is_array( $args ) ? $args : array() );

    // Hard trim for safety.
    $row['username']        = $row['username'] ? joes_scanner_auth_sanitize_device_string( $row['username'], 60 ) : null;
    $row['email']           = $row['email'] ? joes_scanner_auth_sanitize_device_string( $row['email'], 100 ) : null;
    $row['ip']              = $row['ip'] ? joes_scanner_auth_sanitize_device_string( $row['ip'], 45 ) : null;
    $row['device_type']     = $row['device_type'] ? joes_scanner_auth_sanitize_device_string( $row['device_type'], 60 ) : null;
    $row['device_platform'] = $row['device_platform'] ? joes_scanner_auth_sanitize_device_string( $row['device_platform'], 60 ) : null;
    $row['device_model']    = $row['device_model'] ? joes_scanner_auth_sanitize_device_string( $row['device_model'], 120 ) : null;
    $row['app_version']     = $row['app_version'] ? joes_scanner_auth_sanitize_device_string( $row['app_version'], 60 ) : null;

    $row['result']      = joes_scanner_auth_sanitize_device_string( (string) $row['result'], 20 );
    $row['message']     = $row['message'] ? joes_scanner_auth_sanitize_device_string( $row['message'], 255 ) : null;
    $row['session_token'] = $row['session_token'] ? joes_scanner_auth_sanitize_device_string( $row['session_token'], 36 ) : null;

    $wpdb->insert(
        $table,
        array(
            'created_at_utc'  => $row['created_at_utc'],
            'user_id'         => $row['user_id'],
            'username'        => $row['username'],
            'email'           => $row['email'],
            'ip'              => $row['ip'],
            'user_agent'      => $row['user_agent'],
            'device_type'     => $row['device_type'],
            'device_platform' => $row['device_platform'],
            'device_model'    => $row['device_model'],
            'app_version'     => $row['app_version'],
            'device_id_hash'  => $row['device_id_hash'],
            'session_token'   => $row['session_token'],
            'result'          => $row['result'] ? $row['result'] : 'unknown',
            'http_status'     => (int) $row['http_status'],
            'message'         => $row['message'],
        ),
        array(
            '%s','%d','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s','%d','%s'
        )
    );
}

function joes_scanner_auth_touch_device_from_session_token( $session_token ) {
    $row = joes_scanner_auth_get_session_row_by_token( $session_token );
    if ( ! $row || empty( $row['device_id_hash'] ) ) {
        return;
    }

    $device_id_hash = (string) $row['device_id_hash'];
    $user_id        = isset( $row['user_id'] ) ? (int) $row['user_id'] : 0;
    $username       = isset( $row['username'] ) ? (string) $row['username'] : null;
    $email          = isset( $row['email'] ) ? (string) $row['email'] : null;

    joes_scanner_auth_upsert_device(
        $device_id_hash,
        $user_id > 0 ? $user_id : null,
        $username,
        $email,
        array(
            'device_type'     => isset( $row['device_type'] ) ? $row['device_type'] : null,
            'device_platform' => isset( $row['device_platform'] ) ? $row['device_platform'] : null,
            'device_model'    => isset( $row['device_model'] ) ? $row['device_model'] : null,
            'app_version'     => isset( $row['app_version'] ) ? $row['app_version'] : null,
        ),
        isset( $row['last_ip'] ) ? $row['last_ip'] : null,
        null
    );
}

/**
 * Upsert a session row, used by auth and ping.
 */
function joes_scanner_auth_upsert_session( $session_token, $args, $mode ) {
    global $wpdb;

    $table = joes_scanner_auth_table_sessions();

    $session_token = joes_scanner_auth_sanitize_device_string( (string) $session_token, 36 );
    if ( ! $session_token ) {
        return;
    }

    $now = joes_scanner_auth_now_utc_mysql();

    // Optional stream server tracking (which backend the app is connected to).
    $stream_server_url = null;
    if ( array_key_exists( 'stream_server_url', $args ) ) {
        $v = $args['stream_server_url'];
        if ( $v !== null ) {
            $v = trim( (string) $v );
            if ( $v !== '' ) {
                $stream_server_url = joes_scanner_auth_trim_text( $v, 255 );
            }
        }
    }

    $stream_server_is_hosted = -1;
    if ( array_key_exists( 'stream_server_is_hosted', $args ) ) {
        $v = $args['stream_server_is_hosted'];
        if ( $v !== null && $v !== '' ) {
            $stream_server_is_hosted = (int) $v;
            if ( $stream_server_is_hosted !== 0 && $stream_server_is_hosted !== 1 ) {
                $stream_server_is_hosted = -1;
            }
        }
    }

    $stream_server_seen_utc = null;
    if ( ( $stream_server_url !== null && $stream_server_url !== '' ) || ( $stream_server_is_hosted === 0 || $stream_server_is_hosted === 1 ) ) {
        $stream_server_seen_utc = $now;
    }

    // If a session token disappears for a while and then reappears (app relaunch, sleep, network loss),
    // treat it as a new session for reporting by resetting first_seen_utc. This prevents inflated
    // session durations when a client reuses a token across multiple launches.
    $opts              = joes_scanner_auth_get_options();
    $active_window_min = isset( $opts['active_window_minutes'] ) ? (int) $opts['active_window_minutes'] : 5;
    $gap_seconds       = max( 300, $active_window_min * 60 * 2 );

    $row = array(
        'user_id'                 => isset( $args['user_id'] ) ? (int) $args['user_id'] : null,
        'username'                => isset( $args['username'] ) ? joes_scanner_auth_sanitize_device_string( (string) $args['username'], 60 ) : null,
        'email'                   => isset( $args['email'] ) ? joes_scanner_auth_sanitize_device_string( (string) $args['email'], 100 ) : null,
        'device_type'             => isset( $args['device_type'] ) ? joes_scanner_auth_sanitize_device_string( (string) $args['device_type'], 60 ) : null,
        'device_platform'         => isset( $args['device_platform'] ) ? joes_scanner_auth_sanitize_device_string( (string) $args['device_platform'], 60 ) : null,
        'device_model'            => isset( $args['device_model'] ) ? joes_scanner_auth_sanitize_device_string( (string) $args['device_model'], 120 ) : null,
        'app_version'             => isset( $args['app_version'] ) ? joes_scanner_auth_sanitize_device_string( (string) $args['app_version'], 60 ) : null,
        'device_id_hash'          => isset( $args['device_id_hash'] ) ? $args['device_id_hash'] : null,
        'stream_server_url'       => $stream_server_url,
        'stream_server_is_hosted' => $stream_server_is_hosted,
        'stream_server_seen_utc'  => $stream_server_seen_utc,
        'last_ip'                 => isset( $args['ip'] ) ? joes_scanner_auth_sanitize_device_string( (string) $args['ip'], 45 ) : null,
        'last_user_agent'         => isset( $args['user_agent'] ) ? (string) $args['user_agent'] : null,
    );

    $auth_inc = ( $mode === 'auth' ) ? 1 : 0;
    $ping_inc = ( $mode === 'ping' ) ? 1 : 0;

    // Update-first approach avoids noisy duplicate-primary logs during startup races where
    // auth and ping can hit the same session token nearly simultaneously.
    $update_sql = "UPDATE {$table}
        SET
            user_id = COALESCE(NULLIF(%d, 0), user_id),
            username = COALESCE(NULLIF(%s, ''), username),
            email = COALESCE(NULLIF(%s, ''), email),
            device_type = COALESCE(NULLIF(%s, ''), device_type),
            device_platform = COALESCE(NULLIF(%s, ''), device_platform),
            device_model = COALESCE(NULLIF(%s, ''), device_model),
            app_version = COALESCE(NULLIF(%s, ''), app_version),
            device_id_hash = COALESCE(NULLIF(%s, ''), device_id_hash),
            first_seen_utc = IF(last_seen_utc IS NULL OR TIMESTAMPDIFF(SECOND, last_seen_utc, %s) > %d, %s, first_seen_utc),
            last_seen_utc = %s,
            last_ip = COALESCE(NULLIF(%s, ''), last_ip),
            last_user_agent = COALESCE(NULLIF(%s, ''), last_user_agent),
            stream_server_url = COALESCE(NULLIF(%s, ''), stream_server_url),
            stream_server_is_hosted = IF(%d IN (0,1), %d, stream_server_is_hosted),
            stream_server_seen_utc = IF((%s <> '' OR %d IN (0,1)), %s, stream_server_seen_utc),
            auth_count = auth_count + %d,
            ping_count = ping_count + %d
        WHERE session_token = %s";

    $updated = $wpdb->query(
        $wpdb->prepare(
            $update_sql,
            $row['user_id'],
            $row['username'],
            $row['email'],
            $row['device_type'],
            $row['device_platform'],
            $row['device_model'],
            $row['app_version'],
            $row['device_id_hash'],
            $now,
            $gap_seconds,
            $now,
            $now,
            $row['last_ip'],
            $row['last_user_agent'],
            $row['stream_server_url'],
            ( $row['stream_server_is_hosted'] === 0 || $row['stream_server_is_hosted'] === 1 ) ? (int) $row['stream_server_is_hosted'] : -1,
            ( $row['stream_server_is_hosted'] === 0 || $row['stream_server_is_hosted'] === 1 ) ? (int) $row['stream_server_is_hosted'] : -1,
            (string) ( $row['stream_server_url'] !== null ? $row['stream_server_url'] : '' ),
            ( $row['stream_server_is_hosted'] === 0 || $row['stream_server_is_hosted'] === 1 ) ? (int) $row['stream_server_is_hosted'] : -1,
            $row['stream_server_seen_utc'],
            $auth_inc,
            $ping_inc,
            $session_token
        )
    );

    if ( 0 === (int) $updated ) {
        $insert_sql = "INSERT IGNORE INTO {$table}
            (session_token, user_id, username, email, device_type, device_platform, device_model, app_version, device_id_hash, first_seen_utc, last_seen_utc, last_ip, last_user_agent, stream_server_url, stream_server_is_hosted, stream_server_seen_utc, auth_count, ping_count)
            VALUES (%s, %d, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %d, %s, %d, %d)";

        $suppress = $wpdb->suppress_errors( true );
        $inserted = $wpdb->query(
            $wpdb->prepare(
                $insert_sql,
                $session_token,
                $row['user_id'],
                $row['username'],
                $row['email'],
                $row['device_type'],
                $row['device_platform'],
                $row['device_model'],
                $row['app_version'],
                $row['device_id_hash'],
                $now,
                $now,
                $row['last_ip'],
                $row['last_user_agent'],
                $row['stream_server_url'],
                ( $row['stream_server_is_hosted'] === 0 || $row['stream_server_is_hosted'] === 1 ) ? (int) $row['stream_server_is_hosted'] : 0,
                $row['stream_server_seen_utc'],
                $auth_inc,
                $ping_inc
            )
        );
        $wpdb->suppress_errors( $suppress );

        if ( 0 === (int) $inserted ) {
            $wpdb->query(
                $wpdb->prepare(
                    $update_sql,
                    $row['user_id'],
                    $row['username'],
                    $row['email'],
                    $row['device_type'],
                    $row['device_platform'],
                    $row['device_model'],
                    $row['app_version'],
                    $row['device_id_hash'],
                    $now,
                    $gap_seconds,
                    $now,
                    $now,
                    $row['last_ip'],
                    $row['last_user_agent'],
                    $row['stream_server_url'],
                    ( $row['stream_server_is_hosted'] === 0 || $row['stream_server_is_hosted'] === 1 ) ? (int) $row['stream_server_is_hosted'] : -1,
                    ( $row['stream_server_is_hosted'] === 0 || $row['stream_server_is_hosted'] === 1 ) ? (int) $row['stream_server_is_hosted'] : -1,
                    (string) ( $row['stream_server_url'] !== null ? $row['stream_server_url'] : '' ),
                    ( $row['stream_server_is_hosted'] === 0 || $row['stream_server_is_hosted'] === 1 ) ? (int) $row['stream_server_is_hosted'] : -1,
                    $row['stream_server_seen_utc'],
                    $auth_inc,
                    $ping_inc,
                    $session_token
                )
            );
        }
    }

    // Keep the devices table synchronized even when a session token is reused across pings.
    joes_scanner_auth_touch_device_from_session_token( $session_token );

    if ( ! empty( $row['device_id_hash'] ) ) {
        joes_scanner_auth_upsert_device(
            (string) $row['device_id_hash'],
            isset( $row['user_id'] ) ? (int) $row['user_id'] : null,
            isset( $row['username'] ) ? $row['username'] : null,
            isset( $row['email'] ) ? $row['email'] : null,
            array(
                'device_type'     => $row['device_type'],
                'device_platform' => $row['device_platform'],
                'device_model'    => $row['device_model'],
                'app_version'     => $row['app_version'],
            ),
            $row['last_ip'],
            $row['last_user_agent']
        );
    }
}

/**
 * Maintenance: purge old events and sessions.
 */
add_action( JOES_SCANNER_AUTH_CRON_HOOK, 'joes_scanner_auth_run_maintenance' );


function joes_scanner_auth_upsert_device( $device_id_hash, $user_id, $username, $email, $telemetry, $ip, $user_agent ) {
    global $wpdb;

    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );
    if ( ! $device_id_hash ) {
        return;
    }

    $devices_table = joes_scanner_auth_table_devices();
    $now = joes_scanner_auth_now_utc_mysql();

    $user_id  = $user_id ? (int) $user_id : 0;
    $username = $username ? joes_scanner_auth_trim_text( (string) $username, 60 ) : '';
    $email    = $email ? joes_scanner_auth_trim_text( (string) $email, 100 ) : '';

    $row_common = array(
        'device_type'     => isset( $telemetry['device_type'] ) ? joes_scanner_auth_trim_text( (string) $telemetry['device_type'], 60 ) : null,
        'device_platform' => isset( $telemetry['device_platform'] ) ? joes_scanner_auth_trim_text( (string) $telemetry['device_platform'], 60 ) : null,
        'device_model'    => isset( $telemetry['device_model'] ) ? joes_scanner_auth_trim_text( (string) $telemetry['device_model'], 120 ) : null,
        'app_version'     => isset( $telemetry['app_version'] ) ? joes_scanner_auth_trim_text( (string) $telemetry['app_version'], 60 ) : null,
        'last_seen_utc'   => $now,
        'last_ip'         => $ip ? joes_scanner_auth_trim_text( (string) $ip, 45 ) : null,
        'last_user_agent' => $user_agent ? joes_scanner_auth_trim_text( (string) $user_agent, 512 ) : null,
    );

    $existing = $wpdb->get_row(
        $wpdb->prepare(
            "SELECT device_id, user_id FROM {$devices_table} WHERE device_id_hash = %s LIMIT 1",
            $device_id_hash
        ),
        ARRAY_A
    );

    if ( is_array( $existing ) && ! empty( $existing['device_id'] ) ) {
        $existing_id     = (int) $existing['device_id'];
        $existing_user_id = isset( $existing['user_id'] ) ? (int) $existing['user_id'] : 0;

        // Update account binding only when the device is not already linked to a different account.
        // Heartbeats never clear account binding.
        $data    = array();
        $formats = array();

        if ( $user_id > 0 && ( $existing_user_id <= 0 || $existing_user_id === $user_id ) ) {
            $data['user_id']  = $user_id;
            $formats[]        = '%d';

            $data['username'] = $username ? $username : null;
            $formats[]        = '%s';

            $data['email']    = $email ? $email : null;
            $formats[]        = '%s';
        }

        // Always update last seen and network fields.
        // Preserve existing telemetry when the incoming values are missing.
        foreach ( $row_common as $k => $v ) {
            if ( $k === 'last_seen_utc' || $k === 'last_ip' || $k === 'last_user_agent' ) {
                $data[ $k ] = $v;
                $formats[]  = '%s';
                continue;
            }

            if ( $v === null || $v === '' ) {
                continue;
            }

            $data[ $k ] = $v;
            $formats[]  = '%s';
        }

        $wpdb->update(
            $devices_table,
            $data,
            array( 'device_id' => $existing_id ),
            $formats,
            array( '%d' )
        );



        if ( $user_id > 0 && ( $existing_user_id <= 0 || $existing_user_id === $user_id ) ) {
            joes_scanner_auth_apply_device_binding_to_rows( $device_id_hash, $user_id, $username, $email );
        } elseif ( $existing_user_id > 0 && $existing_user_id !== $user_id ) {
            // Keep the existing binding authoritative and normalize related rows to it.
            $existing_user = get_user_by( 'id', $existing_user_id );
            $existing_username = $existing_user ? (string) $existing_user->user_login : null;
            $existing_email    = $existing_user ? (string) $existing_user->user_email : null;
            joes_scanner_auth_apply_device_binding_to_rows( $device_id_hash, $existing_user_id, $existing_username, $existing_email );
        }
        return;
    }

    $wpdb->insert(
        $devices_table,
        array(
            'device_id_hash'   => $device_id_hash,
            'user_id'          => $user_id > 0 ? $user_id : null,
            'username'         => $user_id > 0 && $username ? $username : null,
            'email'            => $user_id > 0 && $email ? $email : null,
            'device_type'      => $row_common['device_type'],
            'device_platform'  => $row_common['device_platform'],
            'device_model'     => $row_common['device_model'],
            'app_version'      => $row_common['app_version'],
            'first_seen_utc'   => $now,
            'last_seen_utc'    => $now,
            'last_ip'          => $row_common['last_ip'],
            'last_user_agent'  => $row_common['last_user_agent'],
        ),
        array( '%s','%d','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s' )
    );


    if ( $user_id > 0 ) {
        joes_scanner_auth_apply_device_binding_to_rows( $device_id_hash, $user_id, $username, $email );
    }
}

function joes_scanner_auth_apply_device_binding_to_rows( $device_id_hash, $user_id, $username, $email ) {
    global $wpdb;

    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );
    if ( ! $device_id_hash ) {
        return;
    }

    $user_id  = (int) $user_id;
    $username = $username ? joes_scanner_auth_sanitize_device_string( (string) $username, 60 ) : null;
    $email    = $email ? joes_scanner_auth_sanitize_device_string( (string) $email, 100 ) : null;

    if ( $user_id <= 0 ) {
        return;
    }

    $sessions_table   = joes_scanner_auth_table_sessions();
    $events_table     = joes_scanner_auth_table_events();
    $app_events_table = joes_scanner_auth_table_app_events();

    // Normalize sessions: any session for this device should inherit the device binding.
    $wpdb->query(
        $wpdb->prepare(
            "UPDATE {$sessions_table}
             SET user_id = %d, username = %s, email = %s
             WHERE device_id_hash = %s
               AND (user_id IS NULL OR user_id = 0 OR user_id <> %d)",
            $user_id,
            $username,
            $email,
            $device_id_hash,
            $user_id
        )
    );

    // Normalize auth events.
    $wpdb->query(
        $wpdb->prepare(
            "UPDATE {$events_table}
             SET user_id = %d, username = %s, email = %s
             WHERE device_id_hash = %s
               AND (user_id IS NULL OR user_id = 0 OR user_id <> %d)",
            $user_id,
            $username,
            $email,
            $device_id_hash,
            $user_id
        )
    );

    // Normalize app events.
    $wpdb->query(
        $wpdb->prepare(
            "UPDATE {$app_events_table}
             SET user_id = %d, username = %s, email = %s
             WHERE device_id_hash = %s
               AND (user_id IS NULL OR user_id = 0 OR user_id <> %d)",
            $user_id,
            $username,
            $email,
            $device_id_hash,
            $user_id
        )
    );
}

function joes_scanner_auth_reconcile_all_bindings() {
    global $wpdb;

    $devices_table    = joes_scanner_auth_table_devices();
    $sessions_table   = joes_scanner_auth_table_sessions();
    $events_table     = joes_scanner_auth_table_events();
    $app_events_table = joes_scanner_auth_table_app_events();

    // Sessions inherit binding from devices when sessions are missing/incorrect.
    $wpdb->query(
        "UPDATE {$sessions_table} s
         INNER JOIN {$devices_table} d ON d.device_id_hash = s.device_id_hash
         SET s.user_id = d.user_id, s.username = d.username, s.email = d.email
         WHERE d.user_id IS NOT NULL AND d.user_id > 0
           AND (s.user_id IS NULL OR s.user_id = 0 OR s.user_id <> d.user_id)"
    );

    // Auth events inherit binding.
    $wpdb->query(
        "UPDATE {$events_table} e
         INNER JOIN {$devices_table} d ON d.device_id_hash = e.device_id_hash
         SET e.user_id = d.user_id, e.username = d.username, e.email = d.email
         WHERE d.user_id IS NOT NULL AND d.user_id > 0
           AND (e.user_id IS NULL OR e.user_id = 0 OR e.user_id <> d.user_id)"
    );

    // App events inherit binding.
    $wpdb->query(
        "UPDATE {$app_events_table} a
         INNER JOIN {$devices_table} d ON d.device_id_hash = a.device_id_hash
         SET a.user_id = d.user_id, a.username = d.username, a.email = d.email
         WHERE d.user_id IS NOT NULL AND d.user_id > 0
           AND (a.user_id IS NULL OR a.user_id = 0 OR a.user_id <> d.user_id)"
    );
}




function joes_scanner_auth_is_ip_blocked( $ip ) {
    global $wpdb;

    $ip = is_string( $ip ) ? trim( $ip ) : '';
    if ( $ip === '' ) {
        return false;
    }

    $table = joes_scanner_auth_table_blocked_ips();

    $exists = $wpdb->get_var(
        $wpdb->prepare(
            "SELECT ip FROM {$table} WHERE ip = %s LIMIT 1",
            $ip
        )
    );

    return (bool) $exists;
}

function joes_scanner_auth_is_device_blocked_by_hash( $device_id_hash ) {
    global $wpdb;

    $device_id_hash = is_string( $device_id_hash ) ? trim( $device_id_hash ) : '';
    $device_id_hash = $device_id_hash ? joes_scanner_auth_sanitize_device_string( $device_id_hash, 64 ) : null;

    if ( ! $device_id_hash ) {
        return false;
    }

    $table = joes_scanner_auth_table_devices();

    $blocked = $wpdb->get_var(
        $wpdb->prepare(
            "SELECT is_blocked FROM {$table} WHERE device_id_hash = %s LIMIT 1",
            $device_id_hash
        )
    );

    return ( (int) $blocked ) === 1;
}


function joes_scanner_auth_get_device_row_by_hash( $device_id_hash ) {
    global $wpdb;

    $device_id_hash = is_string( $device_id_hash ) ? trim( $device_id_hash ) : '';
    $device_id_hash = $device_id_hash ? joes_scanner_auth_sanitize_device_string( $device_id_hash, 64 ) : null;
    if ( ! $device_id_hash ) {
        return null;
    }

    $table = joes_scanner_auth_table_devices();

    $row = $wpdb->get_row(
        $wpdb->prepare(
            "SELECT device_id, device_id_hash, user_id, username, email, is_blocked
             FROM {$table}
             WHERE device_id_hash = %s
             LIMIT 1",
            $device_id_hash
        ),
        ARRAY_A
    );

    return is_array( $row ) ? $row : null;
}

/**
 * Returns the bound account for a device.
 *
 * Binding is sticky: once a device is linked to an account, it stays linked until the device is cleared.
 * If the device row is missing or the device user_id was cleared by older versions, this function will
 * attempt to recover binding from existing session rows.
 */
function joes_scanner_auth_get_bound_user_for_device( $device_id_hash ) {
    global $wpdb;

    $device_id_hash = is_string( $device_id_hash ) ? trim( $device_id_hash ) : '';
    $device_id_hash = $device_id_hash ? joes_scanner_auth_sanitize_device_string( $device_id_hash, 64 ) : null;
    if ( ! $device_id_hash ) {
        return null;
    }

    $devices_table  = joes_scanner_auth_table_devices();
    $sessions_table = joes_scanner_auth_table_sessions();

    $device_row = joes_scanner_auth_get_device_row_by_hash( $device_id_hash );
    if ( $device_row ) {
        $bound_user_id = isset( $device_row['user_id'] ) ? (int) $device_row['user_id'] : 0;
        if ( $bound_user_id > 0 ) {
            return array(
                'user_id'   => $bound_user_id,
                'username'  => isset( $device_row['username'] ) ? (string) $device_row['username'] : '',
                'email'     => isset( $device_row['email'] ) ? (string) $device_row['email'] : '',
                'source'    => 'device',
            );
        }
    }

    // Recovery path: if sessions already show an authenticated user for this device, restore binding in devices table.
    $sess = $wpdb->get_row(
        $wpdb->prepare(
            "SELECT user_id, username, email
             FROM {$sessions_table}
             WHERE device_id_hash = %s
               AND user_id IS NOT NULL
               AND user_id <> 0
             ORDER BY first_seen_utc ASC, last_seen_utc ASC
             LIMIT 1",
            $device_id_hash
        ),
        ARRAY_A
    );

    if ( is_array( $sess ) && ! empty( $sess['user_id'] ) ) {
        $uid = (int) $sess['user_id'];
        if ( $uid > 0 ) {
            $username = isset( $sess['username'] ) ? (string) $sess['username'] : '';
            $email    = isset( $sess['email'] ) ? (string) $sess['email'] : '';

            $now = joes_scanner_auth_now_utc_mysql();

            if ( $device_row && ! empty( $device_row['device_id'] ) ) {
                $wpdb->update(
                    $devices_table,
                    array(
                        'user_id'        => $uid,
                        'username'       => $username ? joes_scanner_auth_trim_text( $username, 60 ) : null,
                        'email'          => $email ? joes_scanner_auth_trim_text( $email, 100 ) : null,
                        'last_seen_utc'  => $now,
                    ),
                    array( 'device_id' => (int) $device_row['device_id'] ),
                    array( '%d', '%s', '%s', '%s' ),
                    array( '%d' )
                );
            } else {
                // Device row missing, recreate minimal record so future lookups are stable.
                $wpdb->insert(
                    $devices_table,
                    array(
                        'device_id_hash' => $device_id_hash,
                        'user_id'        => $uid,
                        'username'       => $username ? joes_scanner_auth_trim_text( $username, 60 ) : null,
                        'email'          => $email ? joes_scanner_auth_trim_text( $email, 100 ) : null,
                        'device_type'    => null,
                        'device_platform'=> null,
                        'device_model'   => null,
                        'app_version'    => null,
                        'first_seen_utc' => $now,
                        'last_seen_utc'  => $now,
                        'last_ip'        => null,
                        'last_user_agent'=> null,
                    ),
                    array( '%s','%d','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s' )
                );
            }

            return array(
                'user_id'   => $uid,
                'username'  => $username,
                'email'     => $email,
                'source'    => 'sessions_recovered',
            );
        }
    }

    return null;
}

/**
 * Bind a device to a user account.
 *
 * A successful authentication always refreshes the binding in the devices table.
 *
 * Note: We also backfill rows so the device does not appear under multiple accounts.
 * This matches the app's expectations when a user switches accounts on the same device.
 */
function joes_scanner_auth_bind_device_to_user( $device_id_hash, $user_id, $username, $email ) {
    global $wpdb;

    $device_id_hash = is_string( $device_id_hash ) ? trim( $device_id_hash ) : '';
    $device_id_hash = $device_id_hash ? joes_scanner_auth_sanitize_device_string( $device_id_hash, 64 ) : null;

    $user_id = (int) $user_id;
    if ( ! $device_id_hash || $user_id <= 0 ) {
        return true;
    }

    $devices_table   = joes_scanner_auth_table_devices();
    $sessions_table  = joes_scanner_auth_table_sessions();
    $app_events_tbl  = joes_scanner_auth_table_app_events();
    $auth_events_tbl = joes_scanner_auth_table_events();

    $username = $username ? joes_scanner_auth_trim_text( (string) $username, 60 ) : '';
    $email    = $email ? joes_scanner_auth_trim_text( (string) $email, 100 ) : '';

    $now = joes_scanner_auth_now_utc_mysql();

    // If the device already has a binding to a different user, rebind on successful authentication.
    // Heartbeats still do not rebind (see joes_scanner_auth_upsert_device_heartbeat).
    $bound = joes_scanner_auth_get_bound_user_for_device( $device_id_hash );
    if ( $bound && isset( $bound['user_id'] ) ) {
        $bound_user_id = (int) $bound['user_id'];
        if ( $bound_user_id > 0 && $bound_user_id !== $user_id ) {
            $device_row = joes_scanner_auth_get_device_row_by_hash( $device_id_hash );
            if ( $device_row && ! empty( $device_row['device_id'] ) ) {
                $wpdb->update(
                    $devices_table,
                    array(
                        'user_id'       => $user_id,
                        'username'      => $username ? $username : null,
                        'email'         => $email ? $email : null,
                        'last_seen_utc' => $now,
                    ),
                    array( 'device_id' => (int) $device_row['device_id'] ),
                    array( '%d', '%s', '%s', '%s' ),
                    array( '%d' )
                );
            }

            joes_scanner_auth_apply_device_binding_to_rows( $device_id_hash, $user_id, $username, $email );
            return true;
        }
    }

    $device_row = joes_scanner_auth_get_device_row_by_hash( $device_id_hash );

    // Persist the binding on the first successful auth. If already bound to this user, refresh name/email.
    if ( $device_row && ! empty( $device_row['device_id'] ) ) {
        $wpdb->update(
            $devices_table,
            array(
                'user_id'        => $user_id,
                'username'       => $username ? $username : null,
                'email'          => $email ? $email : null,
                'last_seen_utc'  => $now,
            ),
            array( 'device_id' => (int) $device_row['device_id'] ),
            array( '%d', '%s', '%s', '%s' ),
            array( '%d' )
        );
    } else {
        $wpdb->insert(
            $devices_table,
            array(
                'device_id_hash' => $device_id_hash,
                'user_id'        => $user_id,
                'username'       => $username ? $username : null,
                'email'          => $email ? $email : null,
                'device_type'    => null,
                'device_platform'=> null,
                'device_model'   => null,
                'app_version'    => null,
                'first_seen_utc' => $now,
                'last_seen_utc'  => $now,
                'last_ip'        => null,
                'last_user_agent'=> null,
            ),
            array( '%s','%d','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s' )
        );
    }

    // Backfill so this device never appears under multiple accounts.
    $wpdb->query(
        $wpdb->prepare(
            "UPDATE {$sessions_table}
             SET user_id = %d,
                 username = %s,
                 email = %s
             WHERE device_id_hash = %s",
            $user_id,
            $username,
            $email,
            $device_id_hash
        )
    );

    $wpdb->query(
        $wpdb->prepare(
            "UPDATE {$app_events_tbl}
             SET user_id = %d,
                 username = %s,
                 email = %s
             WHERE device_id_hash = %s",
            $user_id,
            $username,
            $email,
            $device_id_hash
        )
    );

    $wpdb->query(
        $wpdb->prepare(
            "UPDATE {$auth_events_tbl}
             SET user_id = %d,
                 username = %s,
                 email = %s
             WHERE device_id_hash = %s",
            $user_id,
            $username,
            $email,
            $device_id_hash
        )
    );

    return true;
}


function joes_scanner_auth_admin_block_device( $device_id_hash, $blocked ) {
    global $wpdb;

    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );
    if ( ! $device_id_hash ) {
        return;
    }

    $table = joes_scanner_auth_table_devices();
    $now   = joes_scanner_auth_now_utc_mysql();

    if ( $blocked ) {
        $wpdb->query(
            $wpdb->prepare(
                "UPDATE {$table}
                 SET is_blocked = 1,
                     blocked_at_utc = %s,
                     blocked_reason = %s,
                     blocked_by_user_id = %d
                 WHERE device_id_hash = %s",
                $now,
                'Blocked via admin',
                (int) get_current_user_id(),
                $device_id_hash
            )
        );
        return;
    }

    $wpdb->query(
        $wpdb->prepare(
            "UPDATE {$table}
             SET is_blocked = 0,
                 blocked_at_utc = NULL,
                 blocked_reason = NULL,
                 blocked_by_user_id = NULL
             WHERE device_id_hash = %s",
            $device_id_hash
        )
    );
}

function joes_scanner_auth_admin_block_ip( $ip, $blocked ) {
    global $wpdb;

    $ip = is_string( $ip ) ? trim( $ip ) : '';
    if ( $ip === '' || ! filter_var( $ip, FILTER_VALIDATE_IP ) ) {
        return;
    }

    $table = joes_scanner_auth_table_blocked_ips();
    $now   = joes_scanner_auth_now_utc_mysql();

    if ( $blocked ) {
        $wpdb->replace(
            $table,
            array(
                'ip'                => $ip,
                'blocked_at_utc'     => $now,
                'reason'            => 'Blocked via admin',
                'blocked_by_user_id' => (int) get_current_user_id(),
            ),
            array( '%s', '%s', '%s', '%d' )
        );
        return;
    }

    $wpdb->query(
        $wpdb->prepare(
            "DELETE FROM {$table} WHERE ip = %s",
            $ip
        )
    );
}

function joes_scanner_auth_admin_clear_device( $device_id_hash ) {
    global $wpdb;

    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );
    if ( ! $device_id_hash ) {
        return;
    }

    $devices_table   = joes_scanner_auth_table_devices();
    $sessions_table  = joes_scanner_auth_table_sessions();
    $settings_table  = joes_scanner_auth_table_device_settings();
    $events_table    = joes_scanner_auth_table_app_events();
    $auth_events_tbl = joes_scanner_auth_table_events();

    $wpdb->query(
        $wpdb->prepare(
            "DELETE FROM {$sessions_table} WHERE device_id_hash = %s",
            $device_id_hash
        )
    );

    $wpdb->query(
        $wpdb->prepare(
            "DELETE FROM {$settings_table} WHERE device_id_hash = %s",
            $device_id_hash
        )
    );

    $wpdb->query(
        $wpdb->prepare(
            "DELETE FROM {$events_table} WHERE device_id_hash = %s",
            $device_id_hash
        )
    );

    // Auth events (admin actions and auth telemetry) for this device.
    $wpdb->query(
        $wpdb->prepare(
            "DELETE FROM {$auth_events_tbl} WHERE device_id_hash = %s",
            $device_id_hash
        )
    );

    $wpdb->query(
        $wpdb->prepare(
            "DELETE FROM {$devices_table} WHERE device_id_hash = %s",
            $device_id_hash
        )
    );
}

function joes_scanner_auth_admin_clear_device_for_user( $user_key, $device_id_hash ) {
    global $wpdb;

    $user_key = (int) $user_key;

    $sessions_table  = joes_scanner_auth_table_sessions();
    $events_table    = joes_scanner_auth_table_app_events();
    $auth_events_tbl = joes_scanner_auth_table_events();
    $devices_table   = joes_scanner_auth_table_devices();
    $settings_table  = joes_scanner_auth_table_device_settings();

    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );

    if ( $device_id_hash ) {
        // Clearing a hashed device is global. It removes the device and all related rows.
        $wpdb->query(
            $wpdb->prepare(
                "DELETE FROM {$sessions_table} WHERE device_id_hash = %s",
                $device_id_hash
            )
        );

        $wpdb->query(
            $wpdb->prepare(
                "DELETE FROM {$events_table} WHERE device_id_hash = %s",
                $device_id_hash
            )
        );

        $wpdb->query(
            $wpdb->prepare(
                "DELETE FROM {$auth_events_tbl} WHERE device_id_hash = %s",
                $device_id_hash
            )
        );

        $wpdb->query(
            $wpdb->prepare(
                "DELETE FROM {$settings_table} WHERE device_id_hash = %s",
                $device_id_hash
            )
        );

        $wpdb->query(
            $wpdb->prepare(
                "DELETE FROM {$devices_table} WHERE device_id_hash = %s",
                $device_id_hash
            )
        );

        return;
    }

    // Unknown device clear (no device hash). This is scoped by account key.
    // Important: For unregistered sessions, user_id can be NULL, so we must use COALESCE(user_id, 0).
    if ( $user_key < 0 ) {
        return;
    }

    $wpdb->query(
        $wpdb->prepare(
            "DELETE FROM {$sessions_table}
             WHERE (device_id_hash IS NULL OR device_id_hash = '')
               AND COALESCE(user_id, 0) = %d",
            $user_key
        )
    );

    $wpdb->query(
        $wpdb->prepare(
            "DELETE FROM {$events_table}
             WHERE (device_id_hash IS NULL OR device_id_hash = '')
               AND COALESCE(user_id, 0) = %d",
            $user_key
        )
    );

    $wpdb->query(
        $wpdb->prepare(
            "DELETE FROM {$auth_events_tbl}
             WHERE (device_id_hash IS NULL OR device_id_hash = '')
               AND COALESCE(user_id, 0) = %d",
            $user_key
        )
    );
}

function joes_scanner_auth_admin_clear_unknown_device_for_user( $user_key ) {
    joes_scanner_auth_admin_clear_device_for_user( $user_key, '' );
}

function joes_scanner_auth_run_maintenance( $selected_tables = null ) {
    global $wpdb;

    $opts = joes_scanner_auth_get_options();

    // Keep session/event user fields aligned to the latest device bindings.
    joes_scanner_auth_reconcile_all_bindings();

    // Keep SureCart price list synchronized for auto sync setups.
    joes_scanner_auth_sync_allowed_prices_from_surecart( 'daily_maintenance' );


    $events_table      = joes_scanner_auth_table_events();
    $sessions_table    = joes_scanner_auth_table_sessions();
    $app_events_table  = joes_scanner_auth_table_app_events();
    $comms_messages_tbl = joes_scanner_auth_table_comms_messages();
    $comms_changes_tbl  = joes_scanner_auth_table_comms_changes();

    $all_prunable = array(
        'events'         => true,
        'app_events'     => true,
        'sessions'       => true,
        'comms_messages' => true,
        'comms_changes'  => true,
    );

    $selected = array();
    if ( is_array( $selected_tables ) ) {
        foreach ( $selected_tables as $k ) {
            $k = sanitize_key( (string) $k );
            if ( $k !== '' && isset( $all_prunable[ $k ] ) ) {
                $selected[] = $k;
            }
        }
    }

    if ( empty( $selected ) ) {
        $selected = array_keys( $all_prunable );
    }

    $event_days   = (int) $opts['event_retention_days'];
    $session_days = (int) $opts['session_retention_days'];
    $comms_days   = isset( $opts['comms_retention_days'] ) ? (int) $opts['comms_retention_days'] : 90;

    $event_cutoff = gmdate( 'Y-m-d H:i:s', time() - ( $event_days * DAY_IN_SECONDS ) );
    $sess_cutoff  = gmdate( 'Y-m-d H:i:s', time() - ( $session_days * DAY_IN_SECONDS ) );

    if ( in_array( 'events', $selected, true ) ) {
        $wpdb->query(
            $wpdb->prepare(
                "DELETE FROM {$events_table} WHERE created_at_utc < %s",
                $event_cutoff
            )
        );
    }

    if ( in_array( 'app_events', $selected, true ) ) {
        $wpdb->query(
            $wpdb->prepare(
                "DELETE FROM {$app_events_table} WHERE created_at_utc < %s",
                $event_cutoff
            )
        );
    }

    if ( in_array( 'sessions', $selected, true ) ) {
        $wpdb->query(
            $wpdb->prepare(
                "DELETE FROM {$sessions_table} WHERE last_seen_utc < %s",
                $sess_cutoff
            )
        );
    }

    if ( $comms_days > 0 && ( in_array( 'comms_messages', $selected, true ) || in_array( 'comms_changes', $selected, true ) ) ) {
        $comms_cutoff = gmdate( 'Y-m-d H:i:s', time() - ( $comms_days * DAY_IN_SECONDS ) );

        if ( in_array( 'comms_changes', $selected, true ) ) {
            $wpdb->query(
                $wpdb->prepare(
                    "DELETE FROM {$comms_changes_tbl} WHERE changed_at_utc < %s",
                    $comms_cutoff
                )
            );
        }

        if ( in_array( 'comms_messages', $selected, true ) ) {
            // Keep messages that were updated recently even if they were originally posted earlier.
            $wpdb->query(
                $wpdb->prepare(
                    "DELETE FROM {$comms_messages_tbl} WHERE COALESCE(updated_at_utc, created_at_utc) < %s",
                    $comms_cutoff
                )
            );
        }
    }
}
