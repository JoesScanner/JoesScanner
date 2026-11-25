<?php
// This file is mounted as a must-use plugin in the disposable test site only.
if ( ! defined( 'JOES_SCANNER_FAVORITES_INTEGRATION' ) || JOES_SCANNER_FAVORITES_INTEGRATION !== true ) {
    return;
}

add_filter( 'pre_wp_mail', static function ( $sent, $message ) {
    if ( get_option( 'blogname' ) !== 'JoesScanner Favorites Integration' ) {
        return false;
    }
    $to = is_string( $message['to'] ) ? strtolower( $message['to'] ) : '';
    if ( ! preg_match( '/^[a-z0-9_+-]+@example\.invalid$/D', $to ) ) {
        return false;
    }
    if ( preg_match( '/^Your My Servers verification code is ([0-9]{8})\./', $message['message'], $matches ) ) {
        $inbox = (array) get_option( 'jsfav_test_mailbox', array() );
        $inbox[$to] = $matches[1];
        update_option( 'jsfav_test_mailbox', $inbox, false );
    }
    // Never send mail from the integration site.
    return true;
}, 10, 2 );
