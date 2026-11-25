<?php
/*
//|--------------------------------------------------------------------------//| REST API registration//|--------------------------------------------------------------------------*/

add_action(
    'rest_api_init',
    function () {
        register_rest_route(
            'joes-scanner/v1',
            '/auth',
            array(
                'methods'             => 'POST',
                'callback'            => 'joes_scanner_auth_endpoint',
                'permission_callback' => '__return_true',
            )
        );

        // App heartbeat to keep sessions marked active.
        register_rest_route(
            'joes-scanner/v1',
            '/ping',
            array(
                'methods'             => 'POST',
                'callback'            => 'joes_scanner_auth_ping_endpoint',
                'permission_callback' => '__return_true',
            )
        );

        //
register_rest_route(
    'joes-scanner/v1',
    '/device-settings',
    array(
        'methods'             => array( 'GET', 'POST' ),
        'callback'            => function ( WP_REST_Request $request ) { return joes_scanner_auth_device_settings_endpoint( $request ); },
        'permission_callback' => '__return_true',
    )
);

register_rest_route(
    'joes-scanner/v1',
    '/app-event',
    array(
        'methods'             => 'POST',
        'callback'            => function ( WP_REST_Request $request ) { return joes_scanner_auth_app_event_endpoint( $request ); },
        'permission_callback' => '__return_true',
    )
);



register_rest_route(
    'joes-scanner/v1',
    '/comms/changes',
    array(
        'methods'             => 'POST',
        'callback'            => function ( WP_REST_Request $request ) { return joes_scanner_auth_comms_changes_endpoint( $request ); },
        'permission_callback' => '__return_true',
    )
);

        register_rest_route(
            'joes-scanner/v1',
            '/diag',
            array(
                'methods'             => 'GET',
                'callback'            => function () {
                    return new WP_REST_Response(
                        array(
                            'ok'                  => true,
                            'plugin_version'       => defined( 'JOES_SCANNER_AUTH_VERSION' ) ? JOES_SCANNER_AUTH_VERSION : '',
                            'php_version'          => PHP_VERSION,
                            'wp_timezone_string'   => function_exists( 'wp_timezone_string' ) ? wp_timezone_string() : '',
                            'server_time_utc'      => gmdate( 'c' ),
                            'handlers'             => array(
                                'auth'            => function_exists( 'joes_scanner_auth_endpoint' ),
                                'ping'            => function_exists( 'joes_scanner_auth_ping_endpoint' ),
                                'device_settings' => function_exists( 'joes_scanner_auth_device_settings_endpoint' ),
                                'app_event'       => function_exists( 'joes_scanner_auth_app_event_endpoint' ),
                                'lookups'         => function_exists( 'joes_scanner_auth_lookups_get_endpoint' ),
                                'observed_triples' => function_exists( 'joes_scanner_auth_observed_triples_get_endpoint' ),
                                'observed_triples_v2' => function_exists( 'joes_scanner_auth_observed_triples_v2_get_endpoint' ),
                            ),
                        ),
                        200
                    );
                },
                'permission_callback' => '__return_true',
            )
        );

        // Public list of approved servers for the app.
        register_rest_route(
            'joes-scanner/v1',
            '/servers',
            array(
                'methods'             => 'GET',
                'callback'            => 'joes_scanner_auth_servers_endpoint',
                'permission_callback' => '__return_true',
            )
        );

        register_rest_route(
            'joes-scanner/v1',
            '/server-firewall-credentials',
            array(
                'methods'             => 'POST',
                'callback'            => 'joes_scanner_auth_server_firewall_credentials_endpoint',
                'permission_callback' => '__return_true',
            )
        );

        // Server lookups cache (telemetry-gated).
        register_rest_route(
            'joes-scanner/v1',
            '/lookups',
            array(
                'methods'             => array( 'GET', 'POST' ),
                'callback'            => function ( WP_REST_Request $request ) {
                    if ( $request->get_method() === 'GET' ) {
                        return joes_scanner_auth_lookups_get_endpoint( $request );
                    }
                    return joes_scanner_auth_lookups_post_endpoint( $request );
                },
                'permission_callback' => '__return_true',
            )
        );

        // Observed triples (telemetry-gated). Used for seeding receiver, site, talkgroup associations.
        register_rest_route(
            'joes-scanner/v1',
            '/observed-triples',
            array(
                'methods'             => array( 'GET', 'POST' ),
                'callback'            => function ( WP_REST_Request $request ) {
                    if ( $request->get_method() === 'GET' ) {
                        return joes_scanner_auth_observed_triples_get_endpoint( $request );
                    }
                    return joes_scanner_auth_observed_triples_post_endpoint( $request );
                },
                'permission_callback' => '__return_true',
            )
        );

        // Observed triples v2. Canonical discovery sync for filter hierarchy.
        register_rest_route(
            'joes-scanner/v1',
            '/observed-triples-v2',
            array(
                'methods'             => array( 'GET', 'POST' ),
                'callback'            => function ( WP_REST_Request $request ) {
                    if ( $request->get_method() === 'GET' ) {
                        return joes_scanner_auth_observed_triples_v2_get_endpoint( $request );
                    }
                    return joes_scanner_auth_observed_triples_v2_post_endpoint( $request );
                },
                'permission_callback' => '__return_true',
            )
        );
// Admin only debug endpoints.
        register_rest_route(
            'joes-scanner/v1',
            '/debug-surecart',
            array(
                'methods'             => 'GET',
                'callback'            => 'joes_scanner_auth_debug_surecart',
                'permission_callback' => function () {
                    return current_user_can( 'manage_options' );
                },
            )
        );

        register_rest_route(
            'joes-scanner/v1',
            '/debug-account',
            array(
                'methods'             => 'GET',
                'callback'            => 'joes_scanner_auth_debug_account',
                'permission_callback' => function () {
                    return current_user_can( 'manage_options' );
                },
            )
        );
    }
);

/*
//|--------------------------------------------------------------------------//| GET /wp-json/joes-scanner/v1/lookups
//|--------------------------------------------------------------------------*/

function joes_scanner_auth_lookups_get_endpoint( WP_REST_Request $request ) {
    $params = $request->get_params();

    $server_key_raw = joes_scanner_auth_get_param_first( $params, array( 'server_key', 'server' ) );
    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key_raw );
    if ( $server_key === '' ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_server_key' ), 400 );
    }

    $identity = joes_scanner_auth_lookups_parse_identity( $request, $params );
    if ( isset( $identity['error'] ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => $identity['error'] ), isset( $identity['http_status'] ) ? (int) $identity['http_status'] : 400 );
    }

    $row = joes_scanner_auth_lookups_get_cache_row( $server_key );
    if ( ! $row ) {
        return new WP_REST_Response(
            array(
                'ok'                => true,
                'server_key'        => $server_key,
                'updated_at_utc'    => null,
                'receivers_json'    => null,
                'sites_json'        => null,
                'talkgroups_json'   => null,
                'talkgroup_groups_json' => null,
            ),
            200
        );
    }

    return new WP_REST_Response(
        array(
            'ok'                => true,
            'server_key'        => $server_key,
            'updated_at_utc'    => isset( $row['updated_at_utc'] ) ? (string) $row['updated_at_utc'] : null,
            'receivers_json'    => isset( $row['receivers_json'] ) ? $row['receivers_json'] : null,
            'sites_json'        => isset( $row['sites_json'] ) ? $row['sites_json'] : null,
            'talkgroups_json'   => isset( $row['talkgroups_json'] ) ? $row['talkgroups_json'] : null,
            'talkgroup_groups_json' => isset( $row['talkgroup_groups_json'] ) ? $row['talkgroup_groups_json'] : null,
        ),
        200
    );
}

/*
//|--------------------------------------------------------------------------//| POST /wp-json/joes-scanner/v1/lookups
//|--------------------------------------------------------------------------*/

function joes_scanner_auth_lookups_post_endpoint( WP_REST_Request $request ) {
    if ( joes_scanner_auth_body_too_large( $request, 524288 ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'body_too_large' ), 413 );
    }

    $params = $request->get_params();
    if ( ! is_array( $params ) ) {
        $params = $request->get_params();
    }

    $server_key_raw = joes_scanner_auth_get_param_first( $params, array( 'server_key', 'server' ) );
    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key_raw );
    if ( $server_key === '' ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_server_key' ), 400 );
    }

    $identity = joes_scanner_auth_lookups_parse_identity( $request, $params );
    if ( isset( $identity['error'] ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => $identity['error'] ), isset( $identity['http_status'] ) ? (int) $identity['http_status'] : 400 );
    }

    $device_id_hash = isset( $identity['device_id_hash'] ) ? (string) $identity['device_id_hash'] : '';

    $receivers_json        = isset( $params['receivers_json'] ) ? (string) $params['receivers_json'] : null;
    $sites_json            = isset( $params['sites_json'] ) ? (string) $params['sites_json'] : null;
    $talkgroups_json       = isset( $params['talkgroups_json'] ) ? (string) $params['talkgroups_json'] : null;
    $talkgroup_groups_json = isset( $params['talkgroup_groups_json'] ) ? (string) $params['talkgroup_groups_json'] : null;

    // Basic sanity: at least one blob must be present.
    if ( ( $receivers_json === null || trim( $receivers_json ) === '' )
        && ( $sites_json === null || trim( $sites_json ) === '' )
        && ( $talkgroups_json === null || trim( $talkgroups_json ) === '' ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_payload' ), 400 );
    }

    $payload_hash = isset( $params['payload_hash'] ) ? joes_scanner_auth_sanitize_device_string( (string) $params['payload_hash'], 64 ) : null;
    if ( ! $payload_hash ) {
        // Compute server-side if client omitted.
        $payload_hash = hash( 'sha256', (string) $receivers_json . "|" . (string) $sites_json . "|" . (string) $talkgroups_json . "|" . (string) $talkgroup_groups_json );
    }

    $existing = joes_scanner_auth_lookups_get_cache_row( $server_key );
    if ( $existing && isset( $existing['payload_hash'] ) && (string) $existing['payload_hash'] === (string) $payload_hash ) {
        joes_scanner_auth_lookups_set_last_report_utc( $server_key, $device_id_hash );
        return new WP_REST_Response( array( 'ok' => true, 'unchanged' => true ), 200 );
    }

    $data = array(
        'receivers_json'        => $receivers_json,
        'sites_json'            => $sites_json,
        'talkgroups_json'       => $talkgroups_json,
        'talkgroup_groups_json' => $talkgroup_groups_json,
        'payload_hash'          => $payload_hash,
    );

    $meta = array(
        'device_id_hash' => $device_id_hash,
        'user_id'        => isset( $identity['user_id'] ) ? (int) $identity['user_id'] : null,
        'email'          => isset( $identity['email'] ) ? (string) $identity['email'] : null,
        'ip'             => isset( $identity['ip'] ) ? (string) $identity['ip'] : null,
        'user_agent'     => isset( $identity['user_agent'] ) ? (string) $identity['user_agent'] : null,
    );

    joes_scanner_auth_lookups_upsert_cache_row( $server_key, $data, $meta );
    joes_scanner_auth_lookups_set_last_report_utc( $server_key, $device_id_hash );

    return new WP_REST_Response( array( 'ok' => true ), 200 );
}

