<?php
/*
//|--------------------------------------------------------------------------//| Server Directory (public submission + admin moderation)//|--------------------------------------------------------------------------*/

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

function joes_scanner_auth_normalize_url_for_compare( $url ) {
    $url = trim( (string) $url );
    if ( $url === '' ) {
        return '';
    }
    // Keep scheme/host case-insensitive; remove trailing slashes for stable comparisons.
    $url = rtrim( $url, "/ 	\n\r\0\x0B" );

    return $url;
}

// Shortcode: [joes_scanner_server_submit]
add_action( 'init', function () {
    add_shortcode( 'joes_scanner_server_submit', 'joes_scanner_auth_shortcode_server_submit' );
} );

// Logged in submission handler.
add_action( 'admin_post_joes_scanner_auth_submit_server', 'joes_scanner_auth_admin_post_submit_server' );

// Admin moderation actions.
if ( is_admin() ) {
    add_action( 'admin_post_joes_scanner_auth_review_server_directory', 'joes_scanner_auth_admin_post_review_server_directory' );
    add_action( 'admin_post_joes_scanner_auth_add_default_server', 'joes_scanner_auth_admin_post_add_default_server' );
    add_action( 'admin_post_joes_scanner_auth_update_server_directory_entry', 'joes_scanner_auth_admin_post_update_server_directory_entry' );
    add_action( 'wp_ajax_joes_scanner_auth_save_server_directory_order', 'joes_scanner_auth_ajax_save_server_directory_order' );
}


function joes_scanner_auth_server_directory_agreement_version() {
    return 'v1';
}

function joes_scanner_auth_server_directory_agreement_text() {
    return "I certify that I own this server or I am authorized by the owner to submit it for listing. I grant Joe’s Scanner permission to publicly display and distribute the information I submit, including the server name, server URL, info URL, area label (if provided), and map anchor, in a public server directory and to app users. I understand that users may access this server through third party apps and networks. I understand that submissions are reviewed and may be approved, rejected, disabled, or removed at any time at Joe’s Scanner’s discretion. I am responsible for operating and maintaining the server and any applicable policies. Joe’s Scanner does not guarantee traffic, uptime, or continued inclusion.";
}

function joes_scanner_auth_shortcode_server_submit() {
    if ( ! is_user_logged_in() ) {
        $login_url = wp_login_url( get_permalink() );
        $register_url = wp_registration_url();
        return '<div class="jsauth-server-submit"><p>You must be logged in to submit a server.</p><p><a href="' . esc_url( $login_url ) . '">Log in</a> | <a href="' . esc_url( $register_url ) . '">Create an account</a></p></div>';
    }

    $notice = isset( $_GET['jsauth_server_submitted'] ) ? sanitize_key( (string) $_GET['jsauth_server_submitted'] ) : '';
    $html = '';
    if ( $notice === '1' ) {
        $html .= '<div class="notice notice-success" style="padding:8px 12px;"><p>Server submitted. It will be reviewed before it is listed.</p></div>';
    }

    $admin_post_url = admin_url( 'admin-post.php' );

    $user = wp_get_current_user();

    $html .= '<form method="post" action="' . esc_url( $admin_post_url ) . '" class="jsauth-server-submit">';
    $html .= '<input type="hidden" name="action" value="joes_scanner_auth_submit_server" />';
    $html .= wp_nonce_field( 'joes_scanner_auth_submit_server', '_wpnonce', true, false );

    $html .= '<p><label><strong>Server name</strong> (required)<br />';
    $html .= '<input type="text" name="server_name" required style="width:100%;max-width:520px;" /></label><br />';
    $html .= '<small>The name that will be shown to users when selecting a server. Keep it short and recognizable.</small></p>';

    $html .= '<p><label><strong>Server URL</strong> (required)<br />';
    $html .= '<input type="url" name="server_url" required placeholder="https://example.com" style="width:100%;max-width:520px;" /></label><br />';
    $html .= '<small>The base URL your server is reachable at. Include http/https (required). Subdomains and custom ports work also</small></p>';

    $html .= '<p><label><strong>Map anchor</strong> (required)<br />';
    $html .= '<input type="text" name="map_anchor" required placeholder="Boise, ID" style="width:100%;max-width:520px;" /></label><br />';
    $html .= '<small>Used to improve address mapping. Examples: Boise, ID or Ada County, ID.</small></p>';

    $html .= '<p><label><strong>Info URL (optional)</strong><br />';
    $html .= '<input type="url" name="info_url" placeholder="https://example.com/info" style="width:100%;max-width:520px;" /></label><br />';
    $html .= '<small>Optional web page with details about this server (coverage, rules, contact, status). This link may be shown in the directory.</small></p>';

    $html .= '<p><label><strong>Area label (optional)</strong><br />';
    $html .= '<input type="text" name="area_label" placeholder="Treasure Valley" style="width:100%;max-width:520px;" /></label><br />';
    $html .= '<small>Optional region label used for sorting/filtering in the directory. Not shown as the server name in the app server list.</small></p>';

    $html .= '<p><label><strong>Account</strong><br />';
    $html .= '<input type="text" value="' . esc_attr( (string) $user->user_login ) . '" disabled style="width:100%;max-width:300px;background:#f6f7f7;" /></label><br />';
    $html .= '<small>This submission will be tied to your WordPress account.</small></p>';

    $html .= '<p><label><strong>Administrative contact email</strong><br />';
    $html .= '<input type="email" value="' . esc_attr( (string) $user->user_email ) . '" disabled style="width:100%;max-width:300px;background:#f6f7f7;" /></label><br />';
    $html .= '<small>This email is saved as the administrative contact for this listing.</small></p>';

    if ( joes_scanner_auth_server_directory_has_column( 'use_api_firewall_credentials' ) ) {
        $html .= '<p><label><input type="checkbox" name="use_api_firewall_credentials" value="1" /> <strong>Use API firewall credentials for this server</strong></label><br />';
        $html .= '<small>Used only when this server is accessed through the Joe\'s Scanner WordPress API integration.</small></p>';
    }

    if ( joes_scanner_auth_server_directory_has_column( 'firewall_username' ) ) {
        $html .= '<p><label><strong>Firewall username</strong> (optional)<br />';
        $html .= '<input type="text" name="firewall_username" style="width:100%;max-width:520px;" /></label><br />';
        $html .= '<small>Server connection credential used by the Joe\'s Scanner app only when this server is distributed through the Joe\'s Scanner WordPress API. Leave blank unless Joe has approved this server for API-based app access.</small></p>';
    }

    if ( joes_scanner_auth_server_directory_has_column( 'firewall_password' ) ) {
        $html .= '<p><label><strong>Firewall password</strong> (optional)<br />';
        $html .= '<input type="text" name="firewall_password" style="width:100%;max-width:520px;" /></label><br />';
        $html .= '<small>These are not the end-user login credentials. They are server connection credentials used only with the Joe\'s Scanner WordPress API. Contact Joe for access to the API.</small></p>';
    }

    $agreement_text = joes_scanner_auth_server_directory_agreement_text();
    $html .= '<p style="margin-top:14px;">';
    $html .= '<label><input type="checkbox" name="agreement_accept" value="1" required /> ';
    $html .= '<strong>I agree to the public listing terms for this submission.</strong></label><br />';
    $html .= '<small>' . esc_html( $agreement_text ) . '</small>';
    $html .= '</p>';
$html .= '<p><button type="submit">Submit server</button></p>';
    $html .= '</form>';

    return $html;
}

