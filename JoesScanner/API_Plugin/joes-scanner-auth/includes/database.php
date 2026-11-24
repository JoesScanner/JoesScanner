<?php
/*
//|--------------------------------------------------------------------------//| Database//|--------------------------------------------------------------------------*/

function joes_scanner_auth_table_events() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_events';
}

function joes_scanner_auth_table_sessions() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_sessions';
}



function joes_scanner_auth_table_devices() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_devices';
}

function joes_scanner_auth_table_blocked_ips() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_blocked_ips';
}

function joes_scanner_auth_table_device_settings() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_device_settings';
}

function joes_scanner_auth_table_app_events() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_app_events';
}


function joes_scanner_auth_table_comms_messages() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_comms_messages';
}

function joes_scanner_auth_table_comms_changes() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_comms_changes';
}

function joes_scanner_auth_table_server_directory() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_server_directory';
}

function joes_scanner_auth_table_server_lookups_cache() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_server_lookups_cache';
}

function joes_scanner_auth_table_server_lookups_reports() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_server_lookups_reports';
}

function joes_scanner_auth_table_observed_triples() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_observed_triples';
}

function joes_scanner_auth_table_observed_triples_reports() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_observed_triples_reports';
}


function joes_scanner_auth_table_observed_triples_v2() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_observed_triples_v2';
}

function joes_scanner_auth_table_observed_triples_v2_reports() {
    global $wpdb;
    return $wpdb->prefix . 'joes_scanner_auth_observed_triples_v2_reports';
}

function joes_scanner_auth_log_admin_event( $action, array $data = array() ) {
    global $wpdb;

    $table = joes_scanner_auth_table_app_events();

    $payload = array(
        'action' => (string) $action,
        'data'   => $data,
    );

    $wpdb->insert(
        $table,
        array(
            'event_type'      => 'admin',
            'created_at_utc'  => gmdate( 'Y-m-d H:i:s' ),
            'user_id'         => isset( $data['user_id'] ) ? (int) $data['user_id'] : null,
            'username'        => isset( $data['username'] ) ? (string) $data['username'] : null,
            'email'           => isset( $data['email'] ) ? (string) $data['email'] : null,
            'ip'              => null,
            'device_id_hash'  => isset( $data['device_id_hash'] ) ? (string) $data['device_id_hash'] : null,
            'session_token'   => null,
            'device_platform' => isset( $data['device_platform'] ) ? (string) $data['device_platform'] : null,
            'device_type'     => isset( $data['device_type'] ) ? (string) $data['device_type'] : null,
            'device_model'    => isset( $data['device_model'] ) ? (string) $data['device_model'] : null,
            'app_version'     => isset( $data['app_version'] ) ? (string) $data['app_version'] : null,
            'payload_json'    => wp_json_encode( $payload ),
        ),
        array(
            '%s', '%s', '%d', '%s', '%s', '%s', '%s', '%s', '%s', '%s', '%s', '%s', '%s',
        )
    );
}


/**
 * Create or upgrade tables.
 */

function joes_scanner_auth_install_db() {
    global $wpdb;


    $prev_suppress = false;
    if ( is_object( $wpdb ) && method_exists( $wpdb, 'suppress_errors' ) ) {
        $prev_suppress = $wpdb->suppress_errors( true );
    }
    if ( is_object( $wpdb ) && method_exists( $wpdb, 'hide_errors' ) ) {
        $wpdb->hide_errors();
    }
    require_once ABSPATH . 'wp-admin/includes/upgrade.php';

    $charset_collate = $wpdb->get_charset_collate();

    $events_table    = joes_scanner_auth_table_events();
    $sessions_table  = joes_scanner_auth_table_sessions();
    $devices_table   = joes_scanner_auth_table_devices();
    $blocked_ips_tbl = joes_scanner_auth_table_blocked_ips();
    $device_set_tbl  = joes_scanner_auth_table_device_settings();
    $app_events_tbl  = joes_scanner_auth_table_app_events();


    $comms_messages_tbl = joes_scanner_auth_table_comms_messages();
    $comms_changes_tbl  = joes_scanner_auth_table_comms_changes();

    $server_dir_tbl = joes_scanner_auth_table_server_directory();

    $lookups_cache_tbl   = joes_scanner_auth_table_server_lookups_cache();
    $lookups_reports_tbl = joes_scanner_auth_table_server_lookups_reports();

    $observed_tbl         = joes_scanner_auth_table_observed_triples();
    $observed_reports_tbl = joes_scanner_auth_table_observed_triples_reports();
    $observed_v2_tbl = joes_scanner_auth_table_observed_triples_v2();
    $observed_v2_reports_tbl = joes_scanner_auth_table_observed_triples_v2_reports();
    $sql_events = "CREATE TABLE {$events_table} (
        id BIGINT(20) UNSIGNED NOT NULL AUTO_INCREMENT,
        created_at_utc DATETIME NOT NULL,
        user_id BIGINT(20) UNSIGNED NULL,
        username VARCHAR(60) NULL,
        email VARCHAR(100) NULL,
        ip VARCHAR(45) NULL,
        user_agent TEXT NULL,
        device_type VARCHAR(60) NULL,
        device_platform VARCHAR(60) NULL,
        device_model VARCHAR(120) NULL,
        app_version VARCHAR(60) NULL,
        device_id_hash CHAR(64) NULL,
        session_token CHAR(36) NULL,
        result VARCHAR(20) NOT NULL,
        http_status SMALLINT(5) UNSIGNED NOT NULL DEFAULT 0,
        message VARCHAR(255) NULL,
        PRIMARY KEY  (id),
        KEY created_at_utc (created_at_utc),
        KEY user_id (user_id),
        KEY session_token (session_token),
        KEY device_platform (device_platform),
        KEY device_type (device_type),
        KEY device_id_hash (device_id_hash)
    ) {$charset_collate};";

    $sql_sessions = "CREATE TABLE {$sessions_table} (
        session_token CHAR(36) NOT NULL,
        user_id BIGINT(20) UNSIGNED NULL,
        username VARCHAR(60) NULL,
        email VARCHAR(100) NULL,
        device_type VARCHAR(60) NULL,
        device_platform VARCHAR(60) NULL,
        device_model VARCHAR(120) NULL,
        app_version VARCHAR(60) NULL,
        device_id_hash CHAR(64) NULL,
        stream_server_url VARCHAR(255) NULL,
        stream_server_is_hosted TINYINT(1) UNSIGNED NOT NULL DEFAULT 0,
        stream_server_seen_utc DATETIME NULL,
        first_seen_utc DATETIME NOT NULL,
        last_seen_utc DATETIME NOT NULL,
        last_ip VARCHAR(45) NULL,
        last_user_agent TEXT NULL,
        auth_count BIGINT(20) UNSIGNED NOT NULL DEFAULT 0,
        ping_count BIGINT(20) UNSIGNED NOT NULL DEFAULT 0,
        PRIMARY KEY (session_token),
        KEY last_seen_utc (last_seen_utc),
        KEY user_id (user_id),
        KEY device_id_hash (device_id_hash),
        KEY device_platform (device_platform),
        KEY device_type (device_type)
    ) {$charset_collate};";

    $sql_devices = "CREATE TABLE {$devices_table} (
        device_id BIGINT(20) UNSIGNED NOT NULL AUTO_INCREMENT,
        device_id_hash CHAR(64) NOT NULL,
        user_id BIGINT(20) UNSIGNED NULL,
        username VARCHAR(60) NULL,
        email VARCHAR(100) NULL,
        device_type VARCHAR(60) NULL,
        device_platform VARCHAR(60) NULL,
        device_model VARCHAR(120) NULL,
        app_version VARCHAR(60) NULL,
        first_seen_utc DATETIME NOT NULL,
        last_seen_utc DATETIME NOT NULL,
        last_ip VARCHAR(45) NULL,
        last_user_agent TEXT NULL,
        is_blocked TINYINT(1) UNSIGNED NOT NULL DEFAULT 0,
        blocked_at_utc DATETIME NULL,
        blocked_reason VARCHAR(255) NULL,
        blocked_by_user_id BIGINT(20) UNSIGNED NULL,
        PRIMARY KEY (device_id),
        UNIQUE KEY device_id_hash (device_id_hash),
        KEY last_seen_utc (last_seen_utc),
        KEY user_id (user_id),
        KEY is_blocked (is_blocked)
    ) {$charset_collate};";

    $sql_blocked_ips = "CREATE TABLE {$blocked_ips_tbl} (
        ip VARCHAR(45) NOT NULL,
        blocked_at_utc DATETIME NOT NULL,
        reason VARCHAR(255) NULL,
        blocked_by_user_id BIGINT(20) UNSIGNED NULL,
        PRIMARY KEY (ip),
        KEY blocked_at_utc (blocked_at_utc)
    ) {$charset_collate};";

    $sql_device_settings = "CREATE TABLE {$device_set_tbl} (
        device_id_hash CHAR(64) NOT NULL,
        user_id BIGINT(20) UNSIGNED NULL,
        settings_json LONGTEXT NULL,
        updated_at_utc DATETIME NOT NULL,
        PRIMARY KEY (device_id_hash),
        KEY user_id (user_id),
        KEY updated_at_utc (updated_at_utc)
    ) {$charset_collate};";

    $sql_app_events = "CREATE TABLE {$app_events_tbl} (
        id BIGINT(20) UNSIGNED NOT NULL AUTO_INCREMENT,
        created_at_utc DATETIME NOT NULL,
        event_type VARCHAR(20) NOT NULL,
        session_token CHAR(36) NULL,
        user_id BIGINT(20) UNSIGNED NULL,
        username VARCHAR(60) NULL,
        email VARCHAR(100) NULL,
        device_id_hash CHAR(64) NULL,
        device_platform VARCHAR(60) NULL,
        device_type VARCHAR(60) NULL,
        device_model VARCHAR(120) NULL,
        app_version VARCHAR(60) NULL,
        ip VARCHAR(45) NULL,
        user_agent TEXT NULL,
        payload_json LONGTEXT NULL,
        PRIMARY KEY (id),
        KEY created_at_utc (created_at_utc),
        KEY event_type (event_type),
        KEY session_token (session_token),
        KEY user_id (user_id),
        KEY device_id_hash (device_id_hash)
    ) {$charset_collate};";

    $sql_comms_messages = "CREATE TABLE {$comms_messages_tbl} (
    id BIGINT(20) UNSIGNED NOT NULL AUTO_INCREMENT,
    created_at_utc DATETIME NOT NULL,
    updated_at_utc DATETIME NULL,
    author_user_id BIGINT(20) UNSIGNED NULL,
    author_label VARCHAR(60) NOT NULL,
    heading_text VARCHAR(120) NOT NULL DEFAULT '',
    message_text LONGTEXT NOT NULL,
    is_deleted TINYINT(1) NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    KEY created_at_utc (created_at_utc),
    KEY updated_at_utc (updated_at_utc),
    KEY is_deleted (is_deleted)
) {$charset_collate};";

