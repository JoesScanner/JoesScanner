<?php
/* Server Directory administration. */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

add_action( 'admin_post_joes_scanner_auth_review_server_directory', 'joes_scanner_auth_admin_post_review_server_directory' );
add_action( 'admin_post_joes_scanner_auth_add_default_server', 'joes_scanner_auth_admin_post_save_directory' );
add_action( 'admin_post_joes_scanner_auth_update_server_directory_entry', 'joes_scanner_auth_admin_post_save_directory' );
add_action( 'admin_post_joes_scanner_auth_my_servers_page', 'joes_scanner_auth_admin_post_my_servers_page' );
add_action( 'wp_ajax_joes_scanner_auth_save_server_directory_order', 'joes_scanner_auth_ajax_save_server_directory_order' );

function joes_scanner_auth_dir_admin_url( array $arguments = array() ) {
    return add_query_arg( $arguments + array( 'page' => 'joes-scanner-auth', 'tab' => 'server_directory' ), admin_url( 'options-general.php' ) );
}

function joes_scanner_auth_dir_check_admin() {
    if ( ! current_user_can( 'manage_options' ) || ! is_ssl() || ( $_SERVER['REQUEST_METHOD'] ?? '' ) !== 'POST' ) {
        joes_scanner_auth_dir_form_error( joes_scanner_auth_dir_error( 'Administrator authorization is required.', 403 ) );
    }
}

function joes_scanner_auth_admin_post_my_servers_page() {
    joes_scanner_auth_dir_check_admin();
    check_admin_referer( 'joes_scanner_auth_my_servers_page' );
    $id = joes_scanner_auth_dir_post_id( 'page_id' );
    if ( $id && ! joes_scanner_auth_dir_my_servers_page( $id ) ) {
        joes_scanner_auth_dir_form_error( joes_scanner_auth_dir_error( 'Select a published page containing the My Servers shortcode.' ) );
    }
    update_option( 'joes_scanner_my_servers_page_id', $id );
    wp_safe_redirect( joes_scanner_auth_dir_admin_url() );
    exit;
}

function joes_scanner_auth_admin_post_review_server_directory() {
    joes_scanner_auth_dir_check_admin();
    check_admin_referer( 'joes_scanner_auth_server_directory_actions' );
    $id = joes_scanner_auth_dir_post_id( 'id' );
    $revision = joes_scanner_auth_dir_post_id( 'revision_id' );
    $operation = joes_scanner_auth_dir_post_string( 'op' );
    $note = sanitize_text_field( joes_scanner_auth_dir_post_string( 'note' ) );
    if ( strlen( $note ) > 255 || ( in_array( $operation, array( 'reject', 'request_changes' ), true ) && $note === '' ) ) {
        joes_scanner_auth_dir_form_error( joes_scanner_auth_dir_error( 'Enter a review explanation of up to 255 characters.' ) );
    }
    $result = joes_scanner_auth_dir_moderate( $id, get_current_user_id(), $operation, $note, $revision );
    if ( is_wp_error( $result ) ) {
        joes_scanner_auth_dir_form_error( $result );
    }
    wp_safe_redirect( joes_scanner_auth_dir_admin_url( array(
        'status' => 'all',
        'highlight_id' => $id,
        'updated_server' => '1',
    ) ) );
    exit;
}