function joes_scanner_auth_lookups_parse_identity( WP_REST_Request $request, $params ) {
    $params = is_array( $params ) ? $params : array();

    $session_token  = isset( $params['session_token'] ) ? sanitize_text_field( (string) $params['session_token'] ) : '';
    $device_id_hash = isset( $params['device_id_hash'] ) ? joes_scanner_auth_sanitize_device_string( (string) $params['device_id_hash'], 64 ) : '';
    $device_id      = isset( $params['device_id'] ) ? joes_scanner_auth_sanitize_device_string( (string) $params['device_id'], 120 ) : '';
    if ( ! $device_id_hash && $device_id ) {
        $computed = joes_scanner_auth_hash_device_id( $device_id );
        if ( $computed ) {
            $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $computed, 64 );
        }
    }

    $ip = joes_scanner_auth_get_request_ip( $request );
    if ( joes_scanner_auth_is_ip_blocked( $ip ) ) {
        return array( 'error' => 'blocked', 'http_status' => 403 );
    }

    if ( $device_id_hash && joes_scanner_auth_is_device_blocked_by_hash( $device_id_hash ) ) {
        return array( 'error' => 'blocked', 'http_status' => 403 );
    }

    if ( $session_token === '' && $device_id_hash === '' ) {
        return array( 'error' => 'missing_identity', 'http_status' => 400 );
    }

    $session = null;
    if ( $session_token !== '' ) {
        $session = joes_scanner_auth_get_session_row_by_token( $session_token );
    }

    if ( ! $device_id_hash && $session && isset( $session['device_id_hash'] ) ) {
        $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $session['device_id_hash'], 64 );
    }

    if ( ! $device_id_hash ) {
        return array( 'error' => 'missing_device', 'http_status' => 400 );
    }

    $user_agent = joes_scanner_auth_get_user_agent( $request );
    $user_agent = $user_agent ? joes_scanner_auth_trim_text( $user_agent, 512 ) : null;

    return array(
        'device_id_hash' => $device_id_hash,
        'user_id'        => ( $session && isset( $session['user_id'] ) ) ? (int) $session['user_id'] : null,
        'email'          => ( $session && isset( $session['email'] ) ) ? joes_scanner_auth_trim_text( (string) $session['email'], 100 ) : null,
        'ip'             => $ip,
        'user_agent'     => $user_agent,
    );
}

function joes_scanner_auth_observed_triples_get_endpoint( WP_REST_Request $request ) {
    // Legacy route kept for compatibility. Canonical discovery now lives in v2.
    return joes_scanner_auth_observed_triples_v2_get_endpoint( $request );
}

//|--------------------------------------------------------------------------//| POST /wp-json/joes-scanner/v1/observed-triples

function joes_scanner_auth_observed_triples_post_endpoint( WP_REST_Request $request ) {
    // Legacy route kept for compatibility. Canonical discovery now lives in v2.
    return joes_scanner_auth_observed_triples_v2_post_endpoint( $request );
}


function joes_scanner_auth_observed_triples_v2_get_endpoint( WP_REST_Request $request ) {
    $server_key_raw = (string) $request->get_param( 'server_key' );
    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key_raw );
    if ( $server_key === '' ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_server_key' ), 400 );
    }

    // Identity is passed via query string for GET.
    $params = $request->get_params();
    if ( ! is_array( $params ) ) {
        $params = array();
    }
    $identity = joes_scanner_auth_lookups_parse_identity( $request, $params );
    if ( isset( $identity['error'] ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => $identity['error'] ), isset( $identity['http_status'] ) ? (int) $identity['http_status'] : 400 );
    }

    $limit = (int) $request->get_param( 'limit' );
    if ( $limit < 1 ) {
        $limit = 5000;
    }

    $rows = joes_scanner_auth_observed_triples_v2_get_rows( $server_key, $limit );

    return new WP_REST_Response(
        array(
            'ok'         => true,
            'server_key' => $server_key,
            'count'      => is_array( $rows ) ? count( $rows ) : 0,
            'items'      => $rows,
        ),
        200
    );
}

//|--------------------------------------------------------------------------//| POST /wp-json/joes-scanner/v1/observed-triples-v2

function joes_scanner_auth_observed_triples_v2_post_endpoint( WP_REST_Request $request ) {
    if ( ! joes_scanner_auth_is_json_request( $request ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'unsupported_media_type' ), 415 );
    }

    // Larger payload than ping/app-event. Still capped.
    if ( joes_scanner_auth_body_too_large( $request, 2097152 ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'payload_too_large' ), 413 );
    }

    $params = $request->get_params();
    if ( ! is_array( $params ) ) {
        $params = array();
    }

    $server_key_raw = isset( $params['server_key'] ) ? (string) $params['server_key'] : '';
    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key_raw );
    if ( $server_key === '' ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_server_key' ), 400 );
    }

    $identity = joes_scanner_auth_lookups_parse_identity( $request, $params );
    if ( isset( $identity['error'] ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => $identity['error'] ), isset( $identity['http_status'] ) ? (int) $identity['http_status'] : 400 );
    }

    $device_id_hash = (string) $identity['device_id_hash'];

    $items = isset( $params['items'] ) && is_array( $params['items'] ) ? $params['items'] : array();

    if ( empty( $items ) ) {
        // Still record the sync time even if the client had nothing new to contribute.
        joes_scanner_auth_observed_triples_v2_set_last_report_utc( $server_key, $device_id_hash );
        $rows = joes_scanner_auth_observed_triples_v2_get_rows( $server_key, 5000 );
        return new WP_REST_Response(
            array(
                'ok' => true,
                'inserted' => 0,
                'updated' => 0,
                'server_key' => $server_key,
                'last_reported_at_utc' => joes_scanner_auth_observed_triples_v2_get_last_report_utc_for_server( $server_key ),
                'count' => is_array( $rows ) ? count( $rows ) : 0,
                'items' => $rows,
            ),
            200
        );
    }

    $meta = array(
        'device_id_hash' => $device_id_hash,
        'user_id'        => isset( $identity['user_id'] ) ? (int) $identity['user_id'] : null,
        'email'          => isset( $identity['email'] ) ? (string) $identity['email'] : null,
    );

    $result = joes_scanner_auth_observed_triples_v2_upsert_many( $server_key, $items, $meta );
    joes_scanner_auth_observed_triples_v2_set_last_report_utc( $server_key, $device_id_hash );

    // Return the authoritative list for deterministic client replace.
    $rows = joes_scanner_auth_observed_triples_v2_get_rows( $server_key, 5000 );

    return new WP_REST_Response(
        array(
            'ok'       => true,
            'inserted' => isset( $result['inserted'] ) ? (int) $result['inserted'] : 0,
            'updated'  => isset( $result['updated'] ) ? (int) $result['updated'] : 0,
            'server_key' => $server_key,
            'last_reported_at_utc' => joes_scanner_auth_observed_triples_v2_get_last_report_utc_for_server( $server_key ),
            'count' => is_array( $rows ) ? count( $rows ) : 0,
            'items' => $rows,
        ),
        200
    );

}

/*
//|--------------------------------------------------------------------------//| POST /wp-json/joes-scanner/v1/ping//|--------------------------------------------------------------------------//| Body://| {//|   "session_token": "uuid",//|   "device_type": "Desktop",//|   "device_platform": "Windows",//|   "device_model": "Surface Pro",//|   "app_version": "1.2.3",//|   "device_id": "stable device id string"//| }*/
function joes_scanner_auth_ping_endpoint( $request ) {
    try {
        if ( ! joes_scanner_auth_is_json_request( $request ) ) {
            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'unsupported_media_type',
                    'message' => 'Content-Type must be application/json.',
                ),
                415
            );
        }

        if ( joes_scanner_auth_body_too_large( $request, 8192 ) ) {
            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'payload_too_large',
                    'message' => 'Payload too large.',
                ),
                413
            );
        }

        $params = $request->get_params();
        if ( ! is_array( $params ) ) {
            $params = array();
        }

        $session_token = isset( $params['session_token'] ) ? joes_scanner_auth_sanitize_device_string( (string) $params['session_token'], 36 ) : null;
        if ( ! $session_token ) {
            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'missing_session_token',
                    'message' => 'session_token is required.',
                ),
                400
            );
        }

        $telemetry = joes_scanner_auth_parse_telemetry_from_params( $params );

        $version_check = joes_scanner_auth_check_minimum_app_version( isset( $telemetry['app_version'] ) ? $telemetry['app_version'] : '' );
        if ( empty( $version_check['ok'] ) ) {
            return new WP_REST_Response(
                array(
                    'ok'                  => false,
                    'error'               => isset( $version_check['error'] ) ? $version_check['error'] : 'app_version_too_old',
                    'message'             => isset( $version_check['message'] ) ? $version_check['message'] : 'App version is too old.',
                    'minimum_app_version' => isset( $version_check['minimum_app_version'] ) ? $version_check['minimum_app_version'] : '',
                    'app_version'         => isset( $version_check['app_version'] ) ? $version_check['app_version'] : '',
                    'server_time_utc'     => gmdate( 'c' ),
                ),
                426
            );
        }

        $stream_server_url_raw = joes_scanner_auth_get_param_first( $params, array( 'stream_server_url', 'server_url' ) );
        $stream_server_url = null;
        if ( $stream_server_url_raw !== null ) {
            $stream_server_url = esc_url_raw( (string) $stream_server_url_raw );
            $stream_server_url = joes_scanner_auth_trim_text( $stream_server_url, 255 );
        }

        $stream_server_is_hosted_raw = joes_scanner_auth_get_param_first( $params, array( 'stream_server_is_hosted', 'server_is_hosted' ) );
        $stream_server_is_hosted = -1;
        if ( $stream_server_is_hosted_raw !== null ) {
            if ( is_bool( $stream_server_is_hosted_raw ) ) {
                $stream_server_is_hosted = $stream_server_is_hosted_raw ? 1 : 0;
            } else {
                $v = strtolower( trim( (string) $stream_server_is_hosted_raw ) );
                $stream_server_is_hosted = ( $v === '1' || $v === 'true' || $v === 'yes' ) ? 1 : 0;
            }
        }

        $device_id_hash = null;
        if ( isset( $params['device_id'] ) ) {
            $device_id_hash = joes_scanner_auth_hash_device_id( (string) $params['device_id'] );
        }

        $ip = joes_scanner_auth_get_request_ip( $request );

        if ( joes_scanner_auth_is_ip_blocked( $ip ) ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'blocked' ), 403 );
        }

        if ( $device_id_hash && joes_scanner_auth_is_device_blocked_by_hash( $device_id_hash ) ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'blocked' ), 403 );
        }

        $user_agent = joes_scanner_auth_get_user_agent( $request );
        $user_agent = $user_agent ? joes_scanner_auth_trim_text( $user_agent, 512 ) : null;

        $bound = $device_id_hash ? joes_scanner_auth_get_bound_user_for_device( $device_id_hash ) : null;

        $args = array(
            'device_type'     => $telemetry['device_type'],
            'device_platform' => $telemetry['device_platform'],
            'device_model'    => $telemetry['device_model'],
            'app_version'     => $telemetry['app_version'],
            'device_id_hash'  => $device_id_hash,
            'stream_server_url' => $stream_server_url,
            'stream_server_is_hosted' => $stream_server_is_hosted,
            'ip'              => $ip,
            'user_agent'      => $user_agent,
        );

        if ( is_array( $bound ) && ! empty( $bound['user_id'] ) ) {
            $args['user_id']  = (int) $bound['user_id'];
            $args['username'] = isset( $bound['username'] ) ? (string) $bound['username'] : '';
            $args['email']    = isset( $bound['email'] ) ? (string) $bound['email'] : '';
        }

        joes_scanner_auth_upsert_session( $session_token, $args, 'ping' );

        return new WP_REST_Response(
            array(
                'ok'              => true,
                'server_time_utc' => gmdate( 'c' ),
            ),
            200
        );
    } catch ( \Throwable $e ) {
        return new WP_REST_Response(
            array(
                'ok'      => false,
                'error'   => 'server_error',
                'message' => 'Server error',
            ),
            500
        );
    }
}