$sql_comms_changes = "CREATE TABLE {$comms_changes_tbl} (
    seq BIGINT(20) UNSIGNED NOT NULL AUTO_INCREMENT,
    changed_at_utc DATETIME NOT NULL,
    change_type VARCHAR(10) NOT NULL,
    message_id BIGINT(20) UNSIGNED NOT NULL,
    payload LONGTEXT NULL,
    PRIMARY KEY (seq),
    KEY changed_at_utc (changed_at_utc),
    KEY message_id (message_id),
    KEY change_type (change_type)
) {$charset_collate};";

    $sql_server_directory = "CREATE TABLE {$server_dir_tbl} (
        id BIGINT(20) UNSIGNED NOT NULL AUTO_INCREMENT,
        created_at_utc DATETIME NOT NULL,
        updated_at_utc DATETIME NULL,
        status VARCHAR(20) NOT NULL DEFAULT 'pending',
        sort_order INT NOT NULL DEFAULT 0,
        server_name VARCHAR(120) NOT NULL,
        server_url VARCHAR(255) NOT NULL,
        info_url VARCHAR(255) NULL,
        area_label VARCHAR(120) NULL,
        map_anchor VARCHAR(120) NOT NULL,
        is_official TINYINT(1) NOT NULL DEFAULT 0,
        use_api_firewall_credentials TINYINT(1) NOT NULL DEFAULT 0,
        firewall_username VARCHAR(190) NULL,
        firewall_password TEXT NULL,
        agreement_accepted TINYINT(1) NOT NULL DEFAULT 0,
        agreement_text_version VARCHAR(20) NULL,
        agreement_text_snapshot TEXT NULL,
        agreement_accepted_at_utc DATETIME NULL,
        agreement_accepted_ip VARCHAR(45) NULL,
        agreement_accepted_user_id BIGINT(20) UNSIGNED NULL,
        submitted_by_user_id BIGINT(20) UNSIGNED NULL,
        submitted_username VARCHAR(60) NULL,
        submitted_email VARCHAR(100) NULL,
        submitted_ip VARCHAR(45) NULL,
        contact_user_id BIGINT(20) UNSIGNED NULL,
        contact_username VARCHAR(60) NULL,
        contact_email VARCHAR(100) NULL,
        reviewed_by_user_id BIGINT(20) UNSIGNED NULL,
        reviewed_at_utc DATETIME NULL,
        review_notes TEXT NULL,
        reject_reason VARCHAR(255) NULL,
        PRIMARY KEY (id),
        KEY created_at_utc (created_at_utc),
        KEY status (status),
        KEY sort_order (sort_order),
        KEY is_official (is_official),
        KEY server_url (server_url)
    ) {$charset_collate};";

    $sql_lookups_cache = "CREATE TABLE {$lookups_cache_tbl} (
        server_key VARCHAR(255) NOT NULL,
        receivers_json LONGTEXT NULL,
        sites_json LONGTEXT NULL,
        talkgroups_json LONGTEXT NULL,
        talkgroup_groups_json LONGTEXT NULL,
        payload_hash CHAR(64) NULL,
        updated_at_utc DATETIME NOT NULL,
        updated_by_device_id_hash CHAR(64) NULL,
        updated_by_user_id BIGINT(20) UNSIGNED NULL,
        updated_by_email VARCHAR(100) NULL,
        updated_by_ip VARCHAR(45) NULL,
        updated_by_user_agent TEXT NULL,
        PRIMARY KEY (server_key),
        KEY updated_at_utc (updated_at_utc),
        KEY updated_by_device_id_hash (updated_by_device_id_hash)
    ) {$charset_collate};";

    $sql_lookups_reports = "CREATE TABLE {$lookups_reports_tbl} (
        server_key VARCHAR(255) NOT NULL,
        device_id_hash CHAR(64) NOT NULL,
        last_reported_at_utc DATETIME NOT NULL,
        PRIMARY KEY (server_key, device_id_hash),
        KEY last_reported_at_utc (last_reported_at_utc)
    ) {$charset_collate};";

    $sql_observed_triples = "CREATE TABLE {$observed_tbl} (
        server_key VARCHAR(255) NOT NULL,
        receiver_value VARCHAR(128) NOT NULL,
        receiver_label VARCHAR(255) NOT NULL,
        site_value VARCHAR(128) NOT NULL,
        site_label VARCHAR(255) NOT NULL,
        talkgroup_value VARCHAR(128) NOT NULL,
        talkgroup_label VARCHAR(255) NOT NULL,
        seen_count BIGINT(20) UNSIGNED NOT NULL DEFAULT 0,
        first_seen_utc DATETIME NOT NULL,
        last_seen_utc DATETIME NOT NULL,
        last_source_device_hash CHAR(64) NOT NULL,
        last_source_user_id BIGINT(20) UNSIGNED NULL,
        last_source_email VARCHAR(190) NULL,
        PRIMARY KEY (server_key, receiver_value, site_value, talkgroup_value),
        KEY server_last_seen (server_key, last_seen_utc),
        KEY server_receiver_site (server_key, receiver_value, site_value)
    ) {$charset_collate};";

    $sql_observed_reports = "CREATE TABLE {$observed_reports_tbl} (
        server_key VARCHAR(255) NOT NULL,
        device_id_hash CHAR(64) NOT NULL,
        last_reported_at_utc DATETIME NOT NULL,
        PRIMARY KEY (server_key, device_id_hash),
        KEY last_reported_at_utc (last_reported_at_utc)
    ) {$charset_collate};";

    $sql_observed_triples_v2 = "CREATE TABLE {$observed_v2_tbl} (
        server_key VARCHAR(255) NOT NULL,
        receiver_value VARCHAR(128) NOT NULL,
        receiver_label VARCHAR(255) NOT NULL,
        site_value VARCHAR(128) NOT NULL,
        site_label VARCHAR(255) NOT NULL,
        talkgroup_value VARCHAR(128) NOT NULL,
        talkgroup_label VARCHAR(255) NOT NULL,
        seen_count BIGINT(20) UNSIGNED NOT NULL DEFAULT 0,
        first_seen_utc DATETIME NOT NULL,
        last_seen_utc DATETIME NOT NULL,
        last_source_device_hash CHAR(64) NOT NULL,
        last_source_user_id BIGINT(20) UNSIGNED NULL,
        last_source_email VARCHAR(190) NULL,
        PRIMARY KEY (server_key, receiver_value, site_value, talkgroup_value),
        KEY server_last_seen (server_key, last_seen_utc),
        KEY server_receiver_site (server_key, receiver_value, site_value)
    ) {$charset_collate};";

    $sql_observed_reports_v2 = "CREATE TABLE {$observed_v2_reports_tbl} (
        server_key VARCHAR(255) NOT NULL,
        device_id_hash CHAR(64) NOT NULL,
        last_reported_at_utc DATETIME NOT NULL,
        PRIMARY KEY (server_key, device_id_hash),
        KEY last_reported_at_utc (last_reported_at_utc)
    ) {$charset_collate};";

    dbDelta( $sql_events );
    dbDelta( $sql_sessions );
    dbDelta( $sql_devices );
    dbDelta( $sql_blocked_ips );
    dbDelta( $sql_device_settings );
    dbDelta( $sql_app_events );
    dbDelta( $sql_comms_messages );
    dbDelta( $sql_comms_changes );
    dbDelta( $sql_server_directory );
    dbDelta( $sql_lookups_cache );
    dbDelta( $sql_lookups_reports );
    dbDelta( $sql_observed_triples );
    dbDelta( $sql_observed_reports );
    dbDelta( $sql_observed_triples_v2 );
    dbDelta( $sql_observed_reports_v2 );
    joes_scanner_auth_favorites_install();

    joes_scanner_auth_ensure_devices_device_id_hash_unique();
    joes_scanner_auth_ensure_comms_heading_column();
    joes_scanner_auth_ensure_server_directory_sort_order_column();
    joes_scanner_auth_ensure_server_directory_firewall_columns();

    joes_scanner_auth_drop_sessions_user_device_unique_index();

    joes_scanner_auth_observed_triples_migrate_v1_to_v2();

    update_option( JOES_SCANNER_AUTH_DB_VERSION_KEY, JOES_SCANNER_AUTH_PLUGIN_DB_VERSION );

    if ( is_object( $wpdb ) && method_exists( $wpdb, 'suppress_errors' ) ) {
        $wpdb->suppress_errors( $prev_suppress );
    }
}