function joes_scanner_auth_admin_post_save_directory() {
    joes_scanner_auth_dir_check_admin();
    $add = current_action() === 'admin_post_joes_scanner_auth_add_default_server';
    check_admin_referer( $add ? 'joes_scanner_auth_add_default_server' : 'joes_scanner_auth_update_server_directory_entry' );
    $actor = get_current_user_id();
    $id = $add ? 0 : joes_scanner_auth_dir_post_id( 'id' );
    $row = $id ? joes_scanner_auth_dir_row( $id ) : null;
    if ( ! $add && ! $row ) {
        joes_scanner_auth_dir_form_error( joes_scanner_auth_dir_error( 'Listing not found.', 404 ) );
    }
    $fields = joes_scanner_auth_dir_read_form( (int) ( $row->owner_user_id ?? $actor ), true );
    if ( is_wp_error( $fields ) ) {
        joes_scanner_auth_dir_form_error( $fields );
    }

    $username = trim( joes_scanner_auth_dir_post_string( 'firewall_username' ) );
    $password = trim( joes_scanner_auth_dir_post_string( 'firewall_password' ) );
    $new_origin = joes_scanner_auth_dir_origin( $fields['server_url'] );
    $same_origin = $row && joes_scanner_auth_dir_origin( $row->server_url ) === $new_origin;
    $clear = joes_scanner_auth_dir_post_string( 'clear_firewall_credentials' ) === '1';
    $enable = joes_scanner_auth_dir_post_string( 'use_api_firewall_credentials' ) === '1';
    if ( ( $username === '' ) !== ( $password === '' ) || strlen( $username ) > 190 || strlen( $password ) > 1024
        || preg_match( '/[:\x00-\x1f\x7f]/', $username ) || preg_match( '/[\x00-\x1f\x7f]/', $password ) ) {
        joes_scanner_auth_dir_form_error( joes_scanner_auth_dir_error( 'Enter a valid firewall username and password together.' ) );
    }
    if ( $username !== '' && ( $clear || ! $enable || strpos( $new_origin, 'https://' ) !== 0 ) ) {
        joes_scanner_auth_dir_form_error( joes_scanner_auth_dir_error( 'Managed firewall credentials require an HTTPS recorder and enabled API delivery.' ) );
    }
    $managed = array(
        'is_official' => (int) ( joes_scanner_auth_dir_post_string( 'is_official' ) === '1' ),
        'use_api_firewall_credentials' => (int) ( $enable && ! $clear && ( $username !== ''
            || ( $same_origin && strpos( $new_origin, 'https://' ) === 0 && $row->firewall_username && $row->firewall_password ) ) ),
    );
    if ( $username !== '' || $clear || ! $same_origin ) {
        $managed['firewall_username'] = $username !== '' ? $username : null;
        $managed['firewall_password'] = $password !== '' ? $password : null;
    }
    if ( $managed['use_api_firewall_credentials'] ) {
        $fields['requires_firewall_auth'] = 1;
    }

    $result = joes_scanner_auth_dir_save( $actor, $id, joes_scanner_auth_dir_post_id( 'directory_version' ), $fields, false, $managed );
    if ( is_wp_error( $result ) ) {
        joes_scanner_auth_dir_form_error( $result );
    }
    wp_safe_redirect( joes_scanner_auth_dir_admin_url( array(
        'status' => $result['pending'] ? 'pending' : 'all',
        'highlight_id' => $result['id'],
        'updated_server' => '1',
    ) ) );
    exit;
}

function joes_scanner_auth_ajax_save_server_directory_order() {
    joes_scanner_auth_dir_check_admin();
    check_ajax_referer( 'joes_scanner_auth_save_server_directory_order', 'nonce' );
    $ids = array_unique( array_filter( array_map( 'absint', explode( ',', joes_scanner_auth_dir_post_string( 'ids' ) ) ) ) );
    if ( ! $ids || count( $ids ) > 200 ) {
        wp_send_json_error( array( 'error' => 'invalid_ids' ), 400 );
    }

    global $wpdb;
    $result = joes_scanner_auth_dir_write( static function () use ( $wpdb, $ids ) {
        $table = joes_scanner_auth_table_server_directory();
        $order = 10;
        foreach ( $ids as $id ) {
            if ( $wpdb->update( $table, array( 'sort_order' => $order ), array(
                'id' => $id,
                'status' => 'approved',
                'visibility' => 'public',
            ) ) === false ) {
                return joes_scanner_auth_dir_db_error();
            }
            $order += 10;
        }
        return true;
    } );
    if ( is_wp_error( $result ) ) {
        wp_send_json_error( array( 'error' => 'could_not_save_order' ), 503 );
    }
    wp_send_json_success();
}

