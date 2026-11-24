<?php
/*
 //|--------------------------------------------------------------------------//| Communications (admin broadcast + change feed)//|--------------------------------------------------------------------------*/


function joes_scanner_auth_comms_format_local_dt( $utc_dt ) {
    $utc_dt = is_string( $utc_dt ) ? trim( $utc_dt ) : '';
    if ( $utc_dt === '' ) {
        return '';
    }

    $ts = strtotime( $utc_dt . ' UTC' );
    if ( ! $ts ) {
        return $utc_dt;
    }

    $fmt_date = get_option( 'date_format' );
    $fmt_time = get_option( 'time_format' );
    $fmt      = trim( (string) $fmt_date . ' ' . (string) $fmt_time );

    if ( function_exists( 'wp_date' ) && function_exists( 'wp_timezone' ) ) {
        return wp_date( $fmt, $ts, wp_timezone() );
    }

    return date_i18n( $fmt, $ts, true );
}


function joes_scanner_auth_comms_format_local_time_only( $utc_dt ) {
    $utc_dt = is_string( $utc_dt ) ? trim( $utc_dt ) : '';
    if ( $utc_dt === '' ) {
        return '';
    }

    $ts = strtotime( $utc_dt . ' UTC' );
    if ( ! $ts ) {
        return $utc_dt;
    }

    $fmt = get_option( 'time_format' );

    if ( function_exists( 'wp_date' ) && function_exists( 'wp_timezone' ) ) {
        return wp_date( $fmt, $ts, wp_timezone() );
    }

    return date_i18n( $fmt, $ts, true );
}


function joes_scanner_auth_comms_format_local_date_only( $utc_dt ) {
    $utc_dt = is_string( $utc_dt ) ? trim( $utc_dt ) : '';
    if ( $utc_dt === '' ) {
        return '';
    }

    $ts = strtotime( $utc_dt . ' UTC' );
    if ( ! $ts ) {
        return $utc_dt;
    }

    $fmt = get_option( 'date_format' );

    if ( function_exists( 'wp_date' ) && function_exists( 'wp_timezone' ) ) {
        return wp_date( $fmt, $ts, wp_timezone() );
    }

    return date_i18n( $fmt, $ts, true );
}


function joes_scanner_auth_comms_message_to_dto( $row ) {
    if ( ! $row ) {
        return null;
    }
    return array(
        'id'             => (int) $row->id,
        'created_at_utc' => (string) $row->created_at_utc,
        'updated_at_utc' => isset( $row->updated_at_utc ) ? (string) $row->updated_at_utc : null,
        'author_label'   => isset( $row->author_label ) ? (string) $row->author_label : '',
        'heading_text'   => isset( $row->heading_text ) ? (string) $row->heading_text : '',
        'message_text'   => isset( $row->message_text ) ? (string) $row->message_text : '',
    );
}

function joes_scanner_auth_comms_record_change( $message_id, $change_type ) {
    global $wpdb;

    $change_type = strtolower( (string) $change_type );
    if ( $change_type !== 'upsert' && $change_type !== 'delete' ) {
        return;
    }

    $changes_tbl  = joes_scanner_auth_table_comms_changes();
    $messages_tbl = joes_scanner_auth_table_comms_messages();

    $payload = null;

    if ( $change_type === 'upsert' ) {
        $row = $wpdb->get_row(
            $wpdb->prepare(
                "SELECT id, created_at_utc, updated_at_utc, author_label, heading_text, message_text FROM {$messages_tbl} WHERE id = %d LIMIT 1",
                (int) $message_id
            )
        );
        $dto = joes_scanner_auth_comms_message_to_dto( $row );
        if ( $dto ) {
            $payload = wp_json_encode( $dto );
        }
    }

    $wpdb->insert(
        $changes_tbl,
        array(
            'changed_at_utc' => gmdate( 'Y-m-d H:i:s' ),
            'change_type'    => $change_type,
            'message_id'     => (int) $message_id,
            'payload'        => $payload,
        ),
        array( '%s', '%s', '%d', '%s' )
    );
}

