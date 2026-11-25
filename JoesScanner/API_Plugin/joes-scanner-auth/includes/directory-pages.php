<?php

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

add_action( 'init', static function () {
    add_shortcode( 'joes_scanner_server_submit', 'joes_scanner_auth_shortcode_server_submit' );
    add_shortcode( 'joes_scanner_my_servers', 'joes_scanner_auth_shortcode_my_servers' );
    add_shortcode( 'joes_scanner_server_directory', 'joes_scanner_auth_shortcode_public_directory' );
} );

add_action( 'template_redirect', static function () {
    $post = is_singular() ? get_queried_object() : null;
    if ( $post instanceof WP_Post && ( has_shortcode( $post->post_content, 'joes_scanner_my_servers' )
        || has_shortcode( $post->post_content, 'joes_scanner_server_submit' ) ) ) {
        joes_scanner_auth_dir_private_headers();
        if ( ! is_user_logged_in() ) {
            joes_scanner_auth_dir_browser();
        }
    }
} );

add_action( 'admin_post_joes_scanner_auth_submit_server', 'joes_scanner_auth_admin_post_submit_server' );
add_action( 'admin_post_nopriv_joes_scanner_auth_submit_server', 'joes_scanner_auth_admin_post_submit_server' );

function joes_scanner_auth_server_directory_agreement_version() {
    return 'v1';
}

function joes_scanner_auth_server_directory_agreement_text() {
    return "I certify that I own this server or I am authorized by the owner to submit it for listing. I grant Joe’s Scanner permission to publicly display and distribute the information I submit, including the server name, server URL, info URL, area label (if provided), and map anchor, in a public server directory and to app users. I understand that users may access this server through third party apps and networks. I understand that submissions are reviewed and may be approved, rejected, disabled, or removed at any time at Joe’s Scanner’s discretion. I am responsible for operating and maintaining the server and any applicable policies. Joe’s Scanner does not guarantee traffic, uptime, or continued inclusion.";
}

function joes_scanner_auth_dir_post_string( $name ) {
    return isset( $_POST[$name] ) && is_string( $_POST[$name] ) ? wp_unslash( $_POST[$name] ) : '';
}

function joes_scanner_auth_dir_post_id( $name ) {
    $id = joes_scanner_auth_dir_post_string( $name );
    return ctype_digit( $id ) && strlen( $id ) < 19 ? (int) $id : 0;
}

function joes_scanner_auth_dir_form_error( WP_Error $error ) {
    wp_die( esc_html( $error->get_error_message() ), 'Server Directory', array(
        'response' => (int) ( $error->get_error_data()['status'] ?? 503 ),
        'back_link' => true,
    ) );
}

function joes_scanner_auth_dir_read_form( $actor, $admin = false ) {
    return joes_scanner_auth_dir_read_listing( $actor, wp_unslash( $_POST ), $admin );
}