/*
//|--------------------------------------------------------------------------//| GET /wp-json/joes-scanner/v1/servers//|--------------------------------------------------------------------------*/
function joes_scanner_auth_servers_endpoint( $request ) {
    try {
        if ( ! function_exists( 'joes_scanner_auth_table_server_directory' ) ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'server_directory_not_available' ), 500 );
        }

        global $wpdb;
        $tbl = joes_scanner_auth_table_server_directory();
        $rows = $wpdb->get_results( $wpdb->prepare( "SELECT id, server_name, server_url, info_url, area_label, map_anchor, is_official, use_api_firewall_credentials FROM {$tbl} WHERE status = %s ORDER BY sort_order ASC, server_name ASC", 'approved' ) );

        $out = array();
        foreach ( (array) $rows as $r ) {
            $out[] = array(
                'id'         => (int) $r->id,
                'name'       => (string) $r->server_name,
                'url'        => (string) $r->server_url,
                'info_url'   => isset( $r->info_url ) ? (string) $r->info_url : '',
                'area_label' => isset( $r->area_label ) ? (string) $r->area_label : '',
                'map_anchor'   => (string) $r->map_anchor,
                'is_official' => isset( $r->is_official ) ? ( (int) $r->is_official === 1 ) : false,
                'badge'       => ( isset( $r->is_official ) && (int) $r->is_official === 1 ) ? 'shield' : '',
                'badge_label' => ( isset( $r->is_official ) && (int) $r->is_official === 1 ) ? 'Official' : '',
                'uses_api_firewall_credentials' => isset( $r->use_api_firewall_credentials ) ? ( (int) $r->use_api_firewall_credentials === 1 ) : false,
            );
        }

        return new WP_REST_Response( array( 'ok' => true, 'servers' => $out ), 200 );
    } catch ( Exception $ex ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'exception', 'message' => $ex->getMessage() ), 500 );
    }
}



/**
 * Normalize an email address for identity matching.
 *
 * SureCart's customer endpoint accepts an exact email filter. Normalizing both
 * sides prevents a capitalization difference from separating an otherwise
 * identical WordPress and SureCart account.
 */
function joes_scanner_auth_normalize_lookup_email( $email ) {
    $email = sanitize_email( trim( (string) $email ) );
    return $email !== '' ? strtolower( $email ) : '';
}

/**
 * Read and normalize the email exposed by a SureCart customer model.
 */
function joes_scanner_auth_get_surecart_customer_email( $customer ) {
    if ( is_object( $customer ) && isset( $customer->email ) ) {
        return joes_scanner_auth_normalize_lookup_email( $customer->email );
    }

    if ( is_array( $customer ) && isset( $customer['email'] ) ) {
        return joes_scanner_auth_normalize_lookup_email( $customer['email'] );
    }

    return '';
}

/**
 * Return the iterable records from a SureCart collection response.
 */
function joes_scanner_auth_get_surecart_collection_records( $collection ) {
    if ( is_array( $collection ) ) {
        if ( isset( $collection['data'] ) && is_iterable( $collection['data'] ) ) {
            return $collection['data'];
        }
        return $collection;
    }

    if ( is_object( $collection ) && isset( $collection->data ) && is_iterable( $collection->data ) ) {
        return $collection->data;
    }

    if ( is_iterable( $collection ) ) {
        return $collection;
    }

    return array();
}

/**
 * Find a SureCart customer by email with normalization and a safe fallback.
 *
 * Lookup sequence:
 *  1. Exact query using the supplied sanitized email.
 *  2. Exact query using a lowercase normalized email.
 *  3. SureCart full-text query followed by a case-insensitive exact comparison.
 *
 * The return structure includes diagnostics for the admin Test Account page,
 * while callers that only need the model can use the `customer` element.
 */
function joes_scanner_auth_find_surecart_customer_by_email( $email ) {
    $input_email      = sanitize_email( trim( (string) $email ) );
    $normalized_email = joes_scanner_auth_normalize_lookup_email( $input_email );

    $result = array(
        'customer'         => null,
        'input_email'      => $input_email,
        'normalized_email' => $normalized_email,
        'matched_email'    => null,
        'match_method'     => null,
        'attempts'         => array(),
        'error'            => null,
    );

    if ( $normalized_email === '' ) {
        $result['error'] = array(
            'code'    => 'invalid_email',
            'message' => 'The supplied email address is invalid.',
        );
        return $result;
    }

    if ( ! class_exists( '\\SureCart\\Models\\Customer' ) ) {
        $result['error'] = array(
            'code'    => 'surecart_customer_model_unavailable',
            'message' => 'The SureCart customer model is unavailable.',
        );
        return $result;
    }

    $exact_emails = array_values(
        array_unique(
            array_filter(
                array( $input_email, $normalized_email ),
                static function ( $value ) {
                    return is_string( $value ) && $value !== '';
                }
            )
        )
    );

    foreach ( $exact_emails as $candidate_email ) {
        $customer = \SureCart\Models\Customer::where(
            array(
                'email' => $candidate_email,
            )
        )->first();

        if ( is_wp_error( $customer ) ) {
            $result['attempts'][] = array(
                'method'  => 'exact',
                'email'   => $candidate_email,
                'found'   => false,
                'error'   => $customer->get_error_code(),
                'message' => $customer->get_error_message(),
            );
            continue;
        }

        $returned_email = joes_scanner_auth_get_surecart_customer_email( $customer );
        $found          = is_object( $customer ) || is_array( $customer );
        $matches        = $found && ( $returned_email === '' || hash_equals( $normalized_email, $returned_email ) );

        $result['attempts'][] = array(
            'method'         => 'exact',
            'email'          => $candidate_email,
            'found'          => $found,
            'returned_email' => $returned_email !== '' ? $returned_email : null,
            'matched'        => $matches,
        );

        if ( $matches ) {
            $result['customer']      = $customer;
            $result['matched_email'] = $returned_email !== '' ? $returned_email : $candidate_email;
            $result['match_method']  = $candidate_email === $normalized_email ? 'exact_normalized_email' : 'exact_supplied_email';
            return $result;
        }
    }

    // The SureCart customer list API supports a full-text query. Use it only as
    // a fallback, then require an exact case-insensitive email match locally.
    $query_terms = array_values( array_unique( array_filter( array( $normalized_email, $input_email ) ) ) );
    $matches_by_id = array();

    foreach ( $query_terms as $query_term ) {
        $customers = \SureCart\Models\Customer::where(
            array(
                'query' => $query_term,
                'limit' => 100,
            )
        )->get();

        if ( is_wp_error( $customers ) ) {
            $result['attempts'][] = array(
                'method'  => 'query',
                'email'   => $query_term,
                'found'   => false,
                'error'   => $customers->get_error_code(),
                'message' => $customers->get_error_message(),
            );
            continue;
        }

        $query_match_count = 0;
        foreach ( joes_scanner_auth_get_surecart_collection_records( $customers ) as $candidate ) {
            $candidate_email = joes_scanner_auth_get_surecart_customer_email( $candidate );
            if ( $candidate_email === '' || ! hash_equals( $normalized_email, $candidate_email ) ) {
                continue;
            }

            $candidate_id = '';
            if ( is_object( $candidate ) && isset( $candidate->id ) ) {
                $candidate_id = trim( (string) $candidate->id );
            } elseif ( is_array( $candidate ) && isset( $candidate['id'] ) ) {
                $candidate_id = trim( (string) $candidate['id'] );
            }

            if ( $candidate_id === '' ) {
                $candidate_id = 'email:' . $candidate_email . ':' . $query_match_count;
            }

            $matches_by_id[ $candidate_id ] = $candidate;
            $query_match_count++;
        }

        $result['attempts'][] = array(
            'method'        => 'query',
            'email'         => $query_term,
            'found'         => $query_match_count > 0,
            'matched_count' => $query_match_count,
        );
    }

    if ( ! empty( $matches_by_id ) ) {
        // When duplicates exist, follow SureCart's checkout behavior and prefer
        // the most recently updated matching customer.
        $best_customer = null;
        $best_updated  = -1;

        foreach ( $matches_by_id as $candidate ) {
            $updated_at = 0;
            if ( is_object( $candidate ) && isset( $candidate->updated_at ) ) {
                $updated_at = (int) $candidate->updated_at;
            } elseif ( is_array( $candidate ) && isset( $candidate['updated_at'] ) ) {
                $updated_at = (int) $candidate['updated_at'];
            }

            if ( $best_customer === null || $updated_at > $best_updated ) {
                $best_customer = $candidate;
                $best_updated  = $updated_at;
            }
        }

        $result['customer']      = $best_customer;
        $result['matched_email'] = $normalized_email;
        $result['match_method']  = count( $matches_by_id ) > 1 ? 'query_case_insensitive_most_recent' : 'query_case_insensitive';
        return $result;
    }

    $had_api_error = false;
    foreach ( $result['attempts'] as $attempt ) {
        if ( ! empty( $attempt['error'] ) ) {
            $had_api_error = true;
            break;
        }
    }

    if ( $had_api_error ) {
        $result['error'] = array(
            'code'    => 'surecart_customer_lookup_failed',
            'message' => 'One or more SureCart customer lookup requests failed.',
        );
    }

    return $result;
}


