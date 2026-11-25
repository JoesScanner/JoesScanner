<?php

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

function joes_scanner_auth_dir_cookie_name( $kind ) {
    return '__Host-js-directory-' . $kind . '-' . get_current_blog_id();
}

function joes_scanner_auth_dir_cookie( $kind ) {
    $value = $_COOKIE[joes_scanner_auth_dir_cookie_name( $kind )] ?? '';
    return is_string( $value ) && preg_match( '/^[a-f0-9]{64}$/D', $value ) ? $value : '';
}

function joes_scanner_auth_dir_set_cookie( $kind, $value, $expires ) {
    $name = joes_scanner_auth_dir_cookie_name( $kind );
    if ( ! is_ssl() || ! setcookie( $name, $value, array(
        'expires' => $expires,
        'path' => '/',
        'secure' => true,
        'httponly' => true,
        'samesite' => 'Lax',
    ) ) ) {
        return false;
    }
    $_COOKIE[$name] = $value;
    return true;
}

function joes_scanner_auth_dir_token_key( $purpose, $value ) {
    return hash_hmac( 'sha256', 'server-directory:' . $purpose . ':' . $value, wp_salt( 'auth' ) );
}

function joes_scanner_auth_dir_private_headers() {
    if ( ! defined( 'DONOTCACHEPAGE' ) ) {
        define( 'DONOTCACHEPAGE', true );
    }
    nocache_headers();
    header( 'Cache-Control: private, no-store, max-age=0' );
}

function joes_scanner_auth_dir_browser() {
    $browser = joes_scanner_auth_dir_cookie( 'browser' );
    if ( $browser === '' && is_ssl() && ! headers_sent() ) {
        $value = bin2hex( random_bytes( 32 ) );
        if ( joes_scanner_auth_dir_set_cookie( 'browser', $value, time() + HOUR_IN_SECONDS ) ) {
            return $value;
        }
    }
    return $browser;
}

function joes_scanner_auth_dir_session_key() {
    $browser = joes_scanner_auth_dir_cookie( 'browser' );
    $session = joes_scanner_auth_dir_cookie( 'session' );
    return $browser && $session ? joes_scanner_auth_dir_token_key( 'session', $browser . $session ) : '';
}

function joes_scanner_auth_dir_actor() {
    $authenticated = get_current_user_id();
    if ( $authenticated ) {
        return $authenticated;
    }
    $key = joes_scanner_auth_dir_session_key();
    if ( ! is_ssl() || $key === '' ) {
        return 0;
    }
    global $wpdb;
    $table = joes_scanner_auth_dir_table( 'verification' );
    $id = (int) $wpdb->get_var( $wpdb->prepare(
        "SELECT user_id FROM {$table} WHERE token_key = %s AND expires_at_utc > %s",
        $key,
        gmdate( 'Y-m-d H:i:s' )
    ) );
    return $id && get_userdata( $id ) ? $id : 0;
}

function joes_scanner_auth_dir_nonce() {
    if ( is_user_logged_in() ) {
        return wp_create_nonce( 'joes_scanner_directory_owner' );
    }
    $browser = joes_scanner_auth_dir_browser();
    return $browser === '' ? '' : joes_scanner_auth_dir_token_key( 'form', $browser . joes_scanner_auth_dir_cookie( 'session' ) );
}

function joes_scanner_auth_dir_nonce_valid( $nonce ) {
    if ( is_user_logged_in() ) {
        return (bool) wp_verify_nonce( $nonce, 'joes_scanner_directory_owner' );
    }
    $expected = joes_scanner_auth_dir_nonce();
    return $expected !== '' && hash_equals( $expected, $nonce );
}