function joes_scanner_auth_admin_post_submit_server() {
    if ( ! is_user_logged_in() ) {
        wp_die( 'Not authorized.' );
    }

    $nonce = isset( $_POST['_wpnonce'] ) ? (string) $_POST['_wpnonce'] : '';
    if ( ! wp_verify_nonce( $nonce, 'joes_scanner_auth_submit_server' ) ) {
        wp_die( 'Security check failed.' );
    }

    $server_name = isset( $_POST['server_name'] ) ? sanitize_text_field( wp_unslash( (string) $_POST['server_name'] ) ) : '';
    $server_url  = isset( $_POST['server_url'] ) ? esc_url_raw( wp_unslash( (string) $_POST['server_url'] ) ) : '';
    $info_url    = isset( $_POST['info_url'] ) ? esc_url_raw( wp_unslash( (string) $_POST['info_url'] ) ) : '';
    $area_label  = isset( $_POST['area_label'] ) ? sanitize_text_field( wp_unslash( (string) $_POST['area_label'] ) ) : '';
    $map_anchor  = isset( $_POST['map_anchor'] ) ? sanitize_text_field( wp_unslash( (string) $_POST['map_anchor'] ) ) : '';
    $use_api_firewall_credentials = isset( $_POST['use_api_firewall_credentials'] ) && (string) $_POST['use_api_firewall_credentials'] === '1';
    $firewall_username = isset( $_POST['firewall_username'] ) ? sanitize_text_field( wp_unslash( (string) $_POST['firewall_username'] ) ) : '';
    $firewall_password = isset( $_POST['firewall_password'] ) ? (string) wp_unslash( $_POST['firewall_password'] ) : '';

    $server_name = trim( $server_name );
    $server_url  = trim( $server_url );
    $info_url    = trim( $info_url );
    $area_label  = trim( $area_label );
    $map_anchor  = trim( $map_anchor );
    $firewall_username = trim( $firewall_username );
    $firewall_password = trim( $firewall_password );


    $agreement_accept = isset( $_POST['agreement_accept'] ) ? sanitize_key( (string) $_POST['agreement_accept'] ) : '';
    if ( $agreement_accept !== '1' ) {
        wp_die( 'You must accept the public listing terms.' );
    }

    $agreement_version = joes_scanner_auth_server_directory_agreement_version();
    $agreement_text = joes_scanner_auth_server_directory_agreement_text();

    if ( $server_name === '' || $server_url === '' || $map_anchor === '' ) {
        wp_die( 'Missing required fields.' );
    }

    if ( ! preg_match( '#^https?://#i', $server_url ) ) {
        wp_die( 'Server URL must start with http:// or https://.' );
    }

    if ( $info_url !== '' && ! preg_match( '#^https?://#i', $info_url ) ) {
        wp_die( 'Info URL must start with http:// or https://.' );
    }

    $user = wp_get_current_user();
    $ip = isset( $_SERVER['REMOTE_ADDR'] ) ? sanitize_text_field( (string) $_SERVER['REMOTE_ADDR'] ) : '';

    global $wpdb;
    $tbl = function_exists( 'joes_scanner_auth_table_server_directory' ) ? joes_scanner_auth_table_server_directory() : '';
    if ( $tbl === '' ) {
        wp_die( 'Server directory table not available.' );
    }

    $now = gmdate( 'Y-m-d H:i:s' );

    $max_order = (int) $wpdb->get_var( $wpdb->prepare( "SELECT MAX(sort_order) FROM {$tbl} WHERE status = %s", 'approved' ) );
    $sort_order = ( $max_order > 0 ) ? ( $max_order + 10 ) : 10;

    $inserted = $wpdb->insert(
        $tbl,
        array(
            'created_at_utc'             => $now,
            'updated_at_utc'             => $now,
            'status'                     => 'pending',
            'server_name'                => $server_name,
            'server_url'                 => $server_url,
            'info_url'                   => ( $info_url !== '' ) ? $info_url : null,
            'area_label'                 => ( $area_label !== '' ) ? $area_label : null,
            'map_anchor'                 => $map_anchor,
            'use_api_firewall_credentials' => joes_scanner_auth_server_directory_has_column( 'use_api_firewall_credentials' ) ? ( $use_api_firewall_credentials ? 1 : 0 ) : null,
            'firewall_username'          => joes_scanner_auth_server_directory_has_column( 'firewall_username' ) ? ( $firewall_username !== '' ? $firewall_username : null ) : null,
            'firewall_password'          => joes_scanner_auth_server_directory_has_column( 'firewall_password' ) ? ( $firewall_password !== '' ? $firewall_password : null ) : null,
            'is_official'                => 0,
            'agreement_accepted'         => 1,
            'agreement_text_version'     => $agreement_version,
            'agreement_text_snapshot'    => $agreement_text,
            'agreement_accepted_at_utc'  => $now,
            'agreement_accepted_ip'      => ( $ip !== '' ) ? $ip : null,
            'agreement_accepted_user_id' => (int) $user->ID,
            'submitted_by_user_id'       => (int) $user->ID,
            'submitted_username'         => (string) $user->user_login,
            'submitted_email'            => (string) $user->user_email,
            'contact_user_id'         => (int) $user->ID,
            'contact_username'        => (string) $user->user_login,
            'contact_email'           => (string) $user->user_email,
            'submitted_ip'               => ( $ip !== '' ) ? $ip : null,
            'reviewed_by_user_id'        => null,
            'reviewed_at_utc'            => null,
            'review_notes'               => null,
            'reject_reason'              => null,
        )
    );
if ( ! $inserted ) {
        wp_die( 'Insert failed.' );
    }

    $id = (int) $wpdb->insert_id;
    joes_scanner_auth_notify_admin_server_submission( $id, array(
        'server_name' => $server_name,
        'server_url'  => $server_url,
        'info_url'    => $info_url,
        'area_label'  => $area_label,
        'map_anchor'  => $map_anchor,
        'submitted_by'=> $user->user_login,
        'submitted_email' => $user->user_email,
    ) );

    $redirect = wp_get_referer();
    if ( ! $redirect ) {
        $redirect = home_url( '/' );
    }
    $redirect = add_query_arg( 'jsauth_server_submitted', '1', $redirect );
    wp_safe_redirect( $redirect );
    exit;
}

function joes_scanner_auth_notify_admin_server_submission( $id, $data ) {
    $admin_email = get_option( 'admin_email' );
    if ( ! $admin_email ) {
        return;
    }

    $review_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=server_directory&status=pending&highlight_id=' . (int) $id );

    $subject = 'Joe\'s Scanner server submission pending approval';

    $lines = array();
    $lines[] = 'A server was submitted and is pending approval.';
    $lines[] = '';
    $lines[] = 'Server name: ' . ( isset( $data['server_name'] ) ? (string) $data['server_name'] : '' );
    $lines[] = 'Server URL: ' . ( isset( $data['server_url'] ) ? (string) $data['server_url'] : '' );
    $lines[] = 'Info URL: ' . ( isset( $data['info_url'] ) ? (string) $data['info_url'] : '' );
    $lines[] = 'Area label: ' . ( isset( $data['area_label'] ) ? (string) $data['area_label'] : '' );
    $lines[] = 'Map anchor: ' . ( isset( $data['map_anchor'] ) ? (string) $data['map_anchor'] : '' );
    $lines[] = '';
    $lines[] = 'Submitted by: ' . ( isset( $data['submitted_by'] ) ? (string) $data['submitted_by'] : '' ) . ' (' . ( isset( $data['submitted_email'] ) ? (string) $data['submitted_email'] : '' ) . ')';
    $lines[] = '';
    $lines[] = 'Review and approve:';
    $lines[] = $review_url;

    $body = implode( "\n", $lines );

    wp_mail( $admin_email, $subject, $body );
}

