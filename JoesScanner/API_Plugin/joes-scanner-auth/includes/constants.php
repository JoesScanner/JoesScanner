<?php
/*
//|--------------------------------------------------------------------------//| Constants//|--------------------------------------------------------------------------*/

define( 'JOES_SCANNER_AUTH_PLUGIN_DB_VERSION', '1.5.10' );
if ( ! defined( 'JOES_SCANNER_AUTH_VERSION' ) ) {
    define( 'JOES_SCANNER_AUTH_VERSION', '2.7.5' );
}
define( 'JOES_SCANNER_AUTH_OPTIONS_KEY', 'joes_scanner_auth_options' );
define( 'JOES_SCANNER_AUTH_DB_VERSION_KEY', 'joes_scanner_auth_db_version' );
define( 'JOES_SCANNER_AUTH_CRON_HOOK', 'joes_scanner_auth_daily_maintenance' );
define( 'JOES_SCANNER_AUTH_DEVICE_HASH_KEY_OPTION', 'joes_scanner_auth_device_hash_key' );


define( 'JOES_SCANNER_AUTH_BACKUP_SCHEDULE_OPTION', 'joes_scanner_auth_backup_schedule_v1' );
define( 'JOES_SCANNER_AUTH_BACKUP_CRON_HOOK', 'joes_scanner_auth_backup_tick' );