function joes_scanner_auth_dir_admin_edit_form( $row, $status ) {
    echo '<details open><summary>' . esc_html( $row ? 'Edit server' : 'Add server' ) . '</summary>';
    echo '<form method="post" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '">';
    if ( $row ) {
        echo '<input type="hidden" name="action" value="joes_scanner_auth_update_server_directory_entry" />';
        echo '<input type="hidden" name="id" value="' . (int) $row->id . '" />';
        echo '<input type="hidden" name="directory_version" value="' . (int) $row->directory_version . '" />';
        wp_nonce_field( 'joes_scanner_auth_update_server_directory_entry' );
    } else {
        echo '<input type="hidden" name="action" value="joes_scanner_auth_add_default_server" />';
        wp_nonce_field( 'joes_scanner_auth_add_default_server' );
    }
    $fields = joes_scanner_auth_dir_fields( $row );
    if ( $row && $row->status === 'pending' ) {
        $proposal = joes_scanner_auth_dir_revision( (int) $row->id, 'pending' );
        if ( $proposal ) {
            $fields = array_replace( $fields, array_intersect_key( (array) json_decode( $proposal->changes_json, true ), $fields ) );
        }
    }
    echo joes_scanner_auth_dir_listing_fields( $fields );
    echo '<p><label><input type="checkbox" name="is_official" value="1"' . checked( ! $row || ! empty( $row->is_official ), true, false ) . ' /> Official</label></p>';
    echo '<fieldset><legend>Administrator-managed firewall credentials</legend>';
    echo '<p><label><input type="checkbox" name="use_api_firewall_credentials" value="1"' . checked( ! empty( $row->use_api_firewall_credentials ), true, false ) . ' /> Enable API delivery to entitled clients</label></p>';
    echo '<p><label>New firewall username <input name="firewall_username" type="text" maxlength="190" autocomplete="off" /></label></p>';
    echo '<p><label>New firewall password <input name="firewall_password" type="password" maxlength="1024" autocomplete="new-password" /></label></p>';
    echo '<p><label><input type="checkbox" name="clear_firewall_credentials" value="1" /> Clear saved firewall credentials</label></p>';
    echo '<p>Blank credentials preserve the saved pair for the same origin.</p></fieldset>';
    echo '<p><button type="submit" class="button button-primary">Save server</button> <a class="button" href="'
        . esc_url( joes_scanner_auth_dir_admin_url( array( 'status' => $status ) ) ) . '">Cancel</a></p></form></details>';
}

function joes_scanner_auth_dir_admin_actions( $row, $proposal ) {
    $id = (int) $row->id;
    $operations = array();
    if ( $row->status === 'suspended' ) {
        $operations['reinstate'] = 'Allow resubmission';
    } else {
        if ( $proposal || $row->status === 'pending' ) {
            $operations += array( 'approve' => 'Approve', 'reject' => 'Reject', 'request_changes' => 'Request changes' );
        }
        if ( $row->status === 'approved' ) {
            $operations['disable'] = 'Disable';
        } elseif ( $row->status === 'disabled' ) {
            $operations['enable'] = 'Enable';
        }
        if ( $row->status !== 'removed' ) {
            $operations['remove'] = 'Remove listing';
        }
        $operations['suspend'] = 'Suspend for security';
    }
    echo '<form method="post" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '">';
    echo '<input type="hidden" name="action" value="joes_scanner_auth_review_server_directory" />';
    echo '<input type="hidden" name="id" value="' . $id . '" />';
    echo '<input type="hidden" name="revision_id" value="' . (int) ( $proposal->id ?? 0 ) . '" />';
    wp_nonce_field( 'joes_scanner_auth_server_directory_actions' );
    echo '<p><label>Review explanation <input type="text" name="note" maxlength="255" /></label></p>';
    foreach ( $operations as $op => $label ) {
        echo '<button type="submit" class="button" name="op" value="' . esc_attr( $op ) . '">' . esc_html( $label ) . '</button> ';
    }
    echo '</form>';
}