function joes_scanner_auth_ensure_server_directory_firewall_columns() {
    global $wpdb;

    if ( ! function_exists( 'joes_scanner_auth_table_server_directory' ) ) {
        return;
    }

    $tbl = joes_scanner_auth_table_server_directory();

    $cols = $wpdb->get_col( "SHOW COLUMNS FROM {$tbl}", 0 );
    if ( ! is_array( $cols ) ) {
        return;
    }

    if ( ! in_array( 'use_api_firewall_credentials', $cols, true ) ) {
        $wpdb->query( "ALTER TABLE {$tbl} ADD COLUMN use_api_firewall_credentials TINYINT(1) NOT NULL DEFAULT 0 AFTER is_official" );
    }

    if ( ! in_array( 'firewall_username', $cols, true ) ) {
        $wpdb->query( "ALTER TABLE {$tbl} ADD COLUMN firewall_username VARCHAR(190) NULL AFTER use_api_firewall_credentials" );
    }

    if ( ! in_array( 'firewall_password', $cols, true ) ) {
        $wpdb->query( "ALTER TABLE {$tbl} ADD COLUMN firewall_password TEXT NULL AFTER firewall_username" );
    }
}

function joes_scanner_auth_ensure_server_directory_sort_order_column() {
    global $wpdb;

    if ( ! function_exists( 'joes_scanner_auth_table_server_directory' ) ) {
        return;
    }

    $tbl = joes_scanner_auth_table_server_directory();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return;
    }

    $col = $wpdb->get_row( "SHOW COLUMNS FROM {$tbl} LIKE 'sort_order'" );
    $added = false;
    if ( ! $col ) {
        $wpdb->query( "ALTER TABLE {$tbl} ADD COLUMN sort_order INT NOT NULL DEFAULT 0" );
        $wpdb->query( "ALTER TABLE {$tbl} ADD KEY sort_order (sort_order)" );
        $added = true;
    }

    // Backfill sort_order for approved servers if nothing has been set yet.
    $nonzero = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(1) FROM {$tbl} WHERE status = %s AND sort_order <> 0", 'approved' ) );
    if ( $nonzero > 0 && ! $added ) {
        return;
    }

    $rows = $wpdb->get_results( $wpdb->prepare( "SELECT id FROM {$tbl} WHERE status = %s ORDER BY is_official DESC, server_name ASC", 'approved' ) );
    if ( empty( $rows ) ) {
        return;
    }

    $order = 10;
    foreach ( (array) $rows as $r ) {
        $id = (int) $r->id;
        if ( $id <= 0 ) {
            continue;
        }

        $wpdb->update(
            $tbl,
            array( 'sort_order' => $order ),
            array( 'id' => $id ),
            array( '%d' ),
            array( '%d' )
        );

        $order += 10;
    }
}
function joes_scanner_auth_ensure_comms_heading_column() {
    global $wpdb;

    $messages_tbl = joes_scanner_auth_table_comms_messages();
    if ( ! joes_scanner_auth_table_exists( $messages_tbl ) ) {
        return;
    }

    $col = $wpdb->get_row( "SHOW COLUMNS FROM {$messages_tbl} LIKE 'heading_text'" );
    if ( $col ) {
        return;
    }

    $wpdb->query( "ALTER TABLE {$messages_tbl} ADD COLUMN heading_text VARCHAR(120) NOT NULL DEFAULT ''" );
}

function joes_scanner_auth_ensure_devices_device_id_hash_unique() {
    global $wpdb;

    $devices_tbl = joes_scanner_auth_table_devices();
    if ( ! joes_scanner_auth_table_exists( $devices_tbl ) ) {
        return;
    }

    $idx_rows = $wpdb->get_results( "SHOW INDEX FROM {$devices_tbl} WHERE Key_name = 'device_id_hash'" );
    if ( empty( $idx_rows ) ) {
        // Index missing, attempt to add unique.
        $wpdb->query( "ALTER TABLE {$devices_tbl} ADD UNIQUE KEY device_id_hash (device_id_hash)" );
        return;
    }

    $non_unique = false;
    foreach ( (array) $idx_rows as $r ) {
        if ( isset( $r->Non_unique ) && (int) $r->Non_unique === 1 ) {
            $non_unique = true;
            break;
        }
    }

    if ( ! $non_unique ) {
        return;
    }

    // Replace non-unique index with unique.
    $wpdb->query( "ALTER TABLE {$devices_tbl} DROP INDEX device_id_hash" );
    $wpdb->query( "ALTER TABLE {$devices_tbl} ADD UNIQUE KEY device_id_hash (device_id_hash)" );
}

function joes_scanner_auth_on_activate() {
    $starting_level = function_exists( 'ob_get_level' ) ? ob_get_level() : 0;

    if ( function_exists( 'ob_start' ) ) {
        ob_start();
    }

    try {
        joes_scanner_auth_install_db();

        if ( ! wp_next_scheduled( JOES_SCANNER_AUTH_CRON_HOOK ) ) {
            wp_schedule_event( time() + 60, 'daily', JOES_SCANNER_AUTH_CRON_HOOK );
        joes_scanner_auth_ensure_backup_cron();

        }
    } finally {
        if ( function_exists( 'ob_get_level' ) && function_exists( 'ob_end_clean' ) ) {
            while ( ob_get_level() > $starting_level ) {
                ob_end_clean();
            }
        }
    }
}

function joes_scanner_auth_on_deactivate() {
    $ts = wp_next_scheduled( JOES_SCANNER_AUTH_CRON_HOOK );
    if ( $ts ) {
        wp_unschedule_event( $ts, JOES_SCANNER_AUTH_CRON_HOOK );
    }
    joes_scanner_auth_unschedule_backup_cron();

}

/**
 * Ensure DB is current.
 */
add_action( 'plugins_loaded', 'joes_scanner_auth_maybe_upgrade_db' );


function joes_scanner_auth_table_exists( $table_name ) {
    global $wpdb;
    if ( empty( $table_name ) ) {
        return false;
    }
    $found = $wpdb->get_var( $wpdb->prepare( "SHOW TABLES LIKE %s", $table_name ) );
    return ( $found === $table_name );
}

function joes_scanner_auth_comms_tables_exist() {
    return joes_scanner_auth_table_exists( joes_scanner_auth_table_comms_messages() )
        && joes_scanner_auth_table_exists( joes_scanner_auth_table_comms_changes() );
}

function joes_scanner_auth_maybe_upgrade_db() {
    $starting_level = function_exists( 'ob_get_level' ) ? ob_get_level() : 0;
    if ( function_exists( 'ob_start' ) ) { ob_start(); }

    try {
    $current = get_option( JOES_SCANNER_AUTH_DB_VERSION_KEY, '' );
    if ( $current !== JOES_SCANNER_AUTH_PLUGIN_DB_VERSION || ! joes_scanner_auth_comms_tables_exist() ) {
        joes_scanner_auth_install_db();
        return;
    }

    // Even if db version matches, ensure the unique index exists.
    joes_scanner_auth_ensure_user_device_unique_index();
    joes_scanner_auth_ensure_devices_device_id_hash_unique();
    joes_scanner_auth_ensure_comms_heading_column();
    joes_scanner_auth_ensure_device_hash_key();
    } finally {
        if ( function_exists( 'ob_get_level' ) && function_exists( 'ob_end_clean' ) ) {
            while ( ob_get_level() > $starting_level ) {
                ob_end_clean();
            }
        }
    }
}



