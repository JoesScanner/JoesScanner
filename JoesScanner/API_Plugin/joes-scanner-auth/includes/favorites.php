<?php
/**
 * Account-owned Favorites, group records and deletion tombstones.
 *
 * POST /favorites/sync exchanges a bounded batch of conditional writes and one
 * key-ordered page. An empty batch is a read. The authenticated WordPress user,
 * never a client-supplied ID or a telemetry session, owns every database row.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

function joes_scanner_auth_favorites_table() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_favorites';
}

function joes_scanner_auth_favorites_install() {
    global $wpdb;
    $table = joes_scanner_auth_favorites_table();
    $collation = $wpdb->get_charset_collate();
    dbDelta( "CREATE TABLE {$table} (
        user_id bigint(20) unsigned NOT NULL,
        item_key varchar(65) NOT NULL,
        revision bigint(20) unsigned NOT NULL,
        mutation_id char(32) NOT NULL,
        deleted tinyint(1) NOT NULL,
        payload text NOT NULL,
        PRIMARY KEY  (user_id,item_key)
    ) ENGINE=InnoDB {$collation};" );
}

add_action( 'rest_api_init', function () {
    register_rest_route(
        'joes-scanner/v1',
        '/favorites/sync',
        array(
            'methods'             => WP_REST_Server::CREATABLE,
            'permission_callback' => 'joes_scanner_auth_favorites_authorize',
            'callback'            => 'joes_scanner_auth_favorites_sync',
        )
    );
} );

function joes_scanner_auth_favorites_error( $code, $status ) {
    return new WP_Error( 'joes_favorites_' . $code, 'Favorites request failed.', array( 'status' => $status ) );
}

function joes_scanner_auth_favorites_authorize( WP_REST_Request $request ) {
    if ( ! is_ssl() ) {
        return joes_scanner_auth_favorites_error( 'https_required', 403 );
    }

    $authorization = $request->get_header( 'authorization' );
    // A distinct scheme keeps this account login separate from WordPress Basic
    // application-password handling. The client sends it only over TLS and does
    // not follow redirects. Neither this header nor its contents are stored.
    if ( ! preg_match( '/\AJoesScannerAccount ([A-Za-z0-9+\/=]{4,4096})\z/iD', (string) $authorization, $matches ) ) {
        return joes_scanner_auth_favorites_error( 'authentication_required', 401 );
    }
    $credentials = base64_decode( $matches[1], true );
    if ( $credentials === false || strpos( $credentials, ':' ) === false ) {
        return joes_scanner_auth_favorites_error( 'authentication_required', 401 );
    }
    list( $username, $password ) = explode( ':', $credentials, 2 );
    $username = trim( $username );
    $username = strpos( $username, '@' ) !== false ? sanitize_email( $username ) : sanitize_text_field( $username );
    if ( $username === '' || $password === '' || strlen( $username ) > 254 ) {
        return joes_scanner_auth_favorites_error( 'authentication_required', 401 );
    }

    $ip = joes_scanner_auth_get_request_ip( $request );
    if ( ! joes_scanner_auth_rate_limit_allow( $ip, $username, 20, 10 * MINUTE_IN_SECONDS ) ) {
        return joes_scanner_auth_favorites_error( 'rate_limited', 429 );
    }
    // Reuse the auth plugin's WordPress password handling. No SureCart or
    // scanner entitlement check is part of Favorites authorization.
    $user = wp_authenticate( $username, wp_slash( $password ) );
    if ( is_wp_error( $user ) || ! ( $user instanceof WP_User ) || $user->ID <= 0 ) {
        joes_scanner_auth_rate_limit_record_failure( $ip, $username, 20, 10 * MINUTE_IN_SECONDS );
        return joes_scanner_auth_favorites_error( 'authentication_required', 401 );
    }

    // Overwrite the URL parameter slot that only this route reads for ownership.
    $url = $request->get_url_params();
    $url['_favorites_authenticated_user'] = (int) $user->ID;
    $request->set_url_params( $url );
    return true;
}

function joes_scanner_auth_favorites_key_valid( $key ) {
    return is_string( $key ) && preg_match( '/\A(?:f[a-f0-9]{64}|g[a-f0-9]{32})\z/D', $key );
}

function joes_scanner_auth_favorites_text_valid( $text, $bytes, $allow_empty = true ) {
    return is_string( $text ) && strlen( $text ) <= $bytes && ( $allow_empty || trim( $text ) !== '' )
        && preg_match( '/\A[^\x00-\x1F\x7F-\x9F]*\z/uD', $text );
}

function joes_scanner_auth_favorites_value( $value, $deleted ) {
    if ( ! is_array( $value ) || ! joes_scanner_auth_favorites_key_valid( $value['id'] ?? null ) ) {
        return null;
    }
    $id = $value['id'];
    if ( $deleted ) {
        return array( 'id' => $id );
    }
    $label = $value['label'] ?? null;
    if ( ! joes_scanner_auth_favorites_text_valid( $label, 512, false ) ) {
        return null;
    }
    if ( $id[0] === 'g' ) {
        return array( 'id' => $id, 'label' => $label );
    }

    $key = $value['serverKey'] ?? null;
    $identity = $value['identity'] ?? null;
    $group = $value['groupId'] ?? null;
    if ( ! is_string( $key ) || ! preg_match( '/\A(?:c:[a-f0-9]{32}|d:(?:[1-9][0-9]{0,9}|joe))\z/D', $key )
        || ! is_array( $identity )
        || ( $group !== null && ( ! joes_scanner_auth_favorites_key_valid( $group ) || $group[0] !== 'g' ) ) ) {
        return null;
    }

    $fields = array();
    foreach ( array( 'system', 'site', 'receiver', 'talkgroup' ) as $field ) {
        $text = $identity[$field] ?? null;
        if ( ! joes_scanner_auth_favorites_text_valid( $text, 768, $field !== 'talkgroup' )
            || ( $text !== '' && ! preg_match( '/\A(?:id:|label:).+\z/usD', $text ) ) ) {
            return null;
        }
        $fields[$field] = $text;
    }

    $canonical_id = 'f' . hash( 'sha256', implode( "\x1f", array_merge( array( $key ), array_values( $fields ) ) ) );
    if ( $id !== $canonical_id ) {
        return null;
    }

    return array( 'id' => $id, 'label' => $label, 'serverKey' => $key, 'identity' => $fields, 'groupId' => $group );
}

function joes_scanner_auth_favorites_sync( WP_REST_Request $request ) {
    global $wpdb;
    $user_id = $request->get_url_params()['_favorites_authenticated_user'] ?? 0;
    if ( $user_id <= 0 ) {
        return joes_scanner_auth_favorites_error( 'authentication_required', 401 );
    }
    if ( ! joes_scanner_auth_is_json_request( $request ) || joes_scanner_auth_body_too_large( $request, 262144 ) ) {
        return joes_scanner_auth_favorites_error( 'invalid_payload', 400 );
    }
    $params = $request->get_json_params();
    $owner_id = $params['ownerId'] ?? null;
    if ( $owner_id !== null && $owner_id !== $user_id ) {
        return joes_scanner_auth_favorites_error( 'account_changed', 403 );
    }
    $after = $params['after'] ?? '';
    $changes = $params['changes'] ?? null;
    if ( ( $after !== '' && ! joes_scanner_auth_favorites_key_valid( $after ) )
        || ! is_array( $changes ) || count( $changes ) > 50
        || ( ! empty( $changes ) && $owner_id === null ) ) {
        return joes_scanner_auth_favorites_error( 'invalid_payload', 400 );
    }

    // Validate the whole batch before writing any row.
    $batch = array();
    foreach ( $changes as $change ) {
        $mutation = $change['mutationId'] ?? null;
        $revision = $change['expectedRevision'] ?? null;
        $deleted = $change['deleted'] ?? null;
        $value = is_bool( $deleted ) ? joes_scanner_auth_favorites_value( $change['value'] ?? null, $deleted ) : null;
        if ( ! is_string( $mutation ) || ! preg_match( '/\A[a-f0-9]{32}\z/D', $mutation )
            || ! is_int( $revision ) || $revision < 0 || $revision >= PHP_INT_MAX
            || $value === null || isset( $batch[$value['id']] ) ) {
            return joes_scanner_auth_favorites_error( 'invalid_payload', 400 );
        }
        $batch[$value['id']] = array( $mutation, $revision, $deleted, $value );
    }

    $table = joes_scanner_auth_favorites_table();
    $results = array();
    foreach ( $batch as $id => $change ) {
        list( $mutation, $revision, $deleted, $value ) = $change;
        $payload = wp_json_encode( $value );
        if ( $revision === 0 ) {
            $changed = $wpdb->query( $wpdb->prepare(
                "INSERT INTO {$table} (user_id, item_key, revision, mutation_id, deleted, payload)
                 VALUES (%d, %s, 1, %s, %d, %s) ON DUPLICATE KEY UPDATE item_key = item_key",
                $user_id, $id, $mutation, (int) $deleted, $payload
            ) );
        } else {
            $changed = $wpdb->query( $wpdb->prepare(
                "UPDATE {$table} SET revision = revision + 1, mutation_id = %s, deleted = %d, payload = %s
                 WHERE user_id = %d AND item_key = %s AND revision = %d AND mutation_id <> %s",
                $mutation, (int) $deleted, $payload, $user_id, $id, $revision, $mutation
            ) );
        }
        if ( $changed === false ) {
            return joes_scanner_auth_favorites_error( 'storage_unavailable', 503 );
        }
        $row = $wpdb->get_row( $wpdb->prepare(
            "SELECT revision, mutation_id, deleted, payload FROM {$table} WHERE user_id = %d AND item_key = %s",
            $user_id, $id
        ), ARRAY_A );
        if ( $wpdb->last_error ) {
            return joes_scanner_auth_favorites_error( 'storage_unavailable', 503 );
        }
        $record = $row ? joes_scanner_auth_favorites_record( $row ) : null;
        $results[] = array(
            'id' => $id, 'mutationId' => $mutation,
            'accepted' => $record && $record['mutationId'] === $mutation,
            'record' => $record,
        );
    }

    $page = $wpdb->get_results( $wpdb->prepare(
        "SELECT item_key, revision, mutation_id, deleted, payload FROM {$table}
         WHERE user_id = %d AND item_key > %s ORDER BY item_key LIMIT 201",
        $user_id, $after
    ), ARRAY_A );
    if ( $wpdb->last_error || ! is_array( $page ) ) {
        return joes_scanner_auth_favorites_error( 'storage_unavailable', 503 );
    }
    $next = null;
    if ( count( $page ) > 200 ) {
        array_pop( $page );
        $next = end( $page )['item_key'];
    }
    $response = new WP_REST_Response( array(
        'userId' => $user_id, 'results' => $results,
        'records' => array_map( 'joes_scanner_auth_favorites_record', $page ), 'next' => $next,
    ) );
    $response->header( 'Cache-Control', 'private, no-store' );
    return $response;
}

function joes_scanner_auth_favorites_record( $row ) {
    return array(
        'revision' => (int) $row['revision'], 'mutationId' => $row['mutation_id'],
        'value' => json_decode( $row['payload'], true ), 'deleted' => (bool) $row['deleted'],
    );
}