function joes_scanner_auth_dir_admin_audit( $id ) {
    global $wpdb;
    $table = joes_scanner_auth_dir_table( 'revisions' );
    $rows = $wpdb->get_results( $wpdb->prepare( "SELECT * FROM {$table} WHERE directory_id = %d ORDER BY id DESC LIMIT 20", $id ) );
    if ( ! $rows ) {
        return;
    }
    echo '<details><summary>Recent moderation and changes</summary><ul>';
    foreach ( $rows as $row ) {
        $fields = implode( ', ', array_keys( (array) json_decode( $row->changes_json, true ) ) );
        echo '<li>' . esc_html( $row->created_at_utc . ' UTC · ' . $row->state . ' · Owner #' . $row->owner_user_id
            . ' · Actor #' . $row->actor_user_id . ( $row->reviewer_user_id ? ' · Reviewer #' . $row->reviewer_user_id : '' )
            . ( $fields ? ' · ' . $fields : '' ) . ( $row->note ? ' · ' . $row->note : '' ) ) . '</li>';
    }
    echo '</ul></details>';
}

function joes_scanner_auth_render_server_directory_tab() {
    if ( ! current_user_can( 'manage_options' ) ) {
        return;
    }
    global $wpdb;
    $filters = array(
        'approved' => 'Approved Public',
        'pending' => 'Pending review',
        'all' => 'All',
        'disabled' => 'Disabled',
        'suspended' => 'Suspended',
    );
    $status = isset( $_GET['status'] ) && is_string( $_GET['status'] ) ? sanitize_key( $_GET['status'] ) : 'approved';
    $status = isset( $filters[$status] ) ? $status : 'approved';
    $table = joes_scanner_auth_table_server_directory();
    $revisions = joes_scanner_auth_dir_table( 'revisions' );
    $conditions = array(
        'approved' => "d.status = 'approved' AND d.visibility = 'public'",
        'pending' => "d.status = 'pending' OR EXISTS (SELECT 1 FROM {$revisions} r WHERE r.directory_id = d.id AND r.state = 'pending')",
        'all' => '1 = 1',
        'disabled' => "d.status = 'disabled'",
        'suspended' => "d.status = 'suspended'",
    );
    $where = $conditions[$status];
    $order = $status === 'approved' ? 'd.sort_order,d.server_name,d.id' : 'd.id DESC';
    $page = isset( $_GET['servers_page'] ) ? max( 1, (int) $_GET['servers_page'] ) : 1;
    $page_size = $status === 'approved' ? 200 : 50;
    $pages = max( 1, (int) ceil( (int) $wpdb->get_var( "SELECT COUNT(*) FROM {$table} d WHERE {$where}" ) / $page_size ) );
    $page = min( $pages, $page );
    $rows = $wpdb->get_results( $wpdb->prepare( "SELECT d.* FROM {$table} d WHERE {$where} ORDER BY {$order} LIMIT %d OFFSET %d", $page_size, ( $page - 1 ) * $page_size ) );
    echo '<h2>Server Directory</h2>';
    echo '<form method="post" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '"><p>';
    echo '<input type="hidden" name="action" value="joes_scanner_auth_my_servers_page" />';
    wp_nonce_field( 'joes_scanner_auth_my_servers_page' );
    echo '<label>My Servers page ';
    wp_dropdown_pages( array( 'name' => 'page_id', 'show_option_none' => 'Select a page', 'option_none_value' => '0',
        'selected' => (int) get_option( 'joes_scanner_my_servers_page_id', 0 ) ) );
    echo '</label> <button class="button" type="submit">Save page</button></p></form>';
    if ( isset( $_GET['updated_server'] ) ) {
        echo '<div class="notice notice-success is-dismissible"><p>Server updated.</p></div>';
    }
    echo '<p>';
    foreach ( $filters as $key => $name ) {
        echo '<a class="button' . ( $status === $key ? ' button-primary' : '' ) . '" href="'
            . esc_url( joes_scanner_auth_dir_admin_url( array( 'status' => $key ) ) ) . '">' . esc_html( $name ) . '</a> ';
    }
    echo '<a class="button" href="' . esc_url( joes_scanner_auth_dir_admin_url( array( 'status' => $status, 'add_server' => 1 ) ) ) . '">Add Server</a></p>';
    $edit = isset( $_GET['edit_id'] ) ? joes_scanner_auth_dir_row( (int) $_GET['edit_id'] ) : null;
    if ( isset( $_GET['add_server'] ) || ( $edit && $edit->status !== 'suspended' ) ) {
        joes_scanner_auth_dir_admin_edit_form( $edit, $status );
    }
    if ( ! $rows ) {
        echo '<p>No listings found.</p>';
        return;
    }
    if ( $status === 'approved' ) {
        echo '<p>Drag rows to change app directory order. <span role="status" id="jsauth-order-status"></span></p>';
        joes_scanner_auth_dir_admin_order_script();
    }
    echo '<table class="widefat striped"><thead><tr><th>Server</th><th>Status</th><th>Owner</th><th>Moderation</th></tr></thead><tbody id="jsauth-directory-rows">';
    $highlight = isset( $_GET['highlight_id'] ) ? (int) $_GET['highlight_id'] : 0;
    foreach ( $rows as $row ) {
        $id = (int) $row->id;
        $proposal = joes_scanner_auth_dir_revision( $id, 'pending' );
        $owner = get_userdata( (int) $row->owner_user_id );
        echo '<tr data-server-id="' . $id . '"' . ( $highlight === $id ? ' style="background:#fff8d6"' : '' ) . '><td>';
        if ( $status === 'approved' ) {
            echo '<span class="jsauth-drag" style="cursor:move" aria-label="Reorder server">↕</span> ';
        }
        echo '<strong>' . esc_html( $row->server_name ) . '</strong><br /><small>' . esc_html( joes_scanner_auth_dir_url( $row->server_url ) ) . '</small>';
        echo '</td><td>' . esc_html( ucfirst( $row->visibility ) . ' · ' . ucfirst( $row->status ) ) . '</td>';
        echo '<td>' . esc_html( ( $owner ? $owner->user_login . ' ' : '' ) . '#' . $row->owner_user_id ) . '</td><td>';
        echo joes_scanner_auth_dir_pending_preview( $proposal );
        if ( $row->status !== 'suspended' ) {
            echo '<p><a href="' . esc_url( joes_scanner_auth_dir_admin_url( array( 'edit_id' => $id, 'status' => $status ) ) ) . '">Edit</a></p>';
        }
        joes_scanner_auth_dir_admin_actions( $row, $proposal );
        joes_scanner_auth_dir_admin_audit( $id );
        echo '</td></tr>';
    }
    echo '</tbody></table>';
    if ( $pages > 1 ) {
        joes_scanner_auth_render_pager( joes_scanner_auth_dir_admin_url(), 'servers_page', $page, $pages, array( 'status' => $status ) );
    }
}

function joes_scanner_auth_dir_admin_order_script() {
    wp_enqueue_script( 'jquery-ui-sortable' );
    $nonce = wp_create_nonce( 'joes_scanner_auth_save_server_directory_order' );
    echo '<script>
      jQuery(function($) {
        var rows = $("#jsauth-directory-rows");
        var status = $("#jsauth-order-status");
        rows.sortable({
          handle: ".jsauth-drag",
          update: function() {
            var ids = rows.find("tr").map(function() { return $(this).data("server-id"); }).get();
            status.text("Saving order…");
            $.post(ajaxurl, {
              action: "joes_scanner_auth_save_server_directory_order",
              nonce: "' . esc_js( $nonce ) . '",
              ids: ids.join(",")
            }).done(function(response) {
              status.text(response.success ? "Order saved." : "Unable to save order.");
            }).fail(function() { status.text("Unable to save order."); });
          }
        });
      });
    </script>';
}