function joes_scanner_auth_notify_admin_server_added( $id, $data ) {
    $admin_email = get_option( 'admin_email' );
    if ( ! $admin_email ) {
        return;
    }

    $view_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=server_directory&status=all&highlight_id=' . (int) $id );

    // Use double-quotes to avoid breaking on the apostrophe in "Joe's".
    $subject = "Joe's Scanner server added to directory";

    $lines = array();
    $lines[] = 'A server was added to the server directory.';
    $lines[] = '';
    $lines[] = 'Server name: ' . ( isset( $data['server_name'] ) ? (string) $data['server_name'] : '' );
    $lines[] = 'Server URL: ' . ( isset( $data['server_url'] ) ? (string) $data['server_url'] : '' );
    $lines[] = 'Info URL: ' . ( isset( $data['info_url'] ) ? (string) $data['info_url'] : '' );
    $lines[] = 'Area label: ' . ( isset( $data['area_label'] ) ? (string) $data['area_label'] : '' );
    $lines[] = 'Map anchor: ' . ( isset( $data['map_anchor'] ) ? (string) $data['map_anchor'] : '' );
    $lines[] = 'Official: ' . ( ! empty( $data['is_official'] ) ? 'Yes' : 'No' );
    $lines[] = '';
    $lines[] = 'Added by: ' . ( isset( $data['submitted_by'] ) ? (string) $data['submitted_by'] : '' ) . ' (' . ( isset( $data['submitted_email'] ) ? (string) $data['submitted_email'] : '' ) . ')';
    $lines[] = '';
    $lines[] = 'View in admin:';
    $lines[] = $view_url;

    $body = implode( "\n", $lines );

    wp_mail( $admin_email, $subject, $body );
}


function joes_scanner_auth_admin_post_review_server_directory() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    $nonce = isset( $_POST['_wpnonce'] ) ? (string) $_POST['_wpnonce'] : '';
    if ( ! wp_verify_nonce( $nonce, 'joes_scanner_auth_server_directory_actions' ) ) {
        wp_die( 'Security check failed.' );
    }

    $id = isset( $_POST['id'] ) ? (int) $_POST['id'] : 0;
    $op = isset( $_POST['op'] ) ? sanitize_key( (string) $_POST['op'] ) : '';
    $note = isset( $_POST['note'] ) ? sanitize_text_field( wp_unslash( (string) $_POST['note'] ) ) : '';

    if ( $id <= 0 || ! in_array( $op, array( 'approve', 'reject', 'disable', 'enable', 'clear' ), true ) ) {
        wp_die( 'Invalid request.' );
    }

    $new_status = '';
    $reject_reason = null;
    $review_notes = null;

    global $wpdb;
    $tbl = joes_scanner_auth_table_server_directory();

    if ( $op === 'clear' ) {
        // Permanently delete the row from the directory.
        $wpdb->delete( $tbl, array( 'id' => $id ), array( '%d' ) );

        $redirect = wp_get_referer();
        if ( ! $redirect ) {
            $redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=server_directory' );
        }
        $redirect = add_query_arg( 'cleared', $id, $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    if ( $op === 'approve' || $op === 'enable' ) {
        $new_status = 'approved';
        $review_notes = $note !== '' ? $note : null;
    } elseif ( $op === 'disable' ) {
        $new_status = 'disabled';
        $review_notes = $note !== '' ? $note : null;
    } elseif ( $op === 'reject' ) {
        $new_status = 'rejected';
        $reject_reason = $note !== '' ? $note : 'Rejected';
    }

    $user = wp_get_current_user();

    $update_data = array(
        'status'              => $new_status,
        'updated_at_utc'      => gmdate( 'Y-m-d H:i:s' ),
        'reviewed_by_user_id' => (int) $user->ID,
        'reviewed_at_utc'     => gmdate( 'Y-m-d H:i:s' ),
        'review_notes'        => $review_notes,
        'reject_reason'       => $reject_reason,
    );

    $update_formats = array( '%s', '%s', '%d', '%s', '%s', '%s' );

    if ( $new_status === 'approved' ) {
        $current_order = (int) $wpdb->get_var( $wpdb->prepare( "SELECT sort_order FROM {$tbl} WHERE id = %d", $id ) );
        if ( $current_order === 0 ) {
            $max_order = (int) $wpdb->get_var( $wpdb->prepare( "SELECT MAX(sort_order) FROM {$tbl} WHERE status = %s", 'approved' ) );
            $update_data['sort_order'] = ( $max_order > 0 ) ? ( $max_order + 10 ) : 10;
            $update_formats[] = '%d';
        }
    }

    $wpdb->update(
        $tbl,
        $update_data,
        array( 'id' => $id ),
        $update_formats,
        array( '%d' )
    );
$redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=server_directory' );
    $redirect = add_query_arg( 'status', $new_status, $redirect );
    $redirect = add_query_arg( 'highlight_id', $id, $redirect );
    wp_safe_redirect( $redirect );
    exit;
}

function joes_scanner_auth_get_server_directory_rows( $status, $limit, $offset ) {
    global $wpdb;
    $tbl = joes_scanner_auth_table_server_directory();

    $status = (string) $status;
    $limit = max( 1, min( 200, (int) $limit ) );
    $offset = max( 0, (int) $offset );

    $where = '';
    $args = array();
    if ( $status !== '' && in_array( $status, array( 'pending', 'approved', 'rejected', 'disabled' ), true ) ) {
        $where = 'WHERE status = %s';
        $args[] = $status;
    }

    $order_by = 'created_at_utc DESC';
    if ( $status === 'approved' ) {
        $order_by = 'sort_order ASC, server_name ASC';
    }

    $sql = "SELECT * FROM {$tbl} {$where} ORDER BY {$order_by} LIMIT %d OFFSET %d";
    $args[] = $limit;
    $args[] = $offset;

    return $wpdb->get_results( $wpdb->prepare( $sql, $args ) );
}

function joes_scanner_auth_ajax_save_server_directory_order() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_send_json_error( array( 'error' => 'not_authorized' ), 403 );
    }

    $nonce = isset( $_POST['nonce'] ) ? (string) $_POST['nonce'] : '';
    if ( ! wp_verify_nonce( $nonce, 'joes_scanner_auth_save_server_directory_order' ) ) {
        wp_send_json_error( array( 'error' => 'bad_nonce' ), 400 );
    }

    $ids_raw = isset( $_POST['ids'] ) ? (string) $_POST['ids'] : '';
    $ids_raw = trim( $ids_raw );
    if ( $ids_raw === '' ) {
        wp_send_json_error( array( 'error' => 'missing_ids' ), 400 );
    }

    $ids = array();
    foreach ( explode( ',', $ids_raw ) as $part ) {
        $id = (int) trim( $part );
        if ( $id > 0 ) {
            $ids[] = $id;
        }
    }

    if ( empty( $ids ) ) {
        wp_send_json_error( array( 'error' => 'invalid_ids' ), 400 );
    }

    global $wpdb;
    $tbl = joes_scanner_auth_table_server_directory();

    $order = 10;
    foreach ( $ids as $id ) {
        $wpdb->update(
            $tbl,
            array(
                'sort_order' => $order,
                'updated_at_utc' => gmdate( 'Y-m-d H:i:s' ),
            ),
            array(
                'id' => $id,
                'status' => 'approved',
            ),
            array( '%d', '%s' ),
            array( '%d', '%s' )
        );

        $order += 10;
    }

    wp_send_json_success( array( 'ok' => true ) );
}