function joes_scanner_auth_dir_read_listing( $actor, array $input, $admin = false ) {
    $allowed = array( 'action', '_wpnonce', '_wp_http_referer', 'directory_op', 'id', 'directory_version',
        'server_name', 'server_url', 'info_url', 'map_anchor', 'area_label', 'description', 'visibility',
        'requires_scanner_login', 'requires_firewall_auth', 'audience', 'agreement_accept' );
    if ( $admin ) {
        $allowed = array_merge( $allowed, array( 'return_status', 'is_official', 'use_api_firewall_credentials',
            'firewall_username', 'firewall_password', 'clear_firewall_credentials' ) );
    }
    foreach ( $input as $key => $value ) {
        if ( ! is_string( $value ) || ! in_array( $key, $allowed, true ) ) {
            return joes_scanner_auth_dir_error( 'The form contains an unsupported field. Omit credentials from directory submissions.' );
        }
    }
    $fields = array();
    foreach ( array( 'server_name', 'map_anchor', 'area_label', 'description' ) as $field ) {
        $raw = $input[$field] ?? '';
        $max = $field === 'description' ? 2000 : 120;
        $text = $field === 'description' ? sanitize_textarea_field( $raw ) : sanitize_text_field( $raw );
        if ( strlen( $text ) > $max || preg_match( '#https?://[^\s<>]*(?:@|\?)[^\s<>]*#i', $raw ) ) {
            return joes_scanner_auth_dir_error( 'The description or label is too long or contains a URL with credentials or query parameters.' );
        }
        $fields[$field] = $text;
    }
    $fields['server_url'] = joes_scanner_auth_dir_url( $input['server_url'] ?? '' );
    $info = trim( $input['info_url'] ?? '' );
    $fields['info_url'] = joes_scanner_auth_dir_url( $info );
    if ( $fields['server_name'] === '' || $fields['map_anchor'] === '' || $fields['server_url'] === ''
        || ( $info !== '' && $fields['info_url'] === '' ) ) {
        return joes_scanner_auth_dir_error( 'Provide a name, a map anchor, and a valid HTTP or HTTPS server URL. URLs must omit logins, query parameters, and fragments.' );
    }

    $fields['visibility'] = ( $input['visibility'] ?? '' ) ?: 'public';
    if ( ! in_array( $fields['visibility'], array( 'public', 'private', 'restricted' ), true ) ) {
        return joes_scanner_auth_dir_error( 'Choose Public, Private, or Restricted visibility.' );
    }
    $fields['requires_scanner_login'] = (int) ( ( $input['requires_scanner_login'] ?? '' ) === '1' );
    $fields['requires_firewall_auth'] = (int) ( ( $input['requires_firewall_auth'] ?? '' ) === '1' );
    $fields['audience'] = array();
    if ( $fields['visibility'] === 'restricted' ) {
        $logins = preg_split( '/[\r\n,]+/', trim( $input['audience'] ?? '' ), -1, PREG_SPLIT_NO_EMPTY );
        if ( count( $logins ) > 25 ) {
            return joes_scanner_auth_dir_error( 'Enter at most 25 recipient usernames.' );
        }
        foreach ( $logins as $login ) {
            $login = trim( $login );
            $user = strlen( $login ) <= 60 ? get_user_by( 'login', $login ) : false;
            if ( ! $user || (int) $user->ID === $actor ) {
                return joes_scanner_auth_dir_error( 'Use existing WordPress usernames other than the listing owner for Restricted access.' );
            }
            $fields['audience'][(int) $user->ID] = (int) $user->ID;
        }
        if ( ! $fields['audience'] ) {
            return joes_scanner_auth_dir_error( 'Choose at least one recipient for Restricted access.' );
        }
        sort( $fields['audience'], SORT_NUMERIC );
    }
    return $fields;
}