function joes_scanner_auth_get_subscription_state_for_email( $email ) {
    $email = sanitize_email( (string) $email );
    if ( $email === '' ) {
        return array( 'ok' => false, 'active' => false, 'tier_level' => 0 );
    }

    if ( ! joes_scanner_auth_is_surecart_enabled() ) {
        return array( 'ok' => true, 'active' => true, 'tier_level' => 1 );
    }

    if ( ! class_exists( '\SureCart\Models\Customer' ) || ! class_exists( '\SureCart\Models\Subscription' ) ) {
        return array( 'ok' => false, 'active' => false, 'tier_level' => 0 );
    }

    $subscription_item_levels = joes_scanner_auth_get_subscription_item_levels();
    if ( empty( $subscription_item_levels ) ) {
        joes_scanner_auth_sync_allowed_prices_from_surecart( 'server_firewall_credentials' );
        $subscription_item_levels = joes_scanner_auth_get_subscription_item_levels();
    }

    $allowed_status = joes_scanner_auth_get_allowed_statuses();
    $allowed_item_set = array();
    foreach ( (array) $subscription_item_levels as $item_id => $lvl ) {
        $item_id = trim( (string) $item_id );
        if ( $item_id === '' || (int) $lvl <= 0 ) {
            continue;
        }
        $allowed_item_set[ $item_id ] = true;
    }

    $customer_lookup = joes_scanner_auth_find_surecart_customer_by_email( $email );
    $customer        = isset( $customer_lookup['customer'] ) ? $customer_lookup['customer'] : null;

    if ( ! $customer ) {
        return array( 'ok' => true, 'active' => false, 'tier_level' => 0 );
    }

    $subscriptions = \SureCart\Models\Subscription::where( array( 'customer_ids[]' => $customer->id ) )->get();
    $best_tier_level = 0;

    foreach ( joes_scanner_auth_get_surecart_collection_records( $subscriptions ) as $subscription ) {
        $sub_status = isset( $subscription->status ) ? strtolower( (string) $subscription->status ) : '';
        if ( ! in_array( $sub_status, $allowed_status, true ) ) {
            continue;
        }

        $sub_price_id = '';
        if ( isset( $subscription->price_id ) && trim( (string) $subscription->price_id ) !== '' ) {
            $sub_price_id = trim( (string) $subscription->price_id );
        } elseif ( isset( $subscription->price ) ) {
            if ( is_scalar( $subscription->price ) ) {
                $sub_price_id = trim( (string) $subscription->price );
            } elseif ( is_object( $subscription->price ) && isset( $subscription->price->id ) ) {
                $sub_price_id = trim( (string) $subscription->price->id );
            } elseif ( is_array( $subscription->price ) && isset( $subscription->price['id'] ) ) {
                $sub_price_id = trim( (string) $subscription->price['id'] );
            }
        }

        if ( $sub_price_id === '' ) {
            continue;
        }

        $sub_item_id = joes_scanner_auth_resolve_subscription_item_id_for_price_id( $sub_price_id );
        if ( $sub_item_id === '' || ! isset( $allowed_item_set[ $sub_item_id ] ) ) {
            continue;
        }

        $tier_level = (int) joes_scanner_auth_get_tier_level_for_subscription_item_id( $sub_item_id );
        if ( $tier_level > $best_tier_level ) {
            $best_tier_level = $tier_level;
        }
    }

    return array(
        'ok' => true,
        'active' => $best_tier_level > 0,
        'tier_level' => $best_tier_level,
    );
}

function joes_scanner_auth_server_firewall_credentials_endpoint( $request ) {
    try {
        if ( ! function_exists( 'joes_scanner_auth_table_server_directory' ) ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'server_directory_not_available' ), 500 );
        }

        $params = $request->get_json_params();
        if ( ! is_array( $params ) ) {
            $params = $request->get_params();
        }

        $session_token = isset( $params['session_token'] ) ? sanitize_text_field( (string) $params['session_token'] ) : '';
        $directory_id = isset( $params['directory_id'] ) ? (int) $params['directory_id'] : 0;
        $server_url = isset( $params['server_url'] ) ? esc_url_raw( (string) $params['server_url'] ) : '';
        $device_id_hash = isset( $params['device_id_hash'] ) ? joes_scanner_auth_sanitize_device_string( (string) $params['device_id_hash'], 64 ) : '';
        $device_id = isset( $params['device_id'] ) ? joes_scanner_auth_sanitize_device_string( (string) $params['device_id'], 120 ) : '';

        if ( $session_token === '' ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_session_token' ), 400 );
        }

        if ( $directory_id <= 0 && $server_url === '' ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_server' ), 400 );
        }

        if ( ! $device_id_hash && $device_id ) {
            $computed = joes_scanner_auth_hash_device_id( $device_id );
            if ( $computed ) {
                $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $computed, 64 );
            }
        }

        $session = joes_scanner_auth_get_session_row_by_token( $session_token );
        if ( ! $session ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'unauthorized' ), 401 );
        }

        $session_user_id = isset( $session['user_id'] ) ? (int) $session['user_id'] : 0;
        $session_email = isset( $session['email'] ) ? sanitize_email( (string) $session['email'] ) : '';
        if ( $session_user_id <= 0 || $session_email === '' ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'unauthorized' ), 401 );
        }

        $subscription_state = joes_scanner_auth_get_subscription_state_for_email( $session_email );
        if ( empty( $subscription_state['ok'] ) || empty( $subscription_state['active'] ) || (int) ( $subscription_state['tier_level'] ?? 0 ) < 1 ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'subscription_required' ), 403 );
        }

        if ( $device_id_hash ) {
            $sess_hash = isset( $session['device_id_hash'] ) ? joes_scanner_auth_sanitize_device_string( (string) $session['device_id_hash'], 64 ) : '';
            if ( $sess_hash !== '' && ! hash_equals( $sess_hash, $device_id_hash ) ) {
                return new WP_REST_Response( array( 'ok' => false, 'error' => 'unauthorized' ), 401 );
            }
        }

        global $wpdb;
        $tbl = joes_scanner_auth_table_server_directory();

        if ( $directory_id > 0 ) {
            $row = $wpdb->get_row( $wpdb->prepare(
                "SELECT id, server_url, use_api_firewall_credentials, firewall_username, firewall_password FROM {$tbl} WHERE id = %d AND status = %s LIMIT 1",
                $directory_id,
                'approved'
            ) );
        } else {
            $row = $wpdb->get_row( $wpdb->prepare(
                "SELECT id, server_url, use_api_firewall_credentials, firewall_username, firewall_password FROM {$tbl} WHERE TRIM(TRAILING '/' FROM server_url) = %s AND status = %s LIMIT 1",
                rtrim( joes_scanner_auth_normalize_url_for_compare( $server_url ), '/' ),
                'approved'
            ) );
        }

        if ( ! $row ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'not_found' ), 404 );
        }

        if ( ! isset( $row->use_api_firewall_credentials ) || (int) $row->use_api_firewall_credentials !== 1 ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'not_enabled' ), 404 );
        }

        $username = isset( $row->firewall_username ) ? trim( (string) $row->firewall_username ) : '';
        $password = isset( $row->firewall_password ) ? trim( (string) $row->firewall_password ) : '';

        if ( $username === '' || $password === '' ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'not_configured' ), 404 );
        }

        return new WP_REST_Response(
            array(
                'ok' => true,
                'directory_id' => (int) $row->id,
                'server_url' => (string) $row->server_url,
                'username' => $username,
                'password' => $password,
            ),
            200
        );
    } catch ( Exception $ex ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'exception', 'message' => $ex->getMessage() ), 500 );
    }
}