function joes_scanner_auth_comms_validate_session_token( $session_token ) {
    $session_token = is_string( $session_token ) ? trim( $session_token ) : '';
    if ( $session_token === '' ) {
        return new WP_REST_Response(
            array(
                'ok'    => false,
                'error' => 'missing_session_token',
            ),
            400
        );
    }

    if ( ! function_exists( 'joes_scanner_auth_get_session_row_by_token' ) ) {
        return new WP_REST_Response(
            array(
                'ok'    => false,
                'error' => 'server_misconfigured',
            ),
            500
        );
    }

    $session_row = joes_scanner_auth_get_session_row_by_token( $session_token );
    if ( ! $session_row ) {
        return new WP_REST_Response(
            array(
                'ok'    => false,
                'error' => 'invalid_session_token',
            ),
            403
        );
    }

    return true;
}

function joes_scanner_auth_comms_changes_endpoint( WP_REST_Request $request ) {
    global $wpdb;

    $params = $request->get_json_params();
    if ( ! is_array( $params ) ) {
        $params = array();
    }

    $session_token = isset( $params['session_token'] ) ? (string) $params['session_token'] : '';
    $valid = joes_scanner_auth_comms_validate_session_token( $session_token );
    if ( $valid !== true ) {
        return $valid;
    }

    $since_seq      = isset( $params['since_seq'] ) ? (int) $params['since_seq'] : 0;
    $force_snapshot = ! empty( $params['force_snapshot'] ) ? 1 : 0;

    $messages_tbl = joes_scanner_auth_table_comms_messages();
    $changes_tbl  = joes_scanner_auth_table_comms_changes();

    $next_seq = (int) $wpdb->get_var( "SELECT COALESCE(MAX(seq), 0) FROM {$changes_tbl}" );

    if ( $force_snapshot === 1 || $since_seq <= 0 ) {
        $rows = $wpdb->get_results(
            "SELECT id, created_at_utc, updated_at_utc, author_label, heading_text, message_text
             FROM {$messages_tbl}
             WHERE is_deleted = 0
             ORDER BY id DESC
             LIMIT 300"
        );

        $snapshot = array();
        foreach ( (array) $rows as $r ) {
            $dto = joes_scanner_auth_comms_message_to_dto( $r );
            if ( $dto ) {
                $snapshot[] = $dto;
            }
        }

        return new WP_REST_Response(
            array(
                'ok'       => true,
                'next_seq' => $next_seq,
                'snapshot' => $snapshot,
                'changes'  => array(),
            ),
            200
        );
    }

    $rows = $wpdb->get_results(
        $wpdb->prepare(
            "SELECT seq, change_type, message_id, payload
             FROM {$changes_tbl}
             WHERE seq > %d
             ORDER BY seq ASC
             LIMIT 200",
            $since_seq
        )
    );

    $changes = array();
    $last_seq = $since_seq;

    foreach ( (array) $rows as $r ) {
        $last_seq = (int) $r->seq;

        $type = strtolower( (string) $r->change_type );
        if ( $type === 'delete' ) {
            $changes[] = array(
                'type'       => 'delete',
                'message_id' => (int) $r->message_id,
            );
            continue;
        }

        $msg = null;
        if ( isset( $r->payload ) && is_string( $r->payload ) && trim( $r->payload ) !== '' ) {
            $decoded = json_decode( $r->payload, true );
            if ( is_array( $decoded ) ) {
                $msg = $decoded;
            }
        }

        if ( ! is_array( $msg ) ) {
            $row = $wpdb->get_row(
                $wpdb->prepare(
                    "SELECT id, created_at_utc, updated_at_utc, author_label, heading_text, message_text FROM {$messages_tbl} WHERE id = %d LIMIT 1",
                    (int) $r->message_id
                )
            );
            $msg = joes_scanner_auth_comms_message_to_dto( $row );
        }

        if ( $msg ) {
            $changes[] = array(
                'type'       => 'upsert',
                'message_id' => (int) $r->message_id,
                'message'    => $msg,
            );
        }
    }

    return new WP_REST_Response(
        array(
            'ok'       => true,
            'next_seq' => (int) max( $next_seq, $last_seq ),
            'snapshot' => array(),
            'changes'  => $changes,
        ),
        200
    );
}