function joes_scanner_auth_admin_post_submit_server() {
    joes_scanner_auth_dir_private_headers();
    if ( ! is_ssl() || ( $_SERVER['REQUEST_METHOD'] ?? '' ) !== 'POST'
        || ! joes_scanner_auth_dir_nonce_valid( joes_scanner_auth_dir_post_string( '_wpnonce' ) ) ) {
        joes_scanner_auth_dir_form_error( joes_scanner_auth_dir_error( 'Open the HTTPS My Servers page and submit its form.', 403 ) );
    }
    $op = joes_scanner_auth_dir_post_string( 'directory_op' );
    $actor = joes_scanner_auth_dir_actor();
    $notice = '';
    if ( in_array( $op, array( 'issue', 'verify' ), true ) && ! is_user_logged_in() ) {
        $email = strtolower( trim( joes_scanner_auth_dir_post_string( 'email' ) ) );
        $code = trim( joes_scanner_auth_dir_post_string( 'code' ) );
        if ( strlen( $email ) > 100 || ! is_email( $email ) || ( $op === 'verify' && ! preg_match( '/^[0-9]{8}$/D', $code ) ) ) {
            joes_scanner_auth_dir_form_error( joes_scanner_auth_dir_error( 'Enter a valid email address and the eight-digit code when verifying.' ) );
        }
        $browser = joes_scanner_auth_dir_cookie( 'browser' );
        if ( $op === 'issue' ) {
            $result = joes_scanner_auth_dir_issue_code( $email, $browser );
            $notice = 'sent';
        } else {
            $result = joes_scanner_auth_dir_verify_code( $email, $code, $browser );
            $notice = 'verified';
        }
    } elseif ( $actor && $op === 'end' && ! is_user_logged_in() ) {
        $result = joes_scanner_auth_dir_end_session();
        $notice = 'ended';
    } elseif ( $actor && $op === 'remove' ) {
        $result = joes_scanner_auth_dir_moderate( joes_scanner_auth_dir_post_id( 'id' ), $actor, 'remove', '', 0, true );
        $notice = 'removed';
    } elseif ( $actor && in_array( $op, array( 'save', '' ), true ) ) {
        $fields = joes_scanner_auth_dir_read_form( $actor );
        if ( is_wp_error( $fields ) ) {
            joes_scanner_auth_dir_form_error( $fields );
        }
        $result = joes_scanner_auth_dir_save(
            $actor,
            joes_scanner_auth_dir_post_id( 'id' ),
            joes_scanner_auth_dir_post_id( 'directory_version' ),
            $fields,
            joes_scanner_auth_dir_post_string( 'agreement_accept' ) === '1'
        );
        if ( ! is_wp_error( $result ) ) {
            if ( $result['pending'] ) {
                joes_scanner_auth_notify_admin_server_submission( $result['id'] );
            }
            $notice = $result['pending'] ? 'pending' : 'saved';
        }
    } else {
        $result = joes_scanner_auth_dir_error( 'Verify your directory email or sign in to WordPress.', 403 );
    }
    if ( is_wp_error( $result ) ) {
        joes_scanner_auth_dir_form_error( $result );
    }
    wp_safe_redirect( add_query_arg( 'directory_notice', $notice, wp_get_referer() ?: home_url( '/' ) ) );
    exit;
}

function joes_scanner_auth_notify_admin_server_submission( $id ) {
    $email = get_option( 'admin_email' );
    if ( $email ) {
        $review = admin_url( 'options-general.php?page=joes-scanner-auth&tab=server_directory&status=pending&highlight_id=' . (int) $id );
        wp_mail( $email, "Joe's Scanner directory listing awaiting review", 'Review this listing in WordPress: ' . $review );
    }
}

function joes_scanner_auth_dir_form_start( $op ) {
    return '<form method="post" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '">'
        . '<input type="hidden" name="action" value="joes_scanner_auth_submit_server" />'
        . '<input type="hidden" name="directory_op" value="' . esc_attr( $op ) . '" />'
        . '<input type="hidden" name="_wpnonce" value="' . esc_attr( joes_scanner_auth_dir_nonce() ) . '" />'
        . wp_referer_field( false );
}

function joes_scanner_auth_dir_notice() {
    $notices = array(
        'sent'     => 'Check your email for a verification code.',
        'verified' => 'Email verified for My Servers.',
        'ended'    => 'Directory session ended.',
        'pending'  => 'Changes submitted for review.',
        'saved'    => 'Listing saved.',
        'removed'  => 'Listing removed.',
    );
    $key = isset( $_GET['directory_notice'] ) && is_string( $_GET['directory_notice'] ) ? sanitize_key( $_GET['directory_notice'] ) : '';
    return isset( $notices[$key] ) ? '<p role="status">' . esc_html( $notices[$key] ) . '</p>' : '';
}

function joes_scanner_auth_dir_verify_forms() {
    if ( ! is_ssl() || joes_scanner_auth_dir_browser() === '' ) {
        return '<p>Open My Servers over HTTPS to verify an email address.</p>';
    }
    return '<p><a href="' . esc_url( wp_login_url( add_query_arg( joes_scanner_auth_dir_prefill(), get_permalink() ) ) ) . '">Log in to WordPress</a> or verify your email for My Servers access.</p>'
        . '<p><a href="' . esc_url( wp_lostpassword_url() ) . '">Reset WordPress password</a></p>'
        . joes_scanner_auth_dir_form_start( 'issue' )
        . '<p><label>Email address <input type="email" name="email" maxlength="100" required /></label> '
        . '<button type="submit">Send verification code</button></p></form>'
        . joes_scanner_auth_dir_form_start( 'verify' )
        . '<p><label>Email address <input type="email" name="email" maxlength="100" required /></label> '
        . '<label>Verification code <input type="text" name="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" required /></label> '
        . '<button type="submit">Verify email</button></p></form>';
}