function joes_scanner_auth_get_server_directory_total( $status ) {
    global $wpdb;
    $tbl = joes_scanner_auth_table_server_directory();

    $status = (string) $status;
    if ( $status !== '' && in_array( $status, array( 'pending', 'approved', 'rejected', 'disabled' ), true ) ) {
        return (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(1) FROM {$tbl} WHERE status = %s", $status ) );
    }

    return (int) $wpdb->get_var( "SELECT COUNT(1) FROM {$tbl}" );
}


function joes_scanner_auth_server_directory_has_column( $column_name ) {
    static $cache = array();

    $column_name = sanitize_key( (string) $column_name );
    if ( $column_name === '' ) {
        return false;
    }

    if ( array_key_exists( $column_name, $cache ) ) {
        return (bool) $cache[ $column_name ];
    }

    global $wpdb;
    $tbl = joes_scanner_auth_table_server_directory();
    $sql = $wpdb->prepare( "SHOW COLUMNS FROM {$tbl} LIKE %s", $column_name );
    $cache[ $column_name ] = (bool) $wpdb->get_var( $sql );

    return (bool) $cache[ $column_name ];
}

function joes_scanner_auth_get_server_directory_row_by_id( $id ) {
    global $wpdb;
    $tbl = joes_scanner_auth_table_server_directory();

    return $wpdb->get_row( $wpdb->prepare( "SELECT * FROM {$tbl} WHERE id = %d LIMIT 1", (int) $id ) );
}

function joes_scanner_auth_admin_post_update_server_directory_entry() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    check_admin_referer( 'joes_scanner_auth_update_server_directory_entry' );

    $id          = isset( $_POST['id'] ) ? (int) $_POST['id'] : 0;
    $return_status = isset( $_POST['return_status'] ) ? sanitize_key( (string) wp_unslash( $_POST['return_status'] ) ) : 'approved';
    if ( ! in_array( $return_status, array( 'approved', 'all', 'pending', 'disabled' ), true ) ) {
        $return_status = 'approved';
    }
    $server_name = isset( $_POST['server_name'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['server_name'] ) ) : '';
    $server_url  = isset( $_POST['server_url'] ) ? esc_url_raw( (string) wp_unslash( $_POST['server_url'] ) ) : '';
    $info_url    = isset( $_POST['info_url'] ) ? esc_url_raw( (string) wp_unslash( $_POST['info_url'] ) ) : '';
    $area_label  = isset( $_POST['area_label'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['area_label'] ) ) : '';
    $map_anchor  = isset( $_POST['map_anchor'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['map_anchor'] ) ) : '';
    $use_api_firewall_credentials = isset( $_POST['use_api_firewall_credentials'] ) && (string) $_POST['use_api_firewall_credentials'] === '1';
    $firewall_username = isset( $_POST['firewall_username'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['firewall_username'] ) ) : '';
    $firewall_password = isset( $_POST['firewall_password'] ) ? (string) wp_unslash( $_POST['firewall_password'] ) : '';
    $is_official = isset( $_POST['is_official'] ) && (string) $_POST['is_official'] === '1';

    $server_name = trim( $server_name );
    $server_url  = joes_scanner_auth_normalize_url_for_compare( $server_url );
    $info_url    = trim( $info_url );
    $area_label  = trim( $area_label );
    $map_anchor  = trim( $map_anchor );

    if ( $id <= 0 || $server_name === '' || $server_url === '' || $map_anchor === '' ) {
        wp_die( 'Missing required fields.' );
    }

    if ( ! preg_match( '#^https?://#i', $server_url ) ) {
        wp_die( 'Server URL must start with http:// or https://.' );
    }

    if ( $info_url !== '' && ! preg_match( '#^https?://#i', $info_url ) ) {
        wp_die( 'Info URL must start with http:// or https://.' );
    }

    global $wpdb;
    $tbl = joes_scanner_auth_table_server_directory();
    $existing = joes_scanner_auth_get_server_directory_row_by_id( $id );
    if ( ! $existing ) {
        wp_die( 'Server not found.' );
    }

    $duplicate_id = (int) $wpdb->get_var(
        $wpdb->prepare(
            "SELECT id FROM {$tbl} WHERE TRIM(TRAILING '/' FROM server_url) = %s AND id <> %d LIMIT 1",
            rtrim( $server_url, '/' ),
            $id
        )
    );
    if ( $duplicate_id > 0 ) {
        $redir = add_query_arg(
            array(
                'page'              => 'joes-scanner-auth',
                'tab'               => 'server_directory',
                'status'            => $return_status,
                'edit_id'           => $id,
                'highlight_id'      => $duplicate_id,
                'update_server_error' => 'duplicate_url',
            ),
            admin_url( 'options-general.php' )
        );
        wp_safe_redirect( $redir );
        exit;
    }

    $data = array(
        'updated_at_utc' => gmdate( 'Y-m-d H:i:s' ),
        'server_name'    => $server_name,
        'server_url'     => $server_url,
        'info_url'       => ( $info_url !== '' ) ? $info_url : null,
        'area_label'     => ( $area_label !== '' ) ? $area_label : null,
        'map_anchor'     => $map_anchor,
        'is_official'    => $is_official ? 1 : 0,
    );
    $format = array( '%s', '%s', '%s', '%s', '%s', '%d' );

    if ( joes_scanner_auth_server_directory_has_column( 'use_api_firewall_credentials' ) ) {
        $data['use_api_firewall_credentials'] = ( isset( $_POST['use_api_firewall_credentials'] ) && (string) $_POST['use_api_firewall_credentials'] === '1' ) ? 1 : 0;
        $format[] = '%d';
    }

    if ( joes_scanner_auth_server_directory_has_column( 'firewall_username' ) ) {
        $firewall_username = isset( $_POST['firewall_username'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['firewall_username'] ) ) : '';
        $data['firewall_username'] = trim( $firewall_username ) !== '' ? trim( $firewall_username ) : null;
        $format[] = '%s';
    }

    if ( joes_scanner_auth_server_directory_has_column( 'firewall_password' ) ) {
        $firewall_password = isset( $_POST['firewall_password'] ) ? (string) wp_unslash( $_POST['firewall_password'] ) : '';
        $firewall_password = trim( $firewall_password );
        $data['firewall_password'] = $firewall_password !== '' ? $firewall_password : null;
        $format[] = '%s';
    }

    $wpdb->update(
        $tbl,
        $data,
        array( 'id' => $id ),
        $format,
        array( '%d' )
    );

    $user = wp_get_current_user();
    joes_scanner_auth_log_admin_event(
        'server_directory_edit',
        array(
            'user_id'   => (int) $user->ID,
            'username'  => (string) $user->user_login,
            'email'     => (string) $user->user_email,
            'server_id' => $id,
            'server_url'=> $server_url,
        )
    );

    $redir = add_query_arg(
        array(
            'page'          => 'joes-scanner-auth',
            'tab'           => 'server_directory',
            'status'        => $return_status,
            'highlight_id'  => $id,
            'updated_server'=> '1',
        ),
        admin_url( 'options-general.php' )
    );
    wp_safe_redirect( $redir );
    exit;
}