add_action( 'admin_init', function () {
    // Periodic self healing sync in case SureCart changes do not trigger a WP post save event.
    if ( ! current_user_can( 'manage_options' ) ) {
        return;
    }

    // Be defensive. This has previously caused fatal errors during plugin activation if the
    // SureCart integration functions were not loaded for any reason.
    if ( ! function_exists( 'joes_scanner_auth_sync_allowed_prices_from_surecart' ) ) {
        return;
    }

    if ( ! function_exists( 'joes_scanner_auth_is_surecart_enabled' ) || ! joes_scanner_auth_is_surecart_enabled() ) {
        return;
    }

    $key = 'joes_scanner_auth_surecart_prices_sync';
    if ( get_transient( $key ) ) {
        return;
    }
    set_transient( $key, '1', 10 * MINUTE_IN_SECONDS );
    joes_scanner_auth_sync_allowed_prices_from_surecart( 'admin_periodic' );
} );

add_action( 'save_post_sc_product', function () {
    if ( function_exists( 'joes_scanner_auth_sync_allowed_prices_from_surecart' ) ) {
        joes_scanner_auth_sync_allowed_prices_from_surecart( 'sc_product_saved' );
    }
}, 10, 0 );

add_action( 'deleted_post', function ( $post_id ) {
    $post = get_post( $post_id );
    if ( $post && isset( $post->post_type ) && $post->post_type === 'sc_product' ) {
        if ( function_exists( 'joes_scanner_auth_sync_allowed_prices_from_surecart' ) ) {
            joes_scanner_auth_sync_allowed_prices_from_surecart( 'sc_product_deleted' );
        }
    }
}, 10, 1 );


// Current SureCart API lifecycle hooks. Price IDs may change when a price is
// edited/versioned, so refresh both discovery and the stable product mapping.
foreach ( array( 'surecart/price_created', 'surecart/price_updated', 'surecart/price_deleted' ) as $hook_name ) {
    add_action( $hook_name, function () use ( $hook_name ) {
        if ( function_exists( 'joes_scanner_auth_sync_allowed_prices_from_surecart' ) ) {
            joes_scanner_auth_sync_allowed_prices_from_surecart( $hook_name );
        }
    }, 10, 2 );
}

foreach ( array( 'surecart/product_created', 'surecart/product_updated', 'surecart/product_deleted' ) as $hook_name ) {
    add_action( $hook_name, function () use ( $hook_name ) {
        if ( function_exists( 'joes_scanner_auth_sync_allowed_prices_from_surecart' ) ) {
            joes_scanner_auth_sync_allowed_prices_from_surecart( $hook_name );
        }
    }, 10, 2 );
}



// Backup schedule and WP Cron integration.

function joes_scanner_auth_backup_default_schedule() {
    // Default to 2:00 AM site time to avoid unexpected immediate backups.
    $default_time_min = 120;
    return array(
        'enabled' => false,
        'repeat_every' => 1,
        'repeat_unit' => 'day',
        'repeat_on' => array(),
        'ends_mode' => 'never',
        'ends_on' => '',
        'ends_after' => 0,
        'keep_count' => 10,
        'anchor_local' => 0,
        'time_of_day_minutes' => $default_time_min,
        'next_run_utc' => 0,
        'last_run_utc' => 0,
        'occurrence_count' => 0,
    );
}

function joes_scanner_auth_backup_get_schedule() {
    $stored = get_option( JOES_SCANNER_AUTH_BACKUP_SCHEDULE_OPTION, array() );
    if ( ! is_array( $stored ) ) {
        $stored = array();
    }

    return array_merge( joes_scanner_auth_backup_default_schedule(), $stored );
}

function joes_scanner_auth_backup_save_schedule( $schedule ) {
    if ( ! is_array( $schedule ) ) {
        $schedule = array();
    }

    update_option( JOES_SCANNER_AUTH_BACKUP_SCHEDULE_OPTION, $schedule, false );
}

function joes_scanner_auth_add_minutely_cron_schedule( $schedules ) {
    if ( ! isset( $schedules['jsauth_minutely'] ) ) {
        $schedules['jsauth_minutely'] = array(
            'interval' => 60,
            'display' => 'Every minute',
        );
    }

    return $schedules;
}

add_filter( 'cron_schedules', 'joes_scanner_auth_add_minutely_cron_schedule' );

function joes_scanner_auth_ensure_backup_cron() {
    $schedule = joes_scanner_auth_backup_get_schedule();
    if ( empty( $schedule['enabled'] ) ) {
        joes_scanner_auth_unschedule_backup_cron();
        return;
    }

    if ( ! wp_next_scheduled( JOES_SCANNER_AUTH_BACKUP_CRON_HOOK ) ) {
        wp_schedule_event( time() + 60, 'jsauth_minutely', JOES_SCANNER_AUTH_BACKUP_CRON_HOOK );
    }
}

function joes_scanner_auth_unschedule_backup_cron() {
    $ts = wp_next_scheduled( JOES_SCANNER_AUTH_BACKUP_CRON_HOOK );
    if ( $ts ) {
        wp_unschedule_event( $ts, JOES_SCANNER_AUTH_BACKUP_CRON_HOOK );
    }
}

add_action( 'plugins_loaded', 'joes_scanner_auth_ensure_backup_cron' );

function joes_scanner_auth_backup_is_ipv6_string( $ip ) {
    if ( empty( $ip ) || ! is_string( $ip ) ) {
        return false;
    }

    // A simple check that avoids treating IPv4 as IPv6.
    return ( strpos( $ip, ':' ) !== false );
}

function joes_scanner_auth_backup_end_on_utc_timestamp( $ends_on ) {
    if ( empty( $ends_on ) || ! is_string( $ends_on ) ) {
        return 0;
    }

    $tz = function_exists( 'wp_timezone' ) ? wp_timezone() : new DateTimeZone( 'UTC' );

    try {
        $dt = new DateTimeImmutable( $ends_on . ' 23:59:59', $tz );
        return (int) $dt->setTimezone( new DateTimeZone( 'UTC' ) )->getTimestamp();
    } catch ( Exception $e ) {
        return 0;
    }
}

function joes_scanner_auth_backup_compute_next_run_utc( $schedule, $from_utc_ts ) {
    $tz = function_exists( 'wp_timezone' ) ? wp_timezone() : new DateTimeZone( 'UTC' );

    $repeat_every = isset( $schedule['repeat_every'] ) ? max( 1, (int) $schedule['repeat_every'] ) : 1;
    $repeat_unit  = isset( $schedule['repeat_unit'] ) ? (string) $schedule['repeat_unit'] : 'day';
    $repeat_on    = isset( $schedule['repeat_on'] ) && is_array( $schedule['repeat_on'] ) ? array_values( array_unique( array_map( 'intval', $schedule['repeat_on'] ) ) ) : array();
    $time_min     = isset( $schedule['time_of_day_minutes'] ) ? (int) $schedule['time_of_day_minutes'] : 0;

    $from_local = ( new DateTimeImmutable( '@' . (int) $from_utc_ts ) )->setTimezone( $tz );

    // Anchor is only used for weekly scheduling.
    $anchor_local_ts = isset( $schedule['anchor_local'] ) ? (int) $schedule['anchor_local'] : 0;
    if ( $anchor_local_ts <= 0 ) {
        $anchor_local_ts = (int) ( function_exists( 'current_time' ) ? current_time( 'timestamp' ) : time() );
    }
    $anchor_local = ( new DateTimeImmutable( '@' . $anchor_local_ts ) )->setTimezone( $tz );

    $hour = (int) floor( $time_min / 60 );
    $minute = (int) ( $time_min % 60 );

    if ( $repeat_unit === 'week' ) {
        if ( empty( $repeat_on ) ) {
            $repeat_on = array( (int) $anchor_local->format( 'w' ) );
        }

        // Search forward for the first valid candidate.
        $anchor_day = $anchor_local->setTime( 0, 0, 0 );
        $start_day  = $from_local->setTime( 0, 0, 0 );

        for ( $i = 0; $i < 370; $i++ ) {
            $day = $start_day->add( new DateInterval( 'P' . $i . 'D' ) );
            $candidate = $day->setTime( $hour, $minute, 0 );
            if ( $candidate <= $from_local ) {
                continue;
            }

            $dow = (int) $candidate->format( 'w' );
            if ( ! in_array( $dow, $repeat_on, true ) ) {
                continue;
            }

            $days_since_anchor = (int) floor( ( $day->getTimestamp() - $anchor_day->getTimestamp() ) / DAY_IN_SECONDS );
            $week_index = (int) floor( $days_since_anchor / 7 );
            if ( $week_index < 0 ) {
                $week_index = 0;
            }

            if ( ( $week_index % $repeat_every ) !== 0 ) {
                continue;
            }

            return (int) $candidate->setTimezone( new DateTimeZone( 'UTC' ) )->getTimestamp();
        }

        // Fallback.
        return (int) $from_local->add( new DateInterval( 'PT' . $repeat_every . 'H' ) )->setTimezone( new DateTimeZone( 'UTC' ) )->getTimestamp();
    }

    // For minute, hour, day, month, treat it as a simple interval from the last run.
    $base_utc = isset( $schedule['last_run_utc'] ) ? (int) $schedule['last_run_utc'] : 0;
    if ( $base_utc <= 0 ) {
        $base_utc = $from_utc_ts;
    }

    $base_local = ( new DateTimeImmutable( '@' . $base_utc ) )->setTimezone( $tz );

    if ( $repeat_unit === 'minute' ) {
        $next = $base_local->add( new DateInterval( 'PT' . $repeat_every . 'M' ) );
    } elseif ( $repeat_unit === 'hour' ) {
        $next = $base_local->add( new DateInterval( 'PT' . $repeat_every . 'H' ) );
    } elseif ( $repeat_unit === 'month' ) {
        $next = $base_local->add( new DateInterval( 'P' . $repeat_every . 'M' ) );
    } else {
        // day default.
        $next = $base_local->add( new DateInterval( 'P' . $repeat_every . 'D' ) );
    }

    // Keep the same time of day for day and month schedules.
    if ( $repeat_unit === 'day' || $repeat_unit === 'month' ) {
        $next = $next->setTime( $hour, $minute, 0 );
        if ( $next <= $from_local ) {
            $next = $next->add( new DateInterval( 'P1D' ) );
        }
    }

    return (int) $next->setTimezone( new DateTimeZone( 'UTC' ) )->getTimestamp();
}