function joes_scanner_auth_dir_prefill() {
    $address = $_GET['directory_address'] ?? '';
    $visibility = $_GET['directory_visibility'] ?? '';
    $address = is_string( $address ) ? joes_scanner_auth_dir_handoff_address( wp_unslash( $address ) ) : '';
    if ( $address === '' || ! in_array( $visibility, array( 'private', 'restricted', 'public' ), true ) ) {
        return array();
    }
    return array( 'directory_address' => $address, 'directory_visibility' => $visibility );
}

function joes_scanner_auth_dir_handoff_address( $value ) {
    $address = joes_scanner_auth_dir_url( $value );
    $path = rawurldecode( wp_parse_url( $address, PHP_URL_PATH ) ?? '' );
    return strpbrk( $path, "=@?#\\" ) !== false ? '' : $address;
}

function joes_scanner_auth_dir_labels() {
    return array(
        'server_name' => 'Server name', 'server_url' => 'Server URL', 'info_url' => 'Info URL',
        'map_anchor' => 'Map anchor', 'area_label' => 'Area label', 'description' => 'Description',
        'visibility' => 'Visibility', 'requires_scanner_login' => 'Scanner login required',
        'requires_firewall_auth' => 'Firewall authentication required', 'audience' => 'Recipient usernames',
    );
}

function joes_scanner_auth_dir_recipient_names( array $ids ) {
    $names = array();
    foreach ( $ids as $id ) {
        $user = get_userdata( $id );
        if ( $user ) {
            $names[] = $user->user_login;
        }
    }
    return implode( ', ', $names );
}

function joes_scanner_auth_dir_listing_fields( array $data ) {
    $html = '';
    foreach ( joes_scanner_auth_dir_labels() as $name => $label ) {
        $value = $data[$name];
        $html .= '<p><label>' . esc_html( $label ) . '<br />';
        if ( $name === 'visibility' ) {
            $html .= '<select name="visibility">';
            foreach ( array( 'public', 'private', 'restricted' ) as $choice ) {
                $html .= '<option value="' . esc_attr( $choice ) . '"' . selected( $value, $choice, false ) . '>' . esc_html( ucfirst( $choice ) ) . '</option>';
            }
            $html .= '</select>';
        } elseif ( $name === 'description' || $name === 'audience' ) {
            $value = $name === 'audience' ? joes_scanner_auth_dir_recipient_names( $value ) : $value;
            $html .= '<textarea name="' . esc_attr( $name ) . '" maxlength="2000" rows="3">' . esc_textarea( $value ) . '</textarea>';
        } elseif ( strpos( $name, 'requires_' ) === 0 ) {
            $html .= '<input type="checkbox" name="' . esc_attr( $name ) . '" value="1"' . checked( $value, 1, false ) . ' />';
        } else {
            $url = in_array( $name, array( 'server_url', 'info_url' ), true );
            $required = in_array( $name, array( 'server_name', 'server_url', 'map_anchor' ), true );
            $html .= '<input type="' . ( $url ? 'url' : 'text' ) . '" maxlength="' . ( $url ? 255 : 120 ) . '" name="'
                . esc_attr( $name ) . '" value="' . esc_attr( $value ) . '"' . ( $required ? ' required' : '' ) . ' />';
        }
        $html .= '</label>';
        if ( $name === 'audience' ) {
            $html .= '<br /><small>For Restricted listings, enter existing WordPress usernames separated by commas.</small>';
        }
        $html .= '</p>';
    }
    return $html;
}