function joes_scanner_auth_endpoint( $request ) {
    $ip         = joes_scanner_auth_get_request_ip( $request );
    $user_agent = joes_scanner_auth_get_user_agent( $request );
    $user_agent = $user_agent ? joes_scanner_auth_trim_text( $user_agent, 512 ) : null;

    $session_token_in = null;

    $device_type     = null;
    $device_platform = null;
    $device_model    = null;
    $app_version     = null;
    $device_id_hash  = null;

    try {
        if ( ! joes_scanner_auth_is_json_request( $request ) ) {
            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'unsupported_media_type',
                    'message' => 'Content-Type must be application/json.',
                ),
                415
            );
        }

        if ( joes_scanner_auth_body_too_large( $request, 16384 ) ) {
            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'payload_too_large',
                    'message' => 'Request body too large.',
                ),
                413
            );
        }

        $params = $request->get_params();
        if ( ! is_array( $params ) ) {
            $params = array();
        }

        $username_raw = isset( $params['username'] ) ? trim( (string) $params['username'] ) : '';
        $username = $username_raw !== '' ? sanitize_text_field( $username_raw ) : '';

        // Allow email logins. Do not use sanitize_user here because it strips @ and dots.
        if ( $username_raw !== '' && strpos( $username_raw, '@' ) !== false ) {
            $email = sanitize_email( $username_raw );
            if ( $email !== '' ) {
                $username = $email;
            }
        }
        $password = isset( $params['password'] ) ? (string) $params['password'] : '';

        $telemetry = joes_scanner_auth_parse_telemetry_from_params( $params );
        $device_type     = $telemetry['device_type'];
        $device_platform = $telemetry['device_platform'];
        $device_model    = $telemetry['device_model'];
        $app_version     = $telemetry['app_version'];

        $version_check = joes_scanner_auth_check_minimum_app_version( $app_version );
        if ( empty( $version_check['ok'] ) ) {
            joes_scanner_auth_log_event(
                array(
                    'user_id'         => null,
                    'username'        => $username ? $username : null,
                    'email'           => null,
                    'ip'              => $ip,
                    'user_agent'      => $user_agent,
                    'device_type'     => $device_type,
                    'device_platform' => $device_platform,
                    'device_model'    => $device_model,
                    'app_version'     => $app_version,
                    'device_id_hash'  => $device_id_hash,
                    'session_token'   => $session_token_in,
                    'result'          => 'oldversion',
                    'http_status'     => 426,
                    'message'         => isset( $version_check['message'] ) ? (string) $version_check['message'] : 'App version too old',
                )
            );

            return new WP_REST_Response(
                array(
                    'ok'                  => false,
                    'error'               => isset( $version_check['error'] ) ? $version_check['error'] : 'app_version_too_old',
                    'message'             => isset( $version_check['message'] ) ? $version_check['message'] : 'App version is too old.',
                    'minimum_app_version' => isset( $version_check['minimum_app_version'] ) ? $version_check['minimum_app_version'] : '',
                    'app_version'         => isset( $version_check['app_version'] ) ? $version_check['app_version'] : '',
                    'server_time_utc'     => gmdate( 'c' ),
                ),
                426
            );
        }

        if ( isset( $params['device_id'] ) ) {
            $device_id_hash = joes_scanner_auth_hash_device_id( (string) $params['device_id'] );
        }

        if ( isset( $params['session_token'] ) ) {
            $session_token_in = joes_scanner_auth_sanitize_device_string( (string) $params['session_token'], 36 );
        }

        if ( $username === '' || $password === '' ) {
            joes_scanner_auth_log_event(
                array(
                    'user_id'         => null,
                    'username'        => $username ? $username : null,
                    'email'           => null,
                    'ip'              => $ip,
                    'user_agent'      => $user_agent,
                    'device_type'     => $device_type,
                    'device_platform' => $device_platform,
                    'device_model'    => $device_model,
                    'app_version'     => $app_version,
                    'device_id_hash'  => $device_id_hash,
                    'session_token'   => $session_token_in,
                    'result'          => 'missing',
                    'http_status'     => 400,
                    'message'         => 'Missing credentials',
                )
            );

            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'missing_credentials',
                    'message' => 'Username and password are required.',
                ),
                400
            );
        }

        // Basic brute force protection
        if ( ! joes_scanner_auth_rate_limit_allow( $ip, $username, 20, 10 * MINUTE_IN_SECONDS ) ) {
            joes_scanner_auth_log_event(
                array(
                    'user_id'         => null,
                    'username'        => $username,
                    'email'           => null,
                    'ip'              => $ip,
                    'user_agent'      => $user_agent,
                    'device_type'     => $device_type,
                    'device_platform' => $device_platform,
                    'device_model'    => $device_model,
                    'app_version'     => $app_version,
                    'device_id_hash'  => $device_id_hash,
                    'session_token'   => $session_token_in,
                    'result'          => 'ratelimit',
                    'http_status'     => 429,
                    'message'         => 'Rate limited',
                )
            );

            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'rate_limited',
                    'message' => 'Too many attempts. Try again later.',
                ),
                429
            );
        }

        $user = joes_scanner_auth_authenticate_account( $username, $password );

        if ( is_wp_error( $user ) ) {
            joes_scanner_auth_rate_limit_record_failure( $ip, $username, 20, 10 * MINUTE_IN_SECONDS );

            joes_scanner_auth_log_event(
                array(
                    'user_id'         => null,
                    'username'        => $username,
                    'email'           => null,
                    'ip'              => $ip,
                    'user_agent'      => $user_agent,
                    'device_type'     => $device_type,
                    'device_platform' => $device_platform,
                    'device_model'    => $device_model,
                    'app_version'     => $app_version,
                    'device_id_hash'  => $device_id_hash,
                    'session_token'   => $session_token_in,
                    'result'          => 'badcreds',
                    'http_status'     => 401,
                    'message'         => 'Invalid credentials',
                )
            );

            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'invalid_credentials',
                    'message' => 'Invalid username or password.',
                ),
                401
            );
        }


        if ( $device_id_hash && joes_scanner_auth_is_device_blocked_by_hash( $device_id_hash ) ) {
            joes_scanner_auth_log_event(
                array(
                    'user_id'         => (int) $user->ID,
                    'username'        => $username,
                    'email'           => $user->user_email,
                    'ip'              => $ip,
                    'user_agent'      => $user_agent,
                    'device_type'     => $device_type,
                    'device_platform' => $device_platform,
                    'device_model'    => $device_model,
                    'app_version'     => $app_version,
                    'device_id_hash'  => $device_id_hash,
                    'session_token'   => $session_token_in,
                    'result'          => 'blocked',
                    'http_status'     => 401,
                    'message'         => 'Invalid credentials',
                )
            );

            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'invalid_credentials',
                    'message' => 'Invalid username or password.',
                ),
                401
            );
        }

        $surecart_enabled = joes_scanner_auth_is_surecart_enabled();
        $email             = $user->user_email;

        $active                    = false;
        $status                    = 'none';
        $level_label               = null;
        $expires_at_utc            = null;
        $trial_ends_at_utc         = null;
        $price_id_used             = null;
        $price_label_for_api       = null;
        $period_end_at_utc         = null;
        $found_subscriptions_debug = array();
        $tier_level = 0;
        $subscription_item_id = null;
        $subscription_item_id      = null;

        if ( $surecart_enabled ) {

if ( ! class_exists( '\SureCart\Models\Customer' ) || ! class_exists( '\SureCart\Models\Subscription' ) ) {
            joes_scanner_auth_log_event(
                array(
                    'user_id'         => (int) $user->ID,
                    'username'        => $username,
                    'email'           => $user->user_email,
                    'ip'              => $ip,
                    'user_agent'      => $user_agent,
                    'device_type'     => $device_type,
                    'device_platform' => $device_platform,
                    'device_model'    => $device_model,
                    'app_version'     => $app_version,
                    'device_id_hash'  => $device_id_hash,
                    'session_token'   => $session_token_in,
                    'result'          => 'surecart',
                    'http_status'     => 500,
                    'message'         => 'SureCart unavailable',
                )
            );

            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'surecart_not_available',
                    'message' => 'SureCart models are not available on this site.',
                ),
                500
            );
        }

        $email          = $user->user_email;

        // Subscription levels are configured per SureCart recurring subscription price ID.
        $subscription_item_levels = joes_scanner_auth_get_subscription_item_levels();
        if ( empty( $subscription_item_levels ) ) {
            joes_scanner_auth_sync_allowed_prices_from_surecart( 'auth' );
            $subscription_item_levels = joes_scanner_auth_get_subscription_item_levels();
        }

        $allowed_status = joes_scanner_auth_get_allowed_statuses();
        $allowed_item_set = array();
        foreach ( (array) $subscription_item_levels as $item_id => $lvl ) {
            $item_id = trim( (string) $item_id );
            if ( $item_id === '' || (int) $lvl <= 0 ) {
                continue;
            }
            $allowed_item_set[ $item_id ] = true;
        }

        $auth_debug = false;
        if ( defined( 'JOES_SCANNER_AUTH_DEBUG' ) && JOES_SCANNER_AUTH_DEBUG ) {
            if ( isset( $params['debug'] ) ) {
                $auth_debug = (bool) $params['debug'];
            }
        }
        $customer_lookup = joes_scanner_auth_find_surecart_customer_by_email( $email );
        $customer        = isset( $customer_lookup['customer'] ) ? $customer_lookup['customer'] : null;

        $active              = false;
        $status              = 'none';
        $level_label         = null;
        $expires_at_utc      = null;
        $trial_ends_at_utc   = null;
        $price_id_used       = null;
        $price_label_for_api = null;
        $period_end_at_utc   = null;
        $found_subscriptions_debug = array();
        $tier_level = 0;
        $subscription_item_id = null;

        if ( $customer ) {
            $subscriptions = \SureCart\Models\Subscription::where(
                array(
                    'customer_ids[]' => $customer->id,
                )
            )->get();

            $best_subscription        = null;
            $best_status_priority     = -1;
            $best_plan_priority       = -1;
            $best_end_timestamp       = 0;


foreach ( joes_scanner_auth_get_surecart_collection_records( $subscriptions ) as $subscription ) {
    $sub_status   = isset( $subscription->status ) ? strtolower( (string) $subscription->status ) : '';
    $sub_price_id = '';
    if ( isset( $subscription->price_id ) && trim( (string) $subscription->price_id ) !== '' ) {
        $sub_price_id = trim( (string) $subscription->price_id );
    } elseif ( isset( $subscription->price ) ) {
        if ( is_scalar( $subscription->price ) ) {
            $sub_price_id = trim( (string) $subscription->price );
        } elseif ( is_object( $subscription->price ) && isset( $subscription->price->id ) ) {
            $sub_price_id = trim( (string) $subscription->price->id );
        } elseif ( is_array( $subscription->price ) && isset( $subscription->price['id'] ) ) {
            $sub_price_id = trim( (string) $subscription->price['id'] );
        }
    }

    if ( $sub_price_id === '' ) {
        continue;
    }

    $sub_item_id    = joes_scanner_auth_resolve_subscription_item_id_for_price_id( $sub_price_id );
    $sub_item_label = $sub_item_id !== '' ? joes_scanner_auth_get_plan_label_for_api( $sub_price_id ) : '';

    if ( $auth_debug ) {
        $found_subscriptions_debug[] = array(
            'status'                  => $sub_status,
            'price_id'                => $sub_price_id,
            'subscription_item_id'    => $sub_item_id,
            'subscription_item_label' => $sub_item_label,
            'tier_level'              => $sub_item_id !== '' ? joes_scanner_auth_get_tier_level_for_subscription_item_id( $sub_item_id ) : 0,
        );
    }

    if ( ! in_array( $sub_status, $allowed_status, true ) ) {
        continue;
    }

    if ( $sub_item_id === '' || ! isset( $allowed_item_set[ $sub_item_id ] ) ) {
        continue;
    }

    $status_priority = 1;
    $end_ts = 0;
    if ( ! empty( $subscription->current_period_end_at ) ) {
        $end_ts = max( $end_ts, (int) $subscription->current_period_end_at );
    }
    if ( ! empty( $subscription->trial_end_at ) ) {
        $end_ts = max( $end_ts, (int) $subscription->trial_end_at );
    }

    $tier_level_for_subscription = $sub_item_id !== '' ? joes_scanner_auth_get_tier_level_for_subscription_item_id( $sub_item_id ) : 0;

    if (
        $status_priority > $best_status_priority ||
        ( $status_priority === $best_status_priority && $tier_level_for_subscription > $best_plan_priority ) ||
        ( $status_priority === $best_status_priority && $tier_level_for_subscription === $best_plan_priority && $end_ts > $best_end_timestamp )
    ) {
        $best_subscription    = $subscription;
        $best_status_priority = $status_priority;
        $best_plan_priority   = $tier_level_for_subscription;
        $best_end_timestamp   = $end_ts;
    }
}

if ( $best_subscription ) {

                $active        = true;
                $status        = isset( $best_subscription->status ) ? strtolower( (string) $best_subscription->status ) : '';
                $price_id_used = null;
                if ( isset( $best_subscription->price_id ) && trim( (string) $best_subscription->price_id ) !== '' ) {
                    $price_id_used = trim( (string) $best_subscription->price_id );
                } elseif ( isset( $best_subscription->price ) ) {
                    if ( is_scalar( $best_subscription->price ) ) {
                        $price_id_used = trim( (string) $best_subscription->price );
                    } elseif ( is_object( $best_subscription->price ) && isset( $best_subscription->price->id ) ) {
                        $price_id_used = trim( (string) $best_subscription->price->id );
                    } elseif ( is_array( $best_subscription->price ) && isset( $best_subscription->price['id'] ) ) {
                        $price_id_used = trim( (string) $best_subscription->price['id'] );
                    }
                }

                if ( $price_id_used ) {
                    $subscription_item_id = joes_scanner_auth_resolve_subscription_item_id_for_price_id( $price_id_used );
                    $level_label          = joes_scanner_auth_get_plan_label_for_api( $price_id_used );
                    $price_label_for_api  = joes_scanner_auth_get_price_label_for_api( $price_id_used );
                    $tier_level           = $subscription_item_id !== '' ? joes_scanner_auth_get_tier_level_for_subscription_item_id( $subscription_item_id ) : 0;
                }

                $raw_current_end = null;
                if ( ! empty( $best_subscription->current_period_end_at ) ) {
                    $raw_current_end = (string) $best_subscription->current_period_end_at;
                } elseif ( ! empty( $best_subscription->current_period_end_at_date_time ) ) {
                    $raw_current_end = (string) $best_subscription->current_period_end_at_date_time;
                } elseif ( ! empty( $best_subscription->current_period_end_at_date ) ) {
                    $raw_current_end = (string) $best_subscription->current_period_end_at_date;
                }

                $raw_trial_end = null;
                if ( ! empty( $best_subscription->trial_end_at ) ) {
                    $raw_trial_end = (string) $best_subscription->trial_end_at;
                } elseif ( ! empty( $best_subscription->trial_end_at_date_time ) ) {
                    $raw_trial_end = (string) $best_subscription->trial_end_at_date_time;
                } elseif ( ! empty( $best_subscription->trial_end_at_date ) ) {
                    $raw_trial_end = (string) $best_subscription->trial_end_at_date;
                }

                if ( $raw_current_end ) {
                    $ts = is_numeric( $raw_current_end ) ? (int) $raw_current_end : strtotime( $raw_current_end );
                    if ( $ts ) {
                        $expires_at_utc = gmdate( 'c', $ts );
                    }
                }

                if ( $raw_trial_end ) {
                    $ts = is_numeric( $raw_trial_end ) ? (int) $raw_trial_end : strtotime( $raw_trial_end );
                    if ( $ts ) {
                        $trial_ends_at_utc = gmdate( 'c', $ts );
                    }
                }

                $raw_period = ( $status === 'trialing' )
                    ? ( $raw_trial_end ?: $raw_current_end )
                    : ( $raw_current_end ?: $raw_trial_end );

                if ( $raw_period ) {
                    $ts = is_numeric( $raw_period ) ? (int) $raw_period : strtotime( $raw_period );
                    if ( $ts ) {
                        $period_end_at_utc = gmdate( 'c', $ts );
                    }
                }

                // Defensive expiry check: if the resolved period end is already in the past,
                // treat the subscription as expired even if SureCart still reports an allowed status.
                // This is the source-of-truth fix so no client ever receives ok:true for expired access.
                $effective_end_ts = 0;
                if ( $period_end_at_utc ) {
                    $t = strtotime( $period_end_at_utc );
                    if ( $t ) {
                        $effective_end_ts = max( $effective_end_ts, $t );
                    }
                }
                if ( $expires_at_utc && ! $period_end_at_utc ) {
                    $t = strtotime( $expires_at_utc );
                    if ( $t ) {
                        $effective_end_ts = max( $effective_end_ts, $t );
                    }
                }
                if ( $effective_end_ts > 0 && $effective_end_ts < time() ) {
                    $active = false;
                    $status = 'expired';
                }
            }
        }


        } else {
            $active              = true;
            $status              = 'disabled';
            $level_label         = 'SureCart disabled';
            $price_label_for_api = 'disabled';
        }