function joes_scanner_auth_dir_take_slot( $bucket, $value, $limit, $now ) {
    global $wpdb;
    $table = joes_scanner_auth_dir_table( 'verification' );
    $key = joes_scanner_auth_dir_token_key( $bucket, $value );
    $row = $wpdb->get_row( $wpdb->prepare( "SELECT attempts, expires_at_utc FROM {$table} WHERE token_key = %s", $key ) );
    if ( $wpdb->last_error ) {
        return joes_scanner_auth_dir_db_error();
    }
    $active = $row && $row->expires_at_utc > gmdate( 'Y-m-d H:i:s', $now );
    $count = $active ? (int) $row->attempts : 0;
    if ( $count >= $limit ) {
        return false;
    }
    $saved = $wpdb->replace( $table, array(
        'token_key'      => $key,
        'attempts'       => $count + 1,
        'expires_at_utc' => $active ? $row->expires_at_utc : gmdate( 'Y-m-d H:i:s', $now + 15 * MINUTE_IN_SECONDS ),
    ) );
    return $saved === false ? joes_scanner_auth_dir_db_error() : true;
}

function joes_scanner_auth_dir_throttle( $operation, $identity, $now ) {
    // Use the web server's client address. Forwarded headers are not trusted here.
    $ip = filter_var( $_SERVER['REMOTE_ADDR'] ?? '', FILTER_VALIDATE_IP ) ?: 'unknown';
    $limit = $operation === 'issue' ? array( 3, 30 ) : array( 10, 60 );
    $per_ip = joes_scanner_auth_dir_take_slot( $operation . '-ip', $ip, $limit[1], $now );
    if ( is_wp_error( $per_ip ) || ! $per_ip ) {
        return $per_ip;
    }
    return joes_scanner_auth_dir_take_slot( $operation . '-email', $identity, $limit[0], $now );
}

function joes_scanner_auth_dir_issue_code( $email, $browser ) {
    global $wpdb;
    $table = joes_scanner_auth_dir_table( 'verification' );
    $code = sprintf( '%08d', random_int( 0, 99999999 ) );
    $result = joes_scanner_auth_dir_write( static function () use ( $wpdb, $table, $email, $browser, $code ) {
        $now = time();
        $allowed = joes_scanner_auth_dir_throttle( 'issue', $email, $now );
        if ( is_wp_error( $allowed ) || ! $allowed ) {
            return $allowed;
        }
        if ( $wpdb->query( $wpdb->prepare( "DELETE FROM {$table} WHERE expires_at_utc <= %s", gmdate( 'Y-m-d H:i:s', $now ) ) ) === false ) {
            return joes_scanner_auth_dir_db_error();
        }
        $saved = $wpdb->replace( $table, array(
            'token_key'      => joes_scanner_auth_dir_token_key( 'challenge', $browser ),
            'code_hash'      => joes_scanner_auth_dir_token_key( 'code', $browser . $email . $code ),
            'email'          => $email,
            'expires_at_utc' => gmdate( 'Y-m-d H:i:s', $now + 10 * MINUTE_IN_SECONDS ),
        ) );
        return $saved === false ? joes_scanner_auth_dir_db_error() : true;
    } );
    if ( is_wp_error( $result ) ) {
        return $result;
    }
    if ( ! $result ) {
        return joes_scanner_auth_dir_error( 'Too many verification requests. Try again in 15 minutes.', 429 );
    }
    if ( ! wp_mail(
        $email,
        "Joe's Scanner directory verification code",
        'Your My Servers verification code is ' . $code . '. It expires in 10 minutes and can be used once in the browser that requested it.'
    ) ) {
        return joes_scanner_auth_dir_error( 'The verification email could not be sent. Please try again later.', 503 );
    }
    return true;
}

function joes_scanner_auth_dir_account_for_email( $email ) {
    global $wpdb;
    $ids = $wpdb->get_col( $wpdb->prepare( "SELECT ID FROM {$wpdb->users} WHERE user_email = %s LIMIT 2", $email ) );
    if ( $wpdb->last_error || count( $ids ) > 1 ) {
        return joes_scanner_auth_dir_db_error();
    }
    if ( $ids ) {
        return (int) $ids[0];
    }
    return wp_insert_user( array(
        'user_login' => 'directory_' . bin2hex( random_bytes( 12 ) ),
        'user_email' => $email,
        'user_pass'  => wp_generate_password( 32, true, true ),
        'role'       => 'subscriber',
    ) );
}