function joes_scanner_auth_dir_listing_form( $row = null ) {
    $fields = joes_scanner_auth_dir_fields( $row );
    if ( ! $row && ( $prefill = joes_scanner_auth_dir_prefill() ) ) {
        $fields['server_url'] = $prefill['directory_address'];
        $fields['visibility'] = $prefill['directory_visibility'];
    }
    if ( $row && $row->status === 'pending' ) {
        $proposal = joes_scanner_auth_dir_revision( (int) $row->id, 'pending' );
        if ( $proposal ) {
            $fields = array_replace( $fields, array_intersect_key( (array) json_decode( $proposal->changes_json, true ), $fields ) );
        }
    }
    $html = joes_scanner_auth_dir_form_start( 'save' );
    if ( $row ) {
        $html .= '<input type="hidden" name="id" value="' . (int) $row->id . '" />'
            . '<input type="hidden" name="directory_version" value="' . (int) $row->directory_version . '" />';
    }
    $html .= joes_scanner_auth_dir_listing_fields( $fields );
    if ( empty( $row->agreement_accepted ) ) {
        $html .= '<p><label><input type="checkbox" name="agreement_accept" value="1" /> I accept the terms for Public listings.</label><br /><small>'
            . esc_html( joes_scanner_auth_server_directory_agreement_text() ) . '</small></p>';
    }
    $label = $row && in_array( $row->status, array( 'removed', 'rejected' ), true ) ? 'Resubmit listing' : 'Save listing';
    return $html . '<p><button type="submit">' . esc_html( $label ) . '</button></p></form>';
}

function joes_scanner_auth_shortcode_server_submit() {
    if ( ! headers_sent() ) {
        joes_scanner_auth_dir_private_headers();
    }
    return joes_scanner_auth_dir_notice() . ( is_ssl() && joes_scanner_auth_dir_actor()
        ? joes_scanner_auth_dir_listing_form()
        : joes_scanner_auth_dir_verify_forms() );
}

function joes_scanner_auth_dir_pending_preview( $revision ) {
    if ( ! $revision ) {
        return '';
    }
    $html = '<p><strong>Pending revision</strong></p><dl>';
    foreach ( (array) json_decode( $revision->changes_json, true ) as $field => $value ) {
        $label = joes_scanner_auth_dir_labels()[$field] ?? '';
        if ( $label === '' ) {
            continue;
        }
        if ( is_array( $value ) ) {
            $value = joes_scanner_auth_dir_recipient_names( $value );
        } elseif ( strpos( $field, 'requires_' ) === 0 ) {
            $value = $value ? 'Yes' : 'No';
        }
        $html .= '<dt>' . esc_html( $label ) . '</dt><dd>' . esc_html( $value ) . '</dd>';
    }
    return $html . '</dl>';
}

function joes_scanner_auth_shortcode_my_servers() {
    global $wpdb;
    if ( ! headers_sent() ) {
        joes_scanner_auth_dir_private_headers();
    }
    $html = '<section class="jsauth-my-servers"><h2>My Servers</h2>' . joes_scanner_auth_dir_notice();
    $actor = is_ssl() ? joes_scanner_auth_dir_actor() : 0;
    if ( ! $actor ) {
        return $html . joes_scanner_auth_dir_verify_forms() . '</section>';
    }
    if ( ! is_user_logged_in() ) {
        $html .= joes_scanner_auth_dir_form_start( 'end' ) . '<p><button type="submit">End directory session</button> '
            . '<a href="' . esc_url( wp_lostpassword_url() ) . '">Set or reset WordPress password</a></p></form>';
    }
    $directory = joes_scanner_auth_table_server_directory();
    $mine = $wpdb->get_results( $wpdb->prepare( "SELECT * FROM {$directory} WHERE owner_user_id = %d ORDER BY id DESC", $actor ) );
    foreach ( $mine as $row ) {
        $html .= '<article><h3>' . esc_html( $row->server_name ) . '</h3>'
            . '<p>' . esc_html( ucfirst( $row->visibility ) . ' · ' . ucfirst( $row->status ) ) . '</p>'
            . joes_scanner_auth_dir_pending_preview( joes_scanner_auth_dir_revision( (int) $row->id, 'pending' ) );
        if ( $row->reject_reason ) {
            $html .= '<p>Review explanation — ' . esc_html( $row->reject_reason ) . '</p>';
        }
        if ( $row->status !== 'suspended' ) {
            $html .= '<details><summary>Edit listing</summary>' . joes_scanner_auth_dir_listing_form( $row ) . '</details>';
            if ( $row->status !== 'removed' ) {
                $html .= joes_scanner_auth_dir_form_start( 'remove' )
                    . '<input type="hidden" name="id" value="' . (int) $row->id . '" />'
                    . '<p><button type="submit">Remove listing</button></p></form>';
            }
        }
        $html .= '</article>';
    }
    $recipients = joes_scanner_auth_dir_table( 'recipients' );
    $approved = joes_scanner_auth_dir_shareable_sql( 'd' );
    $shared = $wpdb->get_results( $wpdb->prepare(
        "SELECT d.server_name,d.server_url,d.description FROM {$directory} d JOIN {$recipients} r ON r.directory_id = d.id
            WHERE r.user_id = %d AND d.visibility = 'restricted' AND {$approved} ORDER BY d.server_name",
        $actor
    ) );
    if ( $shared ) {
        $html .= '<h3>Shared with you</h3>' . joes_scanner_auth_dir_render_public_list( $shared );
    }
    return $html . '<details><summary>Add a server</summary>' . joes_scanner_auth_dir_listing_form() . '</details></section>';
}