function joes_scanner_auth_backup_should_stop( $schedule ) {
    $ends_mode = isset( $schedule['ends_mode'] ) ? (string) $schedule['ends_mode'] : 'never';

    if ( $ends_mode === 'after' ) {
        $limit = isset( $schedule['ends_after'] ) ? (int) $schedule['ends_after'] : 0;
        $count = isset( $schedule['occurrence_count'] ) ? (int) $schedule['occurrence_count'] : 0;
        return ( $limit > 0 && $count >= $limit );
    }

    if ( $ends_mode === 'on' ) {
        $end_utc = joes_scanner_auth_backup_end_on_utc_timestamp( isset( $schedule['ends_on'] ) ? (string) $schedule['ends_on'] : '' );
        $next_utc = isset( $schedule['next_run_utc'] ) ? (int) $schedule['next_run_utc'] : 0;
        return ( $end_utc > 0 && $next_utc > 0 && $next_utc > $end_utc );
    }

    return false;
}

function joes_scanner_auth_backup_prune_auto_files( $keep_count ) {
    if ( ! function_exists( 'joes_scanner_auth_list_backup_files' ) ) {
        return;
    }

    $keep_count = max( 1, (int) $keep_count );
    $files = joes_scanner_auth_list_backup_files();
    if ( ! $files ) {
        return;
    }

    $auto = array();
    foreach ( $files as $f ) {
        $name = isset( $f['name'] ) ? (string) $f['name'] : '';
        if ( strpos( $name, 'joes-scanner-auth-auto-' ) === 0 ) {
            $auto[] = $f;
        }
    }

    usort( $auto, function ( $a, $b ) {
        $am = isset( $a['mtime'] ) ? (int) $a['mtime'] : 0;
        $bm = isset( $b['mtime'] ) ? (int) $b['mtime'] : 0;
        return $bm <=> $am;
    } );

    if ( count( $auto ) <= $keep_count ) {
        return;
    }

    $to_delete = array_slice( $auto, $keep_count );
    foreach ( $to_delete as $f ) {
        $name = isset( $f['name'] ) ? (string) $f['name'] : '';
        if ( empty( $name ) ) {
            continue;
        }
        if ( function_exists( 'joes_scanner_auth_resolve_backup_path' ) ) {
            $path = joes_scanner_auth_resolve_backup_path( $name );
            if ( $path && file_exists( $path ) ) {
                @unlink( $path );
            }
        }
    }
}

function joes_scanner_auth_run_backup_tick() {
    $schedule = joes_scanner_auth_backup_get_schedule();
    if ( empty( $schedule['enabled'] ) ) {
        return;
    }

    $now_utc = time();

    // Bootstrap next run.
    if ( empty( $schedule['next_run_utc'] ) ) {
        $schedule['next_run_utc'] = joes_scanner_auth_backup_compute_next_run_utc( $schedule, $now_utc );
        joes_scanner_auth_backup_save_schedule( $schedule );
        return;
    }

    if ( $now_utc < (int) $schedule['next_run_utc'] ) {
        return;
    }

    // Respect ends mode.
    if ( joes_scanner_auth_backup_should_stop( $schedule ) ) {
        $schedule['enabled'] = false;
        joes_scanner_auth_backup_save_schedule( $schedule );
        joes_scanner_auth_unschedule_backup_cron();
        return;
    }

    if ( function_exists( 'joes_scanner_auth_create_backup_file' ) ) {
        $result = joes_scanner_auth_create_backup_file( 'joes-scanner-auth-auto' );
        // Ignore failure, still advance schedule so it does not spin.
    }

    $schedule['last_run_utc'] = $now_utc;
    $schedule['occurrence_count'] = isset( $schedule['occurrence_count'] ) ? ( (int) $schedule['occurrence_count'] + 1 ) : 1;

    // Prune old auto backups.
    $keep = isset( $schedule['keep_count'] ) ? (int) $schedule['keep_count'] : 10;
    joes_scanner_auth_backup_prune_auto_files( $keep );

    $schedule['next_run_utc'] = joes_scanner_auth_backup_compute_next_run_utc( $schedule, $now_utc );

    if ( joes_scanner_auth_backup_should_stop( $schedule ) ) {
        $schedule['enabled'] = false;
        joes_scanner_auth_unschedule_backup_cron();
    }

    joes_scanner_auth_backup_save_schedule( $schedule );
}

/*
//|--------------------------------------------------------------------------//| Server lookups cache helpers
//|--------------------------------------------------------------------------*/

function joes_scanner_auth_lookups_normalize_server_key( $server_key ) {
    $server_key = trim( (string) $server_key );
    if ( $server_key === '' ) {
        return '';
    }

    // Match app behavior: stable base URL without trailing slashes/spaces.
    $server_key = rtrim( $server_key, "/ \t\n\r\0\x0B" );
    return joes_scanner_auth_trim_text( $server_key, 255 ) ?: '';
}

function joes_scanner_auth_lookups_get_server_keys() {
    global $wpdb;

    $tbl = joes_scanner_auth_table_server_lookups_cache();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return array();
    }

    $rows = $wpdb->get_results( "SELECT server_key, updated_at_utc FROM {$tbl} ORDER BY updated_at_utc DESC" );
    $out  = array();
    foreach ( (array) $rows as $r ) {
        if ( ! isset( $r->server_key ) ) {
            continue;
        }
        $out[] = array(
            'server_key'     => (string) $r->server_key,
            'updated_at_utc' => isset( $r->updated_at_utc ) ? (string) $r->updated_at_utc : null,
        );
    }
    return $out;
}

function joes_scanner_auth_lookups_get_cache_row( $server_key ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    if ( $server_key === '' ) {
        return null;
    }

    $tbl = joes_scanner_auth_table_server_lookups_cache();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return null;
    }

    $row = $wpdb->get_row(
        $wpdb->prepare( "SELECT * FROM {$tbl} WHERE server_key = %s", $server_key ),
        ARRAY_A
    );

    return is_array( $row ) ? $row : null;
}



function joes_scanner_auth_lookups_touch_cache_row( $server_key, array $meta ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    if ( $server_key === '' ) {
        return false;
    }

    $tbl = joes_scanner_auth_table_server_lookups_cache();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return false;
    }

    // Only touch if the row already exists.
    $existing = $wpdb->get_var( $wpdb->prepare( "SELECT server_key FROM {$tbl} WHERE server_key = %s", $server_key ) );
    if ( ! $existing ) {
        return false;
    }

    $fields = array(
        'updated_at_utc'            => joes_scanner_auth_now_utc_mysql(),
        'updated_by_device_id_hash' => isset( $meta['device_id_hash'] ) ? (string) $meta['device_id_hash'] : null,
        'updated_by_user_id'        => isset( $meta['user_id'] ) ? (int) $meta['user_id'] : null,
        'updated_by_email'          => isset( $meta['email'] ) ? (string) $meta['email'] : null,
        'updated_by_ip'             => isset( $meta['ip'] ) ? (string) $meta['ip'] : null,
        'updated_by_user_agent'     => isset( $meta['user_agent'] ) ? (string) $meta['user_agent'] : null,
    );

    $formats = array( '%s', '%s', '%d', '%s', '%s', '%s' );

    $wpdb->update(
        $tbl,
        $fields,
        array( 'server_key' => $server_key ),
        $formats,
        array( '%s' )
    );

    return true;
}