// Ensure device binding reflects the most recent successful authentication.
        if ( $device_id_hash ) {
            $bind_result = joes_scanner_auth_bind_device_to_user( $device_id_hash, (int) $user->ID, $username, $email );
            if ( is_wp_error( $bind_result ) ) {
                joes_scanner_auth_log_event(
                    array(
                        'user_id'         => (int) $user->ID,
                        'username'        => $username,
                        'email'           => $email,
                        'ip'              => $ip,
                        'user_agent'      => $user_agent,
                        'device_type'     => $device_type,
                        'device_platform' => $device_platform,
                        'device_model'    => $device_model,
                        'app_version'     => $app_version,
                        'device_id_hash'  => $device_id_hash,
                        'result'          => 'denied',
                        'http_status'     => 409,
                        'message'         => 'device_linked',
                    )
                );
                return new WP_REST_Response(
                    array(
                        'ok'      => false,
                        'error'   => 'device_linked',
                        'message' => $bind_result->get_error_message(),
                    ),
                    409
                );
            }
        }

        // Resolve token: prefer valid session_token, else reuse existing token for this user and device, else create new.
        $session_token = joes_scanner_auth_resolve_session_token( $session_token_in, (int) $user->ID, $device_id_hash );



if ( joes_scanner_auth_is_ip_blocked( $ip ) ) {
            joes_scanner_auth_log_event(
                array(
                    'user_id'         => (int) $user->ID,
                    'username'        => $username,
                    'email'           => $email,
                    'ip'              => $ip,
                    'user_agent'      => $user_agent,
                    'device_type'     => $device_type,
                    'device_platform' => $device_platform,
                    'device_model'    => $device_model,
                    'app_version'     => $app_version,
                    'device_id_hash'  => $device_id_hash,
                    'session_token'   => $session_token,
                    'result'          => 'blocked',
                    'http_status'     => 403,
                    'message'         => 'ip_blocked',
                )
            );
    return new WP_REST_Response( array( 'ok' => false, 'error' => 'blocked' ), 403 );
}

if ( $device_id_hash && joes_scanner_auth_is_device_blocked_by_hash( $device_id_hash ) ) {
            joes_scanner_auth_log_event(
                array(
                    'user_id'         => (int) $user->ID,
                    'username'        => $username,
                    'email'           => $email,
                    'ip'              => $ip,
                    'user_agent'      => $user_agent,
                    'device_type'     => $device_type,
                    'device_platform' => $device_platform,
                    'device_model'    => $device_model,
                    'app_version'     => $app_version,
                    'device_id_hash'  => $device_id_hash,
                    'session_token'   => $session_token,
                    'result'          => 'blocked',
                    'http_status'     => 403,
                    'message'         => 'device_blocked',
                )
            );
    return new WP_REST_Response( array( 'ok' => false, 'error' => 'blocked' ), 403 );
}




        joes_scanner_auth_upsert_session(
            $session_token,
            array(
                'user_id'         => (int) $user->ID,
                'username'        => $username,
                'email'           => $email,
                'device_type'     => $device_type,
                'device_platform' => $device_platform,
                'device_model'    => $device_model,
                'app_version'     => $app_version,
                'device_id_hash'  => $device_id_hash,
                'ip'              => $ip,
                'user_agent'      => $user_agent,
            ),
            'auth'
        );

        if ( ! $active ) {
            $log_result  = ( $status === 'expired' ) ? 'expired' : 'nosub';
            $log_message = ( $status === 'expired' ) ? 'Subscription expired' : 'No active subscription';
            joes_scanner_auth_log_event(
                array(
                    'user_id'         => (int) $user->ID,
                    'username'        => $username,
                    'email'           => $email,
                    'ip'              => $ip,
                    'user_agent'      => $user_agent,
                    'device_type'     => $device_type,
                    'device_platform' => $device_platform,
                    'device_model'    => $device_model,
                    'app_version'     => $app_version,
                    'device_id_hash'  => $device_id_hash,
                    'session_token'   => $session_token,
                    'result'          => $log_result,
                    'http_status'     => 403,
                    'message'         => $log_message,
                )
            );

            $response = array(
                'ok'            => false,
                'error'         => ( $status === 'expired' ) ? 'expired' : 'no_active_subscription',
                'message'       => ( $status === 'expired' )
                    ? ( $period_end_at_utc ? 'Subscription expired on ' . gmdate( 'Y-m-d', strtotime( $period_end_at_utc ) ) . '.' : 'Subscription has expired.' )
                    : 'No active scanner subscription found for this account.',
                'session_token' => $session_token, // required by app
                'user'          => array(
                    'id'           => $user->ID,
                    'email'        => $email,
                    'display_name' => $user->display_name,
                ),
                'subscription'  => array(
                    'active'        => false,
                    'status'        => $status,
                    'level'         => $level_label,
                    'price_id'      => $price_label_for_api,
                    'price_guid'    => $price_id_used,
                    'subscription_item_id' => $subscription_item_id,
                    'subscription_item_label' => $level_label,
                    'tier_level'   => (int) $tier_level,
                    'expires_at'    => $expires_at_utc,
                    'trial_ends_at' => $trial_ends_at_utc,
                    'period_end_at' => $period_end_at_utc,
                ),
                // keep for backwards compatibility
                'session'       => array(
                    'token' => $session_token,
                ),
                'server_time_utc' => gmdate( 'c' ),
            );

            if ( $auth_debug ) {
                $response['debug'] = array(
                    'customer_found'       => ( $customer ? true : false ),
                    'customer_lookup'      => array(
                        'normalized_email' => isset( $customer_lookup['normalized_email'] ) ? $customer_lookup['normalized_email'] : joes_scanner_auth_normalize_lookup_email( $email ),
                        'matched_email'    => isset( $customer_lookup['matched_email'] ) ? $customer_lookup['matched_email'] : null,
                        'match_method'     => isset( $customer_lookup['match_method'] ) ? $customer_lookup['match_method'] : null,
                        'error_code'       => isset( $customer_lookup['error']['code'] ) ? $customer_lookup['error']['code'] : null,
                    ),
                    'allowed_subscription_items' => array_values( array_unique( array_keys( $allowed_item_set ) ) ),
                    'found_subscriptions'  => $found_subscriptions_debug,
                );
            }

            return new WP_REST_Response( $response, 403 );
}

        joes_scanner_auth_log_event(
            array(
                'user_id'         => (int) $user->ID,
                'username'        => $username,
                'email'           => $email,
                'ip'              => $ip,
                'user_agent'      => $user_agent,
                'device_type'     => $device_type,
                'device_platform' => $device_platform,
                'device_model'    => $device_model,
                'app_version'     => $app_version,
                'device_id_hash'  => $device_id_hash,
                'session_token'   => $session_token,
                'result'          => 'ok',
                'http_status'     => 200,
                'message'         => 'Subscription validated',
            )
        );

        return new WP_REST_Response(
            array(
                'ok'           => true,
                'session_token'=> $session_token, // required by app
                'user'         => array(
                    'id'           => $user->ID,
                    'email'        => $email,
                    'display_name' => $user->display_name,
                ),
                'subscription' => array(
                    'active'        => true,
                    'status'        => $status,
                    'level'         => $level_label,
                    'price_id'      => $price_label_for_api,
                    'price_guid'    => $price_id_used,
                    'subscription_item_id' => $subscription_item_id,
                    'subscription_item_label' => $level_label,
                    'tier_level'   => (int) $tier_level,
                    'expires_at'    => $expires_at_utc,
                    'trial_ends_at' => $trial_ends_at_utc,
                    'period_end_at' => $period_end_at_utc,
                ),
                // keep for backwards compatibility
                'session'       => array(
                    'token' => $session_token,
                ),
                'server_time_utc' => gmdate( 'c' ),
            ),
            200
        );
    } catch ( \Throwable $e ) {
        error_log( "Joe's Scanner Auth internal error (auth): " . $e->getMessage() );

        joes_scanner_auth_log_event(
            array(
                'user_id'         => null,
                'username'        => null,
                'email'           => null,
                'ip'              => $ip,
                'user_agent'      => $user_agent,
                'device_type'     => $device_type,
                'device_platform' => $device_platform,
                'device_model'    => $device_model,
                'app_version'     => $app_version,
                'device_id_hash'  => $device_id_hash,
                'session_token'   => $session_token_in,
                'result'          => 'error',
                'http_status'     => 500,
                'message'         => 'Internal error',
            )
        );

        return new WP_REST_Response(
            array(
                'ok'      => false,
                'error'   => 'internal_error',
                'message' => 'Internal error.',
            ),
            500
        );
    }
}
//|--------------------------------------------------------------------------
function joes_scanner_auth_get_session_row_by_token( $session_token ) {
    global $wpdb;

    $session_token = is_string( $session_token ) ? trim( $session_token ) : '';
    if ( $session_token === '' ) {
        return null;
    }

    $sessions_table = joes_scanner_auth_table_sessions();

    $row = $wpdb->get_row(
        $wpdb->prepare(
            "SELECT session_token, user_id, username, email, device_id_hash, device_platform, device_type, device_model, app_version, last_seen_utc, last_ip
             FROM {$sessions_table}
             WHERE session_token = %s
             LIMIT 1",
            $session_token
        ),
        ARRAY_A
    );

    return $row ? $row : null;
}

