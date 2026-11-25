<?php

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

add_action( 'rest_api_init', static function () {
    foreach ( array( 'config' => 'GET', 'submission-grant' => 'POST', 'submissions' => 'POST' ) as $route => $method ) {
        $callbacks = array( 'config' => 'joes_scanner_auth_dir_app_config',
            'submission-grant' => 'joes_scanner_auth_dir_app_grant', 'submissions' => 'joes_scanner_auth_dir_app_submit' );
        register_rest_route( 'joes-scanner/v1', '/server-directory/' . $route, array(
            'methods' => $method,
            'callback' => $callbacks[$route],
            'permission_callback' => '__return_true',
        ) );
    }
} );

function joes_scanner_auth_dir_my_servers_page( $id ) {
    $page = $id ? get_post( $id ) : null;
    return $page instanceof WP_Post && $page->post_type === 'page' && $page->post_status === 'publish'
        && $page->post_password === '' && has_shortcode( $page->post_content, 'joes_scanner_my_servers' ) ? $page : null;
}

function joes_scanner_auth_dir_app_config() {
    if ( ! is_ssl() ) {
        return joes_scanner_auth_dir_error( 'Directory services require HTTPS.', 403 );
    }
    $id = (int) get_option( 'joes_scanner_my_servers_page_id', 0 );
    $page = joes_scanner_auth_dir_my_servers_page( $id );
    return new WP_REST_Response( array(
        'my_servers_url' => $page ? get_permalink( $page ) : null,
        'terms_version' => joes_scanner_auth_server_directory_agreement_version(),
        'terms' => joes_scanner_auth_server_directory_agreement_text(),
    ), 200 );
}

function joes_scanner_auth_dir_app_json( WP_REST_Request $request, array $keys ) {
    if ( ! is_ssl() ) {
        return joes_scanner_auth_dir_error( 'Directory services require HTTPS.', 403 );
    }
    $data = $request->get_json_params();
    if ( ! joes_scanner_auth_is_json_request( $request ) || strlen( $request->get_body() ) > 8192
        || ! is_array( $data ) || array_diff( array_keys( $data ), $keys )
        || $request->get_query_params() || $request->get_header( 'authorization' ) ) {
        return joes_scanner_auth_dir_error( 'The directory request has an unsupported format.' );
    }
    return $data;
}

function joes_scanner_auth_dir_app_grant( WP_REST_Request $request ) {
    $data = joes_scanner_auth_dir_app_json( $request, array( 'account', 'password' ) );
    if ( is_wp_error( $data ) ) {
        return $data;
    }
    $login = $data['account'] ?? '';
    $password = $data['password'] ?? '';
    if ( ! is_string( $login ) || ! is_string( $password ) || trim( $login ) === '' || $password === ''
        || strlen( $login ) > 100 || strlen( $password ) > 1024 ) {
        return joes_scanner_auth_dir_error( 'The account could not be verified.', 401 );
    }
    $allowed = joes_scanner_auth_dir_write( static function () use ( $login ) {
        $now = time();
        $ip = filter_var( $_SERVER['REMOTE_ADDR'] ?? '', FILTER_VALIDATE_IP ) ?: 'unknown';
        $slot = joes_scanner_auth_dir_take_slot( 'app-grant-ip', $ip, 60, $now );
        return $slot === true ? joes_scanner_auth_dir_take_slot( 'app-grant-account', strtolower( trim( $login ) ), 10, $now ) : $slot;
    } );
    if ( is_wp_error( $allowed ) ) {
        return $allowed;
    }
    if ( ! $allowed ) {
        return joes_scanner_auth_dir_error( 'Too many directory sign-in attempts. Try again later.', 429 );
    }
    $user = joes_scanner_auth_authenticate_account( trim( $login ), $password );
    if ( ! ( $user instanceof WP_User ) || ! $user->ID ) {
        return joes_scanner_auth_dir_error( 'The account could not be verified.', 401 );
    }
    global $wpdb;
    $table = joes_scanner_auth_dir_table( 'verification' );
    $grant = bin2hex( random_bytes( 32 ) );
    $saved = joes_scanner_auth_dir_write( static function () use ( $wpdb, $table, $grant, $user ) {
        if ( $wpdb->query( $wpdb->prepare( "DELETE FROM {$table} WHERE expires_at_utc <= %s", gmdate( 'Y-m-d H:i:s' ) ) ) === false
            || $wpdb->insert( $table, array(
                'token_key' => joes_scanner_auth_dir_token_key( 'app-submission', $grant ),
                'user_id' => (int) $user->ID,
                'expires_at_utc' => gmdate( 'Y-m-d H:i:s', time() + 2 * MINUTE_IN_SECONDS ),
            ) ) === false ) {
            return joes_scanner_auth_dir_db_error();
        }
        return true;
    } );
    if ( is_wp_error( $saved ) ) {
        return $saved;
    }
    $response = new WP_REST_Response( array( 'grant' => $grant, 'user_id' => (int) $user->ID ), 200 );
    $response->header( 'Cache-Control', 'private, no-store' );
    return $response;
}