function joes_scanner_auth_render_comms_tab() {
    if ( ! current_user_can( 'manage_options' ) ) {
        echo '<p>You do not have permission to view this page.</p>';
        return;
    }

    global $wpdb;
    $messages_tbl = joes_scanner_auth_table_comms_messages();
    // This tab does not require additional tables beyond comms_messages and comms_changes.

    $notice = '';
    $error  = '';

    if ( isset( $_POST['jsauth_comms_action'] ) ) {
        check_admin_referer( 'jsauth_comms_action', 'jsauth_comms_nonce' );

        $action = sanitize_text_field( wp_unslash( $_POST['jsauth_comms_action'] ) );

        if ( $action === 'send' ) {
            $heading = isset( $_POST['jsauth_heading_text'] ) ? wp_unslash( $_POST['jsauth_heading_text'] ) : '';
            $heading = is_string( $heading ) ? trim( $heading ) : '';
            $text = isset( $_POST['jsauth_message_text'] ) ? wp_unslash( $_POST['jsauth_message_text'] ) : '';
            $text = is_string( $text ) ? trim( $text ) : '';
            if ( $text === '' ) {
                $error = 'Message cannot be empty.';
            } else {
                $user = wp_get_current_user();
                $label = $user && ! empty( $user->display_name ) ? $user->display_name : 'Admin';

                $wpdb->insert(
                    $messages_tbl,
                    array(
                        'created_at_utc' => gmdate( 'Y-m-d H:i:s' ),
                        'updated_at_utc' => null,
                        'author_user_id' => $user ? (int) $user->ID : null,
                        'author_label'   => joes_scanner_auth_trim_text( $label, 60 ),
                        'heading_text'   => joes_scanner_auth_trim_text( $heading, 120 ),
                        'message_text'   => $text,
                        'is_deleted'     => 0,
                    ),
                    array( '%s', '%s', '%d', '%s', '%s', '%s', '%d' )
                );

                $message_id = (int) $wpdb->insert_id;
                if ( $message_id > 0 ) {
                    joes_scanner_auth_comms_record_change( $message_id, 'upsert' );
                    $notice = 'Message sent.';
                } else {
                    $error = 'Failed to send message.';
                }
            }
        } elseif ( $action === 'edit' ) {
            $message_id = isset( $_POST['jsauth_message_id'] ) ? (int) $_POST['jsauth_message_id'] : 0;
            $heading = isset( $_POST['jsauth_heading_text'] ) ? wp_unslash( $_POST['jsauth_heading_text'] ) : '';
            $heading = is_string( $heading ) ? trim( $heading ) : '';
            $text = isset( $_POST['jsauth_message_text'] ) ? wp_unslash( $_POST['jsauth_message_text'] ) : '';
            $text = is_string( $text ) ? trim( $text ) : '';

            if ( $message_id <= 0 ) {
                $error = 'Invalid edit request.';
            } elseif ( $text === '' ) {
                $error = 'Message cannot be empty.';
            } else {
                $row = $wpdb->get_row(
                    $wpdb->prepare(
                        "SELECT id, is_deleted FROM {$messages_tbl} WHERE id = %d LIMIT 1",
                        $message_id
                    )
                );

                if ( ! $row ) {
                    $error = 'Message not found.';
                } elseif ( (int) $row->is_deleted === 1 ) {
                    $error = 'Cannot edit a removed message.';
                } else {
                    $updated = $wpdb->update(
                        $messages_tbl,
                        array(
                            'heading_text'   => joes_scanner_auth_trim_text( $heading, 120 ),
                            'message_text'   => $text,
                            'updated_at_utc' => gmdate( 'Y-m-d H:i:s' ),
                        ),
                        array( 'id' => $message_id ),
                        array( '%s', '%s', '%s' ),
                        array( '%d' )
                    );

                    if ( $updated === false ) {
                        $error = 'Failed to update message.';
                    } else {
                        joes_scanner_auth_comms_record_change( $message_id, 'upsert' );
                        $notice = 'Message updated.';
                    }
                }
            }
        } elseif ( $action === 'remove' ) {
            $message_id = isset( $_POST['jsauth_message_id'] ) ? (int) $_POST['jsauth_message_id'] : 0;
            if ( $message_id <= 0 ) {
                $error = 'Invalid remove request.';
            } else {
                $wpdb->update(
                    $messages_tbl,
                    array(
                        'is_deleted'     => 1,
                        'updated_at_utc' => gmdate( 'Y-m-d H:i:s' ),
                    ),
                    array( 'id' => $message_id ),
                    array( '%d', '%s' ),
                    array( '%d' )
                );
                joes_scanner_auth_comms_record_change( $message_id, 'delete' );
                $notice = 'Message removed from clients.';
            }
        }
    }

    if ( $notice !== '' ) {
        echo '<div class="notice notice-success"><p>' . esc_html( $notice ) . '</p></div>';
    }
    if ( $error !== '' ) {
        echo '<div class="notice notice-error"><p>' . esc_html( $error ) . '</p></div>';
    }

    echo '<h2>Communications</h2>';

    echo '<form method="post" style="margin: 12px 0 18px 0;">';
    wp_nonce_field( 'jsauth_comms_action', 'jsauth_comms_nonce' );
    echo '<input type="hidden" name="jsauth_comms_action" value="send" />';
    echo '<div style="max-width: 900px;">';
    echo '<div style="font-weight: 600; margin: 0 0 4px 0;">Heading</div>';
    echo '<input type="text" name="jsauth_heading_text" style="width: 100%;" placeholder="Heading (optional)" />';
    echo '<div style="height: 10px;"></div>';
    echo '<div style="font-weight: 600; margin: 0 0 4px 0;">Message</div>';
    echo '<textarea name="jsauth_message_text" rows="4" style="width: 100%;" placeholder="Type a message to broadcast to all users"></textarea>';
    echo '</div>';
    echo '<p style="margin-top: 8px;"><button type="submit" class="button button-primary">Send message</button></p>';
    echo '</form>';


$base_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=comms' );

$sort  = isset( $_GET['sort'] ) ? sanitize_key( wp_unslash( $_GET['sort'] ) ) : 'posted';
$order = isset( $_GET['order'] ) ? strtolower( sanitize_key( wp_unslash( $_GET['order'] ) ) ) : 'desc';
if ( $order !== 'asc' && $order !== 'desc' ) {
    $order = 'desc';
}

$sort_map = array(
    'posted'  => 'created_at_utc',
    'updated' => 'COALESCE(updated_at_utc, created_at_utc)',
    'id'      => 'id',
);
$sort_col = isset( $sort_map[ $sort ] ) ? $sort_map[ $sort ] : $sort_map['posted'];

// Current (live) messages.
$rows_current = $wpdb->get_results(
    "SELECT id, created_at_utc, updated_at_utc, author_label, heading_text, message_text
     FROM {$messages_tbl}
     WHERE is_deleted = 0
     ORDER BY {$sort_col} {$order}
     LIMIT 200"
);

// Archived or removed messages.
$rows_archived = $wpdb->get_results(
    "SELECT id, created_at_utc, updated_at_utc, author_label, heading_text, message_text
     FROM {$messages_tbl}
     WHERE is_deleted = 1
     ORDER BY COALESCE(updated_at_utc, created_at_utc) DESC, id DESC
     LIMIT 200"
);

echo '<style type="text/css">
    .jsauth-comms-table th.jsauth-comms-col-time, .jsauth-comms-table td.jsauth-comms-col-time {width: 1%; white-space: nowrap; vertical-align: top;}
    .jsauth-comms-table th.jsauth-comms-col-actions, .jsauth-comms-table td.jsauth-comms-col-actions {width: 1%; white-space: nowrap;}
    .jsauth-comms-dt {line-height: 1.15;}
    .jsauth-comms-dt-date {font-size: 12px;}
    .jsauth-comms-dt-time {font-size: 12px; opacity: 0.9;}
    .jsauth-comms-dt-empty {font-size: 12px; opacity: 0.6;}
</style>';

echo '<h3>Current messages</h3>';
echo '<table class="widefat striped jsauth-comms-table" style="max-width: 1100px;">';

$posted_order  = ( $sort === 'posted' && $order === 'asc' ) ? 'desc' : 'asc';
$updated_order = ( $sort === 'updated' && $order === 'asc' ) ? 'desc' : 'asc';

$posted_url  = esc_url( add_query_arg( array( 'sort' => 'posted', 'order' => $posted_order ), $base_url ) );
$updated_url = esc_url( add_query_arg( array( 'sort' => 'updated', 'order' => $updated_order ), $base_url ) );

$posted_arrow  = $sort === 'posted' ? ( $order === 'asc' ? ' &#9650;' : ' &#9660;' ) : '';
$updated_arrow = $sort === 'updated' ? ( $order === 'asc' ? ' &#9650;' : ' &#9660;' ) : '';

echo '<thead><tr>';
echo '<th class="jsauth-comms-col-time"><a href="' . $posted_url . '">Posted</a>' . $posted_arrow . '</th>';
echo '<th class="jsauth-comms-col-time"><a href="' . $updated_url . '">Updated</a>' . $updated_arrow . '</th>';
echo '<th>Message</th>';
echo '<th class="jsauth-comms-col-actions">Actions</th>';
echo '</tr></thead>';
echo '<tbody>';

foreach ( (array) $rows_current as $r ) {
    $id = (int) $r->id;

    $posted_raw  = (string) $r->created_at_utc;
    $posted_full = joes_scanner_auth_comms_format_local_dt( $posted_raw );
    $posted_date = joes_scanner_auth_comms_format_local_date_only( $posted_raw );
    $posted_time = joes_scanner_auth_comms_format_local_time_only( $posted_raw );

    $updated_raw = isset( $r->updated_at_utc ) ? trim( (string) $r->updated_at_utc ) : '';

    echo '<tr>';

    echo '<td class="jsauth-comms-col-time"><div class="jsauth-comms-dt" title="' . esc_attr( $posted_full ) . '">'
        . '<div class="jsauth-comms-dt-date">' . esc_html( $posted_date ) . '</div>'
        . '<div class="jsauth-comms-dt-time">' . esc_html( $posted_time ) . '</div>'
        . '</div></td>';

    if ( $updated_raw === '' ) {
        echo '<td class="jsauth-comms-col-time"><div class="jsauth-comms-dt-empty">Not updated</div></td>';
    } else {
        $updated_full = joes_scanner_auth_comms_format_local_dt( $updated_raw );
        $updated_date = joes_scanner_auth_comms_format_local_date_only( $updated_raw );
        $updated_time = joes_scanner_auth_comms_format_local_time_only( $updated_raw );

        echo '<td class="jsauth-comms-col-time"><div class="jsauth-comms-dt" title="' . esc_attr( $updated_full ) . '">'
            . '<div class="jsauth-comms-dt-date">' . esc_html( $updated_date ) . '</div>'
            . '<div class="jsauth-comms-dt-time">' . esc_html( $updated_time ) . '</div>'
            . '</div></td>';
    }

    echo '<td>';

    $heading = isset( $r->heading_text ) ? trim( (string) $r->heading_text ) : '';

    echo '<div id="jsauth_comms_display_' . esc_attr( (string) $id ) . '" style="white-space: pre-wrap;">';
    if ( $heading !== '' ) {
        echo '<div style="font-weight:700; margin-bottom:4px;">' . esc_html( $heading ) . '</div>';
    }
    echo '<div>' . esc_html( (string) $r->message_text ) . '</div>';
    echo '</div>';

    echo '<div id="jsauth_comms_edit_' . esc_attr( (string) $id ) . '" style="display:none; margin-top:8px;">';
    echo '<form method="post" style="margin:0;">';
    wp_nonce_field( 'jsauth_comms_action', 'jsauth_comms_nonce' );
    echo '<input type="hidden" name="jsauth_comms_action" value="edit" />';
    echo '<input type="hidden" name="jsauth_message_id" value="' . esc_attr( (string) $id ) . '" />';
    echo '<div style="max-width: 900px;">';
    echo '<div style="font-weight: 600; margin: 0 0 4px 0;">Heading</div>';
    echo '<input type="text" name="jsauth_heading_text" style="width: 100%;" placeholder="Heading (optional)" value="' . esc_attr( (string) $heading ) . '" />';
    echo '<div style="height: 10px;"></div>';
    echo '<div style="font-weight: 600; margin: 0 0 4px 0;">Message</div>';
    echo '<textarea name="jsauth_message_text" rows="4" style="width: 100%;">' . esc_textarea( (string) $r->message_text ) . '</textarea>';
    echo '</div>';
    echo '<p style="margin: 6px 0 0 0;">';
    echo '<button type="submit" class="button button-primary">Save</button> ';
    echo '<button type="button" class="button" onclick="jsauthCommsCancelEdit(' . (int) $id . ');">Cancel</button>';
    echo '</p>';
    echo '</form>';
    echo '</div>';

    echo '</td>';

    echo '<td class="jsauth-comms-col-actions">';
    echo '<button id="jsauth_comms_editbtn_' . esc_attr( (string) $id ) . '" type="button" class="button" onclick="jsauthCommsStartEdit(' . (int) $id . ');">Edit</button> ';
    echo '<span id="jsauth_comms_removewrap_' . esc_attr( (string) $id ) . '">';
    echo '<form method="post" style="margin:0;">';
    wp_nonce_field( 'jsauth_comms_action', 'jsauth_comms_nonce' );
    echo '<input type="hidden" name="jsauth_comms_action" value="remove" />';
    echo '<input type="hidden" name="jsauth_message_id" value="' . esc_attr( (string) $id ) . '" />';
    echo '<button type="submit" class="button" onclick="return confirm(&quot;Remove this message from all clients?&quot;);">Remove</button>';
    echo '</form>';
    echo '</span>';
    echo '</td>';

    echo '</tr>';
}

echo '</tbody></table>';

echo '<h3 style="margin-top: 18px;">Archived or removed messages</h3>';
echo '<table class="widefat striped jsauth-comms-table" style="max-width: 1100px;">';
echo '<thead><tr>';
echo '<th class="jsauth-comms-col-time">Posted</th>';
echo '<th class="jsauth-comms-col-time">Updated</th>';
echo '<th>Message</th>';
echo '</tr></thead>';
echo '<tbody>';

foreach ( (array) $rows_archived as $r ) {
    $posted_raw  = (string) $r->created_at_utc;
    $posted_full = joes_scanner_auth_comms_format_local_dt( $posted_raw );
    $posted_date = joes_scanner_auth_comms_format_local_date_only( $posted_raw );
    $posted_time = joes_scanner_auth_comms_format_local_time_only( $posted_raw );

    $updated_raw = isset( $r->updated_at_utc ) ? trim( (string) $r->updated_at_utc ) : '';

    echo '<tr>';

    echo '<td class="jsauth-comms-col-time"><div class="jsauth-comms-dt" title="' . esc_attr( $posted_full ) . '">'
        . '<div class="jsauth-comms-dt-date">' . esc_html( $posted_date ) . '</div>'
        . '<div class="jsauth-comms-dt-time">' . esc_html( $posted_time ) . '</div>'
        . '</div></td>';

    if ( $updated_raw === '' ) {
        echo '<td class="jsauth-comms-col-time"><div class="jsauth-comms-dt-empty">Not updated</div></td>';
    } else {
        $updated_full = joes_scanner_auth_comms_format_local_dt( $updated_raw );
        $updated_date = joes_scanner_auth_comms_format_local_date_only( $updated_raw );
        $updated_time = joes_scanner_auth_comms_format_local_time_only( $updated_raw );

        echo '<td class="jsauth-comms-col-time"><div class="jsauth-comms-dt" title="' . esc_attr( $updated_full ) . '">'
            . '<div class="jsauth-comms-dt-date">' . esc_html( $updated_date ) . '</div>'
            . '<div class="jsauth-comms-dt-time">' . esc_html( $updated_time ) . '</div>'
            . '</div></td>';
    }

    echo '<td>';
    $heading = isset( $r->heading_text ) ? trim( (string) $r->heading_text ) : '';
    echo '<div style="font-size:12px; opacity:0.75; margin-bottom:4px;"><strong>Removed</strong></div>';
    if ( $heading !== '' ) {
        echo '<div style="white-space: pre-wrap; opacity:0.65; font-weight:700; margin-bottom:4px;">' . esc_html( $heading ) . '</div>';
    }
    echo '<div style="white-space: pre-wrap; opacity:0.65;">' . esc_html( (string) $r->message_text ) . '</div>';
    echo '</td>';

    echo '</tr>';
}

echo '</tbody></table>';
    echo '<script type="text/javascript">
    function jsauthCommsStartEdit(id) {
        try {
            var openEdits = document.querySelectorAll("div[id^=\"jsauth_comms_edit_\"]");
            openEdits.forEach(function(el) {
                if (el && el.style && el.style.display !== "none") {
                    var otherId = el.id.replace("jsauth_comms_edit_", "");
                    jsauthCommsCancelEdit(otherId);
                }
            });
        } catch (e) {}

        var displayEl = document.getElementById("jsauth_comms_display_" + id);
        var editEl = document.getElementById("jsauth_comms_edit_" + id);
        var editBtn = document.getElementById("jsauth_comms_editbtn_" + id);
        var removeWrap = document.getElementById("jsauth_comms_removewrap_" + id);

        if (displayEl) { displayEl.style.display = "none"; }
        if (editEl) { editEl.style.display = "block"; }

        if (editBtn) { editBtn.style.display = "none"; }
        if (removeWrap) { removeWrap.style.display = "none"; }

        if (editEl) {
            var ta = editEl.querySelector("textarea");
            if (ta) { ta.focus(); ta.selectionStart = ta.value.length; ta.selectionEnd = ta.value.length; }
        }
    }

    function jsauthCommsCancelEdit(id) {
        var displayEl = document.getElementById("jsauth_comms_display_" + id);
        var editEl = document.getElementById("jsauth_comms_edit_" + id);
        var editBtn = document.getElementById("jsauth_comms_editbtn_" + id);
        var removeWrap = document.getElementById("jsauth_comms_removewrap_" + id);

        if (displayEl) { displayEl.style.display = "block"; }
        if (editEl) { editEl.style.display = "none"; }

        if (editBtn) { editBtn.style.display = "inline-block"; }
        if (removeWrap) { removeWrap.style.display = "inline"; }
    }
    </script>';

}