function joes_scanner_auth_get_latest_session_token_for_user_device( $user_id, $device_id_hash, $within_seconds ) {
    global $wpdb;

    $user_id        = (int) $user_id;
    $within_seconds = (int) $within_seconds;
    $device_id_hash = is_string( $device_id_hash ) ? $device_id_hash : '';

    if ( $user_id <= 0 || $within_seconds <= 0 ) {
        return null;
    }

    $table = $wpdb->prefix . 'joes_scanner_auth_sessions';

    if ( $device_id_hash === '' ) {
        $row = $wpdb->get_row(
            $wpdb->prepare(
                "SELECT session_token, last_seen_utc FROM {$table} WHERE user_id = %d AND (device_id_hash IS NULL OR device_id_hash = '') ORDER BY last_seen_utc DESC LIMIT 1",
                $user_id
            ),
            ARRAY_A
        );
    } else {
        $row = $wpdb->get_row(
            $wpdb->prepare(
                "SELECT session_token, last_seen_utc FROM {$table} WHERE user_id = %d AND device_id_hash = %s ORDER BY last_seen_utc DESC LIMIT 1",
                $user_id,
                $device_id_hash
            ),
            ARRAY_A
        );
    }

    if ( ! $row || empty( $row['session_token'] ) || empty( $row['last_seen_utc'] ) ) {
        return null;
    }

    $last_seen_ts = strtotime( (string) $row['last_seen_utc'] . ' UTC' );
    if ( $last_seen_ts === false ) {
        return null;
    }

    if ( $last_seen_ts < ( time() - $within_seconds ) ) {
        return null;
    }

    return (string) $row['session_token'];
}


function joes_scanner_auth_device_settings_endpoint( WP_REST_Request $request ) {
    global $wpdb;

    if ( joes_scanner_auth_body_too_large( $request, 16384 ) ) {
        return new WP_REST_Response(
            array( 'ok' => false, 'error' => 'body_too_large', 'message' => 'Request body too large.' ),
            413
        );
    }

    $params = $request->get_params();
    if ( ! is_array( $params ) ) {
        $params = $request->get_params();
    }

    $session_token  = isset( $params['session_token'] ) ? sanitize_text_field( (string) $params['session_token'] ) : '';
    $device_id_hash = isset( $params['device_id_hash'] ) ? joes_scanner_auth_sanitize_device_string( (string) $params['device_id_hash'], 64 ) : '';
    $device_id      = isset( $params['device_id'] ) ? joes_scanner_auth_sanitize_device_string( (string) $params['device_id'], 120 ) : '';
    if ( ! $device_id_hash && $device_id ) {
        $computed = joes_scanner_auth_hash_device_id( $device_id );
        if ( $computed ) {
            $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $computed, 64 );
        }
    }
    $ip             = isset( $_SERVER['REMOTE_ADDR'] ) ? sanitize_text_field( (string) $_SERVER['REMOTE_ADDR'] ) : '';
    $user_agent     = isset( $_SERVER['HTTP_USER_AGENT'] ) ? sanitize_text_field( (string) $_SERVER['HTTP_USER_AGENT'] ) : '';

    if ( joes_scanner_auth_is_ip_blocked( $ip ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'blocked' ), 403 );
    }

    if ( $device_id_hash && joes_scanner_auth_is_device_blocked_by_hash( $device_id_hash ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'blocked' ), 403 );
    }

    $session = joes_scanner_auth_get_session_row_by_token( $session_token );
    if ( ! $session ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'unauthorized' ), 401 );
    }

    if ( ! $device_id_hash ) {
        $device_id_hash = isset( $session['device_id_hash'] ) ? (string) $session['device_id_hash'] : '';
        $device_id_hash = $device_id_hash ? joes_scanner_auth_sanitize_device_string( $device_id_hash, 64 ) : '';
    } else {
        $sess_hash = isset( $session['device_id_hash'] ) ? (string) $session['device_id_hash'] : '';
        $sess_hash = $sess_hash ? joes_scanner_auth_sanitize_device_string( $sess_hash, 64 ) : '';
        if ( $sess_hash && $sess_hash !== $device_id_hash ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'unauthorized' ), 401 );
        }
    }

    if ( ! $device_id_hash ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_device' ), 400 );
    }

    if ( joes_scanner_auth_is_device_blocked_by_hash( $device_id_hash ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'blocked' ), 403 );
    }

    $settings_table = joes_scanner_auth_table_device_settings();

    if ( $request->get_method() === 'POST' ) {
        $settings_value = null;

        if ( isset( $params['settings'] ) ) {
            $settings_value = $params['settings'];
        } elseif ( isset( $params['settings_json'] ) ) {
            $settings_value = $params['settings_json'];
        }

        if ( $settings_value === null ) {
            return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_settings' ), 400 );
        }

        if ( is_array( $settings_value ) ) {
            $settings_json = wp_json_encode( $settings_value );
        } else {
            $settings_json = (string) $settings_value;
        }

        $settings_json = wp_kses_post( $settings_json );

        $wpdb->replace(
            $settings_table,
            array(
                'device_id_hash' => $device_id_hash,
                'user_id'        => isset( $session['user_id'] ) ? (int) $session['user_id'] : null,
                'settings_json'  => $settings_json,
                'updated_at_utc' => joes_scanner_auth_now_utc_mysql(),
            ),
            array( '%s', '%d', '%s', '%s' )
        );

        joes_scanner_auth_upsert_device(
            $device_id_hash,
            isset( $session['user_id'] ) ? (int) $session['user_id'] : null,
            isset( $session['username'] ) ? (string) $session['username'] : null,
            isset( $session['email'] ) ? (string) $session['email'] : null,
            array(),
            $ip,
            $user_agent
        );

        return new WP_REST_Response( array( 'ok' => true ), 200 );
    }

    $row = $wpdb->get_row(
        $wpdb->prepare(
            "SELECT settings_json, updated_at_utc
             FROM {$settings_table}
             WHERE device_id_hash = %s
             LIMIT 1",
            $device_id_hash
        ),
        ARRAY_A
    );

    $settings_json = $row && isset( $row['settings_json'] ) ? (string) $row['settings_json'] : '';
    $updated_utc   = $row && isset( $row['updated_at_utc'] ) ? (string) $row['updated_at_utc'] : '';

    $decoded = null;
    if ( $settings_json !== '' ) {
        $decoded = json_decode( $settings_json, true );
        if ( json_last_error() !== JSON_ERROR_NONE ) {
            $decoded = $settings_json;
        }
    }

    return new WP_REST_Response(
        array(
            'ok'           => true,
            'device_id_hash' => $device_id_hash,
            'updated_at'   => joes_scanner_auth_format_datetime_local( $updated_utc ),
            'settings'     => $decoded,
        ),
        200
    );
}