function joes_scanner_auth_dir_app_submit( WP_REST_Request $request ) {
    $data = joes_scanner_auth_dir_app_json( $request, array( 'grant', 'terms_version', 'listing' ) );
    if ( is_wp_error( $data ) ) {
        return $data;
    }
    $grant = $data['grant'] ?? '';
    if ( ! is_string( $grant ) || ! preg_match( '/^[a-f0-9]{64}$/D', $grant ) ) {
        return joes_scanner_auth_dir_error( 'Sign in to submit a directory listing.', 401 );
    }
    $listing = $data['listing'] ?? null;
    $fields = array( 'server_name', 'server_url', 'info_url', 'map_anchor', 'area_label', 'description',
        'requires_scanner_login', 'requires_firewall_auth' );
    if ( ! is_array( $listing ) || array_diff( array_keys( $listing ), $fields )
        || ! in_array( $listing['requires_scanner_login'] ?? null, array( '0', '1' ), true )
        || ! in_array( $listing['requires_firewall_auth'] ?? null, array( '0', '1' ), true )
        || ( $data['terms_version'] ?? null ) !== joes_scanner_auth_server_directory_agreement_version() ) {
        return joes_scanner_auth_dir_error( 'Review the listing terms and provide only the supported listing fields.' );
    }
    $parsed = joes_scanner_auth_dir_read_listing( 0, $listing + array( 'visibility' => 'public' ) );
    if ( is_wp_error( $parsed ) ) {
        return $parsed;
    }
    if ( joes_scanner_auth_dir_handoff_address( $parsed['server_url'] ) === ''
        || ( $parsed['info_url'] !== '' && joes_scanner_auth_dir_handoff_address( $parsed['info_url'] ) === '' ) ) {
        return joes_scanner_auth_dir_error( 'Use a recorder address without embedded credentials.' );
    }
    global $wpdb;
    $table = joes_scanner_auth_dir_table( 'verification' );
    $owner = joes_scanner_auth_dir_write( static function () use ( $wpdb, $table, $grant ) {
        $key = joes_scanner_auth_dir_token_key( 'app-submission', $grant );
        $user = (int) $wpdb->get_var( $wpdb->prepare(
            "SELECT user_id FROM {$table} WHERE token_key = %s AND expires_at_utc > %s", $key, gmdate( 'Y-m-d H:i:s' )
        ) );
        if ( $wpdb->last_error ) {
            return joes_scanner_auth_dir_db_error();
        }
        if ( ! $user || ! get_userdata( $user ) || $wpdb->delete( $table, array( 'token_key' => $key ) ) !== 1 ) {
            return joes_scanner_auth_dir_error( 'Verify your account to submit a directory listing.', 401 );
        }
        return $user;
    } );
    if ( is_wp_error( $owner ) ) {
        return $owner;
    }
    $result = joes_scanner_auth_dir_save( $owner, 0, 0, $parsed, true );
    if ( is_wp_error( $result ) ) {
        return $result;
    }
    joes_scanner_auth_notify_admin_server_submission( $result['id'] );
    return new WP_REST_Response( array( 'id' => $result['id'], 'status' => 'pending' ), 201 );
}