function joes_scanner_auth_lookups_upsert_cache_row( $server_key, array $data, array $meta ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    if ( $server_key === '' ) {
        return false;
    }

    $tbl = joes_scanner_auth_table_server_lookups_cache();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return false;
    }

    $fields = array(
        'server_key'              => $server_key,
        'receivers_json'          => isset( $data['receivers_json'] ) ? (string) $data['receivers_json'] : null,
        'sites_json'              => isset( $data['sites_json'] ) ? (string) $data['sites_json'] : null,
        'talkgroups_json'         => isset( $data['talkgroups_json'] ) ? (string) $data['talkgroups_json'] : null,
        'talkgroup_groups_json'   => isset( $data['talkgroup_groups_json'] ) ? (string) $data['talkgroup_groups_json'] : null,
        'payload_hash'            => isset( $data['payload_hash'] ) ? (string) $data['payload_hash'] : null,
        'updated_at_utc'          => joes_scanner_auth_now_utc_mysql(),
        'updated_by_device_id_hash' => isset( $meta['device_id_hash'] ) ? (string) $meta['device_id_hash'] : null,
        'updated_by_user_id'      => isset( $meta['user_id'] ) ? (int) $meta['user_id'] : null,
        'updated_by_email'        => isset( $meta['email'] ) ? (string) $meta['email'] : null,
        'updated_by_ip'           => isset( $meta['ip'] ) ? (string) $meta['ip'] : null,
        'updated_by_user_agent'   => isset( $meta['user_agent'] ) ? (string) $meta['user_agent'] : null,
    );

    $formats = array( '%s', '%s', '%s', '%s', '%s', '%s', '%s', '%s', '%d', '%s', '%s', '%s' );

    $existing = $wpdb->get_var( $wpdb->prepare( "SELECT server_key FROM {$tbl} WHERE server_key = %s", $server_key ) );
    if ( $existing ) {
        $wpdb->update(
            $tbl,
            $fields,
            array( 'server_key' => $server_key ),
            $formats,
            array( '%s' )
        );
        return true;
    }

    $wpdb->insert( $tbl, $fields, $formats );
    return true;
}

function joes_scanner_auth_lookups_set_last_report_utc( $server_key, $device_id_hash ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );
    if ( $server_key === '' || ! $device_id_hash ) {
        return false;
    }

    $tbl = joes_scanner_auth_table_server_lookups_reports();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return false;
    }

    $now = joes_scanner_auth_now_utc_mysql();
    $wpdb->replace(
        $tbl,
        array(
            'server_key'           => $server_key,
            'device_id_hash'       => $device_id_hash,
            'last_reported_at_utc' => $now,
        ),
        array( '%s', '%s', '%s' )
    );

    return true;
}

function joes_scanner_auth_lookups_get_last_report_utc( $server_key, $device_id_hash ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );
    if ( $server_key === '' || ! $device_id_hash ) {
        return null;
    }

    $tbl = joes_scanner_auth_table_server_lookups_reports();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return null;
    }

    $val = $wpdb->get_var(
        $wpdb->prepare( "SELECT last_reported_at_utc FROM {$tbl} WHERE server_key = %s AND device_id_hash = %s", $server_key, $device_id_hash )
    );

    return $val ? (string) $val : null;
}

function joes_scanner_auth_lookups_clear_section( $server_key, $section ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    $section = sanitize_key( (string) $section );
    if ( $server_key === '' ) {
        return false;
    }

    $tbl = joes_scanner_auth_table_server_lookups_cache();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return false;
    }

    $fields = array();
    if ( $section === 'receivers' ) {
        $fields['receivers_json'] = null;
    } elseif ( $section === 'sites' ) {
        $fields['sites_json'] = null;
    } elseif ( $section === 'talkgroups' ) {
        $fields['talkgroups_json'] = null;
        $fields['talkgroup_groups_json'] = null;
    } elseif ( $section === 'all' ) {
        $fields['receivers_json'] = null;
        $fields['sites_json'] = null;
        $fields['talkgroups_json'] = null;
        $fields['talkgroup_groups_json'] = null;
    } else {
        return false;
    }

    $fields['updated_at_utc'] = joes_scanner_auth_now_utc_mysql();
    $wpdb->update( $tbl, $fields, array( 'server_key' => $server_key ) );

    return true;
}

//|--------------------------------------------------------------------------//| Observed triples helpers

function joes_scanner_auth_observed_triples_get_last_report_utc( $server_key, $device_id_hash ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );
    if ( $server_key === '' || ! $device_id_hash ) {
        return null;
    }

    $tbl = joes_scanner_auth_table_observed_triples_reports();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return null;
    }

    $val = $wpdb->get_var(
        $wpdb->prepare( "SELECT last_reported_at_utc FROM {$tbl} WHERE server_key = %s AND device_id_hash = %s", $server_key, $device_id_hash )
    );

    return $val ? (string) $val : null;
}

function joes_scanner_auth_observed_triples_set_last_report_utc( $server_key, $device_id_hash ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );
    if ( $server_key === '' || ! $device_id_hash ) {
        return false;
    }

    $tbl = joes_scanner_auth_table_observed_triples_reports();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return false;
    }

    $wpdb->replace(
        $tbl,
        array(
            'server_key'           => $server_key,
            'device_id_hash'       => $device_id_hash,
            'last_reported_at_utc' => joes_scanner_auth_now_utc_mysql(),
        ),
        array( '%s', '%s', '%s' )
    );

    return true;
}

function joes_scanner_auth_observed_triples_upsert_many( $server_key, array $items, array $meta ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    if ( $server_key === '' ) {
        return array( 'inserted' => 0, 'updated' => 0 );
    }

    $tbl = joes_scanner_auth_table_observed_triples();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return array( 'inserted' => 0, 'updated' => 0 );
    }

    $inserted = 0;
    $updated  = 0;

    foreach ( $items as $it ) {
        if ( ! is_array( $it ) ) {
            continue;
        }

        $rv_raw = isset( $it['receiver_value'] ) ? joes_scanner_auth_sanitize_device_string( (string) $it['receiver_value'], 128 ) : '';
        $sv_raw = isset( $it['site_value'] ) ? joes_scanner_auth_sanitize_device_string( (string) $it['site_value'], 128 ) : '';
        $tv_raw = isset( $it['talkgroup_value'] ) ? joes_scanner_auth_sanitize_device_string( (string) $it['talkgroup_value'], 128 ) : '';

        $rl = isset( $it['receiver_label'] ) ? sanitize_text_field( (string) $it['receiver_label'] ) : '';
        $sl = isset( $it['site_label'] ) ? sanitize_text_field( (string) $it['site_label'] ) : '';
        $tl = isset( $it['talkgroup_label'] ) ? sanitize_text_field( (string) $it['talkgroup_label'] ) : '';

        $rv = $rl !== '' ? joes_scanner_auth_sanitize_device_string( $rl, 128 ) : $rv_raw;
        $sv = $sl !== '' ? joes_scanner_auth_sanitize_device_string( $sl, 128 ) : $sv_raw;
        $tv = $tl !== '' ? joes_scanner_auth_sanitize_device_string( $tl, 128 ) : $tv_raw;

        if ( $rv === '' || $sv === '' || $tv === '' ) {
            continue;
        }
        if ( $rl === '' || $sl === '' || $tl === '' ) {
            continue;
        }

        $delta = 1;
        if ( isset( $it['seen_count_delta'] ) && is_numeric( $it['seen_count_delta'] ) ) {
            $delta = (int) $it['seen_count_delta'];
            if ( $delta < 1 ) {
                $delta = 1;
            }
        }

        $last_seen = null;
        if ( isset( $it['last_seen_utc'] ) && is_string( $it['last_seen_utc'] ) ) {
            $candidate = sanitize_text_field( $it['last_seen_utc'] );
            if ( $candidate ) {
                $last_seen = $candidate;
            }
        }
        if ( ! $last_seen ) {
            $last_seen = joes_scanner_auth_now_utc_mysql();
        }

        $device_hash = isset( $meta['device_id_hash'] ) ? (string) $meta['device_id_hash'] : '';
        $user_id     = isset( $meta['user_id'] ) ? (int) $meta['user_id'] : null;
        $email       = isset( $meta['email'] ) ? (string) $meta['email'] : null;

        $sql = "INSERT INTO {$tbl} (
            server_key, receiver_value, receiver_label, site_value, site_label, talkgroup_value, talkgroup_label,
            seen_count, first_seen_utc, last_seen_utc,
            last_source_device_hash, last_source_user_id, last_source_email
        ) VALUES (%s,%s,%s,%s,%s,%s,%s,%d,%s,%s,%s,%d,%s)
        ON DUPLICATE KEY UPDATE
            receiver_label = CASE
                WHEN receiver_label IS NULL OR receiver_label = '' THEN VALUES(receiver_label)
                WHEN VALUES(receiver_label) IS NULL OR VALUES(receiver_label) = '' THEN receiver_label
                WHEN CHAR_LENGTH(VALUES(receiver_label)) > CHAR_LENGTH(receiver_label) THEN VALUES(receiver_label)
                ELSE receiver_label
            END,
            site_label = CASE
                WHEN site_label IS NULL OR site_label = '' THEN VALUES(site_label)
                WHEN VALUES(site_label) IS NULL OR VALUES(site_label) = '' THEN site_label
                WHEN CHAR_LENGTH(VALUES(site_label)) > CHAR_LENGTH(site_label) THEN VALUES(site_label)
                ELSE site_label
            END,
            talkgroup_label = CASE
                WHEN talkgroup_label IS NULL OR talkgroup_label = '' THEN VALUES(talkgroup_label)
                WHEN VALUES(talkgroup_label) IS NULL OR VALUES(talkgroup_label) = '' THEN talkgroup_label
                WHEN CHAR_LENGTH(VALUES(talkgroup_label)) > CHAR_LENGTH(talkgroup_label) THEN VALUES(talkgroup_label)
                ELSE talkgroup_label
            END,
            seen_count = seen_count + VALUES(seen_count),
            last_seen_utc = GREATEST(last_seen_utc, VALUES(last_seen_utc)),
            last_source_device_hash = VALUES(last_source_device_hash),
            last_source_user_id = VALUES(last_source_user_id),
            last_source_email = VALUES(last_source_email)";

        $res = $wpdb->query(
            $wpdb->prepare(
                $sql,
                $server_key,
                $rv,
                $rl,
                $sv,
                $sl,
                $tv,
                $tl,
                $delta,
                joes_scanner_auth_now_utc_mysql(),
                $last_seen,
                $device_hash,
                $user_id ? $user_id : 0,
                $email
            )
        );

        if ( $res === false ) {
            continue;
        }

        // $wpdb->query returns 1 for insert, 2 for update in many cases.
        if ( (int) $wpdb->rows_affected === 1 ) {
            $inserted++;
        } else {
            $updated++;
        }
    }

    return array( 'inserted' => $inserted, 'updated' => $updated );
}