function joes_scanner_auth_app_event_endpoint( WP_REST_Request $request ) {
    global $wpdb;

    if ( joes_scanner_auth_body_too_large( $request, 16384 ) ) {
        return new WP_REST_Response(
            array( 'ok' => false, 'error' => 'body_too_large', 'message' => 'Request body too large.' ),
            413
        );
    }

    $params = $request->get_params();
    if ( ! is_array( $params ) ) {
        $params = $request->get_params();
    }

    $event_type     = isset( $params['event_type'] ) ? sanitize_key( (string) $params['event_type'] ) : '';
    $session_token  = isset( $params['session_token'] ) ? sanitize_text_field( (string) $params['session_token'] ) : '';
    $device_id_hash = isset( $params['device_id_hash'] ) ? joes_scanner_auth_sanitize_device_string( (string) $params['device_id_hash'], 64 ) : '';
    $device_id      = isset( $params['device_id'] ) ? joes_scanner_auth_sanitize_device_string( (string) $params['device_id'], 120 ) : '';
    if ( ! $device_id_hash && $device_id ) {
        $computed = joes_scanner_auth_hash_device_id( $device_id );
        if ( $computed ) {
            $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $computed, 64 );
        }
    }

    $payload_app_version     = isset( $params['app_version'] ) ? joes_scanner_auth_trim_text( (string) $params['app_version'], 60 ) : '';
    $payload_device_platform = isset( $params['device_platform'] ) ? joes_scanner_auth_trim_text( (string) $params['device_platform'], 60 ) : '';
    $payload_device_type     = isset( $params['device_type'] ) ? joes_scanner_auth_trim_text( (string) $params['device_type'], 60 ) : '';
    $payload_device_model    = isset( $params['device_model'] ) ? joes_scanner_auth_trim_text( (string) $params['device_model'], 120 ) : '';
    $payload        = isset( $params['payload'] ) ? $params['payload'] : null;

    $ip         = isset( $_SERVER['REMOTE_ADDR'] ) ? sanitize_text_field( (string) $_SERVER['REMOTE_ADDR'] ) : '';
    $user_agent = isset( $_SERVER['HTTP_USER_AGENT'] ) ? sanitize_text_field( (string) $_SERVER['HTTP_USER_AGENT'] ) : '';

    if ( $event_type === '' ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_event_type' ), 400 );
    }

    if ( joes_scanner_auth_is_ip_blocked( $ip ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'blocked' ), 403 );
    }

    if ( $device_id_hash && joes_scanner_auth_is_device_blocked_by_hash( $device_id_hash ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'blocked' ), 403 );
    }

    $reported_app_version = $payload_app_version;
    if ( $reported_app_version === '' && $session && isset( $session['app_version'] ) ) {
        $reported_app_version = joes_scanner_auth_trim_text( (string) $session['app_version'], 60 );
    }

    $version_check = joes_scanner_auth_check_minimum_app_version( $reported_app_version );
    if ( empty( $version_check['ok'] ) ) {
        return new WP_REST_Response(
            array(
                'ok'                  => false,
                'error'               => isset( $version_check['error'] ) ? $version_check['error'] : 'app_version_too_old',
                'message'             => isset( $version_check['message'] ) ? $version_check['message'] : 'App version is too old.',
                'minimum_app_version' => isset( $version_check['minimum_app_version'] ) ? $version_check['minimum_app_version'] : '',
                'app_version'         => isset( $version_check['app_version'] ) ? $version_check['app_version'] : '',
                'server_time_utc'     => gmdate( 'c' ),
            ),
            426
        );
    }

    if ( $session_token === '' && $device_id_hash === '' ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'missing_identity' ), 400 );
    }

    $session = null;
    if ( $session_token !== '' ) {
        $session = joes_scanner_auth_get_session_row_by_token( $session_token );
    }

    if ( ! $device_id_hash && $session && isset( $session['device_id_hash'] ) ) {
        $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $session['device_id_hash'], 64 );
    }

    if ( $device_id_hash && joes_scanner_auth_is_device_blocked_by_hash( $device_id_hash ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'blocked' ), 403 );
    }

    $payload_json = null;
    if ( $payload !== null ) {
        if ( is_array( $payload ) ) {
            $payload_json = wp_json_encode( $payload );
        } else {
            $payload_json = (string) $payload;
        }
    }

    $table = joes_scanner_auth_table_app_events();

    $wpdb->insert(
        $table,
        array(
            'created_at_utc'   => joes_scanner_auth_now_utc_mysql(),
            'event_type'       => joes_scanner_auth_trim_text( (string) $event_type, 20 ),
            'session_token'    => $session_token ? joes_scanner_auth_trim_text( (string) $session_token, 36 ) : null,
            'user_id'          => ( $session && isset( $session['user_id'] ) ) ? (int) $session['user_id'] : null,
            'username'         => ( $session && isset( $session['username'] ) ) ? joes_scanner_auth_trim_text( (string) $session['username'], 60 ) : null,
            'email'            => ( $session && isset( $session['email'] ) ) ? joes_scanner_auth_trim_text( (string) $session['email'], 100 ) : null,
            'device_id_hash'   => $device_id_hash ? joes_scanner_auth_trim_text( (string) $device_id_hash, 64 ) : null,
            'device_platform'  => ( $session && isset( $session['device_platform'] ) ) ? joes_scanner_auth_trim_text( (string) $session['device_platform'], 60 ) : ( $payload_device_platform !== '' ? $payload_device_platform : null ),
            'device_type'      => ( $session && isset( $session['device_type'] ) ) ? joes_scanner_auth_trim_text( (string) $session['device_type'], 60 ) : ( $payload_device_type !== '' ? $payload_device_type : null ),
            'device_model'     => ( $session && isset( $session['device_model'] ) ) ? joes_scanner_auth_trim_text( (string) $session['device_model'], 120 ) : ( $payload_device_model !== '' ? $payload_device_model : null ),
            'app_version'      => ( $session && isset( $session['app_version'] ) ) ? joes_scanner_auth_trim_text( (string) $session['app_version'], 60 ) : ( $payload_app_version !== '' ? $payload_app_version : null ),
            'ip'               => $ip ? joes_scanner_auth_trim_text( (string) $ip, 45 ) : null,
            'user_agent'       => $user_agent ? joes_scanner_auth_trim_text( (string) $user_agent, 512 ) : null,
            'payload_json'     => $payload_json,
        ),
        array( '%s','%s','%s','%d','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s','%s' )
    );

    if ( $device_id_hash ) {
        joes_scanner_auth_upsert_device(
            $device_id_hash,
            ( $session && isset( $session['user_id'] ) ) ? (int) $session['user_id'] : null,
            ( $session && isset( $session['username'] ) ) ? (string) $session['username'] : null,
            ( $session && isset( $session['email'] ) ) ? (string) $session['email'] : null,
            array(),
            $ip,
            $user_agent
        );
    }

    return new WP_REST_Response( array( 'ok' => true ), 200 );
}


//| Debug endpoints//|--------------------------------------------------------------------------
function joes_scanner_auth_build_debug_account_data( $email ) {
    $email = $email ? sanitize_email( $email ) : '';

    if ( $email === '' ) {
        return array(
            'ok'      => false,
            'error'   => 'missing_email',
            'message' => 'Provide an email address.',
        );
    }

    $out = array(
        'ok'               => true,
        'email'            => $email,
        'wp_user'          => null,
        'sc_customer'      => null,
        'sc_subscriptions' => array(),
        'plugin_config'    => array(
            'allowed_subscription_item_ids' => joes_scanner_auth_get_allowed_subscription_item_ids(),
            'subscription_item_levels'      => joes_scanner_auth_get_subscription_item_levels(),
            'allowed_statuses'              => joes_scanner_auth_get_allowed_statuses(),
            'allowed_price_ids'             => joes_scanner_auth_get_allowed_price_ids(),
            'price_levels'                  => joes_scanner_auth_get_price_levels(),
            'subscription_product_map'      => (array) ( joes_scanner_auth_get_options()['surecart_subscription_product_map'] ?? array() ),
        ),
    );

    $wp_user = get_user_by( 'email', $email );
    if ( $wp_user instanceof WP_User ) {
        // Expose only fields useful for account diagnostics. Never include the
        // password hash, activation key, or the complete WP_User internals.
        $out['wp_user'] = array(
            'ID'              => (int) $wp_user->ID,
            'user_login'      => (string) $wp_user->user_login,
            'user_nicename'   => (string) $wp_user->user_nicename,
            'user_email'      => (string) $wp_user->user_email,
            'user_registered' => (string) $wp_user->user_registered,
            'display_name'    => (string) $wp_user->display_name,
            'roles'           => array_values( (array) $wp_user->roles ),
        );
    }

    if ( ! class_exists( '\SureCart\Models\Customer' ) || ! class_exists( '\SureCart\Models\Subscription' ) ) {
        $out['surecart_available'] = false;
        return $out;
    }

    $out['surecart_available'] = true;

    $customer_lookup = joes_scanner_auth_find_surecart_customer_by_email( $email );
    $customer        = isset( $customer_lookup['customer'] ) ? $customer_lookup['customer'] : null;
    $out['customer_lookup'] = array(
        'input_email'      => isset( $customer_lookup['input_email'] ) ? $customer_lookup['input_email'] : $email,
        'normalized_email' => isset( $customer_lookup['normalized_email'] ) ? $customer_lookup['normalized_email'] : joes_scanner_auth_normalize_lookup_email( $email ),
        'matched_email'    => isset( $customer_lookup['matched_email'] ) ? $customer_lookup['matched_email'] : null,
        'match_method'     => isset( $customer_lookup['match_method'] ) ? $customer_lookup['match_method'] : null,
        'attempts'         => isset( $customer_lookup['attempts'] ) ? $customer_lookup['attempts'] : array(),
        'error'            => isset( $customer_lookup['error'] ) ? $customer_lookup['error'] : null,
    );
    $out['lookup_status'] = $customer ? 'customer_found' : ( ! empty( $customer_lookup['error'] ) ? 'customer_lookup_error' : 'customer_not_found' );

    if ( $customer ) {
        $out['sc_customer'] = json_decode( wp_json_encode( $customer ), true );

        $subscriptions = \SureCart\Models\Subscription::where(
            array(
                'customer_ids[]' => $customer->id,
            )
        )->get();

        $subs_out = array();

        foreach ( joes_scanner_auth_get_surecart_collection_records( $subscriptions ) as $subscription ) {
            $sub_arr = json_decode( wp_json_encode( $subscription ), true );

            $price_id = '';
            if ( isset( $sub_arr['price_id'] ) && $sub_arr['price_id'] !== '' ) {
                $price_id = (string) $sub_arr['price_id'];
            } elseif ( isset( $sub_arr['price'] ) && $sub_arr['price'] !== '' ) {
                $price_id = (string) $sub_arr['price'];
            }

            if ( $price_id !== '' ) {
                $price_label = joes_scanner_auth_get_price_label_for_api( $price_id );

                $subscription_item_id = joes_scanner_auth_resolve_subscription_item_id_for_price_id( $price_id );
                $sub_arr['price']                   = $price_label;
                $sub_arr['price_id']                = $price_id;
                $sub_arr['price_text']              = $price_label;
                $sub_arr['price_label']             = joes_scanner_auth_get_price_label_by_id( $price_id );
                $sub_arr['price_norm']              = joes_scanner_auth_normalize_price_token( $price_id );
                $sub_arr['price_product_id']         = joes_scanner_auth_get_surecart_price_product_id( $price_id );
                $sub_arr['subscription_item_id']    = $subscription_item_id;
                $sub_arr['subscription_item_label'] = $subscription_item_id !== '' ? joes_scanner_auth_get_plan_label_for_api( $price_id ) : '';
                $sub_arr['tier_level']              = $subscription_item_id !== '' ? joes_scanner_auth_get_tier_level_for_subscription_item_id( $subscription_item_id ) : 0;
            }

            $subs_out[] = $sub_arr;
        }

        $out['sc_subscriptions'] = $subs_out;
    }

    return $out;
}

function joes_scanner_auth_debug_account( $request ) {
    $email = $request->get_param( 'email' );
    $out   = joes_scanner_auth_build_debug_account_data( $email );
    $code  = ( isset( $out['ok'] ) && ! $out['ok'] ) ? 400 : 200;

    return new WP_REST_Response( $out, $code );
}

function joes_scanner_auth_debug_surecart() {
    $out = array(
        'products'          => array(),
        'products_found'    => 0,
        'normalized_prices' => array(),
        'subscription_items' => array(),
    );

    try {
        if ( function_exists( 'sc_get_product' ) && class_exists( 'WP_Query' ) ) {
            $query = new WP_Query(
                array(
                    'post_type'      => 'sc_product',
                    'post_status'    => 'publish',
                    'posts_per_page' => 50,
                    'no_found_rows'  => true,
                    'fields'         => 'ids',
                )
            );

            $out['products_found'] = count( $query->posts );
            foreach ( $query->posts as $post_id ) {
                $prod              = sc_get_product( $post_id );
                $out['products'][] = json_decode( wp_json_encode( $prod ), true );
            }
        }
    } catch ( \Throwable $e ) {
        $out['products_error'] = $e->getMessage();
    }

    try {
        $out['normalized_prices'] = joes_scanner_auth_get_all_subscription_prices();
        $out['subscription_items'] = function_exists( 'joes_scanner_auth_get_all_subscription_items' ) ? joes_scanner_auth_get_all_subscription_items() : array();
    } catch ( \Throwable $e ) {
        $out['prices_error'] = $e->getMessage();
    }

    return $out;
}
