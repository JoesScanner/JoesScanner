<?php
/**
 * Plugin Name: Joe's Scanner Auth API
 * Description: REST endpoint and admin settings for authenticating Joe's Scanner app users against WordPress + SureCart subscriptions, with connection tracking and reporting.
 * Version: 2.7.4
 * Author: Joe's Scanner
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}



// Refactored include loader
require_once __DIR__ . '/includes/constants.php';
require_once __DIR__ . '/includes/options.php';
require_once __DIR__ . '/includes/database.php';
require_once __DIR__ . '/includes/tracking.php';
require_once __DIR__ . '/includes/admin.php';
require_once __DIR__ . '/includes/comms.php';
require_once __DIR__ . '/includes/server-directory.php';
require_once __DIR__ . '/includes/rest.php';
require_once __DIR__ . '/includes/favorites.php';

// Activation and deactivation hooks must be registered from the main plugin file
register_activation_hook( __FILE__, 'joes_scanner_auth_on_activate' );
register_deactivation_hook( __FILE__, 'joes_scanner_auth_on_deactivate' );