function joes_scanner_auth_observed_triples_get_rows( $server_key, $limit = 5000 ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    if ( $server_key === '' ) {
        return array();
    }

    $tbl = joes_scanner_auth_table_observed_triples();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return array();
    }

    $limit = (int) $limit;
    if ( $limit < 1 ) {
        $limit = 5000;
    }
    if ( $limit > 20000 ) {
        $limit = 20000;
    }

    // Order for admin display and deterministic export style: Receiver then Site then Talkgroup.
    $sql = $wpdb->prepare(
        "SELECT receiver_value, receiver_label, site_value, site_label, talkgroup_value, talkgroup_label, seen_count, first_seen_utc, last_seen_utc, last_source_device_hash, last_source_email
         FROM {$tbl}
         WHERE server_key = %s
         ORDER BY
            COALESCE(NULLIF(receiver_label, ''), receiver_value) ASC,
            COALESCE(NULLIF(site_label, ''), site_value) ASC,
            COALESCE(NULLIF(talkgroup_label, ''), talkgroup_value) ASC
         LIMIT %d",
        $server_key,
        $limit
    );

    $rows = $wpdb->get_results( $sql, ARRAY_A );
    return is_array( $rows ) ? $rows : array();
}

function joes_scanner_auth_observed_triples_get_server_keys() {
    global $wpdb;

    $tbl = joes_scanner_auth_table_observed_triples();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return array();
    }

    $rows = $wpdb->get_results( "SELECT server_key, MAX(last_seen_utc) AS newest_last_seen_utc FROM {$tbl} GROUP BY server_key ORDER BY newest_last_seen_utc DESC", ARRAY_A );
    return is_array( $rows ) ? $rows : array();
}

function joes_scanner_auth_observed_triples_clear_all( $server_key ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    if ( $server_key === '' ) {
        return false;
    }

    $tbl = joes_scanner_auth_table_observed_triples();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return false;
    }

    $wpdb->query( $wpdb->prepare( "DELETE FROM {$tbl} WHERE server_key = %s", $server_key ) );
    return true;
}


function joes_scanner_auth_observed_triples_v2_get_last_report_utc( $server_key, $device_id_hash ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );
    if ( $server_key === '' || ! $device_id_hash ) {
        return null;
    }

    $tbl = joes_scanner_auth_table_observed_triples_v2_reports();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return null;
    }

    $val = $wpdb->get_var(
        $wpdb->prepare( "SELECT last_reported_at_utc FROM {$tbl} WHERE server_key = %s AND device_id_hash = %s", $server_key, $device_id_hash )
    );

    return $val ? (string) $val : null;
}

function joes_scanner_auth_observed_triples_v2_set_last_report_utc( $server_key, $device_id_hash ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );
    if ( $server_key === '' || ! $device_id_hash ) {
        return false;
    }

    $tbl = joes_scanner_auth_table_observed_triples_v2_reports();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return false;
    }

    $wpdb->replace(
        $tbl,
        array(
            'server_key'           => $server_key,
            'device_id_hash'       => $device_id_hash,
            'last_reported_at_utc' => joes_scanner_auth_now_utc_mysql(),
        ),
        array( '%s', '%s', '%s' )
    );

    return true;
}

function joes_scanner_auth_observed_triples_v2_upsert_many( $server_key, array $items, array $meta ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    if ( $server_key === '' ) {
        return array( 'inserted' => 0, 'updated' => 0 );
    }

    $tbl = joes_scanner_auth_table_observed_triples_v2();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return array( 'inserted' => 0, 'updated' => 0 );
    }

    $inserted = 0;
    $updated  = 0;

    foreach ( $items as $it ) {
        if ( ! is_array( $it ) ) {
            continue;
        }

        $rv = isset( $it['receiver_value'] ) ? joes_scanner_auth_sanitize_device_string( (string) $it['receiver_value'], 128 ) : '';
        $sv = isset( $it['site_value'] ) ? joes_scanner_auth_sanitize_device_string( (string) $it['site_value'], 128 ) : '';
        $tv = isset( $it['talkgroup_value'] ) ? joes_scanner_auth_sanitize_device_string( (string) $it['talkgroup_value'], 128 ) : '';

        $rl = isset( $it['receiver_label'] ) ? sanitize_text_field( (string) $it['receiver_label'] ) : '';
        $sl = isset( $it['site_label'] ) ? sanitize_text_field( (string) $it['site_label'] ) : '';
        $tl = isset( $it['talkgroup_label'] ) ? sanitize_text_field( (string) $it['talkgroup_label'] ) : '';

        if ( $rv === '' || $sv === '' || $tv === '' ) {
            continue;
        }
        if ( $rl === '' || $sl === '' || $tl === '' ) {
            continue;
        }

        $delta = 1;
        if ( isset( $it['seen_count_delta'] ) && is_numeric( $it['seen_count_delta'] ) ) {
            $delta = (int) $it['seen_count_delta'];
            if ( $delta < 1 ) {
                $delta = 1;
            }
        }

        $last_seen = null;
        if ( isset( $it['last_seen_utc'] ) && is_string( $it['last_seen_utc'] ) ) {
            $candidate = sanitize_text_field( $it['last_seen_utc'] );
            if ( $candidate ) {
                $last_seen = $candidate;
            }
        }
        if ( ! $last_seen ) {
            $last_seen = joes_scanner_auth_now_utc_mysql();
        }

        $device_hash = isset( $meta['device_id_hash'] ) ? (string) $meta['device_id_hash'] : '';
        $user_id     = isset( $meta['user_id'] ) ? (int) $meta['user_id'] : null;
        $email       = isset( $meta['email'] ) ? (string) $meta['email'] : null;

        $sql = "INSERT INTO {$tbl} (
            server_key, receiver_value, receiver_label, site_value, site_label, talkgroup_value, talkgroup_label,
            seen_count, first_seen_utc, last_seen_utc,
            last_source_device_hash, last_source_user_id, last_source_email
        ) VALUES (%s,%s,%s,%s,%s,%s,%s,%d,%s,%s,%s,%d,%s)
        ON DUPLICATE KEY UPDATE
            receiver_label = CASE
                WHEN receiver_label IS NULL OR receiver_label = '' THEN VALUES(receiver_label)
                WHEN VALUES(receiver_label) IS NULL OR VALUES(receiver_label) = '' THEN receiver_label
                WHEN CHAR_LENGTH(VALUES(receiver_label)) > CHAR_LENGTH(receiver_label) THEN VALUES(receiver_label)
                ELSE receiver_label
            END,
            site_label = CASE
                WHEN site_label IS NULL OR site_label = '' THEN VALUES(site_label)
                WHEN VALUES(site_label) IS NULL OR VALUES(site_label) = '' THEN site_label
                WHEN CHAR_LENGTH(VALUES(site_label)) > CHAR_LENGTH(site_label) THEN VALUES(site_label)
                ELSE site_label
            END,
            talkgroup_label = CASE
                WHEN talkgroup_label IS NULL OR talkgroup_label = '' THEN VALUES(talkgroup_label)
                WHEN VALUES(talkgroup_label) IS NULL OR VALUES(talkgroup_label) = '' THEN talkgroup_label
                WHEN CHAR_LENGTH(VALUES(talkgroup_label)) > CHAR_LENGTH(talkgroup_label) THEN VALUES(talkgroup_label)
                ELSE talkgroup_label
            END,
            seen_count = seen_count + VALUES(seen_count),
            last_seen_utc = GREATEST(last_seen_utc, VALUES(last_seen_utc)),
            last_source_device_hash = VALUES(last_source_device_hash),
            last_source_user_id = VALUES(last_source_user_id),
            last_source_email = VALUES(last_source_email)";

        $res = $wpdb->query(
            $wpdb->prepare(
                $sql,
                $server_key,
                $rv,
                $rl,
                $sv,
                $sl,
                $tv,
                $tl,
                $delta,
                joes_scanner_auth_now_utc_mysql(),
                $last_seen,
                $device_hash,
                $user_id ? $user_id : 0,
                $email
            )
        );

        if ( $res === false ) {
            continue;
        }

        // $wpdb->query returns 1 for insert, 2 for update in many cases.
        if ( (int) $wpdb->rows_affected === 1 ) {
            $inserted++;
        } else {
            $updated++;
        }
    }

    return array( 'inserted' => $inserted, 'updated' => $updated );
}

