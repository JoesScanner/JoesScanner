<?php
// Run by WP-CLI inside the disposable integration project.
if ( ! defined( 'WP_CLI' ) || ! WP_CLI || ! defined( 'JOES_SCANNER_FAVORITES_INTEGRATION' )
    || JOES_SCANNER_FAVORITES_INTEGRATION !== true || get_option( 'blogname' ) !== 'JoesScanner Favorites Integration' ) {
    throw new RuntimeException( 'This fixture requires the disposable integration site.' );
}

global $wpdb;
if ( $wpdb->prefix !== 'jsfav_' || wp_parse_url( home_url(), PHP_URL_HOST ) !== 'localhost' ) {
    WP_CLI::error( 'Unexpected integration database or site address.' );
}

function fixture_check( $ok, $message ) {
    if ( ! $ok ) {
        WP_CLI::error( $message );
    }
}

$mode = $args[0] ?? '';
$input = json_decode( stream_get_contents( STDIN ), true ) ?? array();
$table = joes_scanner_auth_favorites_table();

switch ( $mode ) {
    case 'schema':
        fixture_check( get_option( JOES_SCANNER_AUTH_DB_VERSION_KEY ) === '1.5.9', 'Expected schema version 1.5.9.' );
        $definition = $wpdb->get_row( "SHOW CREATE TABLE {$table}", ARRAY_N );
        fixture_check( is_array( $definition ) && strpos( $definition[1], 'ENGINE=InnoDB' ) !== false,
            'Favorites table must exist and use InnoDB.' );
        $columns = $wpdb->get_col( "SHOW COLUMNS FROM {$table}" );
        fixture_check( $columns === array( 'user_id', 'item_key', 'revision', 'mutation_id', 'deleted', 'payload' ),
            'Unexpected Favorites table columns.' );
        $index = $wpdb->get_results( "SHOW INDEX FROM {$table} WHERE Key_name = 'PRIMARY'", ARRAY_A );
        fixture_check( array_column( $index, 'Column_name' ) === array( 'user_id', 'item_key' ),
            'The Favorites primary key must be scoped to the WordPress user.' );
        break;

    case 'upgrade-from-1.5.8':
        // Leave the pre-existing plugin tables and options in place.
        update_option( JOES_SCANNER_AUTH_DB_VERSION_KEY, '1.5.8' );
        update_option( 'jsfav_legacy_marker', 'pre-favorites' );
        fixture_check( $wpdb->query( "DROP TABLE {$table}" ) !== false, 'Could not prepare the legacy schema fixture.' );
        break;

    case 'missing-table':
        fixture_check( get_option( JOES_SCANNER_AUTH_DB_VERSION_KEY ) === '1.5.9', 'Missing-table fixture requires the current version.' );
        fixture_check( $wpdb->query( "DROP TABLE {$table}" ) !== false, 'Could not prepare the missing-table fixture.' );
        break;

    case 'seed':
        fixture_check( get_option( 'jsfav_legacy_marker' ) === 'pre-favorites', 'The upgrade lost a legacy option.' );
        foreach ( array( 'alpha', 'beta' ) as $name ) {
            $account = $input[$name];
            // Alpha uses the literal core creation API. Beta reproduces the
            // password representation supplied by the WordPress admin form.
            $user = wp_create_user( $account['username'],
                $name === 'alpha' ? $account['password'] : wp_slash( $account['password'] ), $account['email'] );
            fixture_check( is_int( $user ) && $user > 0, 'Could not create a synthetic subscriber.' );
            ( new WP_User( $user ) )->set_role( 'subscriber' );
        }
        break;

    case 'auth-policy':
        add_filter( 'wp_authenticate_user', static function () {
            return new WP_Error( 'integration_denied', 'Synthetic policy denied access.' );
        }, 100 );
        $account = $input['alpha'];
        $denied = joes_scanner_auth_authenticate_account( $account['username'], $account['password'] );
        fixture_check( is_wp_error( $denied ) && 'integration_denied' === $denied->get_error_code(),
            'WordPress authentication filters must still control account authorization.' );
        break;

    case 'legacy-settings':
        fixture_check( function_exists( 'joes_scanner_auth_add_admin_menu' ), 'Legacy administration callback is missing.' );
        $opts = joes_scanner_auth_sanitize_options( array(
            'allowed_statuses' => array( 'active', 'trialing' ),
            'surecart_enabled' => 0,
        ) );
        update_option( JOES_SCANNER_AUTH_OPTIONS_KEY, $opts );
        $opts = joes_scanner_auth_sanitize_options( array( 'event_retention_days' => 14 ) );
        update_option( JOES_SCANNER_AUTH_OPTIONS_KEY, $opts );
        fixture_check( joes_scanner_auth_get_allowed_statuses() === array( 'active', 'trialing' )
            && ! joes_scanner_auth_is_surecart_enabled(), 'Legacy settings must retain SureCart statuses across unrelated changes.' );
        break;

    case 'storage':
        $payloads = $wpdb->get_col( "SELECT payload FROM {$table}" );
        fixture_check( count( $payloads ) > 200, 'The Favorites table must contain the pagination fixture.' );
        foreach ( $input['redact'] as $secret ) {
            $escaped = trim( wp_json_encode( $secret ), '"' );
            foreach ( $payloads as $text ) {
                fixture_check( strpos( $text, $secret ) === false && strpos( $text, $escaped ) === false,
                    'A synthetic authorization credential reached Favorites storage.' );
            }
        }
        break;

    default:
        WP_CLI::error( 'Unknown fixture operation.' );
}