function joes_scanner_auth_dir_public_rows( $search = '', $alphabetical = false ) {
    global $wpdb;
    $table = joes_scanner_auth_table_server_directory();
    $published = joes_scanner_auth_dir_shareable_sql( 'd' );
    $sql = "SELECT id,server_name,server_url,info_url,area_label,map_anchor,description,is_official,
            use_api_firewall_credentials,requires_scanner_login,requires_firewall_auth
        FROM {$table} d WHERE d.visibility = 'public' AND {$published}";
    if ( $search !== '' ) {
        $like = '%' . $wpdb->esc_like( $search ) . '%';
        $sql .= $wpdb->prepare( ' AND (d.server_name LIKE %s OR d.area_label LIKE %s OR d.description LIKE %s)', $like, $like, $like );
    }
    $sql .= $alphabetical ? ' ORDER BY d.server_name,d.id' : ' ORDER BY d.sort_order,d.server_name,d.id';
    return $wpdb->get_results( $sql );
}

function joes_scanner_auth_dir_render_public_list( array $rows ) {
    if ( ! $rows ) {
        return '<p>No servers found.</p>';
    }
    $html = '<ul>';
    foreach ( $rows as $row ) {
        $url = joes_scanner_auth_dir_url( $row->server_url );
        if ( $url === '' ) {
            continue;
        }
        $html .= '<li><a href="' . esc_url( $url ) . '">' . esc_html( $row->server_name ) . '</a>';
        if ( ! empty( $row->area_label ) ) {
            $html .= ' · ' . esc_html( $row->area_label );
        }
        if ( ! empty( $row->description ) ) {
            $html .= '<p>' . esc_html( $row->description ) . '</p>';
        }
        $html .= '</li>';
    }
    return $html . '</ul>';
}

function joes_scanner_auth_shortcode_public_directory() {
    $search = isset( $_GET['directory_search'] ) && is_string( $_GET['directory_search'] )
        ? sanitize_text_field( wp_unslash( $_GET['directory_search'] ) ) : '';
    $action = get_permalink();
    // Plain WordPress permalinks identify a page through the query string.
    $page_field = get_option( 'permalink_structure' ) ? '' : '<input type="hidden" name="page_id" value="' . (int) get_queried_object_id() . '" />';
    return '<form method="get" action="' . esc_url( $action ) . '">' . $page_field
        . '<p><label>Search servers <input type="search" name="directory_search" value="' . esc_attr( $search ) . '" /></label> '
        . '<button type="submit">Search</button></p></form>'
        . joes_scanner_auth_dir_render_public_list( joes_scanner_auth_dir_public_rows( $search, true ) );
}