function joes_scanner_auth_observed_triples_v2_get_rows( $server_key, $limit = 5000 ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    if ( $server_key === '' ) {
        return array();
    }

    $tbl = joes_scanner_auth_table_observed_triples_v2();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return array();
    }

    $limit = (int) $limit;
    if ( $limit < 1 ) {
        $limit = 5000;
    }
    if ( $limit > 20000 ) {
        $limit = 20000;
    }

    // Order for admin display and deterministic export style: Receiver then Site then Talkgroup.
    $sql = $wpdb->prepare(
        "SELECT receiver_value, receiver_label, site_value, site_label, talkgroup_value, talkgroup_label, seen_count, first_seen_utc, last_seen_utc, last_source_device_hash, last_source_email
         FROM {$tbl}
         WHERE server_key = %s
         ORDER BY
            COALESCE(NULLIF(receiver_label, ''), receiver_value) ASC,
            COALESCE(NULLIF(site_label, ''), site_value) ASC,
            COALESCE(NULLIF(talkgroup_label, ''), talkgroup_value) ASC
         LIMIT %d",
        $server_key,
        $limit
    );

    $rows = $wpdb->get_results( $sql, ARRAY_A );
    return is_array( $rows ) ? $rows : array();
}

function joes_scanner_auth_observed_triples_v2_get_server_keys() {
    global $wpdb;

    $tbl = joes_scanner_auth_table_observed_triples_v2();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return array();
    }

    $rows = $wpdb->get_results( "SELECT server_key, MAX(last_seen_utc) AS newest_last_seen_utc FROM {$tbl} GROUP BY server_key ORDER BY newest_last_seen_utc DESC", ARRAY_A );
    return is_array( $rows ) ? $rows : array();
}

function joes_scanner_auth_observed_triples_v2_clear_all( $server_key ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    if ( $server_key === '' ) {
        return false;
    }

    $tbl = joes_scanner_auth_table_observed_triples_v2();
    if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
        return false;
    }

    $wpdb->query( $wpdb->prepare( "DELETE FROM {$tbl} WHERE server_key = %s", $server_key ) );
    return true;
}


function joes_scanner_auth_observed_triples_migrate_v1_to_v2( $server_key = '' ) {
    global $wpdb;

    $source_tbl = joes_scanner_auth_table_observed_triples();
    $target_tbl = joes_scanner_auth_table_observed_triples_v2();

    if ( ! joes_scanner_auth_table_exists( $source_tbl ) || ! joes_scanner_auth_table_exists( $target_tbl ) ) {
        return array( 'migrated_servers' => 0, 'migrated_rows' => 0 );
    }

    $where = '';
    $params = array();

    $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );
    if ( $server_key !== '' ) {
        $where = 'WHERE server_key = %s';
        $params[] = $server_key;
    }

    $sql = "SELECT server_key, receiver_value, receiver_label, site_value, site_label, talkgroup_value, talkgroup_label, seen_count, first_seen_utc, last_seen_utc, last_source_device_hash, last_source_user_id, last_source_email FROM {$source_tbl} {$where} ORDER BY server_key ASC, id ASC";
    if ( ! empty( $params ) ) {
        $rows = $wpdb->get_results( $wpdb->prepare( $sql, $params ), ARRAY_A );
    } else {
        $rows = $wpdb->get_results( $sql, ARRAY_A );
    }

    if ( ! is_array( $rows ) || empty( $rows ) ) {
        return array( 'migrated_servers' => 0, 'migrated_rows' => 0 );
    }

    $items_by_server = array();
    foreach ( $rows as $row ) {
        if ( ! is_array( $row ) ) {
            continue;
        }

        $k = isset( $row['server_key'] ) ? joes_scanner_auth_lookups_normalize_server_key( (string) $row['server_key'] ) : '';
        if ( $k === '' ) {
            continue;
        }

        $items_by_server[ $k ][] = array(
            'receiver_value'  => isset( $row['receiver_value'] ) ? (string) $row['receiver_value'] : '',
            'receiver_label'  => isset( $row['receiver_label'] ) ? (string) $row['receiver_label'] : '',
            'site_value'      => isset( $row['site_value'] ) ? (string) $row['site_value'] : '',
            'site_label'      => isset( $row['site_label'] ) ? (string) $row['site_label'] : '',
            'talkgroup_value' => isset( $row['talkgroup_value'] ) ? (string) $row['talkgroup_value'] : '',
            'talkgroup_label' => isset( $row['talkgroup_label'] ) ? (string) $row['talkgroup_label'] : '',
            'seen_count_delta'=> isset( $row['seen_count'] ) && is_numeric( $row['seen_count'] ) ? max( 1, (int) $row['seen_count'] ) : 1,
            'last_seen_utc'   => isset( $row['last_seen_utc'] ) ? (string) $row['last_seen_utc'] : null,
        );
    }

    $migrated_servers = 0;
    $migrated_rows = 0;
    foreach ( $items_by_server as $k => $items ) {
        $res = joes_scanner_auth_observed_triples_v2_upsert_many(
            $k,
            $items,
            array(
                'device_id_hash' => '',
                'user_id' => null,
                'email' => null,
            )
        );
        $migrated_servers++;
        $migrated_rows += isset( $res['inserted'] ) ? (int) $res['inserted'] : 0;
        $migrated_rows += isset( $res['updated'] ) ? (int) $res['updated'] : 0;
    }

    return array( 'migrated_servers' => $migrated_servers, 'migrated_rows' => $migrated_rows );
}

function joes_scanner_auth_observed_triples_delete_row( $server_key, $receiver_value, $site_value, $talkgroup_value ) {
    global $wpdb;

    $server_key = joes_scanner_auth_lookups_normalize_server_key( (string) $server_key );
    if ( $server_key === '' ) {
        return;
    }

    $table = joes_scanner_auth_table_observed_triples();

    $wpdb->delete(
        $table,
        array(
            'server_key'      => $server_key,
            'receiver_value'  => (string) $receiver_value,
            'site_value'      => (string) $site_value,
            'talkgroup_value' => (string) $talkgroup_value,
        ),
        array( '%s', '%s', '%s', '%s' )
    );
}


add_action( JOES_SCANNER_AUTH_BACKUP_CRON_HOOK, 'joes_scanner_auth_run_backup_tick' );

// Returns the most recent lookups report time for a server across all devices.
if ( ! function_exists( 'joes_scanner_auth_lookups_get_last_report_utc_for_server' ) ) {
    function joes_scanner_auth_lookups_get_last_report_utc_for_server( $server_key ) {
        global $wpdb;

        $server_key = joes_scanner_auth_lookups_normalize_server_key( (string) $server_key );
        if ( $server_key === '' ) {
            return null;
        }

        $tbl = joes_scanner_auth_table_server_lookups_reports();
        if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
            return null;
        }

        // phpcs:ignore WordPress.DB.DirectDatabaseQuery.DirectQuery, WordPress.DB.DirectDatabaseQuery.NoCaching
        $val = $wpdb->get_var( $wpdb->prepare( "SELECT MAX(last_reported_at_utc) FROM {$tbl} WHERE server_key = %s", $server_key ) );
        return $val ? (string) $val : null;
    }
}

// Returns the most recent observed-triples report time for a server across all devices.
if ( ! function_exists( 'joes_scanner_auth_observed_triples_get_last_report_utc_for_server' ) ) {
    function joes_scanner_auth_observed_triples_get_last_report_utc_for_server( $server_key ) {
        global $wpdb;

        $server_key = joes_scanner_auth_lookups_normalize_server_key( (string) $server_key );
        if ( $server_key === '' ) {
            return null;
        }

        $tbl = joes_scanner_auth_table_observed_triples_reports();
        if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
            return null;
        }

        // phpcs:ignore WordPress.DB.DirectDatabaseQuery.DirectQuery, WordPress.DB.DirectDatabaseQuery.NoCaching
        $val = $wpdb->get_var( $wpdb->prepare( "SELECT MAX(last_reported_at_utc) FROM {$tbl} WHERE server_key = %s", $server_key ) );
        return $val ? (string) $val : null;
    }
}


if ( ! function_exists( 'joes_scanner_auth_observed_triples_v2_get_last_report_utc_for_server' ) ) {
    function joes_scanner_auth_observed_triples_v2_get_last_report_utc_for_server( $server_key ) {
        global $wpdb;

        $server_key = joes_scanner_auth_lookups_normalize_server_key( (string) $server_key );
        if ( $server_key === '' ) {
            return null;
        }

        $tbl = joes_scanner_auth_table_observed_triples_v2_reports();
        if ( ! joes_scanner_auth_table_exists( $tbl ) ) {
            return null;
        }

        $val = $wpdb->get_var( $wpdb->prepare( "SELECT MAX(last_reported_at_utc) FROM {$tbl} WHERE server_key = %s", $server_key ) );
        return $val ? (string) $val : null;
    }
}