function joes_scanner_auth_dir_verify_code( $email, $code, $browser ) {
    global $wpdb;
    $table = joes_scanner_auth_dir_table( 'verification' );
    $challenge = joes_scanner_auth_dir_token_key( 'challenge', $browser );
    $session = bin2hex( random_bytes( 32 ) );
    $old_session = joes_scanner_auth_dir_session_key();
    $expires = time() + 30 * MINUTE_IN_SECONDS;
    $result = joes_scanner_auth_dir_write( static function () use ( $wpdb, $table, $email, $code, $browser, $challenge, $session, $old_session, $expires ) {
        $now = time();
        $time = gmdate( 'Y-m-d H:i:s', $now );
        $allowed = joes_scanner_auth_dir_throttle( 'verify', $email, $now );
        if ( is_wp_error( $allowed ) || ! $allowed ) {
            return is_wp_error( $allowed ) ? $allowed : 'limited';
        }
        $row = $wpdb->get_row( $wpdb->prepare( "SELECT * FROM {$table} WHERE token_key = %s", $challenge ) );
        if ( $wpdb->last_error ) {
            return joes_scanner_auth_dir_db_error();
        }
        if ( ! $row || $row->expires_at_utc <= $time ) {
            return 'invalid';
        }
        if ( (int) $row->attempts >= 5 ) {
            return 'limited';
        }
        if ( $email !== $row->email || ! hash_equals(
            $row->code_hash,
            joes_scanner_auth_dir_token_key( 'code', $browser . $email . $code )
        ) ) {
            $updated = $wpdb->update( $table, array( 'attempts' => (int) $row->attempts + 1 ), array( 'token_key' => $challenge ) );
            return $updated === false ? joes_scanner_auth_dir_db_error() : 'invalid';
        }
        $id = joes_scanner_auth_dir_account_for_email( $email );
        if ( is_wp_error( $id ) ) {
            return joes_scanner_auth_dir_db_error();
        }
        if ( $wpdb->delete( $table, array( 'token_key' => $challenge ) ) !== 1
            || ( $old_session && $wpdb->delete( $table, array( 'token_key' => $old_session ) ) === false )
            || $wpdb->insert( $table, array(
                'token_key' => joes_scanner_auth_dir_token_key( 'session', $browser . $session ),
                'user_id'   => $id,
                'expires_at_utc' => gmdate( 'Y-m-d H:i:s', $expires ),
            ) ) === false ) {
            return joes_scanner_auth_dir_db_error();
        }
        return 'verified';
    } );
    if ( is_wp_error( $result ) ) {
        return $result;
    }
    if ( $result === 'limited' ) {
        return joes_scanner_auth_dir_error( 'Too many verification attempts. Request a new code or try again later.', 429 );
    }
    if ( $result !== 'verified' ) {
        return joes_scanner_auth_dir_error( 'The verification code is invalid or expired.' );
    }
    if ( ! joes_scanner_auth_dir_set_cookie( 'session', $session, $expires ) ) {
        return joes_scanner_auth_dir_db_error();
    }
    return true;
}

function joes_scanner_auth_dir_end_session() {
    global $wpdb;
    $key = joes_scanner_auth_dir_session_key();
    if ( $key !== '' && $wpdb->delete( joes_scanner_auth_dir_table( 'verification' ), array( 'token_key' => $key ) ) === false ) {
        return joes_scanner_auth_dir_db_error();
    }
    joes_scanner_auth_dir_set_cookie( 'session', '', time() - HOUR_IN_SECONDS );
    return true;
}
