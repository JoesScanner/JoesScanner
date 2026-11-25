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
        fixture_check( get_option( JOES_SCANNER_AUTH_DB_VERSION_KEY ) === '1.5.10', 'Expected schema version 1.5.10.' );
        $definition = $wpdb->get_row( "SHOW CREATE TABLE {$table}", ARRAY_N );
        fixture_check( is_array( $definition ) && strpos( $definition[1], 'ENGINE=InnoDB' ) !== false,
            'Favorites table must exist and use InnoDB.' );
        $columns = $wpdb->get_col( "SHOW COLUMNS FROM {$table}" );
        fixture_check( $columns === array( 'user_id', 'item_key', 'revision', 'mutation_id', 'deleted', 'payload' ),
            'Unexpected Favorites table columns.' );
        $index = $wpdb->get_results( "SHOW INDEX FROM {$table} WHERE Key_name = 'PRIMARY'", ARRAY_A );
        fixture_check( array_column( $index, 'Column_name' ) === array( 'user_id', 'item_key' ),
            'The Favorites primary key must be scoped to the WordPress user.' );
        $directory = joes_scanner_auth_table_server_directory();
        foreach ( array( $directory, joes_scanner_auth_dir_table( 'addresses' ), joes_scanner_auth_dir_table( 'revisions' ),
            joes_scanner_auth_dir_table( 'recipients' ), joes_scanner_auth_dir_table( 'verification' ) ) as $directory_table ) {
            $definition = $wpdb->get_row( "SHOW CREATE TABLE {$directory_table}", ARRAY_N );
            fixture_check( is_array( $definition ) && strpos( $definition[1], 'ENGINE=InnoDB' ) !== false, 'A directory table is missing or is not transactional.' );
        }
        break;

    case 'upgrade-from-1.5.8':
        // Leave the pre-existing plugin tables and options in place.
        update_option( JOES_SCANNER_AUTH_DB_VERSION_KEY, '1.5.8' );
        update_option( 'jsfav_legacy_marker', 'pre-favorites' );
        fixture_check( $wpdb->query( "DROP TABLE {$table}" ) !== false, 'Could not prepare the legacy schema fixture.' );
        break;

    case 'missing-table':
        fixture_check( get_option( JOES_SCANNER_AUTH_DB_VERSION_KEY ) === '1.5.10', 'Missing-table fixture requires the current version.' );
        fixture_check( $wpdb->query( "DROP TABLE {$table}" ) !== false, 'Could not prepare the missing-table fixture.' );
        break;

    case 'upgrade-from-1.5.9':
        $directory = joes_scanner_auth_table_server_directory();
        $admin_id = (int) get_user_by( 'login', 'integration_admin' )->ID;
        foreach ( array( 'addresses', 'recipients', 'revisions', 'verification' ) as $name ) {
            $new_table = joes_scanner_auth_dir_table( $name );
            fixture_check( $wpdb->query( "DROP TABLE {$new_table}" ) !== false, 'Could not remove the new table from the legacy fixture.' );
        }
        fixture_check( $wpdb->query( "ALTER TABLE {$directory} DROP INDEX owner_user_id, DROP INDEX address_key,
            DROP COLUMN owner_user_id, DROP COLUMN visibility, DROP COLUMN description, DROP COLUMN requires_scanner_login,
            DROP COLUMN requires_firewall_auth, DROP COLUMN address_key, DROP COLUMN was_public, DROP COLUMN directory_version" ) !== false,
            'Could not restore the original directory schema.' );
        fixture_check( $wpdb->insert( $directory, array(
            'id' => 21, 'created_at_utc' => gmdate( 'Y-m-d H:i:s' ), 'status' => 'approved', 'sort_order' => 10,
            'server_name' => 'Legacy public listing', 'server_url' => 'https://legacy-current.example.net',
            'map_anchor' => 'Boise, ID', 'submitted_by_user_id' => $admin_id, 'contact_email' => 'integration_alpha@example.invalid',
            'is_official' => 1,
        ) ) === 1, 'Could not seed the old Public listing.' );
        fixture_check( $wpdb->insert( $directory, array(
            'id' => 22, 'created_at_utc' => gmdate( 'Y-m-d H:i:s' ), 'status' => 'disabled',
            'server_name' => 'Legacy custodian listing', 'server_url' => 'https://legacy-custody.example.net',
            'map_anchor' => 'Boise, ID', 'contact_email' => 'integration_alpha@example.invalid',
        ) ) === 1, 'Could not seed the legacy listing without a numeric account link.' );
        joes_scanner_auth_log_admin_event( 'server_directory_edit', array( 'user_id' => $admin_id,
            'server_id' => 21, 'server_url' => 'https://legacy-history.example.net' ) );
        fixture_check( $wpdb->insert( $table, array( 'user_id' => $admin_id, 'item_key' => 'legacy-fixture', 'revision' => 1,
            'mutation_id' => str_repeat( 'a', 32 ), 'deleted' => 1, 'payload' => '{}' ) ) === 1,
            'Could not seed the previous Favorites schema.' );
        update_option( JOES_SCANNER_AUTH_DB_VERSION_KEY, '1.5.9' );
        break;

    case 'directory-schema':
        $directory = joes_scanner_auth_table_server_directory();
        $addresses = joes_scanner_auth_dir_table( 'addresses' );
        $admin_id = (int) get_user_by( 'login', 'integration_admin' )->ID;
        $public = $wpdb->get_row( "SELECT * FROM {$directory} WHERE id = 21" );
        $custody = $wpdb->get_row( "SELECT * FROM {$directory} WHERE id = 22" );
        fixture_check( $public && (int) $public->owner_user_id === $admin_id && $public->status === 'approved'
            && $public->visibility === 'public' && (int) $public->was_public === 1 && (int) $public->is_official === 1,
            'Migration must preserve the old listing ID, trusted owner, publication and official badge.' );
        fixture_check( $custody && (int) $custody->owner_user_id === $admin_id && $custody->status === 'disabled',
            'Legacy rows without a trusted account require administrator custody.' );
        fixture_check( (int) $wpdb->get_var( "SELECT COUNT(*) FROM {$addresses} WHERE directory_id = 21" ) === 2,
            'Migration must retain the current and known historical URL keys.' );
        fixture_check( (int) $wpdb->get_var( "SELECT COUNT(*) FROM {$table} WHERE item_key = 'legacy-fixture' AND deleted = 1" ) === 1,
            'Directory migration must preserve Favorites.' );
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
        $surecart_enabled = ! empty( $input['surecartEnabled'] );
        $opts = joes_scanner_auth_sanitize_options( array(
            'allowed_statuses' => array( 'active', 'trialing' ),
            'surecart_enabled' => (int) $surecart_enabled,
        ) );
        update_option( JOES_SCANNER_AUTH_OPTIONS_KEY, $opts );
        $opts = joes_scanner_auth_sanitize_options( array( 'event_retention_days' => 14 ) );
        update_option( JOES_SCANNER_AUTH_OPTIONS_KEY, $opts );
        fixture_check( joes_scanner_auth_get_allowed_statuses() === array( 'active', 'trialing' )
            && joes_scanner_auth_is_surecart_enabled() === $surecart_enabled,
            'Legacy settings must retain SureCart statuses across unrelated changes.' );
        break;

    case 'directory-pages':
        foreach ( array( 'my-servers' => '[joes_scanner_my_servers]', 'submit-server' => '[joes_scanner_server_submit]',
            'public-servers' => '[joes_scanner_server_directory]' ) as $slug => $shortcode ) {
            $post_id = wp_insert_post( array( 'post_title' => $slug, 'post_name' => $slug, 'post_type' => 'page',
                'post_status' => 'publish', 'post_content' => $shortcode ), true );
            fixture_check( ! is_wp_error( $post_id ) && $post_id > 0, 'Could not create the shortcode fixture page.' );
        }
        $existing = get_user_by( 'email', $input['existingEmail'] );
        fixture_check( $existing instanceof WP_User, 'The existing user fixture was not found.' );
        update_option( 'jsfav_directory_existing_user', array( 'id' => $existing->ID,
            'password' => $existing->user_pass, 'roles' => $existing->roles,
            'surecart' => get_option( JOES_SCANNER_AUTH_OPTIONS_KEY ) ), false );
        break;

    case 'directory-mail':
        $code = get_option( 'jsfav_test_mailbox', array() )[strtolower( $input['email'] )] ?? '';
        fixture_check( preg_match( '/^[0-9]{8}$/D', $code ), 'No code reached the controlled mail transport.' );
        echo $code;
        break;

    case 'directory-expire':
        fixture_check( $wpdb->update( joes_scanner_auth_dir_table( 'verification' ),
            array( 'expires_at_utc' => gmdate( 'Y-m-d H:i:s', time() - 1 ) ), array( 'email' => $input['email'] ) ) === 1,
            'Could not expire the directory code fixture.' );
        break;

    case 'directory-accounts':
        $existing = get_user_by( 'email', $input['existingEmail'] );
        $created = get_user_by( 'email', $input['createdEmail'] );
        $original = get_option( 'jsfav_directory_existing_user' );
        fixture_check( (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->users} WHERE user_email = %s",
            $input['existingEmail'] ) ) === 1 && $existing && $existing->ID === $original['id']
            && $existing->user_pass === $original['password'] && $existing->roles === $original['roles'],
            'Email verification must reuse the original account without changing its password or roles.' );
        fixture_check( $created && $created->roles === array( 'subscriber' ), 'Directory verification must create only a subscriber.' );
        $directory = joes_scanner_auth_table_server_directory();
        fixture_check( (int) $wpdb->get_var( "SELECT owner_user_id FROM {$directory} WHERE server_name = 'Email-created private listing'" )
            === (int) $created->ID, 'The new listing must belong to the new permanent user ID.' );
        fixture_check( get_option( JOES_SCANNER_AUTH_OPTIONS_KEY ) === $original['surecart'], 'Directory verification modified SureCart options.' );
        break;

    case 'directory-audit':
        $id = (int) $input['id'];
        $revisions = joes_scanner_auth_dir_table( 'revisions' );
        $rows = $wpdb->get_results( $wpdb->prepare( "SELECT * FROM {$revisions} WHERE directory_id = %d ORDER BY id", $id ) );
        $owner_id = (int) get_user_by( 'email', $input['ownerEmail'] )->ID;
        $admin_id = (int) get_user_by( 'login', 'integration_admin' )->ID;
        $states = array();
        foreach ( $rows as $row ) {
            fixture_check( (int) $row->owner_user_id === $owner_id, 'The audit changed listing ownership.' );
            $states[] = $row->state;
            if ( $row->state === 'rejected' && isset( json_decode( $row->changes_json, true )['server_url'] ) ) {
                fixture_check( (int) $row->actor_user_id === $owner_id && (int) $row->reviewer_user_id === $admin_id && $row->note,
                    'The revision audit must retain the customer, reviewer, changed fields, and explanation.' );
            }
            fixture_check( strpos( $row->changes_json, $input['secret'] ) === false, 'The directory audit stored a credential.' );
        }
        foreach ( array( 'approved', 'applied', 'rejected', 'remove', 'suspend', 'reinstate' ) as $state ) {
            fixture_check( in_array( $state, $states, true ), 'An expected review or action is missing from the audit.' );
        }
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