function joes_scanner_auth_render_server_directory_tab() {
    if ( ! current_user_can( 'manage_options' ) ) {
        echo '<p>Not authorized.</p>';
        return;
    }

    $status = isset( $_GET['status'] ) ? sanitize_key( (string) $_GET['status'] ) : 'approved';
    if ( ! in_array( $status, array( 'approved', 'all', 'pending', 'disabled' ), true ) ) {
        // 'rejected' is intentionally not exposed as a tab; it is visible under All.
        $status = 'approved';
    }

    $highlight_id = isset( $_GET['highlight_id'] ) ? (int) $_GET['highlight_id'] : 0;

    $page_size = 50;
    if ( $status === 'approved' ) {
        // Approved is the list shown to app users. Show more rows so ordering is easy.
        $page_size = 500;
    }
    $page_param = 'servers_page';
    $page = isset( $_GET[ $page_param ] ) ? max( 1, (int) $_GET[ $page_param ] ) : 1;
    $offset = ( $page - 1 ) * $page_size;

    $query_status = ( $status === 'all' ) ? '' : $status;
    $total = joes_scanner_auth_get_server_directory_total( $query_status );
    $pages = max( 1, (int) ceil( $total / $page_size ) );
    if ( $page > $pages ) {
        $page = $pages;
        $offset = ( $page - 1 ) * $page_size;
    }

    $rows = joes_scanner_auth_get_server_directory_rows( $query_status, $page_size, $offset );

    $base_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=server_directory' );

    echo '<h2>Server Directory</h2>';
    echo '<p>Public submissions require login and are moderated here before being listed to app users.</p>';
    if ( $status === 'approved' ) {
        echo '<p><strong>Ordering:</strong> Drag the handle at the left of each row to rearrange the order shown in the app.</p>';
    }

    if ( isset( $_GET['cleared'] ) ) {
        echo '<div class="notice notice-success is-dismissible"><p>Server cleared from directory.</p></div>';
    }
    if ( isset( $_GET['updated_server'] ) ) {
        echo '<div class="notice notice-success is-dismissible"><p>Server updated.</p></div>';
    }

    $edit_id = isset( $_GET['edit_id'] ) ? (int) $_GET['edit_id'] : 0;
    $edit_row = $edit_id > 0 ? joes_scanner_auth_get_server_directory_row_by_id( $edit_id ) : null;
    $show_add_server = isset( $_GET['add_server'] ) && (string) $_GET['add_server'] === '1';

    if ( $show_add_server ) {
    echo '<div style="margin-top:18px;max-width:900px;">';
    echo '<div style="margin-top:8px;">';
    if ( isset( $_GET['add_default_error'] ) ) {
        $err = sanitize_key( (string) $_GET['add_default_error'] );
        if ( $err === 'missing_required' ) {
            echo '<div class="notice notice-error"><p>Please fill in Server name, Server URL, and Map anchor.</p></div>';
        } elseif ( $err === 'duplicate_url' ) {
            echo '<div class="notice notice-warning"><p>A server with that URL already exists in the directory.</p></div>';
        }
    }

    echo '<form method="post" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '" style="background:#fff;border:1px solid #ccd0d4;border-radius:6px;padding:12px;max-width:900px;">';
    echo '<input type="hidden" name="action" value="joes_scanner_auth_add_default_server" />';
    wp_nonce_field( 'joes_scanner_auth_add_default_server' );
    echo '<table class="form-table" style="margin:0;">';
    echo '<tr><th scope="row"><label for="jsa_def_server_name">Server name</label></th><td><input id="jsa_def_server_name" name="server_name" type="text" class="regular-text" required /></td></tr>';
    echo '<tr><th scope="row"><label for="jsa_def_server_url">Server URL</label></th><td><input id="jsa_def_server_url" name="server_url" type="url" class="regular-text" required placeholder="https://example.com" /></td></tr>';
    echo '<tr><th scope="row"><label for="jsa_def_info_url">Info URL</label></th><td><input id="jsa_def_info_url" name="info_url" type="url" class="regular-text" placeholder="https://example.com/info (optional)" /></td></tr>';
    echo '<tr><th scope="row"><label for="jsa_def_area_label">Area label</label></th><td><input id="jsa_def_area_label" name="area_label" type="text" class="regular-text" placeholder="Optional display label (example: Treasure Valley)" /></td></tr>';
    echo '<tr><th scope="row"><label for="jsa_def_map_anchor">Map anchor</label></th><td><input id="jsa_def_map_anchor" name="map_anchor" type="text" class="regular-text" required placeholder="Required (example: Boise, ID)" /></td></tr>';
    if ( joes_scanner_auth_server_directory_has_column( 'use_api_firewall_credentials' ) ) {
        echo '<tr><th scope="row">Firewall credentials</th><td><label><input type="checkbox" name="use_api_firewall_credentials" value="1" /> Use API firewall credentials for this directory server</label><p class="description">Used only when this server is accessed through the Joe\'s Scanner WordPress API integration.</p></td></tr>';
    }
    if ( joes_scanner_auth_server_directory_has_column( 'firewall_username' ) ) {
        echo '<tr><th scope="row"><label for="jsa_def_firewall_username">Firewall username</label></th><td><input id="jsa_def_firewall_username" name="firewall_username" type="text" class="regular-text" /><p class="description">Optional. Leave blank unless Joe has approved this server for API-based app access.</p></td></tr>';
    }
    if ( joes_scanner_auth_server_directory_has_column( 'firewall_password' ) ) {
        echo '<tr><th scope="row"><label for="jsa_def_firewall_password">Firewall password</label></th><td><input id="jsa_def_firewall_password" name="firewall_password" type="text" class="regular-text" /><p class="description">These are server connection credentials for Joe\'s Scanner API-based app access, not the end-user login. Contact Joe for API access.</p></td></tr>';
    }
    echo '<tr><th scope="row">Official</th><td><label><input type="checkbox" name="is_official" value="1" checked /> Mark as Official (shows 🛡️ badge in app list)</label></td></tr>';
    echo '</table>';
    echo '<p style="margin:10px 0 0;"><button type="submit" class="button button-primary">Add Server</button> <a class="button" href="' . esc_url( add_query_arg( array( 'status' => $status ), $base_url ) ) . '">Cancel</a></p>';
    echo '</form>';
    echo '</div>';
    echo '</div>';
    }

    if ( $edit_row ) {
        echo '<details class="jsauth-collapsible" open style="margin-top:18px;max-width:900px;">';
        echo '<summary style="cursor:pointer;user-select:none;font-size:14px;font-weight:600;padding:10px 12px;background:#fff;border:1px solid #ccd0d4;border-radius:6px;">Edit Server #' . (int) $edit_row->id . '</summary>';
        echo '<div style="margin-top:8px;">';
        if ( isset( $_GET['update_server_error'] ) ) {
            $edit_err = sanitize_key( (string) $_GET['update_server_error'] );
            if ( $edit_err === 'duplicate_url' ) {
                echo '<div class="notice notice-warning"><p>A different server already uses that URL.</p></div>';
            }
        }
        echo '<form method="post" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '" style="background:#fff;border:1px solid #ccd0d4;border-radius:6px;padding:12px;max-width:900px;">';
        echo '<input type="hidden" name="action" value="joes_scanner_auth_update_server_directory_entry" />';
        echo '<input type="hidden" name="id" value="' . (int) $edit_row->id . '" />';
        echo '<input type="hidden" name="return_status" value="' . esc_attr( $status ) . '" />';
        wp_nonce_field( 'joes_scanner_auth_update_server_directory_entry' );
        echo '<table class="form-table" style="margin:0;">';
        echo '<tr><th scope="row"><label for="jsa_edit_server_name">Server name</label></th><td><input id="jsa_edit_server_name" name="server_name" type="text" class="regular-text" required value="' . esc_attr( (string) $edit_row->server_name ) . '" /></td></tr>';
        echo '<tr><th scope="row"><label for="jsa_edit_server_url">Server URL</label></th><td><input id="jsa_edit_server_url" name="server_url" type="url" class="regular-text" required value="' . esc_attr( (string) $edit_row->server_url ) . '" /></td></tr>';
        echo '<tr><th scope="row"><label for="jsa_edit_info_url">Info URL</label></th><td><input id="jsa_edit_info_url" name="info_url" type="url" class="regular-text" value="' . esc_attr( (string) $edit_row->info_url ) . '" /></td></tr>';
        echo '<tr><th scope="row"><label for="jsa_edit_area_label">Area label</label></th><td><input id="jsa_edit_area_label" name="area_label" type="text" class="regular-text" value="' . esc_attr( (string) $edit_row->area_label ) . '" /></td></tr>';
        echo '<tr><th scope="row"><label for="jsa_edit_map_anchor">Map anchor</label></th><td><input id="jsa_edit_map_anchor" name="map_anchor" type="text" class="regular-text" required value="' . esc_attr( (string) $edit_row->map_anchor ) . '" /></td></tr>';
        echo '<tr><th scope="row">Official</th><td><label><input type="checkbox" name="is_official" value="1"' . checked( ! empty( $edit_row->is_official ), true, false ) . ' /> Mark as Official</label></td></tr>';
        if ( joes_scanner_auth_server_directory_has_column( 'use_api_firewall_credentials' ) ) {
            echo '<tr><th scope="row">Firewall credentials</th><td><label><input type="checkbox" name="use_api_firewall_credentials" value="1"' . checked( ! empty( $edit_row->use_api_firewall_credentials ), true, false ) . ' /> Use API firewall credentials for this directory server</label><p class="description">Used only when this server is accessed through the Joe\'s Scanner WordPress API integration.</p></td></tr>';
        }
        if ( joes_scanner_auth_server_directory_has_column( 'firewall_username' ) ) {
            echo '<tr><th scope="row"><label for="jsa_edit_firewall_username">Firewall username</label></th><td><input id="jsa_edit_firewall_username" name="firewall_username" type="text" class="regular-text" value="' . esc_attr( (string) $edit_row->firewall_username ) . '" /><p class="description">Optional. Leave blank unless Joe has approved this server for API-based app access.</p></td></tr>';
        }
        if ( joes_scanner_auth_server_directory_has_column( 'firewall_password' ) ) {
            echo '<tr><th scope="row"><label for="jsa_edit_firewall_password">Firewall password</label></th><td><input id="jsa_edit_firewall_password" name="firewall_password" type="text" class="regular-text" value="' . esc_attr( (string) $edit_row->firewall_password ) . '" /><p class="description">These are server connection credentials for Joe\'s Scanner API-based app access, not the end-user login. Contact Joe for API access.</p></td></tr>';
        }
        echo '</table>';
        echo '<p style="margin:10px 0 0;"><button type="submit" class="button button-primary">Save Changes</button> <a class="button" href="' . esc_url( add_query_arg( array( 'status' => $status ), $base_url ) ) . '">Cancel</a></p>';
        echo '</form>';
        echo '</div>';
        echo '</details>';
    }

    $filters = array(
        'approved' => 'Approved',
        'all'      => 'All',
        'pending'  => 'Pending',
        'disabled' => 'Disabled',
    );

        $pending_total = joes_scanner_auth_get_server_directory_total( 'pending' );

echo '<div style="margin:10px 0;display:flex;flex-wrap:wrap;gap:6px;align-items:center;">';
    foreach ( $filters as $k => $label ) {
        $url = add_query_arg( array( 'status' => $k ), $base_url );
        $cls = ( $status === $k ) ? 'button button-primary' : 'button';
        $extra_style = '';
        if ( $k === 'pending' && $pending_total > 0 ) {
            $extra_style = 'border:2px solid #d63638;';
        }
        echo '<a class="' . esc_attr( $cls ) . '" href="' . esc_url( $url ) . '" style="' . esc_attr( $extra_style ) . '">' . esc_html( $label ) . '</a>';
    }
    if ( ! $show_add_server ) {
        $add_server_url = add_query_arg( array( 'status' => $status, 'add_server' => 1 ), $base_url );
        echo '<a class="button button-secondary" href="' . esc_url( $add_server_url ) . '">Add Server</a>';
    }
    if ( $status === 'approved' ) {
        echo '<span id="jsauth-order-status" class="jsauth-order-saving"></span>';
    }
    echo '</div>';

    echo '<style>
      .jsauth-highlight-row { background: #fff8d6; }
      .jsauth-server-actions { display:flex; flex-wrap:wrap; gap:6px; }
      .jsauth-server-actions form { display:inline; margin:0; }
      .jsauth-drag { cursor: move; font-size:16px; line-height:1; padding:2px 6px; display:inline-block; color:#555; }
      .jsauth-order-saving { display:inline-block; margin-left:10px; color:#555; }
    </style>';

    echo '<script>
      function jsauthPromptReject(id){
        var reason = window.prompt("Reject reason (required):", "");
        if(!reason){ return false; }
        var form = document.getElementById("jsauth-reject-"+id);
        if(!form){ return false; }
        form.querySelector("input[name=note]").value = reason;
        return true;
      }
      function jsauthPromptNote(id, msg){
        var note = window.prompt(msg, "");
        if(note === null){ return false; }
        var form = document.getElementById("jsauth-note-"+id);
        if(!form){ return false; }
        form.querySelector("input[name=note]").value = note;
        return true;
      }
    </script>';

    if ( $status === 'approved' ) {
        wp_enqueue_script( 'jquery-ui-sortable' );
        $save_nonce = wp_create_nonce( 'joes_scanner_auth_save_server_directory_order' );
        echo '<script>
          jQuery(function($){
            var $tbody = $("#jsauth-approved-sortable");
            if(!$tbody.length){ return; }

            function collectIds(){
              var ids = [];
              $tbody.find("tr").each(function(){
                var id = parseInt($(this).attr("data-server-id"), 10);
                if(id > 0){ ids.push(id); }
              });
              return ids;
            }

            var saveTimer = null;
            function scheduleSave(){
              if(saveTimer){ window.clearTimeout(saveTimer); }
              saveTimer = window.setTimeout(doSave, 350);
            }

            function doSave(){
              var ids = collectIds();
              if(!ids.length){ return; }
              $("#jsauth-order-status").text("Saving order...");
              $.post(ajaxurl, {
                action: "joes_scanner_auth_save_server_directory_order",
                nonce: "' . esc_js( $save_nonce ) . '",
                ids: ids.join(",")
              }).done(function(resp){
                if(resp && resp.success){
                  $("#jsauth-order-status").text("Order saved.");
                  window.setTimeout(function(){ $("#jsauth-order-status").text(""); }, 1500);
                } else {
                  $("#jsauth-order-status").text("Save failed.");
                }
              }).fail(function(){
                $("#jsauth-order-status").text("Save failed.");
              });
            }

            $tbody.sortable({
              handle: ".jsauth-drag",
              helper: function(e, tr){
                var $originals = tr.children();
                var $helper = tr.clone();
                $helper.children().each(function(index){
                  $(this).width($originals.eq(index).width());
                });
                return $helper;
              },
              update: function(){
                scheduleSave();
              }
            });
          });
        </script>';
    }

    $admin_post_url = admin_url( 'admin-post.php' );
    $nonce = wp_create_nonce( 'joes_scanner_auth_server_directory_actions' );

    echo '<table class="widefat jsauth-table" style="margin-top:10px;">';
    echo '<thead><tr>';
    if ( $status === 'approved' ) {
        echo '<th style="width:36px;"></th>';
    }
    echo '<th>ID</th>';
    echo '<th>Status</th>';
    echo '<th>Server</th>';
    echo '<th>URLs</th>';
    echo '<th>Area</th>';
    echo '<th>Submitted</th>';
    echo '<th>Reviewed</th>';
    echo '<th>Actions</th>';
    echo '</tr></thead>';

    if ( $status === 'approved' ) {
        echo '<tbody id="jsauth-approved-sortable">';
    } else {
        echo '<tbody>';
    }

    if ( empty( $rows ) ) {
        $colspan = ( $status === 'approved' ) ? 9 : 8;
        echo '<tr><td colspan="' . esc_attr( (string) $colspan ) . '">No rows.</td></tr>';
    } else {
        foreach ( $rows as $r ) {
            $rid = (int) $r->id;
            $row_cls = ( $highlight_id > 0 && $rid === $highlight_id ) ? ' class="jsauth-highlight-row"' : '';
            $data_attr = ( $status === 'approved' ) ? ' data-server-id="' . esc_attr( $rid ) . '"' : '';
            echo '<tr' . $row_cls . $data_attr . '>';
            if ( $status === 'approved' ) {
                echo '<td><span class="jsauth-drag" title="Drag to reorder">☰</span></td>';
            }
            echo '<td>' . esc_html( $rid ) . '</td>';
            echo '<td>' . esc_html( (string) $r->status ) . '</td>';
            echo '<td class="jsauth-wrap">' . esc_html( (string) $r->server_name ) . '<br /><small>' . esc_html( (string) $r->server_url ) . '</small></td>';

            $info_url = isset( $r->info_url ) ? (string) $r->info_url : '';
            echo '<td class="jsauth-wrap">';
            echo '<a href="' . esc_url( (string) $r->server_url ) . '" target="_blank" rel="noopener">Open server</a>';
            if ( $info_url !== '' ) {
                echo '<br /><a href="' . esc_url( $info_url ) . '" target="_blank" rel="noopener">Info</a>';
            }
            echo '</td>';

            $area_label = isset( $r->area_label ) ? (string) $r->area_label : '';
            $map_anchor = (string) $r->map_anchor;
            echo '<td class="jsauth-wrap">';
            if ( $area_label !== '' ) {
                echo esc_html( $area_label ) . '<br />';
            }
            echo '<small>Map anchor: ' . esc_html( $map_anchor ) . '</small>';
            echo '</td>';

            $submitted = (string) $r->created_at_utc;
            $submitted_by = isset( $r->submitted_username ) ? (string) $r->submitted_username : '';
            $submitted_email = isset( $r->submitted_email ) ? (string) $r->submitted_email : '';
            $submitted_ip = isset( $r->submitted_ip ) ? (string) $r->submitted_ip : '';
            echo '<td class="jsauth-wrap"><small>' . esc_html( $submitted ) . '<br />' . esc_html( $submitted_by ) . '<br />' . esc_html( $submitted_email ) . '<br />' . esc_html( $submitted_ip ) . '</small></td>';

            $reviewed = isset( $r->reviewed_at_utc ) ? (string) $r->reviewed_at_utc : '';
            $review_notes = isset( $r->review_notes ) ? (string) $r->review_notes : '';
            $reject_reason = isset( $r->reject_reason ) ? (string) $r->reject_reason : '';
            echo '<td class="jsauth-wrap"><small>' . esc_html( $reviewed ) . '</small>';
            if ( $review_notes !== '' ) {
                echo '<br /><small>Note: ' . esc_html( $review_notes ) . '</small>';
            }
            if ( $reject_reason !== '' ) {
                echo '<br /><small>Reject: ' . esc_html( $reject_reason ) . '</small>';
            }
            echo '</td>';

            echo '<td>';
            echo '<div class="jsauth-server-actions">';

            $edit_link_args = array(
                'page'      => 'joes-scanner-auth',
                'tab'       => 'server_directory',
                'status'    => $status,
                'edit_id'   => $rid,
            );
            if ( $page > 1 ) {
                $edit_link_args[ $page_param ] = $page;
            }
            if ( $highlight_id > 0 ) {
                $edit_link_args['highlight_id'] = $highlight_id;
            }
            $edit_url = add_query_arg( $edit_link_args, admin_url( 'options-general.php' ) );
            echo '<p style="margin:0 0 8px;"><a class="button" href="' . esc_url( $edit_url ) . '">Edit</a></p>';

            if ( (string) $r->status === 'pending' ) {
                echo '<form method="post" action="' . esc_url( $admin_post_url ) . '" onsubmit="return confirm(\'Approve this server?\');">';
                echo '<input type="hidden" name="action" value="joes_scanner_auth_review_server_directory" />';
                echo '<input type="hidden" name="op" value="approve" />';
                echo '<input type="hidden" name="id" value="' . esc_attr( $rid ) . '" />';
                echo '<input type="hidden" name="note" value="" />';
                echo '<input type="hidden" name="_wpnonce" value="' . esc_attr( $nonce ) . '" />';
                echo '<button class="button button-primary" type="submit">Approve</button>';
                echo '</form>';

                echo '<form id="jsauth-reject-' . esc_attr( $rid ) . '" method="post" action="' . esc_url( $admin_post_url ) . '" onsubmit="return jsauthPromptReject(' . esc_js( $rid ) . ');">';
                echo '<input type="hidden" name="action" value="joes_scanner_auth_review_server_directory" />';
                echo '<input type="hidden" name="op" value="reject" />';
                echo '<input type="hidden" name="id" value="' . esc_attr( $rid ) . '" />';
                echo '<input type="hidden" name="note" value="" />';
                echo '<input type="hidden" name="_wpnonce" value="' . esc_attr( $nonce ) . '" />';
                echo '<button class="button" type="submit">Reject</button>';
                echo '</form>';
            } elseif ( (string) $r->status === 'approved' ) {
                echo '<form id="jsauth-note-' . esc_attr( $rid ) . '" method="post" action="' . esc_url( $admin_post_url ) . '" onsubmit="return jsauthPromptNote(' . esc_js( $rid ) . ', \"Disable note (optional):\");">';
                echo '<input type="hidden" name="action" value="joes_scanner_auth_review_server_directory" />';
                echo '<input type="hidden" name="op" value="disable" />';
                echo '<input type="hidden" name="id" value="' . esc_attr( $rid ) . '" />';
                echo '<input type="hidden" name="note" value="" />';
                echo '<input type="hidden" name="_wpnonce" value="' . esc_attr( $nonce ) . '" />';
                echo '<button class="button" type="submit">Disable</button>';
                echo '</form>';
            } elseif ( (string) $r->status === 'disabled' ) {
                echo '<form method="post" action="' . esc_url( $admin_post_url ) . '" onsubmit="return confirm(\'Re-enable this server?\');">';
                echo '<input type="hidden" name="action" value="joes_scanner_auth_review_server_directory" />';
                echo '<input type="hidden" name="op" value="enable" />';
                echo '<input type="hidden" name="id" value="' . esc_attr( $rid ) . '" />';
                echo '<input type="hidden" name="note" value="" />';
                echo '<input type="hidden" name="_wpnonce" value="' . esc_attr( $nonce ) . '" />';
                echo '<button class="button" type="submit">Enable</button>';
                echo '</form>';
            }

            echo '<form method="post" action="' . esc_url( $admin_post_url ) . '" onsubmit="return confirm(\'Permanently clear (delete) this server from the directory?\');">';
            echo '<input type="hidden" name="action" value="joes_scanner_auth_review_server_directory" />';
            echo '<input type="hidden" name="op" value="clear" />';
            echo '<input type="hidden" name="id" value="' . esc_attr( $rid ) . '" />';
            echo '<input type="hidden" name="note" value="" />';
            echo '<input type="hidden" name="_wpnonce" value="' . esc_attr( $nonce ) . '" />';
            echo '<button class="button button-link-delete" type="submit">Clear</button>';
            echo '</form>';

            echo '</div>';
            echo '</td>';

            echo '</tr>';
        }
    }

    echo '</tbody></table>';

    if ( function_exists( 'joes_scanner_auth_render_pager' ) && $pages > 1 ) {
        $other = array( 'status' => $status );
        joes_scanner_auth_render_pager( $base_url, $page_param, $page, $pages, $other );
    }
}



function joes_scanner_auth_admin_post_add_default_server() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    check_admin_referer( 'joes_scanner_auth_add_default_server' );

    $server_name = isset( $_POST['server_name'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['server_name'] ) ) : '';
    $server_url  = isset( $_POST['server_url'] ) ? esc_url_raw( (string) wp_unslash( $_POST['server_url'] ) ) : '';
    $info_url    = isset( $_POST['info_url'] ) ? esc_url_raw( (string) wp_unslash( $_POST['info_url'] ) ) : '';
    $area_label  = isset( $_POST['area_label'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['area_label'] ) ) : '';
    $map_anchor  = isset( $_POST['map_anchor'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['map_anchor'] ) ) : '';
    $use_api_firewall_credentials = isset( $_POST['use_api_firewall_credentials'] ) && (string) $_POST['use_api_firewall_credentials'] === '1';
    $firewall_username = isset( $_POST['firewall_username'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['firewall_username'] ) ) : '';
    $firewall_password = isset( $_POST['firewall_password'] ) ? (string) wp_unslash( $_POST['firewall_password'] ) : '';
    $is_official = isset( $_POST['is_official'] ) && (string) $_POST['is_official'] === '1';

    $server_url = joes_scanner_auth_normalize_url_for_compare( $server_url );
    $firewall_username = trim( $firewall_username );
    $firewall_password = trim( $firewall_password );

    if ( $server_name === '' || $server_url === '' || $map_anchor === '' ) {
        $redir = add_query_arg(
            array(
                'page' => 'joes-scanner-auth',
                'tab'  => 'server_directory',
                'add_default_error' => 'missing_required',
            ),
            admin_url( 'options-general.php' )
        );
        wp_safe_redirect( $redir );
        exit;
    }

    global $wpdb;
    $tbl = joes_scanner_auth_table_server_directory();

    // De-dupe by normalized URL (ignore trailing slash differences).
    $existing_id = (int) $wpdb->get_var( $wpdb->prepare( "SELECT id FROM {$tbl} WHERE TRIM(TRAILING '/' FROM server_url) = %s LIMIT 1", rtrim( $server_url, '/' ) ) );
    if ( $existing_id > 0 ) {
        $redir = add_query_arg(
            array(
                'page' => 'joes-scanner-auth',
                'tab'  => 'server_directory',
                'add_default_error' => 'duplicate_url',
                'highlight_id'      => $existing_id,
                'status'            => 'all',
            ),
            admin_url( 'options-general.php' )
        );
        wp_safe_redirect( $redir );
        exit;
    }

    $user = wp_get_current_user();

    $now = gmdate( 'Y-m-d H:i:s' );
    $max_order = (int) $wpdb->get_var( $wpdb->prepare( "SELECT MAX(sort_order) FROM {$tbl} WHERE status = %s", 'approved' ) );
    $sort_order = ( $max_order > 0 ) ? ( $max_order + 10 ) : 10;

    $insert_data = array(
        'created_at_utc'         => $now,
        'updated_at_utc'         => $now,
        'status'                 => 'approved',
        'sort_order'             => $sort_order,
        'server_name'            => $server_name,
        'server_url'             => $server_url,
        'info_url'               => ( $info_url !== '' ) ? $info_url : null,
        'area_label'             => ( $area_label !== '' ) ? $area_label : null,
        'map_anchor'             => $map_anchor,
        'use_api_firewall_credentials' => joes_scanner_auth_server_directory_has_column( 'use_api_firewall_credentials' ) ? ( $use_api_firewall_credentials ? 1 : 0 ) : null,
        'firewall_username'      => joes_scanner_auth_server_directory_has_column( 'firewall_username' ) ? ( $firewall_username !== '' ? $firewall_username : null ) : null,
        'firewall_password'      => joes_scanner_auth_server_directory_has_column( 'firewall_password' ) ? ( $firewall_password !== '' ? $firewall_password : null ) : null,
        'is_official'            => $is_official ? 1 : 0,
        'agreement_accepted'     => 0,
        'agreement_text_version' => null,
        'agreement_text_snapshot'=> null,
        'agreement_accepted_at_utc' => null,
        'agreement_accepted_ip'  => null,
        'agreement_accepted_user_id' => null,
        'submitted_by_user_id'   => (int) $user->ID,
        'submitted_username'     => (string) $user->user_login,
        'submitted_email'        => (string) $user->user_email,
        'contact_user_id'        => (int) $user->ID,
        'contact_username'       => (string) $user->user_login,
        'contact_email'          => (string) $user->user_email,
        'submitted_ip'           => isset( $_SERVER['REMOTE_ADDR'] ) ? sanitize_text_field( (string) $_SERVER['REMOTE_ADDR'] ) : null,
        'reviewed_by_user_id'    => (int) $user->ID,
        'reviewed_at_utc'        => $now,
        'review_notes'           => 'Added via admin: default server',
        'reject_reason'          => null,
    );

    $insert_formats = array();
    foreach ( $insert_data as $key => $value ) {
        if ( in_array( $key, array( 'sort_order', 'use_api_firewall_credentials', 'is_official', 'agreement_accepted', 'agreement_accepted_user_id', 'submitted_by_user_id', 'contact_user_id', 'reviewed_by_user_id' ), true ) ) {
            $insert_formats[] = '%d';
        } else {
            $insert_formats[] = '%s';
        }
    }

    $wpdb->insert(
        $tbl,
        $insert_data,
        $insert_formats
    );

    $new_id = (int) $wpdb->insert_id;

    joes_scanner_auth_log_admin_event(
        'server_directory_add_default',
        array(
            'user_id'   => (int) $user->ID,
            'username'  => (string) $user->user_login,
            'email'     => (string) $user->user_email,
            'server_id' => $new_id,
            'server_url'=> $server_url,
            'is_official' => $is_official ? 1 : 0,
        )
    );

    joes_scanner_auth_notify_admin_server_added( $new_id, array(
        'server_name' => $server_name,
        'server_url'  => $server_url,
        'info_url'    => $info_url,
        'area_label'  => $area_label,
        'map_anchor'  => $map_anchor,
        'is_official' => $is_official ? 1 : 0,
        'submitted_by'=> $user->user_login,
        'submitted_email' => $user->user_email,
    ) );


    $redir = add_query_arg(
        array(
            'page' => 'joes-scanner-auth',
            'tab'  => 'server_directory',
            'status' => 'approved',
            'highlight_id' => $new_id,
        ),
        admin_url( 'options-general.php' )
    );
    wp_safe_redirect( $redir );
    exit;
}
