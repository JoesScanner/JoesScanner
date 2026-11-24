<?php
/*
//|--------------------------------------------------------------------------//| Admin UI//|--------------------------------------------------------------------------*/

function joes_scanner_auth_section_main_cb() {
    echo '<p>Configure which current SureCart recurring prices count as valid scanner subscriptions. Existing subscriptions that still use an older version of a configured price are matched automatically through the SureCart product. Set level 0 to ignore a price.</p>';
}

function joes_scanner_auth_format_subscription_amount_label( $amount, $currency ) {
    if ( $amount === null || $amount === '' || ! is_numeric( $amount ) ) {
        return '';
    }

    $currency = strtolower( trim( (string) $currency ) );
    if ( $currency === '' ) {
        $currency = 'usd';
    }

    if ( function_exists( 'sc_format_money' ) ) {
        try {
            $formatted = sc_format_money( (int) $amount, $currency );
            if ( is_string( $formatted ) && trim( $formatted ) !== '' ) {
                return trim( $formatted );
            }
        } catch ( \Throwable $e ) {
        }
    }

    $value = ((float) $amount) / 100;
    $number = number_format( $value, ( floor( $value ) == $value ) ? 0 : 2 );
    $symbol = '$';
    if ( $currency !== 'usd' ) {
        $symbol = strtoupper( $currency ) . ' ';
    }

    return $symbol . $number;
}

function joes_scanner_auth_build_subscription_label( $product_name, $price ) {
    $product_name = trim( (string) $product_name );
    $price_name = '';
    $currency = '';
    $amount = null;
    $interval = '';
    $interval_count = null;

    if ( is_array( $price ) ) {
        foreach ( array( 'nickname', 'name', 'label', 'display_name', 'price_label' ) as $key ) {
            if ( isset( $price[ $key ] ) && trim( (string) $price[ $key ] ) !== '' ) {
                $price_name = trim( (string) $price[ $key ] );
                break;
            }
        }
        $currency = isset( $price['currency'] ) ? (string) $price['currency'] : '';
        if ( isset( $price['amount'] ) ) {
            $amount = $price['amount'];
        } elseif ( isset( $price['ad_hoc_amount'] ) ) {
            $amount = $price['ad_hoc_amount'];
        } elseif ( isset( $price['subtotal_amount'] ) ) {
            $amount = $price['subtotal_amount'];
        }
        $interval = isset( $price['recurring_interval'] ) ? (string) $price['recurring_interval'] : '';
        $interval_count = isset( $price['recurring_interval_count'] ) ? $price['recurring_interval_count'] : null;
    } elseif ( is_object( $price ) ) {
        foreach ( array( 'nickname', 'name', 'label', 'display_name', 'price_label' ) as $key ) {
            if ( isset( $price->$key ) && trim( (string) $price->$key ) !== '' ) {
                $price_name = trim( (string) $price->$key );
                break;
            }
        }
        $currency = isset( $price->currency ) ? (string) $price->currency : '';
        if ( isset( $price->amount ) ) {
            $amount = $price->amount;
        } elseif ( isset( $price->ad_hoc_amount ) ) {
            $amount = $price->ad_hoc_amount;
        } elseif ( isset( $price->subtotal_amount ) ) {
            $amount = $price->subtotal_amount;
        }
        $interval = isset( $price->recurring_interval ) ? (string) $price->recurring_interval : '';
        $interval_count = isset( $price->recurring_interval_count ) ? $price->recurring_interval_count : null;
    }

    $parts = array();
    if ( $product_name !== '' ) {
        $parts[] = $product_name;
    }
    if ( $price_name !== '' && strcasecmp( $price_name, $product_name ) !== 0 ) {
        $parts[] = $price_name;
    }

    $amount_label = joes_scanner_auth_format_subscription_amount_label( $amount, $currency );
    if ( $amount_label !== '' ) {
        $cadence = '';
        $interval = strtolower( trim( (string) $interval ) );
        $interval_count = is_numeric( $interval_count ) ? (int) $interval_count : 0;
        if ( $interval !== '' ) {
            $cadence = ' / ';
            if ( $interval_count > 1 ) {
                $cadence .= $interval_count . ' ' . $interval;
                if ( substr( $interval, -1 ) !== 's' ) {
                    $cadence .= 's';
                }
            } else {
                $cadence .= $interval;
            }
        }
        $parts[] = '(' . $amount_label . $cadence . ')';
    }

    $label = trim( implode( ' ', $parts ) );
    return $label !== '' ? $label : 'Unnamed subscription';
}


function joes_scanner_auth_truncate_table_text( $text, $max_len = 50 ) {
    $text = trim( (string) $text );
    $max_len = max( 4, (int) $max_len );

    if ( $text === '' ) {
        return array( '', false );
    }

    if ( function_exists( 'mb_strlen' ) && function_exists( 'mb_substr' ) ) {
        if ( mb_strlen( $text ) <= $max_len ) {
            return array( $text, false );
        }
        return array( rtrim( mb_substr( $text, 0, $max_len - 1 ) ) . '…', true );
    }

    if ( strlen( $text ) <= $max_len ) {
        return array( $text, false );
    }

    return array( rtrim( substr( $text, 0, $max_len - 1 ) ) . '…', true );
}

function joes_scanner_auth_format_ip_for_table( $ip ) {
    $ip = trim( (string) $ip );
    if ( $ip === '' ) {
        return array( '', '', '' );
    }

    $title = $ip;
    $cls   = '';
    $disp  = $ip;

    if ( strpos( $ip, ':' ) !== false ) {
        $cls = 'jsauth-ipv6';
        if ( strlen( $ip ) > 24 ) {
            $disp = substr( $ip, 0, 12 ) . '…' . substr( $ip, -8 );
        }
    } elseif ( strlen( $ip ) > 20 ) {
        $disp = substr( $ip, 0, 20 ) . '…';
    }

    return array( $disp, $title, $cls );
}

/**
 * Return iterable records from a SureCart model collection without depending
 * on the REST include (admin.php is loaded before rest.php).
 */
function joes_scanner_auth_admin_get_surecart_collection_records( $collection ) {
    if ( is_wp_error( $collection ) ) {
        return array();
    }

    if ( is_array( $collection ) ) {
        if ( isset( $collection['data'] ) && is_iterable( $collection['data'] ) ) {
            return $collection['data'];
        }
        return $collection;
    }

    if ( is_object( $collection ) && isset( $collection->data ) && is_iterable( $collection->data ) ) {
        return $collection->data;
    }

    if ( is_iterable( $collection ) ) {
        return $collection;
    }

    return array();
}

/**
 * Read a field from either a SureCart model object or an array response.
 */
function joes_scanner_auth_admin_get_surecart_field( $record, $key, $default = null ) {
    if ( is_array( $record ) && array_key_exists( $key, $record ) ) {
        return $record[ $key ];
    }

    if ( is_object( $record ) && isset( $record->$key ) ) {
        return $record->$key;
    }

    return $default;
}

/**
 * Fetch a paginated SureCart model collection. SureCart currently caps list
 * requests at 100 records, so collect several pages defensively.
 */
function joes_scanner_auth_admin_get_all_surecart_models( $class_name, $filters = array(), $max_pages = 10 ) {
    $records = array();

    if ( ! class_exists( $class_name ) || ! method_exists( $class_name, 'where' ) ) {
        return $records;
    }

    $max_pages = max( 1, min( 50, (int) $max_pages ) );

    for ( $page = 1; $page <= $max_pages; $page++ ) {
        $params = is_array( $filters ) ? $filters : array();
        $params['limit'] = 100;
        $params['page']  = $page;

        try {
            $query = $class_name::where( $params );
            $collection = is_object( $query ) && method_exists( $query, 'get' ) ? $query->get() : array();
        } catch ( \Throwable $e ) {
            break;
        }

        if ( is_wp_error( $collection ) ) {
            break;
        }

        $page_records = joes_scanner_auth_admin_get_surecart_collection_records( $collection );
        $page_count   = 0;

        foreach ( $page_records as $record ) {
            $records[] = $record;
            $page_count++;
        }

        if ( $page_count < 100 ) {
            break;
        }
    }

    return $records;
}

/**
 * Normalize a SureCart relationship value into an object ID.
 */
function joes_scanner_auth_admin_get_surecart_relation_id( $value ) {
    if ( is_scalar( $value ) ) {
        return trim( (string) $value );
    }

    if ( is_array( $value ) && isset( $value['id'] ) ) {
        return trim( (string) $value['id'] );
    }

    if ( is_object( $value ) && isset( $value->id ) ) {
        return trim( (string) $value->id );
    }

    return '';
}

/**
 * Build a normalized list of current recurring SureCart prices.
 *
 * SureCart products and prices are API resources. Older plugin versions read
 * only two internal sc_get_product() fields, which can omit prices after a
 * SureCart update or after a price is versioned. The current implementation
 * uses the public Product and Price PHP models first, then merges the legacy
 * WordPress product data as a compatibility fallback.
 */
function joes_scanner_auth_get_all_subscription_items() {
    $results     = array();
    $product_map = array();

    // Current SureCart API model path.
    foreach ( joes_scanner_auth_admin_get_all_surecart_models( '\\SureCart\\Models\\Product', array() ) as $product ) {
        $product_id = trim( (string) joes_scanner_auth_admin_get_surecart_field( $product, 'id', '' ) );
        if ( $product_id === '' ) {
            continue;
        }

        $product_map[ $product_id ] = array(
            'name'      => trim( (string) joes_scanner_auth_admin_get_surecart_field( $product, 'name', $product_id ) ),
            'recurring' => (bool) joes_scanner_auth_admin_get_surecart_field( $product, 'recurring', false ),
            'post_id'   => 0,
        );
    }

    // Preserve WordPress post names and support older SureCart releases.
    if ( function_exists( 'sc_get_product' ) && class_exists( 'WP_Query' ) ) {
        $query = new WP_Query(
            array(
                'post_type'      => 'sc_product',
                'post_status'    => array( 'publish', 'private', 'draft' ),
                'posts_per_page' => 500,
                'no_found_rows'  => true,
                'fields'         => 'ids',
            )
        );

        foreach ( (array) $query->posts as $post_id ) {
            try {
                $product_data = sc_get_product( $post_id );
            } catch ( \Throwable $e ) {
                $product_data = null;
            }

            if ( ! $product_data ) {
                continue;
            }

            $product_name      = get_the_title( $post_id );
            $product_recurring = false;
            $prices            = array();

            if ( is_array( $product_data ) ) {
                if ( ! empty( $product_data['name'] ) ) {
                    $product_name = (string) $product_data['name'];
                } elseif ( ! empty( $product_data['post']['post_title'] ) ) {
                    $product_name = (string) $product_data['post']['post_title'];
                }

                if ( isset( $product_data['recurring'] ) ) {
                    $product_recurring = (bool) $product_data['recurring'];
                } elseif ( isset( $product_data['raw_product_sample']['recurring'] ) ) {
                    $product_recurring = (bool) $product_data['raw_product_sample']['recurring'];
                }

                if ( isset( $product_data['prices']['data'] ) && is_iterable( $product_data['prices']['data'] ) ) {
                    $prices = $product_data['prices']['data'];
                } elseif ( isset( $product_data['raw_product_sample']['prices']['data'] ) && is_iterable( $product_data['raw_product_sample']['prices']['data'] ) ) {
                    $prices = $product_data['raw_product_sample']['prices']['data'];
                } elseif ( isset( $product_data['active_prices'] ) && is_iterable( $product_data['active_prices'] ) ) {
                    $prices = $product_data['active_prices'];
                }
            } elseif ( is_object( $product_data ) ) {
                if ( ! empty( $product_data->name ) ) {
                    $product_name = (string) $product_data->name;
                } elseif ( ! empty( $product_data->post->post_title ) ) {
                    $product_name = (string) $product_data->post->post_title;
                }

                if ( isset( $product_data->recurring ) ) {
                    $product_recurring = (bool) $product_data->recurring;
                } elseif ( isset( $product_data->raw_product_sample->recurring ) ) {
                    $product_recurring = (bool) $product_data->raw_product_sample->recurring;
                }

                if ( isset( $product_data->prices->data ) && is_iterable( $product_data->prices->data ) ) {
                    $prices = $product_data->prices->data;
                } elseif ( isset( $product_data->raw_product_sample->prices->data ) && is_iterable( $product_data->raw_product_sample->prices->data ) ) {
                    $prices = $product_data->raw_product_sample->prices->data;
                } elseif ( isset( $product_data->active_prices ) && is_iterable( $product_data->active_prices ) ) {
                    $prices = $product_data->active_prices;
                }
            }

            $possible_refs = function_exists( 'joes_scanner_auth_collect_possible_product_refs' )
                ? joes_scanner_auth_collect_possible_product_refs( $product_data )
                : array();

            foreach ( $possible_refs as $product_ref ) {
                $product_ref = trim( (string) $product_ref );
                if ( $product_ref === '' || ctype_digit( $product_ref ) ) {
                    continue;
                }

                if ( ! isset( $product_map[ $product_ref ] ) ) {
                    $product_map[ $product_ref ] = array(
                        'name'      => trim( (string) $product_name ),
                        'recurring' => $product_recurring,
                        'post_id'   => (int) $post_id,
                    );
                } else {
                    if ( trim( (string) $product_name ) !== '' ) {
                        $product_map[ $product_ref ]['name'] = trim( (string) $product_name );
                    }
                    $product_map[ $product_ref ]['recurring'] = $product_map[ $product_ref ]['recurring'] || $product_recurring;
                    $product_map[ $product_ref ]['post_id']   = (int) $post_id;
                }
            }

            // Merge any prices still exposed by the legacy product helper.
            foreach ( $prices as $price ) {
                $price_id = trim( (string) joes_scanner_auth_admin_get_surecart_field( $price, 'id', '' ) );
                if ( $price_id === '' ) {
                    continue;
                }

                $product_id = joes_scanner_auth_admin_get_surecart_relation_id(
                    joes_scanner_auth_admin_get_surecart_field( $price, 'product', '' )
                );

                $interval       = joes_scanner_auth_admin_get_surecart_field( $price, 'recurring_interval', null );
                $interval_count = joes_scanner_auth_admin_get_surecart_field( $price, 'recurring_interval_count', null );
                $period_count   = joes_scanner_auth_admin_get_surecart_field( $price, 'recurring_period_count', null );
                $price_recurring = ! empty( $interval ) || ! empty( $interval_count ) || ! empty( $period_count ) || $product_recurring;

                if ( ! $price_recurring ) {
                    continue;
                }

                $results[ $price_id ] = array(
                    'id'         => $price_id,
                    'label'      => joes_scanner_auth_build_subscription_label( $product_name, $price ),
                    'post_id'    => (int) $post_id,
                    'product_id' => $product_id,
                    'price_ids'  => array( $price_id ),
                );
            }
        }
    }

    // Direct price model discovery is the authoritative current path.
    foreach ( joes_scanner_auth_admin_get_all_surecart_models( '\\SureCart\\Models\\Price', array() ) as $price ) {
        $price_id = trim( (string) joes_scanner_auth_admin_get_surecart_field( $price, 'id', '' ) );
        if ( $price_id === '' ) {
            continue;
        }

        $archived = joes_scanner_auth_admin_get_surecart_field( $price, 'archived', false );
        if ( $archived === true || $archived === 1 || $archived === '1' ) {
            continue;
        }

        // Do not clutter settings with historical price versions. Existing
        // subscriptions on an older version are mapped back to the current
        // configured item by product ID during authentication.
        $current_version = joes_scanner_auth_admin_get_surecart_field( $price, 'current_version', null );
        if ( $current_version === false || $current_version === 0 || $current_version === '0' ) {
            continue;
        }

        $product_id = joes_scanner_auth_admin_get_surecart_relation_id(
            joes_scanner_auth_admin_get_surecart_field( $price, 'product', '' )
        );

        $product_name      = $product_id !== '' && isset( $product_map[ $product_id ] ) ? (string) $product_map[ $product_id ]['name'] : '';
        $product_recurring = $product_id !== '' && isset( $product_map[ $product_id ] ) ? (bool) $product_map[ $product_id ]['recurring'] : false;
        $post_id           = $product_id !== '' && isset( $product_map[ $product_id ] ) ? (int) $product_map[ $product_id ]['post_id'] : 0;

        $interval       = joes_scanner_auth_admin_get_surecart_field( $price, 'recurring_interval', null );
        $interval_count = joes_scanner_auth_admin_get_surecart_field( $price, 'recurring_interval_count', null );
        $period_count   = joes_scanner_auth_admin_get_surecart_field( $price, 'recurring_period_count', null );
        $price_recurring = ! empty( $interval ) || ! empty( $interval_count ) || ! empty( $period_count ) || $product_recurring;

        if ( ! $price_recurring ) {
            continue;
        }

        if ( $product_name === '' ) {
            $product_name = 'SureCart subscription';
        }

        $results[ $price_id ] = array(
            'id'         => $price_id,
            'label'      => joes_scanner_auth_build_subscription_label( $product_name, $price ),
            'post_id'    => $post_id,
            'product_id' => $product_id,
            'price_ids'  => array( $price_id ),
        );
    }

    uasort(
        $results,
        function ( $a, $b ) {
            $cmp = strcasecmp( (string) $a['label'], (string) $b['label'] );
            if ( $cmp !== 0 ) {
                return $cmp;
            }
            return strcasecmp( (string) $a['id'], (string) $b['id'] );
        }
    );

    return array_values( $results );
}

/**
 * Backward-compatible helper that flattens current subscription items into a price list.
 */
function joes_scanner_auth_get_all_subscription_prices() {
    $results = array();
    foreach ( joes_scanner_auth_get_all_subscription_items() as $item ) {
        $label = isset( $item['label'] ) ? (string) $item['label'] : '';
        $price_ids = isset( $item['price_ids'] ) && is_array( $item['price_ids'] ) ? $item['price_ids'] : array();
        foreach ( $price_ids as $price_id ) {
            $price_id = trim( (string) $price_id );
            if ( $price_id === '' ) {
                continue;
            }
            $results[ $price_id ] = array(
                'id'    => $price_id,
                'label' => $label !== '' ? $label : $price_id,
            );
        }
    }
    return array_values( $results );
}

function joes_scanner_auth_get_subscription_item_for_price_id( $price_id ) {
    $price_id = trim( (string) $price_id );
    if ( $price_id === '' ) {
        return null;
    }

    foreach ( joes_scanner_auth_get_all_subscription_items() as $item ) {
        if ( isset( $item['id'] ) && (string) $item['id'] === $price_id ) {
            return $item;
        }
    }

    return null;
}

function joes_scanner_auth_get_label_parts_for_price( $price_id ) {
    $label = joes_scanner_auth_get_price_label_by_id( $price_id );
    return array(
        'full'  => $label,
        'plan'  => $label,
        'price' => $label,
    );
}

function joes_scanner_auth_get_price_label_for_api( $price_id ) {
    $item = joes_scanner_auth_get_subscription_item_for_price_id( $price_id );
    if ( is_array( $item ) && isset( $item['label'] ) ) {
        return (string) $item['label'];
    }
    return trim( (string) $price_id );
}

function joes_scanner_auth_get_plan_label_for_api( $price_id ) {
    return joes_scanner_auth_get_price_label_for_api( $price_id );
}

function joes_scanner_auth_get_price_label_by_id( $price_id ) {
    $price_id = trim( (string) $price_id );
    if ( $price_id === '' ) {
        return '';
    }

    $item = joes_scanner_auth_get_subscription_item_for_price_id( $price_id );
    if ( is_array( $item ) && isset( $item['label'] ) && trim( (string) $item['label'] ) !== '' ) {
        return (string) $item['label'];
    }

    return $price_id;
}

function joes_scanner_auth_allowed_price_ids_render() {
    $options     = joes_scanner_auth_get_options();
    $item_levels = isset( $options['subscription_item_levels'] ) && is_array( $options['subscription_item_levels'] ) ? $options['subscription_item_levels'] : array();

    $available_items = joes_scanner_auth_get_all_subscription_items();

    if ( empty( $available_items ) ) {
        echo '<p>No SureCart recurring subscriptions could be detected. Make sure SureCart is active and your subscriptions are published.</p>';
        return;
    }

    echo '<p>Each current recurring SureCart price is listed separately. Historical price versions used by existing subscribers are matched automatically. Set level 0 to ignore a price, or any positive number to grant that level.</p>';

    foreach ( $available_items as $item ) {
        if ( ! isset( $item['id'], $item['label'] ) ) {
            continue;
        }

        $id    = (string) $item['id'];
        $label = (string) $item['label'];
        $lvl   = isset( $item_levels[ $id ] ) && is_numeric( $item_levels[ $id ] ) ? (int) $item_levels[ $id ] : 0;
        $lvl   = max( 0, min( 99, $lvl ) );
        ?>
        <label style="display:block; margin: 2px 0;">
            <span style="display:inline-block; min-width: 55px;">Level:</span>
            <input
                type="number"
                name="joes_scanner_auth_options[subscription_item_levels][<?php echo esc_attr( $id ); ?>]"
                value="<?php echo esc_attr( $lvl ); ?>"
                min="0"
                max="99"
                step="1"
                style="width:70px; margin-right:10px;"
            />
            <?php echo esc_html( $label ); ?>
            <code style="margin-left:8px; font-size:11px;"><?php echo esc_html( $id ); ?></code>
        </label>
        <?php
    }
}

function joes_scanner_auth_get_supported_subscription_statuses() {
    return array(
        'active'     => 'Active',
        'trialing'   => 'Trialing',
        'past_due'   => 'Past due',
        'incomplete' => 'Incomplete',
        'unpaid'     => 'Unpaid',
        'canceled'   => 'Canceled',
        'completed'  => 'Completed',
    );
}

function joes_scanner_auth_allowed_statuses_render() {
    $selected = joes_scanner_auth_get_allowed_statuses();
    $selected_set = array_fill_keys( $selected, true );
    $available = joes_scanner_auth_get_supported_subscription_statuses();

    echo '<p>Choose which SureCart subscription statuses should count as valid scanner access.</p>';
    echo '<input type="hidden" name="joes_scanner_auth_options[allowed_statuses_present]" value="1" />';

    foreach ( $available as $status_key => $status_label ) {
        $input_id = 'joes_scanner_auth_status_' . sanitize_html_class( $status_key );
        echo '<label for="' . esc_attr( $input_id ) . '" style="display:block; margin: 2px 0;">';
        echo '<input id="' . esc_attr( $input_id ) . '" type="checkbox" name="joes_scanner_auth_options[allowed_statuses][]" value="' . esc_attr( $status_key ) . '" ' . checked( isset( $selected_set[ $status_key ] ), true, false ) . ' /> ';
        echo esc_html( $status_label ) . ' <code>(' . esc_html( $status_key ) . ')</code>';
        echo '</label>';
    }

    echo '<p class="description">If none are checked, the plugin will fall back to <strong>active</strong>.</p>';
}

function joes_scanner_auth_tracking_display_settings_render() {
    $opts = joes_scanner_auth_get_options();
    ?>
    <table class="form-table" role="presentation">
        <tbody>
            <tr>
                <th scope="row"><label for="joes_scanner_auth_active_window_minutes">Active window minutes</label></th>
                <td>
                    <input
                        type="number"
                        min="1"
                        max="120"
                        id="joes_scanner_auth_active_window_minutes"
                        name="joes_scanner_auth_options[active_window_minutes]"
                        value="<?php echo esc_attr( (int) $opts['active_window_minutes'] ); ?>"
                    />
                    <p class="description">A session is considered active if it pinged within this many minutes.</p>
                </td>
            </tr>
            <tr>
                <th scope="row"><label for="joes_scanner_auth_active_tab_inactive_sessions">Recently inactive sessions</label></th>
                <td>
                    <input
                        type="number"
                        min="0"
                        max="500"
                        id="joes_scanner_auth_active_tab_inactive_sessions"
                        name="joes_scanner_auth_options[active_tab_inactive_sessions]"
                        value="<?php echo esc_attr( (int) $opts['active_tab_inactive_sessions'] ); ?>"
                    />
                    <p class="description">How many inactive sessions to show under the active list. Set to 0 to hide.</p>
                </td>
            </tr>
<tr>
    <th scope="row"><label for="joes_scanner_auth_active_tab_refresh_seconds">Auto refresh seconds</label></th>
    <td>
        <input
            type="number"
            min="0"
            max="600"
            id="joes_scanner_auth_active_tab_refresh_seconds"
            name="joes_scanner_auth_options[active_tab_refresh_seconds]"
            value="<?php echo esc_attr( (int) $opts['active_tab_refresh_seconds'] ); ?>"
        />
        <p class="description">Set to 0 to disable. Allowed range is 1 to 600 seconds.</p>
    </td>
</tr>
        </tbody>
    </table>
    <?php
}


function joes_scanner_auth_field_surecart_enabled() {
    $opts    = joes_scanner_auth_get_options();
    $enabled = isset( $opts['surecart_enabled'] ) ? (int) $opts['surecart_enabled'] : 1;

    echo '<input type="hidden" name="' . esc_attr( JOES_SCANNER_AUTH_OPTIONS_KEY ) . '[surecart_enabled]" value="0" />';
    echo '<label>';
    echo '<input type="checkbox" name="' . esc_attr( JOES_SCANNER_AUTH_OPTIONS_KEY ) . '[surecart_enabled]" value="1" ' . checked( 1, $enabled, false ) . ' /> ';
    echo 'Enable SureCart subscription validation';
    echo '</label>';
    echo '<p class="description">When disabled, the SureCart tab will show no settings and the API will not validate subscriptions.</p>';
}

function joes_scanner_auth_field_active_window_minutes() {
    $opts = joes_scanner_auth_get_options();
    ?>
    <input
        type="number"
        min="1"
        max="120"
        id="joes_scanner_auth_active_window_minutes"
        name="joes_scanner_auth_options[active_window_minutes]"
        value="<?php echo esc_attr( (int) $opts['active_window_minutes'] ); ?>"
    />
    <p class="description">A session is considered active if it pinged within this many minutes.</p>
    <?php
}

function joes_scanner_auth_field_active_tab_inactive_sessions() {
    $opts = joes_scanner_auth_get_options();
    ?>
    <input
        type="number"
        min="0"
        max="500"
        id="joes_scanner_auth_active_tab_inactive_sessions"
        name="joes_scanner_auth_options[active_tab_inactive_sessions]"
        value="<?php echo esc_attr( (int) $opts['active_tab_inactive_sessions'] ); ?>"
    />
    <p class="description">How many inactive sessions to show under the active list. Set to 0 to hide.</p>
    <?php
}

function joes_scanner_auth_field_active_tab_refresh_seconds() {
    $opts = joes_scanner_auth_get_options();
    ?>
    <input
        type="number"
        min="0"
        max="600"
        id="joes_scanner_auth_active_tab_refresh_seconds"
        name="joes_scanner_auth_options[active_tab_refresh_seconds]"
        value="<?php echo esc_attr( (int) $opts['active_tab_refresh_seconds'] ); ?>"
    />
    <p class="description">Set to 0 to disable. Allowed range is 1 to 600 seconds.</p>
    <?php
}

function joes_scanner_auth_field_minimum_app_version() {
    $opts = joes_scanner_auth_get_options();
    $value = isset( $opts['minimum_app_version'] ) ? (string) $opts['minimum_app_version'] : '';
    ?>
    <input
        type="text"
        inputmode="numeric"
        id="joes_scanner_auth_minimum_app_version"
        name="joes_scanner_auth_options[minimum_app_version]"
        value="<?php echo esc_attr( $value ); ?>"
        placeholder="1.0.0"
        pattern="[0-9.]*"
    />
    <p class="description">Leave blank to allow all versions. Enter the minimum allowed app version, such as 26, 1.2.3, or 2026.3.14. Older app versions will be rejected by the server during auth and ping.</p>
    <?php
}


function joes_scanner_auth_event_retention_days_render() {
    $opts  = joes_scanner_auth_get_options();
    $value = isset( $opts['event_retention_days'] ) ? (int) $opts['event_retention_days'] : 90;
    ?>
    <input
        type="number"
        min="0"
        max="3650"
        step="1"
        class="small-text"
        name="<?php echo esc_attr( JOES_SCANNER_AUTH_OPTIONS_KEY ); ?>[event_retention_days]"
        value="<?php echo esc_attr( $value ); ?>"
    />
    <p class="description">How long to keep auth events for reporting.</p>
    <?php
}

function joes_scanner_auth_session_retention_days_render() {
    $opts  = joes_scanner_auth_get_options();
    $value = isset( $opts['session_retention_days'] ) ? (int) $opts['session_retention_days'] : 30;
    ?>
    <input
        type="number"
        min="0"
        max="3650"
        step="1"
        class="small-text"
        name="<?php echo esc_attr( JOES_SCANNER_AUTH_OPTIONS_KEY ); ?>[session_retention_days]"
        value="<?php echo esc_attr( $value ); ?>"
    />
    <p class="description">How long to keep inactive sessions.</p>
    <?php
}

function joes_scanner_auth_comms_retention_days_render() {
    $opts  = joes_scanner_auth_get_options();
    $value = isset( $opts['comms_retention_days'] ) ? (int) $opts['comms_retention_days'] : 90;
    ?>
    <input
        type="number"
        min="0"
        max="3650"
        step="1"
        class="small-text"
        name="<?php echo esc_attr( JOES_SCANNER_AUTH_OPTIONS_KEY ); ?>[comms_retention_days]"
        value="<?php echo esc_attr( $value ); ?>"
    />
    <p class="description">How long to keep communications messages and change history. Set to 0 to keep forever.</p>
    <?php
}

function joes_scanner_auth_retention_settings_render() {
    $opts = joes_scanner_auth_get_options();
    ?>
    <table class="form-table" role="presentation">
        <tbody>
<tr>
                <th scope="row"><label for="joes_scanner_auth_event_retention_days">Event retention days</label></th>
                <td>
                    <input
                        type="number"
                        min="1"
                        max="3650"
                        id="joes_scanner_auth_event_retention_days"
                        name="joes_scanner_auth_options[event_retention_days]"
                        value="<?php echo esc_attr( (int) $opts['event_retention_days'] ); ?>"
                    />
                    <p class="description">How long to keep auth events for reporting.</p>
                </td>
            </tr>
            <tr>
                <th scope="row"><label for="joes_scanner_auth_session_retention_days">Session retention days</label></th>
                <td>
                    <input
                        type="number"
                        min="1"
                        max="3650"
                        id="joes_scanner_auth_session_retention_days"
                        name="joes_scanner_auth_options[session_retention_days]"
                        value="<?php echo esc_attr( (int) $opts['session_retention_days'] ); ?>"
                    />
                    <p class="description">How long to keep inactive sessions.</p>
                </td>
            </tr>
        </tbody>
    </table>
    <p class="description">These retention settings are applied when you run Prune Database, and during the daily automatic prune.</p>
    <?php
}


/**
 * Admin settings hooks.
 */
if ( is_admin() ) {
    add_action( 'admin_menu', 'joes_scanner_auth_add_admin_menu' );
    add_action( 'admin_init', 'joes_scanner_auth_settings_init' );
    add_action( 'wp_ajax_joes_scanner_auth_history_account_details', 'joes_scanner_auth_ajax_history_account_details' );
    add_action( 'admin_post_joes_scanner_auth_clear_device', 'joes_scanner_auth_admin_post_clear_device' );
    add_action( 'admin_post_joes_scanner_auth_set_device_blocked', 'joes_scanner_auth_admin_post_set_device_blocked' );
    add_action( 'admin_post_joes_scanner_auth_set_ip_blocked', 'joes_scanner_auth_admin_post_set_ip_blocked' );

    add_action( 'admin_post_joes_scanner_auth_create_backup', 'joes_scanner_auth_admin_post_create_backup' );
    add_action( 'admin_post_joes_scanner_auth_download_backup', 'joes_scanner_auth_admin_post_download_backup' );
    add_action( 'admin_post_joes_scanner_auth_delete_backup', 'joes_scanner_auth_admin_post_delete_backup' );
    add_action( 'admin_post_joes_scanner_auth_restore_backup', 'joes_scanner_auth_admin_post_restore_backup' );
    add_action( 'admin_post_joes_scanner_auth_restore_upload', 'joes_scanner_auth_admin_post_restore_upload' );
    add_action( 'admin_post_joes_scanner_auth_save_backup_schedule', 'joes_scanner_auth_admin_post_save_backup_schedule' );

}

/**
 * Adds a settings page under Settings.
 */


function joes_scanner_auth_ajax_history_account_details() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_send_json_error( array( 'message' => 'Not authorized.' ), 403 );
    }

    $nonce = '';
if ( isset( $_POST['nonce'] ) ) {
    $nonce = sanitize_text_field( wp_unslash( $_POST['nonce'] ) );
} elseif ( isset( $_GET['nonce'] ) ) {
    $nonce = sanitize_text_field( wp_unslash( $_GET['nonce'] ) );
}

if ( empty( $nonce ) || ! wp_verify_nonce( $nonce, 'joes_scanner_auth_history_ajax' ) ) {
    wp_send_json_error( array( 'message' => 'Invalid request.' ), 400 );
}

$user_key = 0;
if ( isset( $_POST['user_key'] ) ) {
    $user_key = (int) wp_unslash( $_POST['user_key'] );
} elseif ( isset( $_GET['user_key'] ) ) {
    $user_key = (int) wp_unslash( $_GET['user_key'] );
}

if ( $user_key < 0 ) {
    wp_send_json_error( array( 'message' => 'Invalid account.' ), 400 );
}


    global $wpdb;
        $devices_table  = joes_scanner_auth_table_devices();
    $sessions_table = joes_scanner_auth_table_sessions();

    $devices = $wpdb->get_results(
        $wpdb->prepare(
            "SELECT
                d.device_id_hash,
                d.is_blocked,
                d.last_ip,
                COALESCE(NULLIF(d.device_platform, ''), (SELECT s.device_platform FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.device_platform IS NOT NULL AND s.device_platform <> '' ORDER BY s.last_seen_utc DESC LIMIT 1)) AS platform,
                COALESCE(NULLIF(d.device_type, ''), (SELECT s.device_type FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.device_type IS NOT NULL AND s.device_type <> '' ORDER BY s.last_seen_utc DESC LIMIT 1)) AS device_type,
                COALESCE(NULLIF(d.device_model, ''), (SELECT s.device_model FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.device_model IS NOT NULL AND s.device_model <> '' ORDER BY s.last_seen_utc DESC LIMIT 1)) AS device_model,
                COALESCE(NULLIF(d.app_version, ''), (SELECT s.app_version FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.app_version IS NOT NULL AND s.app_version <> '' ORDER BY s.last_seen_utc DESC LIMIT 1)) AS app_version,
                (SELECT s.stream_server_url FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.stream_server_url IS NOT NULL AND s.stream_server_url <> '' ORDER BY s.last_seen_utc DESC LIMIT 1) AS stream_server_url,
                (SELECT s.stream_server_is_hosted FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.stream_server_url IS NOT NULL AND s.stream_server_url <> '' ORDER BY s.last_seen_utc DESC LIMIT 1) AS stream_server_is_hosted,
                d.first_seen_utc,
                d.last_seen_utc,
                COALESCE(s.session_count, 0) AS session_count,
                COALESCE(s.total_seconds, 0) AS total_seconds
            FROM {$devices_table} d
            LEFT JOIN (
                SELECT
                    device_id_hash,
                    COUNT(*) AS session_count,
                    SUM(TIMESTAMPDIFF(SECOND, first_seen_utc, last_seen_utc)) AS total_seconds
                FROM {$sessions_table}
                WHERE COALESCE(user_id, 0) = %d
                GROUP BY COALESCE(device_id_hash, '')
            ) s ON s.device_id_hash = d.device_id_hash
            WHERE COALESCE(d.user_id, 0) = %d
            ORDER BY d.last_seen_utc DESC",
            $user_key,
            $user_key
        )
    );


    if ( empty( $devices ) ) {
        // Fallback: if the devices table is missing rows, derive device history from sessions.
        $devices = $wpdb->get_results(
            $wpdb->prepare(
                "SELECT
                    COALESCE(device_id_hash, '') AS device_id_hash,
                    0 AS is_blocked,
                    MAX(last_ip) AS last_ip,
                    MAX(device_platform) AS platform,
                    MAX(device_type) AS device_type,
                    MAX(device_model) AS device_model,
                    MAX(app_version) AS app_version,
                    SUBSTRING_INDEX(GROUP_CONCAT(NULLIF(stream_server_url, '') ORDER BY last_seen_utc DESC SEPARATOR '|'), '|', 1) AS stream_server_url,
                    SUBSTRING_INDEX(GROUP_CONCAT(stream_server_is_hosted ORDER BY last_seen_utc DESC SEPARATOR '|'), '|', 1) AS stream_server_is_hosted,
                    MIN(first_seen_utc) AS first_seen_utc,
                    MAX(last_seen_utc) AS last_seen_utc,
                    COUNT(*) AS session_count,
                    SUM(TIMESTAMPDIFF(SECOND, first_seen_utc, last_seen_utc)) AS total_seconds
                 FROM {$sessions_table}
                 WHERE COALESCE(user_id, 0) = %d
                 GROUP BY COALESCE(device_id_hash, '')
                 ORDER BY last_seen_utc DESC",
                $user_key
            )
        );
    }

    ob_start();

    echo '<table class="widefat striped jsauth-table jsauth-subtable">';
    echo '<thead><tr>';
    echo '<th class="jsauth-col-device">Device</th>';
    echo '<th class="jsauth-col-num">Blocked</th>';
    echo '<th class="jsauth-col-ip">IP</th>';
    echo '<th class="jsauth-col-text">Platform</th>';
    echo '<th class="jsauth-col-text">Model</th>';
    echo '<th class="jsauth-col-app">App</th>';
    echo '<th class="jsauth-col-server">Server</th>';
    echo '<th class="jsauth-col-dt">First seen</th>';
    echo '<th class="jsauth-col-dt">Last seen</th>';
    echo '<th class="jsauth-col-num">Sessions</th>';
    echo '<th class="jsauth-col-dur">Total time</th>';
    echo '<th class="jsauth-col-action">Clear</th>';
    echo '</tr></thead><tbody>';

    if ( empty( $devices ) ) {
        echo '<tr><td colspan="12">No devices for this account.</td></tr>';
    } else {
        $device_index = 0;
        foreach ( $devices as $dev ) {
            $dev_first = ! empty( $dev->first_seen_utc ) ? joes_scanner_auth_format_datetime_local( $dev->first_seen_utc ) : '';
            $dev_last  = ! empty( $dev->last_seen_utc ) ? joes_scanner_auth_format_datetime_local( $dev->last_seen_utc ) : '';
            $dev_time  = joes_scanner_auth_format_duration( (int) $dev->total_seconds );
            $device_index++;
            $dev_summary_main = 'Device ' . (int) $device_index;

            $dev_hash = isset( $dev->device_id_hash ) ? (string) $dev->device_id_hash : '';
            $dev_hash = trim( $dev_hash );
            $is_unknown_device = ( $dev_hash === '' );

            $is_blocked = ( (int) $dev->is_blocked ) === 1;

            $admin_post_url = admin_url( 'admin-post.php' );
            $device_action_nonce = wp_create_nonce( 'joes_scanner_auth_device_action' );

            if ( $is_unknown_device ) {
                $sessions = $wpdb->get_results(
                    $wpdb->prepare(
                        "SELECT
                            session_token,
                            first_seen_utc,
                            last_seen_utc,
                            last_ip,
                            app_version,
                            stream_server_url,
                            stream_server_is_hosted,
                            TIMESTAMPDIFF(SECOND, first_seen_utc, last_seen_utc) AS duration_seconds
                        FROM {$sessions_table}
                        WHERE (device_id_hash IS NULL OR device_id_hash = '') AND COALESCE(user_id, 0) = %d
                        ORDER BY last_seen_utc DESC
                        LIMIT 50",
                        $user_key
                    )
                );
            } else {
                $sessions = $wpdb->get_results(
                    $wpdb->prepare(
                        "SELECT
                            session_token,
                            first_seen_utc,
                            last_seen_utc,
                            last_ip,
                            app_version,
                            stream_server_url,
                            stream_server_is_hosted,
                            TIMESTAMPDIFF(SECOND, first_seen_utc, last_seen_utc) AS duration_seconds
                        FROM {$sessions_table}
                        WHERE device_id_hash = %s AND COALESCE(user_id, 0) = %d
                        ORDER BY last_seen_utc DESC
                        LIMIT 50",
                        $dev_hash,
                        $user_key
                    )
                );
            }

            $dev_row_id = 'jsauth_dev_' . (int) $user_key . '_' . (int) $device_index;

            echo '<tr>';
            echo '<td>';
            echo '<a href="#" class="jsauth-device-toggle" data-target="' . esc_attr( $dev_row_id ) . '" aria-expanded="false">';
            echo '<span class="jsauth-summary-main">' . esc_html( $dev_summary_main ) . '</span>';
            echo '</a>';
                        echo '</td>';

            echo '<td>';
            if ( $is_unknown_device ) {
                echo '<span class="description">N/A</span>';
            } else {
                echo $is_blocked ? 'Yes' : 'No';
            }
            echo '</td>';
            list( $ip_disp, $ip_title, $ip_cls ) = joes_scanner_auth_format_ip_for_table( $dev->last_ip );
            if ( $ip_disp !== '' ) {
                echo '<td class="jsauth-ip-cell ' . esc_attr( $ip_cls ) . '" title="' . esc_attr( $ip_title ) . '"><span class="jsauth-ellipsis">' . esc_html( $ip_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
            echo '<td>' . esc_html( (string) $dev->platform ) . '</td>';
            $dev_model_raw = (string) $dev->device_model;
            list( $dev_model_disp, $dev_model_trunc ) = joes_scanner_auth_truncate_table_text( $dev_model_raw, 25 );
            if ( $dev_model_disp !== '' ) {
                echo '<td title="' . esc_attr( $dev_model_raw ) . '"><span class="jsauth-ellipsis">' . esc_html( $dev_model_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
            echo '<td>' . esc_html( (string) $dev->app_version ) . '</td>';
            $server_url = isset( $dev->stream_server_url ) ? (string) $dev->stream_server_url : '';
            $server_hosted = isset( $dev->stream_server_is_hosted ) ? (int) $dev->stream_server_is_hosted : -1;
            $server_disp = joes_scanner_auth_format_stream_server_display( $server_url, $server_hosted );
            if ( ! empty( $server_disp[0] ) ) {
                list( $srv_disp, $srv_trunc ) = joes_scanner_auth_truncate_table_text( $server_disp[0], 50 );
                echo '<td title="' . esc_attr( $server_disp[1] ) . '"><span class="jsauth-ellipsis">' . esc_html( $srv_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
            echo '<td>' . esc_html( $dev_first ) . '</td>';
            echo '<td>' . esc_html( $dev_last ) . '</td>';
            echo '<td>' . esc_html( (int) $dev->session_count ) . '</td>';
            echo '<td>' . esc_html( $dev_time ) . '</td>';
            echo '<td>';
            echo '<form method="post" action="' . esc_url( $admin_post_url ) . '" style="display:inline;" onsubmit="return confirm(\'Clear sessions for this account and device?\');">';
            echo '<input type="hidden" name="action" value="joes_scanner_auth_clear_device" />';
            echo '<input type="hidden" name="nonce" value="' . esc_attr( $device_action_nonce ) . '" />';
            echo '<input type="hidden" name="scope" value="global" />';
            echo '<input type="hidden" name="user_key" value="' . esc_attr( (string) $user_key ) . '" />';
            echo '<input type="hidden" name="device_id_hash" value="' . esc_attr( (string) $dev_hash ) . '" />';
echo '<button type="submit" class="button button-small">Clear</button>';
            echo '</form>';
            echo '</td>';
            echo '</tr>';

            echo '<tr id="' . esc_attr( $dev_row_id ) . '" class="jsauth-device-details-row" style="display:none;">';
            echo '<td colspan="12">';
            echo '<div class="jsauth-device-details-wrap">';
            if ( $is_unknown_device ) {
            echo '<div class="jsauth-device-id-line"><strong>Device id:</strong> <em>none</em></div>';
        } else {
            echo '<div class="jsauth-device-id-line"><strong>Device id:</strong> <code>' . esc_html( $dev_hash ) . '</code></div>';
        }
            echo '<div class="description" style="margin: 0 0 8px;">Showing most recent 50 sessions for this device.</div>';

echo '<table class="widefat striped jsauth-table jsauth-subtable">';
            echo '<thead><tr>';
            echo '<th class="jsauth-col-session">Session</th>';
            echo '<th class="jsauth-col-ip">IP</th>';
            echo '<th class="jsauth-col-app">App</th>';
            echo '<th class="jsauth-col-server">Server</th>';
            echo '<th class="jsauth-col-dt">Session start</th>';
            echo '<th class="jsauth-col-dt">Last seen</th>';
            echo '<th class="jsauth-col-dur">Session time</th>';
            echo '</tr></thead><tbody>';

            if ( empty( $sessions ) ) {
                echo '<tr><td colspan="7">No sessions for this device.</td></tr>';
            } else {
                foreach ( $sessions as $sess ) {
                    $sess_start = ! empty( $sess->first_seen_utc ) ? joes_scanner_auth_format_datetime_local( $sess->first_seen_utc ) : '';
                    $sess_last  = ! empty( $sess->last_seen_utc ) ? joes_scanner_auth_format_datetime_local( $sess->last_seen_utc ) : '';
                    $sess_time  = joes_scanner_auth_format_duration( (int) $sess->duration_seconds );

                    $token_short = $sess->session_token;
                    if ( is_string( $token_short ) && strlen( $token_short ) > 8 ) {
                        $token_short = substr( $token_short, 0, 8 );
                    }

                    echo '<tr>';
                    echo '<td title="' . esc_attr( $sess->session_token ) . '">' . esc_html( $token_short ) . '</td>';
                    list( $ip_disp, $ip_title, $ip_cls ) = joes_scanner_auth_format_ip_for_table( $sess->last_ip );
            if ( $ip_disp !== '' ) {
                echo '<td class="jsauth-ip-cell ' . esc_attr( $ip_cls ) . '" title="' . esc_attr( $ip_title ) . '"><span class="jsauth-ellipsis">' . esc_html( $ip_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
                    echo '<td>' . esc_html( $sess->app_version ) . '</td>';
                    $server_disp = joes_scanner_auth_format_stream_server_display( isset( $sess->stream_server_url ) ? (string) $sess->stream_server_url : '', isset( $sess->stream_server_is_hosted ) ? (int) $sess->stream_server_is_hosted : -1 );
                    if ( ! empty( $server_disp[0] ) ) {
                        list( $srv_disp, $srv_trunc ) = joes_scanner_auth_truncate_table_text( $server_disp[0], 50 );
                echo '<td title="' . esc_attr( $server_disp[1] ) . '"><span class="jsauth-ellipsis">' . esc_html( $srv_disp ) . '</span></td>';
                    } else {
                        echo '<td></td>';
                    }
                    echo '<td>' . esc_html( $sess_start ) . '</td>';
                    echo '<td>' . esc_html( $sess_last ) . '</td>';
                    echo '<td>' . esc_html( $sess_time ) . '</td>';
                    echo '</tr>';
                }
            }

            echo '</tbody></table>';

            echo '</div>';
            echo '</td>';
            echo '</tr>';

        }
    }

    echo '</tbody></table>';

    $html = ob_get_clean();
    wp_send_json_success( array( 'html' => $html ) );
}


function joes_scanner_auth_admin_post_clear_device() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    $nonce = isset( $_POST['nonce'] ) ? sanitize_text_field( wp_unslash( $_POST['nonce'] ) ) : '';
    if ( ! $nonce || ! wp_verify_nonce( $nonce, 'joes_scanner_auth_device_action' ) ) {
        wp_die( 'Invalid request.' );
    }

    $user_key = isset( $_POST['user_key'] ) ? (int) sanitize_text_field( wp_unslash( $_POST['user_key'] ) ) : 0;
    $device_id_hash = isset( $_POST['device_id_hash'] ) ? sanitize_text_field( wp_unslash( $_POST['device_id_hash'] ) ) : '';
    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );

    // Clear is global across all admin pages. A device is one device everywhere.
    if ( $device_id_hash ) {
        joes_scanner_auth_admin_clear_device( $device_id_hash );
    } else {
        // Unknown device clear is scoped by account key (no device hash).
        // This supports both registered accounts (> 0) and the unregistered bucket (0).
        joes_scanner_auth_admin_clear_unknown_device_for_user( $user_key );
    }

    joes_scanner_auth_log_admin_event(
        'clear_device',
        array(
            'scope'         => 'global',
            'user_id'        => $user_key > 0 ? $user_key : null,
            'device_id_hash' => $device_id_hash,
        )
    );

    $redirect = wp_get_referer();
    if ( ! $redirect ) {
        $redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=history' );
    }
    $redirect = add_query_arg( 'jsauth_notice', 'device_cleared', $redirect );
    wp_safe_redirect( $redirect );
    exit;
}

function joes_scanner_auth_admin_post_set_device_blocked() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    $nonce = isset( $_POST['nonce'] ) ? sanitize_text_field( wp_unslash( $_POST['nonce'] ) ) : '';
    if ( ! $nonce || ! wp_verify_nonce( $nonce, 'joes_scanner_auth_device_action' ) ) {
        wp_die( 'Invalid request.' );
    }

    $device_id_hash = isset( $_POST['device_id_hash'] ) ? sanitize_text_field( wp_unslash( $_POST['device_id_hash'] ) ) : '';
    $device_id_hash = joes_scanner_auth_sanitize_device_string( (string) $device_id_hash, 64 );

    $blocked = isset( $_POST['blocked'] ) ? (int) wp_unslash( $_POST['blocked'] ) : 0;
    $blocked = $blocked === 1;

    if ( $device_id_hash ) {
        joes_scanner_auth_admin_block_device( $device_id_hash, $blocked );
    }

    $redirect = wp_get_referer();
    if ( ! $redirect ) {
        $redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=history' );
    }
    $redirect = add_query_arg( 'jsauth_notice', $blocked ? 'device_blocked' : 'device_unblocked', $redirect );
    wp_safe_redirect( $redirect );
    exit;
}

function joes_scanner_auth_admin_post_set_ip_blocked() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    $nonce = isset( $_POST['nonce'] ) ? sanitize_text_field( wp_unslash( $_POST['nonce'] ) ) : '';
    if ( ! $nonce || ! wp_verify_nonce( $nonce, 'joes_scanner_auth_device_action' ) ) {
        wp_die( 'Invalid request.' );
    }

    $ip = isset( $_POST['ip'] ) ? sanitize_text_field( wp_unslash( $_POST['ip'] ) ) : '';
    $ip = is_string( $ip ) ? trim( $ip ) : '';

    $blocked = isset( $_POST['blocked'] ) ? (int) wp_unslash( $_POST['blocked'] ) : 0;
    $blocked = $blocked === 1;

    if ( $ip !== '' && filter_var( $ip, FILTER_VALIDATE_IP ) ) {
        joes_scanner_auth_admin_block_ip( $ip, $blocked );
    }

    $redirect = wp_get_referer();
    if ( ! $redirect ) {
        $redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=devices' );
    }

    $redirect = add_query_arg( 'jsauth_notice', $blocked ? 'ip_blocked' : 'ip_unblocked', $redirect );
    wp_safe_redirect( $redirect );
    exit;
}



function joes_scanner_auth_render_test_account_tab() {
    $email = isset( $_GET['test_email'] ) ? sanitize_email( wp_unslash( $_GET['test_email'] ) ) : '';
    $data  = null;

    if ( $email !== '' && function_exists( 'joes_scanner_auth_build_debug_account_data' ) ) {
        $data = joes_scanner_auth_build_debug_account_data( $email );
    }

    echo '<div class="jsauth-card">';
    echo '<h3>Test account</h3>';
    echo '<p class="description">Enter an email address to inspect the WordPress user, SureCart customer, subscriptions, and the plugin\'s current subscription settings.</p>';
    echo '<form method="get" action="">';
    echo '<input type="hidden" name="page" value="joes-scanner-auth" />';
    echo '<input type="hidden" name="tab" value="test_account" />';
    echo '<div style="display:flex; gap:10px; align-items:center; flex-wrap:wrap; margin-top:12px;">';
    echo '<input type="email" name="test_email" value="' . esc_attr( $email ) . '" placeholder="customer@example.com" style="min-width:340px;" class="regular-text" />';
    submit_button( 'Run test', 'primary', '', false );
    if ( $email !== '' ) {
        $base_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=test_account' );
        echo '<a class="button button-secondary" href="' . esc_url( $base_url ) . '">Clear</a>';
    }
    echo '</div>';
    echo '</form>';

    if ( $email !== '' ) {
        echo '<div style="margin-top:16px;">';
        if ( is_array( $data ) && isset( $data['ok'] ) && ! $data['ok'] ) {
            echo '<div class="notice notice-error inline"><p>' . esc_html( isset( $data['message'] ) ? (string) $data['message'] : 'Unable to inspect that account.' ) . '</p></div>';
        } else {
            echo '<h4 style="margin-bottom:8px;">Results for ' . esc_html( $email ) . '</h4>';
            echo '<textarea readonly="readonly" style="width:100%; min-height:520px; font-family:Consolas,Monaco,monospace; white-space:pre;">' . esc_textarea( wp_json_encode( $data, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES ) ) . '</textarea>';
        }
        echo '</div>';
    }

    echo '</div>';
}

function joes_scanner_auth_add_admin_menu() {
    add_options_page(
        "Joe's Scanner Auth",
        "Joe's Scanner Auth",
        'manage_options',
        'joes-scanner-auth',
        'joes_scanner_auth_options_page'
    );
}

function joes_scanner_auth_settings_init() {
    register_setting(
        'joes_scanner_auth',
        JOES_SCANNER_AUTH_OPTIONS_KEY,
        array(
            'type'              => 'array',
            'sanitize_callback' => 'joes_scanner_auth_sanitize_options',
            'default'           => array(),
        )
    );

    add_settings_section(
        'joes_scanner_auth_section_main',
        'Subscription validation settings',
        'joes_scanner_auth_section_main_cb',
        'joes_scanner_auth_surecart'
    );

    add_settings_section(
        'joes_scanner_auth_section_integrations',
        'Integrations',
        function () {
            echo '<p>Enable or disable integrations used by the API.</p>';
        },
        'joes_scanner_auth'
    );

    add_settings_field(
        'joes_scanner_auth_surecart_enabled',
        'Enable SureCart integration',
        'joes_scanner_auth_field_surecart_enabled',
        'joes_scanner_auth',
        'joes_scanner_auth_section_integrations'
    );

    add_settings_field(
        'joes_scanner_auth_surecart_auto_sync_prices',
        'Auto sync SureCart subscriptions',
        'joes_scanner_auth_field_surecart_auto_sync_prices',
        'joes_scanner_auth_surecart',
        'joes_scanner_auth_section_main'
    );

    add_settings_field(
        'joes_scanner_auth_allowed_price_ids',
        'SureCart subscription items',
        'joes_scanner_auth_allowed_price_ids_render',
        'joes_scanner_auth_surecart',
        'joes_scanner_auth_section_main'
    );

    add_settings_field(
        'joes_scanner_auth_allowed_statuses',
        'Subscription status handling',
        'joes_scanner_auth_allowed_statuses_render',
        'joes_scanner_auth_surecart',
        'joes_scanner_auth_section_main'
    );

    add_settings_section(
        'joes_scanner_auth_section_tracking',
        'Active tab',
        function () {
            echo '<p>Controls for how the Active tab detects connections and refreshes.</p>';
        },
        'joes_scanner_auth'
    );

    add_settings_section(
        'joes_scanner_auth_section_app_versions',
        'App version enforcement',
        function () {
            echo '<p>Reject outdated app builds at the API level.</p>';
        },
        'joes_scanner_auth'
    );

    add_settings_field(
        'joes_scanner_auth_minimum_app_version',
        'Minimum app version',
        'joes_scanner_auth_field_minimum_app_version',
        'joes_scanner_auth',
        'joes_scanner_auth_section_app_versions'
    );

    add_settings_field(
        'joes_scanner_auth_active_window_minutes',
        'Active window minutes',
        'joes_scanner_auth_field_active_window_minutes',
        'joes_scanner_auth',
        'joes_scanner_auth_section_tracking'
    );

    add_settings_field(
        'joes_scanner_auth_active_tab_inactive_sessions',
        'Recently inactive sessions',
        'joes_scanner_auth_field_active_tab_inactive_sessions',
        'joes_scanner_auth',
        'joes_scanner_auth_section_tracking'
    );

    add_settings_field(
        'joes_scanner_auth_active_tab_refresh_seconds',
        'Auto refresh seconds',
        'joes_scanner_auth_field_active_tab_refresh_seconds',
        'joes_scanner_auth',
        'joes_scanner_auth_section_tracking'
    );
add_settings_section(
        'joes_scanner_auth_section_database',
        '',
        function () {
            // Section description is shown in the Retention card header.
        },
        'joes_scanner_auth_database'
    );
add_settings_field(
        'joes_scanner_auth_event_retention_days',
        'Event retention days',
        'joes_scanner_auth_event_retention_days_render',
        'joes_scanner_auth_database',
        'joes_scanner_auth_section_database'
    );

    add_settings_field(
        'joes_scanner_auth_session_retention_days',
        'Session retention days',
        'joes_scanner_auth_session_retention_days_render',
        'joes_scanner_auth_database',
        'joes_scanner_auth_section_database'
    );

    add_settings_field(
        'joes_scanner_auth_comms_retention_days',
        'Comms retention days',
        'joes_scanner_auth_comms_retention_days_render',
        'joes_scanner_auth_database',
        'joes_scanner_auth_section_database'
    );
}


function joes_scanner_auth_repair_orphan_devices_once() {
    // Repair legacy device rows that were written without a user_id (these show up under "Unknown").
    // Run at most once per hour to avoid extra load.
    $key = 'joes_scanner_auth_orphan_devices_repair';
    if ( get_transient( $key ) ) {
        return;
    }
    set_transient( $key, '1', HOUR_IN_SECONDS );

    joes_scanner_auth_repair_orphan_devices();
}

function joes_scanner_auth_repair_orphan_devices() {
    global $wpdb;

    $devices_table  = joes_scanner_auth_table_devices();
    $sessions_table = joes_scanner_auth_table_sessions();

    // 1) If a proper device row exists for the same hash, delete the orphan row(s).
    $wpdb->query(
        "DELETE d
         FROM {$devices_table} d
         INNER JOIN {$devices_table} d2
            ON d.device_id_hash = d2.device_id_hash
         WHERE (d.user_id IS NULL OR d.user_id = 0)
           AND (d2.user_id IS NOT NULL AND d2.user_id <> 0)"
    );

    // 2) For remaining orphans, attach them to the most recently seen user in sessions.
    // Uses GROUP_CONCAT to pick the user_id/username/email associated with the newest last_seen_utc.
    $wpdb->query(
        "UPDATE {$devices_table} d
         INNER JOIN (
            SELECT
                device_id_hash,
                SUBSTRING_INDEX(GROUP_CONCAT(user_id ORDER BY last_seen_utc DESC), ',', 1) AS user_id,
                SUBSTRING_INDEX(GROUP_CONCAT(username ORDER BY last_seen_utc DESC), ',', 1) AS username,
                SUBSTRING_INDEX(GROUP_CONCAT(email ORDER BY last_seen_utc DESC), ',', 1) AS email
            FROM {$sessions_table}
            WHERE device_id_hash IS NOT NULL
              AND device_id_hash <> ''
              AND user_id IS NOT NULL
              AND user_id <> 0
            GROUP BY device_id_hash
         ) s
            ON s.device_id_hash = d.device_id_hash
         SET
            d.user_id = CAST(s.user_id AS UNSIGNED),
            d.username = NULLIF(s.username, ''),
            d.email = NULLIF(s.email, '')
         WHERE (d.user_id IS NULL OR d.user_id = 0)"
    );

    // 3) If any orphans are still present (no sessions exist), leave them as unknown.
}

/**
 * Options page markup with tabs: Active, History, Settings.
 */
function joes_scanner_auth_options_page() {
    if ( ! current_user_can( 'manage_options' ) ) {
        return;
    }

    $tab = isset( $_GET['tab'] ) ? sanitize_key( (string) $_GET['tab'] ) : 'active';
    if ( ! in_array( $tab, array( 'active', 'history', 'devices', 'logs', 'lookups', 'surecart', 'test_account', 'comms', 'server_directory', 'settings', 'database' ), true ) ) {
        $tab = 'active';
    }


    $notice = isset( $_GET['jsauth_notice'] ) ? sanitize_key( (string) $_GET['jsauth_notice'] ) : '';
    if ( $notice ) {
        $msg = '';
        $type = 'success';

        if ( $notice === 'backup_created' ) {
            $file = isset( $_GET['backup_file'] ) ? sanitize_text_field( (string) $_GET['backup_file'] ) : '';
            $msg = $file ? 'Backup created: ' . $file : 'Backup created.';
        } elseif ( $notice === 'backup_deleted' ) {
            $msg = 'Backup deleted.';
        } elseif ( $notice === 'backup_restored' ) {
            $msg = 'Backup restored.';
        } elseif ( $notice === 'backup_create_failed' ) {
            $msg = 'Backup create failed.';
            $type = 'error';
        } elseif ( $notice === 'backup_delete_failed' ) {
            $msg = 'Backup delete failed.';
            $type = 'error';
        } elseif ( $notice === 'backup_restore_failed' ) {
            $msg = 'Backup restore failed.';
            $type = 'error';
        } elseif ( $notice === 'device_cleared' ) {
            $msg = 'Device cleared.';
        } elseif ( $notice === 'device_blocked' ) {
            $msg = 'Device blocked.';
        } elseif ( $notice === 'device_unblocked' ) {
            $msg = 'Device unblocked.';
        } elseif ( $notice === 'ip_blocked' ) {
            $msg = 'IP blocked.';
        } elseif ( $notice === 'ip_unblocked' ) {
            $msg = 'IP unblocked.';
        }

        if ( $msg ) {
            echo '<div class="notice notice-' . esc_attr( $type ) . '"><p>' . esc_html( $msg ) . '</p></div>';
        }
    }

// Repair any legacy orphan device rows so devices do not appear under "Unknown".
    joes_scanner_auth_repair_orphan_devices_once();

    if ( in_array( $tab, array( 'database', 'settings', 'history', 'lookups' ), true ) && isset( $_POST['joes_scanner_auth_action'] ) ) {
        $nonce = isset( $_POST['_wpnonce'] ) ? (string) $_POST['_wpnonce'] : '';
        $nonce_ok =
            wp_verify_nonce( $nonce, 'joes_scanner_auth_database_actions' ) ||
            wp_verify_nonce( $nonce, 'joes_scanner_auth_settings_actions' ) ||
            wp_verify_nonce( $nonce, 'joes_scanner_auth_history_actions' ) ||
            wp_verify_nonce( $nonce, 'joes_scanner_auth_admin_actions' );
        if ( ! $nonce_ok ) {
            wp_die( 'Security check failed.' );
        }

        $action = sanitize_key( (string) $_POST['joes_scanner_auth_action'] );

        if ( $action === 'prune_database' || $action === 'run_maintenance' ) {
            $selected = array();
            if ( isset( $_POST['prune_tables'] ) ) {
                $selected = (array) wp_unslash( $_POST['prune_tables'] );
            }
            joes_scanner_auth_run_maintenance( $selected );
            echo '<div class="notice notice-success"><p>Database pruned.</p></div>';
        }

        if ( $action === 'clear_all' ) {
            $selected = array();
            if ( isset( $_POST['clear_tables'] ) ) {
                $selected = (array) wp_unslash( $_POST['clear_tables'] );
            }
            joes_scanner_auth_clear_all_tracking_data( $selected );
            echo '<div class="notice notice-success"><p>Databases cleared.</p></div>';
        }

        if ( $action === 'lookups_clear' ) {
            $server_key = isset( $_POST['server_key'] ) ? (string) wp_unslash( $_POST['server_key'] ) : '';
            $section    = isset( $_POST['section'] ) ? (string) wp_unslash( $_POST['section'] ) : '';
            $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );

            if ( $server_key !== '' ) {
                joes_scanner_auth_lookups_clear_section( $server_key, $section );
                echo '<div class="notice notice-success"><p>Lookups cleared.</p></div>';
            }
        }

        if ( $action === 'observed_triples_clear' ) {
            $server_key = isset( $_POST['server_key'] ) ? (string) wp_unslash( $_POST['server_key'] ) : '';
            $server_key = joes_scanner_auth_lookups_normalize_server_key( $server_key );

            if ( $server_key !== '' ) {
                joes_scanner_auth_observed_triples_v2_clear_all( $server_key );
                joes_scanner_auth_observed_triples_clear_all( $server_key );
                echo '<div class="notice notice-success"><p>Observed discovery rows cleared.</p></div>';
            }
        }
    }
    // Device/IP actions are handled by admin-post endpoints to avoid ambiguous form submissions.

    $active_url   = admin_url( 'options-general.php?page=joes-scanner-auth&tab=active' );
    $history_url  = admin_url( 'options-general.php?page=joes-scanner-auth&tab=history' );
    $devices_url  = admin_url( 'options-general.php?page=joes-scanner-auth&tab=devices' );
    $logs_url    = admin_url( 'options-general.php?page=joes-scanner-auth&tab=logs' );
    $settings_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=settings' );
    $surecart_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=surecart' );
    $test_account_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=test_account' );

    $lookups_url  = admin_url( 'options-general.php?page=joes-scanner-auth&tab=lookups' );

    $server_dir_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=server_directory' );


    $comms_url   = admin_url( 'options-general.php?page=joes-scanner-auth&tab=comms' );
    $database_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=database' );

    echo '<div class="wrap">';
    echo '<h1>Joe\'s Scanner Auth</h1>';

    echo '<style>
        /* Compact tables across plugin pages */
        div.wrap { overflow-x: auto; }

        table.widefat.jsauth-table { border-collapse: collapse; width: auto !important; display: inline-table; table-layout: auto !important; }
        table.widefat.jsauth-table th, table.widefat.jsauth-table td { padding: 4px 8px !important; line-height: 1.2; vertical-align: top; font-size: 13px; }
        table.widefat.jsauth-table th { white-space: nowrap; }
        table.widefat.jsauth-table td { white-space: nowrap; }

        /* Allow selective wrapping where needed */
        .jsauth-wrap { white-space: normal !important; word-break: break-word; overflow: visible !important; text-overflow: clip !important; }

        .jsauth-expand { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; width: 100%; padding: 0; margin: 0; text-align: left; }
        .jsauth-expand:before { content: "\25B8"; color: #646970; }
        .jsauth-expand[aria-expanded="true"]:before { content: "\25BE"; }

        .jsauth-summary-main { display: inline; font-weight: 600; }
        .jsauth-summary-meta { display: inline; font-size: 12px; color: #666; white-space: normal; }

        /* Devices Actions column: keep buttons on one row (no wrapping) */
        .jsauth-actions { display: flex; flex-wrap: nowrap; gap: 6px; align-items: center; white-space: nowrap; overflow-x: auto; }
        .jsauth-actions .button { margin: 0; padding: 0 8px; min-height: 26px; line-height: 24px; }

        .jsauth-details summary { cursor: pointer; display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }

        .jsauth-subtable { margin-top: 6px; }
        .jsauth-subtable th, .jsauth-subtable td { font-size: 12px; padding: 3px 6px !important; line-height: 1.2; }

        .jsauth-acct-details-row td { white-space: normal !important; overflow: visible !important; }
        .jsauth-acct-details-row { background: #f6f7f7; }

        /* Resizable table columns (Devices and History tables) */
        table.widefat.jsauth-table th,
        table.widefat.jsauth-subtable th { position: relative; }

        .jsauth-col-resize-handle {
            position: absolute;
            top: 0;
            right: 0;
            width: 7px;
            height: 100%;
            cursor: col-resize;
            user-select: none;
        }

        .jsauth-col-resizing {
            cursor: col-resize !important;
        }

        /* Compact, IP-aware rendering with ellipsis */
        .jsauth-ip-cell { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace; max-width: 220px; overflow: hidden; text-overflow: ellipsis; }
        .jsauth-ip-cell .jsauth-ellipsis { display: inline-block; max-width: 220px; overflow: hidden; text-overflow: ellipsis; vertical-align: top; }
        .jsauth-ip-cell.jsauth-ipv6 { font-size: 12px; }
        .jsauth-subtable .jsauth-ip-cell.jsauth-ipv6 { font-size: 11px; }

        /* Server/Model columns: keep compact but readable */
        .jsauth-ellipsis { display: inline-block; max-width: 360px; overflow: hidden; text-overflow: ellipsis; vertical-align: top; }
        .jsauth-col-server .jsauth-ellipsis { max-width: 380px; }
        .jsauth-col-model .jsauth-ellipsis { max-width: 360px; }

</style>';

    echo '<script>
(function(){
  function getTableKey(table){
    var heads = Array.prototype.slice.call(table.querySelectorAll("thead th")).map(function(th){ return (th.textContent || "").trim(); });
    var key = "jsauth_colwidths:" + heads.join("|");
    return key;
  }

  function loadWidths(table){
    try{
      var key = getTableKey(table);
      var raw = window.localStorage.getItem(key);
      if(!raw){ return; }
      var arr = JSON.parse(raw);
      if(!Array.isArray(arr)){ return; }
      var ths = table.querySelectorAll("thead th");
      for(var i=0;i<ths.length && i<arr.length;i++){
        if(typeof arr[i] === "number" && arr[i] > 30){
          ths[i].style.width = arr[i] + "px";
        }
      }
    }catch(e){}
  }

  function saveWidths(table){
    try{
      var key = getTableKey(table);
      var ths = table.querySelectorAll("thead th");
      var arr = Array.prototype.slice.call(ths).map(function(th){
        return Math.round(th.getBoundingClientRect().width);
      });
      window.localStorage.setItem(key, JSON.stringify(arr));
    }catch(e){}
  }

  function makeResizable(table){
    var ths = table.querySelectorAll("thead th");
    if(!ths || ths.length < 2){ return; }

    loadWidths(table);

    Array.prototype.forEach.call(ths, function(th, i){
      if(i === ths.length - 1){ return; }
      if(th.querySelector(".jsauth-col-resize-handle")){ return; }

      var h = document.createElement("div");
      h.className = "jsauth-col-resize-handle";
      th.appendChild(h);

      var startX = 0;
      var startW = 0;

      function onMove(ev){
        var dx = (ev.clientX || 0) - startX;
        var newW = Math.max(50, startW + dx);
        th.style.width = newW + "px";
      }

      function onUp(){
        document.documentElement.classList.remove("jsauth-col-resizing");
        document.removeEventListener("mousemove", onMove);
        document.removeEventListener("mouseup", onUp);
        saveWidths(table);
      }

      h.addEventListener("mousedown", function(ev){
        ev.preventDefault();
        startX = ev.clientX || 0;
        startW = th.getBoundingClientRect().width;
        document.documentElement.classList.add("jsauth-col-resizing");
        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
      });
    });
  }

  function init(){
    var tables = document.querySelectorAll("table.widefat.jsauth-table, table.widefat.jsauth-subtable");
    Array.prototype.forEach.call(tables, makeResizable);
  }

  // Initial load
  init();

  // Re-init after History AJAX expands a row
  document.addEventListener("click", function(ev){
    var el = ev.target;
    if(!el){ return; }
    var toggle = el.closest ? el.closest(".jsauth-acct-toggle, .jsauth-device-toggle") : null;
    if(!toggle){ return; }
    window.setTimeout(init, 50);
  });

})();
</script>';



    echo '<h2 class="nav-tab-wrapper">';
    echo '<a href="' . esc_url( $active_url ) . '" class="nav-tab ' . ( $tab === 'active' ? 'nav-tab-active' : '' ) . '">Active</a>';
    echo '<a href="' . esc_url( $history_url ) . '" class="nav-tab ' . ( $tab === 'history' ? 'nav-tab-active' : '' ) . '">History</a>';
    echo '<a href="' . esc_url( $devices_url ) . '" class="nav-tab ' . ( $tab === 'devices' ? 'nav-tab-active' : '' ) . '">Devices</a>';
    echo '<a href="' . esc_url( $logs_url ) . '" class="nav-tab ' . ( $tab === 'logs' ? 'nav-tab-active' : '' ) . '">Logs</a>';
    echo '<a href="' . esc_url( $lookups_url ) . '" class="nav-tab ' . ( $tab === 'lookups' ? 'nav-tab-active' : '' ) . '">Lookups</a>';
    echo '<a href="' . esc_url( $surecart_url ) . '" class="nav-tab ' . ( $tab === 'surecart' ? 'nav-tab-active' : '' ) . '">SureCart</a>';
    echo '<a href="' . esc_url( $test_account_url ) . '" class="nav-tab ' . ( $tab === 'test_account' ? 'nav-tab-active' : '' ) . '">Test Account</a>';
    echo '<a href="' . esc_url( $server_dir_url ) . '" class="nav-tab ' . ( $tab === 'server_directory' ? 'nav-tab-active' : '' ) . '">Server Directory</a>';
    echo '<a href="' . esc_url( $comms_url ) . '" class="nav-tab ' . ( $tab === 'comms' ? 'nav-tab-active' : '' ) . '">Communications</a>';
    echo '<a href="' . esc_url( $settings_url ) . '" class="nav-tab ' . ( $tab === 'settings' ? 'nav-tab-active' : '' ) . '">Settings</a>';
    echo '<a href="' . esc_url( $database_url ) . '" class="nav-tab ' . ( $tab === 'database' ? 'nav-tab-active' : '' ) . '">Database</a>';
    echo '</h2>';

    if ( $tab === 'settings' ) {
        joes_scanner_auth_sync_allowed_prices_from_surecart( 'surecart_tab_render' );

            echo '<style>
        /* Database tab: compact retention layout (one row per setting) */
        .jsauth-card .form-table { margin-top: 8px; }
        .jsauth-card .form-table th { width: 160px !important; padding: 6px 10px 6px 0; white-space: nowrap; vertical-align: middle; }
        .jsauth-card .form-table td { padding: 6px 0; vertical-align: middle; }
        .jsauth-card .form-table td input[type="number"] { width: 90px; }
        .jsauth-card .form-table td p.description { display: inline; margin: 0 0 0 10px; }
    </style>';

echo '<form action="options.php" method="post">';
        settings_fields( 'joes_scanner_auth' );
        do_settings_sections( 'joes_scanner_auth' );
        submit_button();
        echo '</form>';
    } elseif ( $tab === 'surecart' ) {
        joes_scanner_auth_render_surecart_tab();
    } elseif ( $tab === 'test_account' ) {
        joes_scanner_auth_render_test_account_tab();
    } elseif ( $tab === 'server_directory' ) {
        if ( function_exists( 'joes_scanner_auth_render_server_directory_tab' ) ) {
            joes_scanner_auth_render_server_directory_tab();
        } else {
            echo '<p>Server directory module not loaded.</p>';
        }
    } elseif ( $tab === 'comms' ) {
        joes_scanner_auth_render_comms_tab();
    } elseif ( $tab === 'database' ) {
        joes_scanner_auth_render_database_tab();
    } elseif ( $tab === 'active' ) {
        joes_scanner_auth_render_active_tab();
    } elseif ( $tab === 'devices' ) {
        joes_scanner_auth_render_devices_tab();
    } elseif ( $tab === 'logs' ) {
        joes_scanner_auth_render_logs_tab();
    } elseif ( $tab === 'lookups' ) {
        joes_scanner_auth_render_lookups_tab();
    } else {
        joes_scanner_auth_render_history_tab();
    }

    $history_nonce = wp_create_nonce( 'joes_scanner_auth_history_ajax' );
    echo '<script>
(function(){
  var jsauthHistoryNonce = "' . esc_js( $history_nonce ) . '";
  function qsa(root, sel){
    return Array.prototype.slice.call((root || document).querySelectorAll(sel));
  }

  function updateParentState(group){
    var parent = group.querySelector(".jsauth-tree-parent");
    if(!parent){ return; }
    var kids = qsa(group, ".jsauth-tree-child");
    if(kids.length === 0){
      parent.checked = false;
      parent.indeterminate = false;
      return;
    }
    var checkedCount = kids.filter(function(c){ return c.checked; }).length;

    if(checkedCount === 0){
      parent.checked = false;
      parent.indeterminate = false;
      return;
    }
    if(checkedCount === kids.length){
      parent.checked = true;
      parent.indeterminate = false;
      return;
    }
    parent.checked = false;
    parent.indeterminate = true;
  }

  function updateAllParents(tree){
    qsa(tree, ".jsauth-tree-group").forEach(updateParentState);
  }

  function setAll(tree, checked){
    qsa(tree, ".jsauth-tree-child").forEach(function(c){ c.checked = checked; });
    qsa(tree, ".jsauth-tree-parent").forEach(function(p){ p.checked = checked; p.indeterminate = false; });
  }

  function initTree(tree){
    qsa(tree, ".jsauth-tree-parent").forEach(function(parent){
      parent.addEventListener("change", function(){
        var group = parent.closest(".jsauth-tree-group");
        if(!group){ return; }
        qsa(group, ".jsauth-tree-child").forEach(function(c){ c.checked = parent.checked; });
        parent.indeterminate = false;
        updateAllParents(tree);
      });
    });

    qsa(tree, ".jsauth-tree-child").forEach(function(child){
      child.addEventListener("change", function(){
        var group = child.closest(".jsauth-tree-group");
        if(group){ updateParentState(group); }
      });
    });

    updateAllParents(tree);
  }

  function toggleRow(row, open){
    if(!row){ return; }
    row.style.display = open ? "table-row" : "none";
  }

  function setExpanded(el, expanded){
    if(!el){ return; }
    el.setAttribute("aria-expanded", expanded ? "true" : "false");
  }

  function loadAccountDetails(btn, row){
    var body = row ? row.querySelector(".jsauth-history-body") : null;
    if(!body){ return; }
    if(body.getAttribute("data-loaded") === "1"){ return; }

    body.innerHTML = "<em>Loading...</em>";

    var userKey = btn.getAttribute("data-userkey") || "0";

    var fd = new FormData();
    fd.append("action", "joes_scanner_auth_history_account_details");
    fd.append("nonce", jsauthHistoryNonce || "");
    fd.append("user_key", userKey);

    var url = (typeof ajaxurl !== "undefined") ? ajaxurl : "";
    if(!url){
      body.innerHTML = "<span style=\"color:#b32d2e;\">ajaxurl is not defined.</span>";
      return;
    }

    fetch(url, { method: "POST", credentials: "same-origin", body: fd })
      .then(function(r){ return r.json(); })
      .then(function(json){
        if(json && json.success && json.data && typeof json.data.html === "string"){
          body.innerHTML = json.data.html;
          body.setAttribute("data-loaded", "1");
        } else {
          var msg = (json && json.data && json.data.message) ? json.data.message : "Failed to load account details.";
          body.innerHTML = "<span style=\"color:#b32d2e;\">" + msg + "</span>";
        }
      })
      .catch(function(){
        body.innerHTML = "<span style=\"color:#b32d2e;\">Failed to load account details.</span>";
      });
  }

  function bindHistoryExpands(){
    document.addEventListener("click", function(ev){
      var acctBtn = ev.target && ev.target.closest ? ev.target.closest(".jsauth-expand") : null;
      if(acctBtn){
        ev.preventDefault();
        var targetId = acctBtn.getAttribute("data-target");
        var row = targetId ? document.getElementById(targetId) : null;
        if(!row){ return; }

        var open = row.style.display === "none";
        toggleRow(row, open);
        setExpanded(acctBtn, open);

        if(open){
          loadAccountDetails(acctBtn, row);
        }
        return;
      }

      var devLink = ev.target && ev.target.closest ? ev.target.closest(".jsauth-device-toggle") : null;
      if(devLink){
        ev.preventDefault();
        var targetId2 = devLink.getAttribute("data-target");
        var row2 = targetId2 ? document.getElementById(targetId2) : null;
        if(!row2){ return; }

        var open2 = row2.style.display === "none";
        toggleRow(row2, open2);
        setExpanded(devLink, open2);
      }
    });
  }

  function onReady(fn){
    if(document.readyState === "loading"){
      document.addEventListener("DOMContentLoaded", fn);
    } else {
      fn();
    }
  }

  onReady(function(){
    bindHistoryExpands();
    qsa(document, ".jsauth-tree").forEach(initTree);

	    qsa(document, ".jsauth-tree-selectall").forEach(function(btn){
      btn.addEventListener("click", function(){
        var target = btn.getAttribute("data-target");
	        // Attribute selector does not require quotes for simple values like "prune" and "clear".
	        // Using no quotes avoids breaking this script block inside a PHP single-quoted string.
	        var tree = document.querySelector(".jsauth-tree[data-tree=" + target + "]");
        if(tree){
          setAll(tree, true);
          updateAllParents(tree);
        }
      });
    });

    qsa(document, ".jsauth-tree-selectnone").forEach(function(btn){
      btn.addEventListener("click", function(){
        var target = btn.getAttribute("data-target");
	        // See note above.
	        var tree = document.querySelector(".jsauth-tree[data-tree=" + target + "]");
        if(tree){
          setAll(tree, false);
          updateAllParents(tree);
        }
      });
    });
  });
})();
</script>';

    echo '</div>';
}
function joes_scanner_auth_clear_all_tracking_data( $selected_tables = null ) {
    global $wpdb;

    $events_table    = joes_scanner_auth_table_events();
    $sessions_table  = joes_scanner_auth_table_sessions();
    $devices_table   = joes_scanner_auth_table_devices();
    $blocked_ips_tbl = joes_scanner_auth_table_blocked_ips();
    $device_set_tbl  = joes_scanner_auth_table_device_settings();
    $app_events_tbl  = joes_scanner_auth_table_app_events();
    $comms_messages_tbl = joes_scanner_auth_table_comms_messages();
    $comms_changes_tbl  = joes_scanner_auth_table_comms_changes();

    $all = array(
        'events'          => $events_table,
        'sessions'        => $sessions_table,
        'devices'         => $devices_table,
        'blocked_ips'     => $blocked_ips_tbl,
        'device_settings' => $device_set_tbl,
        'app_events'      => $app_events_tbl,
        'comms_messages'  => $comms_messages_tbl,
        'comms_changes'   => $comms_changes_tbl,
    );

    $selected = array();
    if ( is_array( $selected_tables ) ) {
        foreach ( $selected_tables as $k ) {
            $k = sanitize_key( (string) $k );
            if ( $k !== '' && isset( $all[ $k ] ) ) {
                $selected[] = $k;
            }
        }
    }

    if ( empty( $selected ) ) {
        $selected = array_keys( $all );
    }

    foreach ( $selected as $key ) {
        $tbl = $all[ $key ];
        // Table names are internal and not user input.
        $wpdb->query( "TRUNCATE TABLE {$tbl}" );
    }
}

function joes_scanner_auth_backup_table_names() {
    return array(
        joes_scanner_auth_table_events(),
        joes_scanner_auth_table_app_events(),
        joes_scanner_auth_table_sessions(),
        joes_scanner_auth_table_devices(),
        joes_scanner_auth_table_blocked_ips(),
        joes_scanner_auth_table_device_settings(),
        joes_scanner_auth_table_comms_messages(),
        joes_scanner_auth_table_comms_changes(),
    );
}

function joes_scanner_auth_backup_dir_path() {
    $uploads = wp_upload_dir();
    $base = isset( $uploads['basedir'] ) ? (string) $uploads['basedir'] : '';
    return trailingslashit( $base ) . 'joes-scanner-auth-backups';
}

function joes_scanner_auth_ensure_backup_dir() {
    $dir = joes_scanner_auth_backup_dir_path();
    if ( ! $dir ) {
        return new WP_Error( 'backup_dir', 'Unable to resolve uploads directory.' );
    }

    if ( ! file_exists( $dir ) ) {
        wp_mkdir_p( $dir );
    }

    if ( ! is_dir( $dir ) || ! is_writable( $dir ) ) {
        return new WP_Error( 'backup_dir', 'Backup directory is not writable.' );
    }

    $index = trailingslashit( $dir ) . 'index.html';
    if ( ! file_exists( $index ) ) {
        @file_put_contents( $index, '' );
    }

    $htaccess = trailingslashit( $dir ) . '.htaccess';
    if ( ! file_exists( $htaccess ) ) {
        @file_put_contents( $htaccess, "Deny from all\n" );
    }

    return $dir;
}

function joes_scanner_auth_list_backup_files() {
    $dir = joes_scanner_auth_backup_dir_path();
    if ( ! $dir || ! is_dir( $dir ) ) {
        return array();
    }

    $files = glob( trailingslashit( $dir ) . '*.sql' );
    if ( ! is_array( $files ) ) {
        return array();
    }

    $out = array();
    foreach ( $files as $path ) {
        $name = basename( (string) $path );
        $out[] = array(
            'name' => $name,
            'path' => $path,
            'mtime' => @filemtime( $path ) ?: 0,
            'size' => @filesize( $path ) ?: 0,
        );
    }

    usort(
        $out,
        static function ( $a, $b ) {
            return (int) $b['mtime'] <=> (int) $a['mtime'];
        }
    );

    return $out;
}

function joes_scanner_auth_sql_quote_value( $value ) {
    if ( is_null( $value ) ) {
        return 'NULL';
    }

    if ( is_bool( $value ) ) {
        return $value ? '1' : '0';
    }

    if ( is_int( $value ) || is_float( $value ) ) {
        return (string) $value;
    }

    $s = (string) $value;

    // Escape for MySQL.
    $s = str_replace( "\\", "\\\\", $s );
    $s = str_replace( "'", "\\'", $s );
    $s = str_replace( "\0", "\\0", $s );
    $s = str_replace( "\n", "\\n", $s );
    $s = str_replace( "\r", "\\r", $s );
    $s = str_replace( "\x1a", "\\Z", $s );

    return "'" . $s . "'";
}

function joes_scanner_auth_build_backup_sql() {
    global $wpdb;

    $tables = joes_scanner_auth_backup_table_names();

    $out = '';
    $out .= "# Joe's Scanner Auth API database backup\n";
    $out .= '# Created UTC: ' . gmdate( 'Y-m-d H:i:s' ) . "\n";
    $out .= '# Site: ' . ( function_exists( 'site_url' ) ? site_url() : '' ) . "\n";
    $out .= '# Plugin version: ' . JOES_SCANNER_AUTH_VERSION . "\n";
    $out .= "\n";
    $out .= "SET FOREIGN_KEY_CHECKS=0;\n";
    $out .= "SET SQL_MODE='NO_AUTO_VALUE_ON_ZERO';\n\n";

    foreach ( $tables as $table ) {
        $table = (string) $table;

        // Skip if table does not exist.
        $exists = $wpdb->get_var( $wpdb->prepare( "SHOW TABLES LIKE %s", $table ) );
        if ( $exists !== $table ) {
            continue;
        }

        $create_row = $wpdb->get_row( "SHOW CREATE TABLE {$table}", ARRAY_A );
        if ( ! is_array( $create_row ) ) {
            continue;
        }

        $create_sql = '';
        foreach ( $create_row as $k => $v ) {
            if ( stripos( (string) $k, 'Create Table' ) !== false ) {
                $create_sql = (string) $v;
                break;
            }
        }
        if ( ! $create_sql ) {
            continue;
        }

        $out .= "# Table: {$table}\n";
        $out .= "DROP TABLE IF EXISTS `{$table}`;\n";
        $out .= $create_sql . ";\n\n";

        $columns = $wpdb->get_results( "DESCRIBE {$table}", ARRAY_A );
        if ( ! is_array( $columns ) || ! $columns ) {
            continue;
        }

        $fields = array();
        foreach ( $columns as $col ) {
            if ( isset( $col['Field'] ) ) {
                $fields[] = (string) $col['Field'];
            }
        }

        $field_list = '`' . implode( '`,`', $fields ) . '`';

        $rows = $wpdb->get_results( "SELECT * FROM {$table}", ARRAY_A );
        if ( is_array( $rows ) && $rows ) {
            $batch = array();
            $batch_size = 200;

            foreach ( $rows as $row ) {
                $values = array();
                foreach ( $fields as $f ) {
                    $values[] = joes_scanner_auth_sql_quote_value( array_key_exists( $f, $row ) ? $row[ $f ] : null );
                }
                $batch[] = '(' . implode( ',', $values ) . ')';

                if ( count( $batch ) >= $batch_size ) {
                    $out .= "INSERT INTO `{$table}` ({$field_list}) VALUES\n" . implode( ",\n", $batch ) . ";\n";
                    $batch = array();
                }
            }

            if ( $batch ) {
                $out .= "INSERT INTO `{$table}` ({$field_list}) VALUES\n" . implode( ",\n", $batch ) . ";\n";
            }
        }

        $out .= "\n";
    }

    $out .= "SET FOREIGN_KEY_CHECKS=1;\n";

    return $out;
}

function joes_scanner_auth_create_backup_file( $prefix = 'joes-scanner-auth-backup' ) {
    $dir = joes_scanner_auth_ensure_backup_dir();
    if ( is_wp_error( $dir ) ) {
        return $dir;
    }

    $safe_prefix = preg_replace( '/[^a-z0-9\-]/i', '', (string) $prefix );
    if ( ! $safe_prefix ) {
        $safe_prefix = 'joes-scanner-auth-backup';
    }

    $name = $safe_prefix . '-' . gmdate( 'Ymd-His' ) . '.sql';
    $path = trailingslashit( (string) $dir ) . $name;

    $sql = joes_scanner_auth_build_backup_sql();
    $ok = @file_put_contents( $path, $sql );

    if ( $ok === false ) {
        return new WP_Error( 'backup_write', 'Unable to write backup file.' );
    }

    return $name;
}

function joes_scanner_auth_split_sql_statements( $sql ) {
    $statements = array();
    $len = strlen( $sql );
    $buf = '';
    $in_string = false;
    $escape = false;

    for ( $i = 0; $i < $len; $i++ ) {
        $ch = $sql[ $i ];

        if ( $in_string ) {
            $buf .= $ch;

            if ( $escape ) {
                $escape = false;
                continue;
            }

            if ( $ch === "\\" ) {
                $escape = true;
                continue;
            }

            if ( $ch === "'" ) {
                $in_string = false;
            }

            continue;
        }

        // Not in string.
        if ( $ch === "'" ) {
            $in_string = true;
            $buf .= $ch;
            continue;
        }

        if ( $ch === ';' ) {
            $stmt = trim( $buf );
            $buf = '';
            if ( $stmt ) {
                $statements[] = $stmt;
            }
            continue;
        }

        $buf .= $ch;
    }

    $tail = trim( $buf );
    if ( $tail ) {
        $statements[] = $tail;
    }

    return $statements;
}

function joes_scanner_auth_run_sql_dump( $sql ) {
    global $wpdb;

    $errors = array();

    $sql = (string) $sql;
    $sql = preg_replace( "/\r\n|\r/", "\n", $sql );

    // Remove full line comments.
    $lines = explode( "\n", $sql );
    $filtered = array();
    foreach ( $lines as $line ) {
        $trim = ltrim( $line );
        if ( $trim === '' ) {
            $filtered[] = $line;
            continue;
        }
        if ( strpos( $trim, '#' ) === 0 || strpos( $trim, '--' ) === 0 ) {
            continue;
        }
        $filtered[] = $line;
    }
    $sql = implode( "\n", $filtered );

    $statements = joes_scanner_auth_split_sql_statements( $sql );
    if ( ! $statements ) {
        return array( 'ok' => false, 'errors' => array( 'No SQL statements found.' ) );
    }

    $wpdb->suppress_errors( true );

    foreach ( $statements as $stmt ) {
        $stmt = trim( $stmt );
        if ( $stmt === '' ) {
            continue;
        }

        $r = $wpdb->query( $stmt );
        if ( $r === false ) {
            $msg = $wpdb->last_error ? $wpdb->last_error : 'Unknown database error.';
            $errors[] = $msg . ' Statement: ' . substr( $stmt, 0, 200 );
        }
    }

    $wpdb->suppress_errors( false );

    return array( 'ok' => empty( $errors ), 'errors' => $errors );
}

function joes_scanner_auth_resolve_backup_path( $filename ) {
    $dir = joes_scanner_auth_backup_dir_path();
    if ( ! $dir ) {
        return '';
    }

    $base = basename( (string) $filename );
    if ( $base !== (string) $filename ) {
        return '';
    }

    if ( substr( $base, -4 ) !== '.sql' ) {
        return '';
    }

    $path = trailingslashit( $dir ) . $base;
    if ( ! file_exists( $path ) || ! is_file( $path ) ) {
        return '';
    }

    return $path;
}

function joes_scanner_auth_admin_post_create_backup() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    check_admin_referer( 'joes_scanner_auth_backup_actions' );

    @set_time_limit( 0 );

    $result = joes_scanner_auth_create_backup_file();
    $redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=database' );

    if ( is_wp_error( $result ) ) {
        $redirect = add_query_arg( 'jsauth_notice', 'backup_create_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    $redirect = add_query_arg(
        array(
            'jsauth_notice' => 'backup_created',
            'backup_file' => rawurlencode( (string) $result ),
        ),
        $redirect
    );
    wp_safe_redirect( $redirect );
    exit;
}

function joes_scanner_auth_admin_post_download_backup() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    $file = isset( $_GET['file'] ) ? (string) wp_unslash( $_GET['file'] ) : '';
    $nonce = isset( $_GET['_wpnonce'] ) ? (string) wp_unslash( $_GET['_wpnonce'] ) : '';
    $expected = 'joes_scanner_auth_download_backup:' . $file;

    if ( ! $nonce || ! wp_verify_nonce( $nonce, $expected ) ) {
        wp_die( 'Security check failed.' );
    }

    $path = joes_scanner_auth_resolve_backup_path( $file );
    if ( ! $path ) {
        wp_die( 'Backup file not found.' );
    }

    $size = @filesize( $path );
    if ( ! $size ) {
        $size = 0;
    }

    nocache_headers();
    header( 'Content-Type: application/sql; charset=utf-8' );
    header( 'Content-Disposition: attachment; filename="' . basename( $path ) . '"' );
    header( 'Content-Length: ' . (int) $size );

    $fp = fopen( $path, 'rb' );
    if ( $fp ) {
        while ( ! feof( $fp ) ) {
            echo fread( $fp, 8192 );
        }
        fclose( $fp );
    }
    exit;
}

function joes_scanner_auth_admin_post_delete_backup() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    check_admin_referer( 'joes_scanner_auth_backup_actions' );

    $file = isset( $_POST['file'] ) ? (string) wp_unslash( $_POST['file'] ) : '';
    $path = joes_scanner_auth_resolve_backup_path( $file );
    $redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=database' );

    if ( ! $path ) {
        $redirect = add_query_arg( 'jsauth_notice', 'backup_delete_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    @unlink( $path );

    $redirect = add_query_arg( 'jsauth_notice', 'backup_deleted', $redirect );
    wp_safe_redirect( $redirect );
    exit;
}

function joes_scanner_auth_admin_post_restore_backup() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    check_admin_referer( 'joes_scanner_auth_backup_actions' );

    @set_time_limit( 0 );

    $file = isset( $_POST['file'] ) ? (string) wp_unslash( $_POST['file'] ) : '';
    $path = joes_scanner_auth_resolve_backup_path( $file );
    $redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=database' );

    if ( ! $path ) {
        $redirect = add_query_arg( 'jsauth_notice', 'backup_restore_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    // Safety backup before restore.
    joes_scanner_auth_create_backup_file( 'joes-scanner-auth-pre-restore' );

    $sql = @file_get_contents( $path );
    if ( $sql === false ) {
        $redirect = add_query_arg( 'jsauth_notice', 'backup_restore_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    $res = joes_scanner_auth_run_sql_dump( $sql );
    if ( ! isset( $res['ok'] ) || ! $res['ok'] ) {
        $redirect = add_query_arg( 'jsauth_notice', 'backup_restore_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    $redirect = add_query_arg( 'jsauth_notice', 'backup_restored', $redirect );
    wp_safe_redirect( $redirect );
    exit;
}

function joes_scanner_auth_admin_post_restore_upload() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    check_admin_referer( 'joes_scanner_auth_backup_actions' );

    @set_time_limit( 0 );

    if ( ! isset( $_FILES['backup_file'] ) || ! is_array( $_FILES['backup_file'] ) ) {
        $redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=database' );
        $redirect = add_query_arg( 'jsauth_notice', 'backup_restore_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    $dir = joes_scanner_auth_ensure_backup_dir();
    $redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=database' );

    if ( is_wp_error( $dir ) ) {
        $redirect = add_query_arg( 'jsauth_notice', 'backup_restore_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    require_once ABSPATH . 'wp-admin/includes/file.php';

    $overrides = array(
        'test_form' => false,
        'mimes' => array(
            'sql' => 'application/sql',
            'txt' => 'text/plain',
        ),
    );

    $upload = wp_handle_upload( $_FILES['backup_file'], $overrides );
    if ( ! is_array( $upload ) || empty( $upload['file'] ) ) {
        $redirect = add_query_arg( 'jsauth_notice', 'backup_restore_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    $uploaded_path = (string) $upload['file'];
    $ext = strtolower( pathinfo( $uploaded_path, PATHINFO_EXTENSION ) );
    if ( $ext !== 'sql' && $ext !== 'txt' ) {
        @unlink( $uploaded_path );
        $redirect = add_query_arg( 'jsauth_notice', 'backup_restore_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    $final_name = 'joes-scanner-auth-upload-' . gmdate( 'Ymd-His' ) . '.sql';
    $final_path = trailingslashit( (string) $dir ) . $final_name;

    @rename( $uploaded_path, $final_path );

    // Safety backup before restore.
    joes_scanner_auth_create_backup_file( 'joes-scanner-auth-pre-restore' );

    $sql = @file_get_contents( $final_path );
    if ( $sql === false ) {
        $redirect = add_query_arg( 'jsauth_notice', 'backup_restore_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    $res = joes_scanner_auth_run_sql_dump( $sql );
    if ( ! isset( $res['ok'] ) || ! $res['ok'] ) {
        $redirect = add_query_arg( 'jsauth_notice', 'backup_restore_failed', $redirect );
        wp_safe_redirect( $redirect );
        exit;
    }

    $redirect = add_query_arg( 'jsauth_notice', 'backup_restored', $redirect );
    wp_safe_redirect( $redirect );
    exit;
}

function joes_scanner_auth_admin_post_save_backup_schedule() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( 'Not authorized.' );
    }

    check_admin_referer( 'joes_scanner_auth_backup_schedule' );

    $existing = joes_scanner_auth_backup_get_schedule();

    $enabled = isset( $_POST['jsauth_backup_enabled'] );

    $repeat_every = isset( $_POST['jsauth_backup_repeat_every'] ) ? (int) $_POST['jsauth_backup_repeat_every'] : 1;
    if ( $repeat_every < 1 ) { $repeat_every = 1; }
    if ( $repeat_every > 1000000 ) { $repeat_every = 1000000; }

    $unit = isset( $_POST['jsauth_backup_repeat_unit'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['jsauth_backup_repeat_unit'] ) ) : 'day';
    $allowed_units = array( 'minute', 'hour', 'day', 'week', 'month' );
    if ( ! in_array( $unit, $allowed_units, true ) ) {
        $unit = 'day';
    }

    $repeat_on = array();
    if ( $unit === 'week' && isset( $_POST['jsauth_backup_repeat_on'] ) && is_array( $_POST['jsauth_backup_repeat_on'] ) ) {
        foreach ( (array) $_POST['jsauth_backup_repeat_on'] as $d ) {
            $di = (int) $d;
            if ( $di >= 0 && $di <= 6 ) {
                $repeat_on[] = $di;
            }
        }
        $repeat_on = array_values( array_unique( $repeat_on ) );
        sort( $repeat_on );
    }

    // Time of day is interpreted in the site timezone. Default is 02:00.
    $time_of_day_minutes = isset( $existing['time_of_day_minutes'] ) ? (int) $existing['time_of_day_minutes'] : 120;
    if ( ( $unit === 'day' || $unit === 'week' || $unit === 'month' ) && isset( $_POST['jsauth_backup_time'] ) ) {
        $time_str = sanitize_text_field( (string) wp_unslash( $_POST['jsauth_backup_time'] ) );
        if ( preg_match( '/^(\d{1,2}):(\d{2})$/', $time_str, $m ) ) {
            $hh = (int) $m[1];
            $mm = (int) $m[2];
            if ( $hh >= 0 && $hh <= 23 && $mm >= 0 && $mm <= 59 ) {
                $time_of_day_minutes = ( $hh * 60 ) + $mm;
            }
        }
    }

    $ends_mode = isset( $_POST['jsauth_backup_ends_mode'] ) ? sanitize_text_field( (string) wp_unslash( $_POST['jsauth_backup_ends_mode'] ) ) : 'never';
    $allowed_ends = array( 'never', 'on', 'after' );
    if ( ! in_array( $ends_mode, $allowed_ends, true ) ) {
        $ends_mode = 'never';
    }

    $ends_on = '';
    if ( $ends_mode === 'on' && isset( $_POST['jsauth_backup_ends_on'] ) ) {
        $ends_on = sanitize_text_field( (string) wp_unslash( $_POST['jsauth_backup_ends_on'] ) );
    }

    $ends_after = 0;
    if ( $ends_mode === 'after' && isset( $_POST['jsauth_backup_ends_after'] ) ) {
        $ends_after = (int) $_POST['jsauth_backup_ends_after'];
        if ( $ends_after < 1 ) { $ends_after = 1; }
        if ( $ends_after > 1000000 ) { $ends_after = 1000000; }
    }

    $keep_count = isset( $_POST['jsauth_backup_keep_count'] ) ? (int) $_POST['jsauth_backup_keep_count'] : 10;
    if ( $keep_count < 1 ) { $keep_count = 1; }
    if ( $keep_count > 500 ) { $keep_count = 500; }

    $schedule = array(
        'enabled' => $enabled,
        'repeat_every' => $repeat_every,
        'repeat_unit' => $unit,
        'repeat_on' => $repeat_on,
        'time_of_day_minutes' => $time_of_day_minutes,
        'ends_mode' => $ends_mode,
        'ends_on' => $ends_on,
        'ends_after' => $ends_after,
        'keep_count' => $keep_count,
        'occurrence_count' => isset( $existing['occurrence_count'] ) ? (int) $existing['occurrence_count'] : 0,
        'last_run_utc' => isset( $existing['last_run_utc'] ) ? (int) $existing['last_run_utc'] : 0,
        'anchor_local' => (int) current_time( 'timestamp' ),
    );

    // When schedule changes, reset the anchor to now for intuitive behavior.
    $schedule['next_run_utc'] = joes_scanner_auth_backup_compute_next_run_utc( $schedule, time() );

    joes_scanner_auth_backup_save_schedule( $schedule );

    if ( $enabled ) {
        joes_scanner_auth_ensure_backup_cron();
    } else {
        joes_scanner_auth_unschedule_backup_cron();
    }

    $redirect = admin_url( 'options-general.php?page=joes-scanner-auth&tab=database' );
    $redirect = add_query_arg( 'jsauth_notice', 'backup_schedule_saved', $redirect );
    wp_safe_redirect( $redirect );
    exit;
}


function joes_scanner_auth_get_site_timezone_label() {
    $tz = function_exists( 'wp_timezone_string' ) ? wp_timezone_string() : '';
    if ( $tz ) {
        return $tz;
    }

    $offset = get_option( 'gmt_offset' );
    if ( $offset === '' || $offset === null ) {
        return 'UTC';
    }

    $hours = (int) $offset;
    $mins  = (int) round( abs( $offset - $hours ) * 60 );
    $sign  = ( $offset >= 0 ) ? '+' : '-';

    return sprintf( 'UTC%s%02d:%02d', $sign, abs( $hours ), $mins );
}

function joes_scanner_auth_format_datetime_local( $utc_mysql ) {
    $utc_mysql = is_string( $utc_mysql ) ? trim( $utc_mysql ) : '';
    if ( $utc_mysql === '' ) {
        return '';
    }

    // Resolve site timezone safely.
    $site_tz = null;
    if ( function_exists( 'wp_timezone' ) ) {
        try {
            $site_tz = wp_timezone();
        } catch ( Exception $e ) {
            $site_tz = null;
        }
    }

    if ( ! $site_tz ) {
        $tz_string = get_option( 'timezone_string' );
        if ( is_string( $tz_string ) ) {
            $tz_string = trim( $tz_string );
        } else {
            $tz_string = '';
        }

        if ( $tz_string !== '' ) {
            try {
                $site_tz = new DateTimeZone( $tz_string );
            } catch ( Exception $e ) {
                $site_tz = null;
            }
        }
    }

    if ( ! $site_tz ) {
        try {
            $site_tz = new DateTimeZone( 'UTC' );
        } catch ( Exception $e ) {
            // Extremely defensive fallback.
            return $utc_mysql;
        }
    }

    $utc_tz = null;
    try {
        $utc_tz = new DateTimeZone( 'UTC' );
    } catch ( Exception $e ) {
        return $utc_mysql;
    }

    $dt = null;

    try {
        // Epoch milliseconds (13 digits) or seconds (10 digits).
        if ( preg_match( '/^\d{13}$/', $utc_mysql ) ) {
            $sec = (int) floor( ( (int) $utc_mysql ) / 1000 );
            $dt  = new DateTimeImmutable( '@' . (string) $sec );
            $dt  = $dt->setTimezone( $utc_tz );
        } elseif ( preg_match( '/^\d{10}$/', $utc_mysql ) ) {
            $dt = new DateTimeImmutable( '@' . (string) (int) $utc_mysql );
            $dt = $dt->setTimezone( $utc_tz );
        } else {
            // MySQL UTC with optional microseconds.
            if ( preg_match( '/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+$/', $utc_mysql ) ) {
                $dt = DateTimeImmutable::createFromFormat( 'Y-m-d H:i:s.u', $utc_mysql, $utc_tz );
            }

            if ( ! $dt ) {
                $dt = DateTimeImmutable::createFromFormat( 'Y-m-d H:i:s', $utc_mysql, $utc_tz );
            }

            if ( ! $dt ) {
                // ISO-8601 (with or without Z/offset), or other strtotime-compatible formats.
                $dt = new DateTimeImmutable( $utc_mysql, $utc_tz );
            }
        }
    } catch ( Exception $e ) {
        $dt = null;
    }

    if ( ! $dt ) {
        return $utc_mysql;
    }

    try {
        $dt_local = $dt->setTimezone( $site_tz );
    } catch ( Exception $e ) {
        $dt_local = $dt;
    }

    $ts = (int) $dt_local->format( 'U' );

    if ( function_exists( 'wp_date' ) ) {
        try {
            return (string) wp_date( 'Y-m-d H:i:s', $ts, $site_tz );
        } catch ( Exception $e ) {
            // Fall through.
        }
    }

    return date_i18n( 'Y-m-d H:i:s', $ts, false );
}

function joes_scanner_auth_format_duration( $seconds ) {
    $seconds = (int) $seconds;
    if ( $seconds < 0 ) {
        $seconds = 0;
    }

    $hours = (int) floor( $seconds / 3600 );
    $mins  = (int) floor( ( $seconds % 3600 ) / 60 );
    $secs  = (int) ( $seconds % 60 );

    return sprintf( '%02d:%02d:%02d', $hours, $mins, $secs );
}

function joes_scanner_auth_format_device_hash_short( $hash ) {
    $hash = is_string( $hash ) ? trim( $hash ) : '';
    if ( $hash === '' ) {
        return '';
    }
    return substr( $hash, 0, 8 );
}

function joes_scanner_auth_format_stream_server_display( $url, $is_hosted ) {
    $url = is_string( $url ) ? trim( $url ) : '';
    if ( $url === '' ) {
        return array( '', '' );
    }
    $disp = $url;

    // Keep the server column clean. We do not prefix with Hosted or Custom.
    return array( $disp, $url );
}

function joes_scanner_auth_render_pager( $base_url, $param_name, $page, $pages, $other_params ) {
    $page  = max( 1, (int) $page );
    $pages = max( 1, (int) $pages );

    $prev = max( 1, $page - 1 );
    $next = min( $pages, $page + 1 );

    $prev_url = add_query_arg( array_merge( (array) $other_params, array( $param_name => $prev ) ), $base_url );
    $next_url = add_query_arg( array_merge( (array) $other_params, array( $param_name => $next ) ), $base_url );

    echo '<div style="margin: 10px 0; display:flex; gap: 10px; align-items:center;">';
    echo '<span>Page ' . esc_html( $page ) . ' of ' . esc_html( $pages ) . '</span>';

    if ( $page > 1 ) {
        echo '<a class="button button-secondary" href="' . esc_url( $prev_url ) . '">Previous</a>';
    } else {
        echo '<span class="button button-secondary" style="opacity:0.5; pointer-events:none;">Previous</span>';
    }

    if ( $page < $pages ) {
        echo '<a class="button button-secondary" href="' . esc_url( $next_url ) . '">Next</a>';
    } else {
        echo '<span class="button button-secondary" style="opacity:0.5; pointer-events:none;">Next</span>';
    }

    echo '</div>';
}
function joes_scanner_auth_render_surecart_tab() {
    if ( ! joes_scanner_auth_is_surecart_enabled() ) {
        echo '<div class="notice notice-info"><p>SureCart integration is disabled. Enable it in Settings to configure SureCart subscription validation.</p></div>';
        return;
    }

    joes_scanner_auth_sync_allowed_prices_from_surecart( 'surecart_tab_render' );

    echo '<form action="options.php" method="post">';
    settings_fields( 'joes_scanner_auth' );
    do_settings_sections( 'joes_scanner_auth_surecart' );
    submit_button();
    echo '</form>';
}

function joes_scanner_auth_render_database_tab() {
    echo '<h2>Database</h2>';

    // Layout styles for this tab only.
    echo '<style>
        .jsauth-db-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin-top: 12px; }
        @media (max-width: 1100px) { .jsauth-db-grid { grid-template-columns: 1fr; } }
        .jsauth-card { background: #fff; border: 1px solid #c3c4c7; border-radius: 8px; padding: 16px; }
        .jsauth-card h3 { margin: 0 0 10px 0; }
        .jsauth-card p.description { margin: 6px 0 0 0; }
        .jsauth-card .form-table th { width: 220px; }
        .jsauth-card .form-table { margin-top: 6px; }
        .jsauth-card .form-table th, .jsauth-card .form-table td { padding-top: 6px; padding-bottom: 6px; }
        .jsauth-card .form-table td { padding-left: 10px; }
        .jsauth-card .form-table p.description { margin: 2px 0 0 0; }
        .jsauth-card .form-table input[type="text"],
        .jsauth-card .form-table input[type="number"],
        .jsauth-card .form-table select { margin: 0; }
                .jsauth-retention-card .form-table { table-layout: auto; width: 100%; }
        .jsauth-retention-card .form-table th { white-space: nowrap; }
        .jsauth-inline-actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
        .jsauth-inline-actions form { margin: 0; }
        .jsauth-muted { color: #646970; }
        .jsauth-backup-table td { vertical-align: middle; }
        .jsauth-backup-actions { white-space: nowrap; }
    </style>';

    echo '<div class="jsauth-db-grid">';

    // Left column: backups and restore.
    echo '<div class="jsauth-card jsauth-retention-card">';
    echo '<h3>Backups and restore</h3>';
    echo '<p class="description">Backups include plugin tables only. Restore replaces current plugin tables and cannot be undone.</p>';

    echo '<div class="jsauth-inline-actions" style="margin-top: 12px;">';
    echo '<form method="post" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '">';
    echo '<input type="hidden" name="action" value="joes_scanner_auth_create_backup" />';
    wp_nonce_field( 'joes_scanner_auth_backup_actions' );
    echo '<button class="button button-primary" type="submit">Create backup</button>';
    echo '</form>';
    echo '</div>';



    // Automatic backups schedule.
    $schedule = function_exists( 'joes_scanner_auth_backup_get_schedule' ) ? joes_scanner_auth_backup_get_schedule() : array();
    $auto_enabled = ! empty( $schedule['enabled'] );
    $repeat_every = isset( $schedule['repeat_every'] ) ? (int) $schedule['repeat_every'] : 1;
    $repeat_unit = isset( $schedule['repeat_unit'] ) ? (string) $schedule['repeat_unit'] : 'day';
    $repeat_on = isset( $schedule['repeat_on'] ) && is_array( $schedule['repeat_on'] ) ? array_map( 'intval', $schedule['repeat_on'] ) : array();
    $ends_mode = isset( $schedule['ends_mode'] ) ? (string) $schedule['ends_mode'] : 'never';
    $ends_on = isset( $schedule['ends_on'] ) ? (string) $schedule['ends_on'] : '';
    $ends_after = isset( $schedule['ends_after'] ) ? (int) $schedule['ends_after'] : 0;
    $keep_count = isset( $schedule['keep_count'] ) ? (int) $schedule['keep_count'] : 10;
    $next_run_utc = isset( $schedule['next_run_utc'] ) ? (int) $schedule['next_run_utc'] : 0;

    echo '<div style="margin-top: 16px;">';
    echo '<h3 style="margin-bottom: 8px;">Automatic backups</h3>';
    echo '<form method="post" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '">';
    echo '<input type="hidden" name="action" value="joes_scanner_auth_save_backup_schedule" />';
    wp_nonce_field( 'joes_scanner_auth_backup_schedule' );

    echo '<div style="display:flex; flex-wrap:wrap; gap: 14px; align-items: center; margin-bottom: 12px;">';
    echo '<label style="display:inline-flex; gap:8px; align-items:center;">';
    echo '<input type="checkbox" name="jsauth_backup_enabled" value="1" ' . checked( $auto_enabled, true, false ) . ' />';
    echo '<strong>Enable</strong>';
    echo '</label>';

    echo '<div style="display:flex; gap:8px; align-items:center;">';
    echo '<span class="jsauth-muted">Repeat every</span>';
    echo '<input type="number" min="1" step="1" name="jsauth_backup_repeat_every" value="' . esc_attr( max( 1, $repeat_every ) ) . '" style="width: 90px;" />';
    echo '<select name="jsauth_backup_repeat_unit" id="jsauth-backup-unit">';
    $units = array(
        'minute' => 'minute',
        'hour' => 'hour',
        'day' => 'day',
        'week' => 'week',
        'month' => 'month',
    );
    foreach ( $units as $k => $label ) {
        echo '<option value="' . esc_attr( $k ) . '" ' . selected( $repeat_unit, $k, false ) . '>' . esc_html( $label ) . '</option>';
    }
    echo '</select>';
    echo '</div>';

    $time_min = isset( $schedule['time_of_day_minutes'] ) ? (int) $schedule['time_of_day_minutes'] : 120;
    if ( $time_min < 0 ) { $time_min = 0; }
    if ( $time_min > 1439 ) { $time_min = 1439; }
    $time_h = (int) floor( $time_min / 60 );
    $time_m = (int) ( $time_min % 60 );
    $time_str = sprintf( '%02d:%02d', $time_h, $time_m );
    $tz_label = joes_scanner_auth_get_site_timezone_label();

    echo '<div id="jsauth-backup-time-wrap" style="display:none; gap:8px; align-items:center;">';
    echo '<span class="jsauth-muted">at</span>';
    echo '<input type="time" name="jsauth_backup_time" value="' . esc_attr( $time_str ) . '" step="60" style="width: 130px;" />';
    echo '<span class="jsauth-muted">(' . esc_html( $tz_label ) . ')</span>';
    echo '</div>';

    echo '<div style="display:flex; gap:8px; align-items:center;">';
    echo '<span class="jsauth-muted">Keep</span>';
    echo '<input type="number" min="1" step="1" name="jsauth_backup_keep_count" value="' . esc_attr( max( 1, $keep_count ) ) . '" style="width: 90px;" />';
    echo '<span class="jsauth-muted">files</span>';
    echo '</div>';

    echo '</div>';

    // Repeat on weekdays.
    $dow_labels = array( 'S', 'M', 'T', 'W', 'T', 'F', 'S' );
    echo '<div id="jsauth-backup-weekdays" style="margin: 10px 0 12px 0; display:none;">';
    echo '<div class="jsauth-muted" style="margin-bottom: 8px;">Repeat on</div>';
    echo '<div style="display:flex; gap: 8px; flex-wrap: wrap;">';
    for ( $i = 0; $i <= 6; $i++ ) {
        $is_on = in_array( $i, $repeat_on, true );
        $style = $is_on ? 'background:#2271b1; border-color:#2271b1; color:#fff;' : 'background:#f0f0f1; border-color:#c3c4c7; color:#1d2327;';
        echo '<label style="display:inline-flex; align-items:center; justify-content:center; width: 34px; height: 34px; border-radius: 18px; border:1px solid; cursor:pointer; user-select:none; ' . esc_attr( $style ) . '">';
        echo '<input type="checkbox" name="jsauth_backup_repeat_on[]" value="' . esc_attr( $i ) . '" ' . checked( $is_on, true, false ) . ' style="display:none;" />';
        echo esc_html( $dow_labels[ $i ] );
        echo '</label>';
    }
    echo '</div>';
    echo '</div>';

    // Ends.
    echo '<div style="margin-top: 8px;">';
    echo '<div class="jsauth-muted" style="margin-bottom: 8px;">Ends</div>';
    echo '<div style="display:flex; flex-wrap: wrap; gap: 14px; align-items: center;">';

    echo '<label style="display:inline-flex; gap: 6px; align-items:center;">';
    echo '<input type="radio" name="jsauth_backup_ends_mode" value="never" ' . checked( $ends_mode, 'never', false ) . ' />';
    echo 'Never';
    echo '</label>';

    echo '<label style="display:inline-flex; gap: 6px; align-items:center;">';
    echo '<input type="radio" name="jsauth_backup_ends_mode" value="on" ' . checked( $ends_mode, 'on', false ) . ' />';
    echo 'On';
    echo '<input type="date" id="jsauth-backup-ends-on" name="jsauth_backup_ends_on" value="' . esc_attr( $ends_on ) . '" style="margin-left: 6px;" />';
    echo '</label>';

    echo '<label style="display:inline-flex; gap: 6px; align-items:center;">';
    echo '<input type="radio" name="jsauth_backup_ends_mode" value="after" ' . checked( $ends_mode, 'after', false ) . ' />';
    echo 'After';
    echo '<input type="number" id="jsauth-backup-ends-after" min="0" step="1" name="jsauth_backup_ends_after" value="' . esc_attr( max( 0, $ends_after ) ) . '" style="width: 90px; margin-left: 6px;" />';
    echo '<span class="jsauth-muted">occurrences</span>';
    echo '</label>';

    echo '</div>';
    echo '</div>';

    if ( $next_run_utc > 0 ) {
        echo '<p class="description" style="margin-top: 10px;">Next backup (site time): <strong>' . esc_html( function_exists( 'wp_date' ) ? wp_date( 'Y-m-d H:i:s', $next_run_utc ) : date_i18n( 'Y-m-d H:i:s', $next_run_utc ) ) . '</strong> <span class="jsauth-muted">(' . esc_html( joes_scanner_auth_get_site_timezone_label() ) . ')</span></p>';
    } else {
        echo '<p class="description" style="margin-top: 10px;">Next backup (site time): <strong>Not scheduled</strong></p>';
    }

    submit_button( 'Save automatic backup settings', 'secondary', 'submit', false );
    echo '</form>';

    echo '<script>(function(){
        function updateWeekdays(){
            var unit = document.getElementById("jsauth-backup-unit");
            var w = document.getElementById("jsauth-backup-weekdays");
            if(!unit || !w){return;}
            w.style.display = (unit.value === "week") ? "block" : "none";
        }

        function updateTime(){
            var unit = document.getElementById("jsauth-backup-unit");
            var wrap = document.getElementById("jsauth-backup-time-wrap");
            if(!unit || !wrap){return;}
            var show = (unit.value === "day" || unit.value === "week" || unit.value === "month");
            wrap.style.display = show ? "flex" : "none";
            var t = wrap.querySelector("input[type=time]");
            if(t){ t.disabled = !show; }
        }


        function updateEnds(){
            var mode = document.querySelector("input[name=jsauth_backup_ends_mode]:checked");
            var endsOn = document.getElementById("jsauth-backup-ends-on");
            var endsAfter = document.getElementById("jsauth-backup-ends-after");

            var m = mode ? mode.value : "never";

            if(endsOn){
                endsOn.disabled = (m !== "on");
                if(m !== "on"){
                    endsOn.value = endsOn.value || "";
                }
            }

            if(endsAfter){
                endsAfter.disabled = (m !== "after");
                if(m !== "after"){
                    endsAfter.value = "0";
                } else {
                    // Allow 0 while editing, server only enforces 1 when mode is after.
                    if(endsAfter.value === ""){
                        endsAfter.value = "1";
                    }
                }
            }
        }

        document.addEventListener("DOMContentLoaded", function(){
            updateWeekdays();
            updateTime();
            updateEnds();

            var unit = document.getElementById("jsauth-backup-unit");
            if(unit){unit.addEventListener("change", function(){ updateWeekdays(); updateTime(); });}

            document.querySelectorAll("input[name=jsauth_backup_ends_mode]").forEach(function(r){
                r.addEventListener("change", updateEnds);
            });

            document.querySelectorAll("#jsauth-backup-weekdays label").forEach(function(lbl){
                lbl.addEventListener("click", function(){
                    var input = lbl.querySelector("input[type=checkbox]");
                    if(!input){return;}
                    setTimeout(function(){
                        if(input.checked){
                            lbl.style.background="#2271b1";
                            lbl.style.borderColor="#2271b1";
                            lbl.style.color="#fff";
                        } else {
                            lbl.style.background="#f0f0f1";
                            lbl.style.borderColor="#c3c4c7";
                            lbl.style.color="#1d2327";
                        }
                    },0);
                });
            });
        });
    })();</script>';

    echo '</div>';
    $backups = joes_scanner_auth_list_backup_files();
    echo '<div style="margin-top: 12px;">';
    if ( ! $backups ) {
        echo '<p class="jsauth-muted">No backups found.</p>';
    } else {
        echo '<table class="widefat striped jsauth-table jsauth-backup-table">';
        echo '<thead><tr>';
        echo '<th>File</th>';
        echo '<th>Created (UTC)</th>';
        echo '<th>Size</th>';
        echo '<th class="jsauth-backup-actions">Actions</th>';
        echo '</tr></thead><tbody>';

        foreach ( $backups as $b ) {
            $name  = isset( $b['name'] ) ? (string) $b['name'] : '';
            $mtime = isset( $b['mtime'] ) ? (int) $b['mtime'] : 0;
            $size  = isset( $b['size'] ) ? (int) $b['size'] : 0;

            $download_nonce = wp_create_nonce( 'joes_scanner_auth_download_backup:' . $name );
            $download_url   = admin_url( 'admin-post.php?action=joes_scanner_auth_download_backup&file=' . rawurlencode( $name ) . '&_wpnonce=' . rawurlencode( $download_nonce ) );

            echo '<tr>';
            echo '<td>' . esc_html( $name ) . '</td>';
            echo '<td>' . esc_html( $mtime ? gmdate( 'Y-m-d H:i:s', $mtime ) : '' ) . '</td>';
            echo '<td>' . esc_html( size_format( $size, 2 ) ) . '</td>';
            echo '<td class="jsauth-backup-actions">';

            echo '<div class="jsauth-inline-actions">';
            echo '<a class="button button-secondary" href="' . esc_url( $download_url ) . '">Download</a>';

            echo '<form method="post" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '" style="display:inline-block;">';
            echo '<input type="hidden" name="action" value="joes_scanner_auth_restore_backup" />';
            echo '<input type="hidden" name="file" value="' . esc_attr( $name ) . '" />';
            wp_nonce_field( 'joes_scanner_auth_backup_actions' );
            echo '<button class="button button-secondary" type="submit" onclick="return confirm(\'Restore this backup? This will replace current plugin tables and cannot be undone.\');">Restore</button>';
            echo '</form>';

            echo '<form method="post" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '" style="display:inline-block;">';
            echo '<input type="hidden" name="action" value="joes_scanner_auth_delete_backup" />';
            echo '<input type="hidden" name="file" value="' . esc_attr( $name ) . '" />';
            wp_nonce_field( 'joes_scanner_auth_backup_actions' );
            echo '<button class="button button-secondary" type="submit" onclick="return confirm(\'Delete this backup file? This cannot be undone.\');">Delete</button>';
            echo '</form>';
            echo '</div>';

            echo '</td>';
            echo '</tr>';
        }

        echo '</tbody></table>';
    }
    echo '</div>';

    echo '<div style="margin-top: 16px;">';
    echo '<h3 style="margin-bottom: 8px;">Restore from upload</h3>';
    echo '<form method="post" enctype="multipart/form-data" action="' . esc_url( admin_url( 'admin-post.php' ) ) . '">';
    echo '<input type="hidden" name="action" value="joes_scanner_auth_restore_upload" />';
    wp_nonce_field( 'joes_scanner_auth_backup_actions' );
    echo '<div class="jsauth-inline-actions">';
    echo '<input type="file" name="backup_file" accept=".sql,.txt" required />';
    echo '<button class="button button-secondary" type="submit" onclick="return confirm(\'Restore from uploaded file? This will replace current plugin tables and cannot be undone.\');">Restore upload</button>';
    echo '</div>';
    echo '</form>';
    echo '<p class="description">Upload a backup file created by this plugin and restore it.</p>';
    echo '</div>';

    echo '</div>'; // backups card

    // Right column: retention and actions.
    echo '<div>';

    echo '<div class="jsauth-card">';
    echo '<h3>Retention</h3>';
    echo '<p class="description">Controls how long plugin data is retained and when it is deleted during pruning.</p>';
    joes_scanner_auth_sync_allowed_prices_from_surecart( 'surecart_tab_render' );

    echo '<form action="options.php" method="post">';
    settings_fields( 'joes_scanner_auth' );
    do_settings_sections( 'joes_scanner_auth_database' );
    submit_button( 'Save retention settings' );
    echo '</form>';
    echo '<p class="description">Retention settings are applied when you run Prune Database and during the daily automatic prune.</p>';
    echo '</div>';

    // Database action selection trees.
    $prune_tree = array(
        'Tracking' => array(
            'events'     => 'Auth events',
            'app_events' => 'Admin events',
            'sessions'   => 'Sessions',
        ),
        'Communications' => array(
            'comms_messages' => 'Messages',
            'comms_changes'  => 'Change history',
        ),
    );

    $clear_tree = array(
        'Tracking' => array(
            'events'          => 'Auth events',
            'app_events'      => 'Admin events',
            'sessions'        => 'Sessions',
            'devices'         => 'Devices',
            'blocked_ips'     => 'Blocked IPs',
            'device_settings' => 'Device settings',
        ),
        'Communications' => array(
            'comms_messages' => 'Messages',
            'comms_changes'  => 'Change history',
        ),
    );

    echo '<style>
        .jsauth-tree-wrap { margin-top: 10px; }
        .jsauth-tree-actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-bottom: 8px; }
        .jsauth-tree { border: 1px solid #dcdcde; background: #fff; padding: 10px 12px; border-radius: 4px; }
        .jsauth-tree-group { margin: 0 0 10px 0; }
        .jsauth-tree-group:last-child { margin-bottom: 0; }
        .jsauth-tree-parent-label { font-weight: 600; }
        .jsauth-tree-children { margin: 6px 0 0 18px; padding: 0 0 0 10px; border-left: 2px solid #dcdcde; }
        .jsauth-tree-children li { margin: 2px 0; }
        .jsauth-tree-children ul { list-style: none; margin: 0; padding: 0; }
        .jsauth-tree-children label { display: inline-flex; gap: 6px; align-items: center; }
        .jsauth-tree-group label { display: inline-flex; gap: 6px; align-items: center; }
    </style>';

    echo '<script>(function(){
        function updateParentState(group){
            var parent = group.querySelector(".jsauth-tree-parent");
            if(!parent){return;}
            var kids = Array.prototype.slice.call(group.querySelectorAll(".jsauth-tree-child"));
            if(kids.length===0){parent.indeterminate=false;return;}
            var checked = kids.filter(function(c){return c.checked;}).length;
            if(checked===0){parent.checked=false; parent.indeterminate=false;return;}
            if(checked===kids.length){parent.checked=true; parent.indeterminate=false;return;}
            parent.checked=false; parent.indeterminate=true;
        }

        function initTree(tree){
            var groups = Array.prototype.slice.call(tree.querySelectorAll(".jsauth-tree-group"));
            groups.forEach(function(g){
                updateParentState(g);
                var parent = g.querySelector(".jsauth-tree-parent");
                if(parent){
                    parent.addEventListener("change", function(){
                        var kids = Array.prototype.slice.call(g.querySelectorAll(".jsauth-tree-child"));
                        kids.forEach(function(c){c.checked = parent.checked;});
                        parent.indeterminate = false;
                    });
                }
                var kids = Array.prototype.slice.call(g.querySelectorAll(".jsauth-tree-child"));
                kids.forEach(function(c){
                    c.addEventListener("change", function(){ updateParentState(g); });
                });
            });
        }

        function setAll(tree, checked){
            var kids = Array.prototype.slice.call(tree.querySelectorAll(".jsauth-tree-child"));
            kids.forEach(function(c){c.checked=checked;});
            var parents = Array.prototype.slice.call(tree.querySelectorAll(".jsauth-tree-parent"));
            parents.forEach(function(p){p.checked=checked; p.indeterminate=false;});
        }

        document.addEventListener("DOMContentLoaded", function(){
            var trees = Array.prototype.slice.call(document.querySelectorAll(".jsauth-tree"));
            trees.forEach(initTree);

            Array.prototype.slice.call(document.querySelectorAll(".jsauth-tree-selectall")).forEach(function(btn){
                btn.addEventListener("click", function(){
                    var target = btn.getAttribute("data-target");
                    var tree = document.querySelector(".jsauth-tree[data-tree=\\\"" + target + "\\\"]");
                    if(tree){setAll(tree,true);}
                });
            });
            Array.prototype.slice.call(document.querySelectorAll(".jsauth-tree-selectnone")).forEach(function(btn){
                btn.addEventListener("click", function(){
                    var target = btn.getAttribute("data-target");
                    var tree = document.querySelector(".jsauth-tree[data-tree=\\\"" + target + "\\\"]");
                    if(tree){setAll(tree,false);}
                });
            });
        });
    })();</script>';

    echo '<div class="jsauth-card" style="margin-top: 16px;">';
    echo '<h3>Prune database</h3>';
    echo '<form method="post" action="">';
    wp_nonce_field( 'joes_scanner_auth_database_actions' );
    echo '<input type="hidden" name="joes_scanner_auth_action" value="prune_database" />';

    echo '<div class="jsauth-tree-wrap">';
    echo '<div class="jsauth-tree-actions">';
    echo '<button type="button" class="button button-secondary jsauth-tree-selectall" data-target="prune">Select all</button>';
    echo '<button type="button" class="button button-secondary jsauth-tree-selectnone" data-target="prune">Select none</button>';
    echo '</div>';
    echo '<div class="jsauth-tree" data-tree="prune">';
    foreach ( $prune_tree as $group_label => $items ) {
        echo '<div class="jsauth-tree-group">';
        echo '<label class="jsauth-tree-parent-label"><input type="checkbox" class="jsauth-tree-parent" /> ' . esc_html( $group_label ) . '</label>';
        echo '<div class="jsauth-tree-children"><ul>';
        foreach ( $items as $key => $label ) {
            echo '<li><label><input class="jsauth-tree-child" type="checkbox" name="prune_tables[]" value="' . esc_attr( $key ) . '" checked /> ' . esc_html( $label ) . '</label></li>';
        }
        echo '</ul></div>';
        echo '</div>';
    }
    echo '</div>';
    echo '</div>';

    echo '<p><button class="button button-secondary" type="submit" onclick="return confirm(\'Prune database now? This deletes old rows based on the retention settings.\');">Prune Database</button></p>';
    echo '<p class="description">Deletes old rows based on the retention settings for the selected tables. This also runs the binding reconciliation tasks.</p>';
    echo '</form>';
    echo '</div>';

    echo '<div class="jsauth-card" style="margin-top: 16px;">';
    echo '<h3>Clear databases</h3>';
    echo '<form method="post" action="">';
    wp_nonce_field( 'joes_scanner_auth_database_actions' );
    echo '<input type="hidden" name="joes_scanner_auth_action" value="clear_all" />';

    echo '<div class="jsauth-tree-wrap">';
    echo '<div class="jsauth-tree-actions">';
    echo '<button type="button" class="button button-secondary jsauth-tree-selectall" data-target="clear">Select all</button>';
    echo '<button type="button" class="button button-secondary jsauth-tree-selectnone" data-target="clear">Select none</button>';
    echo '</div>';
    echo '<div class="jsauth-tree" data-tree="clear">';
    foreach ( $clear_tree as $group_label => $items ) {
        echo '<div class="jsauth-tree-group">';
        echo '<label class="jsauth-tree-parent-label"><input type="checkbox" class="jsauth-tree-parent" /> ' . esc_html( $group_label ) . '</label>';
        echo '<div class="jsauth-tree-children"><ul>';
        foreach ( $items as $key => $label ) {
            echo '<li><label><input class="jsauth-tree-child" type="checkbox" name="clear_tables[]" value="' . esc_attr( $key ) . '" checked /> ' . esc_html( $label ) . '</label></li>';
        }
        echo '</ul></div>';
        echo '</div>';
    }
    echo '</div>';
    echo '</div>';

    echo '<p><button class="button button-secondary" type="submit" onclick="return confirm(\'Clear selected databases? This deletes rows from the selected tables and cannot be undone.\');">Clear databases</button></p>';
    echo '<p class="description">Truncates the selected tables. This cannot be undone.</p>';
    echo '</form>';
    echo '</div>';

    echo '</div>'; // right column container
    echo '</div>'; // grid
}

function joes_scanner_auth_render_devices_tab() {
    global $wpdb;

    $devices_table   = joes_scanner_auth_table_devices();
    $sessions_table  = joes_scanner_auth_table_sessions();
    $blocked_ips_tbl = joes_scanner_auth_table_blocked_ips();

    $page_size  = 50;
    $page_param = 'devices_page';
    $page       = isset( $_GET[ $page_param ] ) ? max( 1, (int) $_GET[ $page_param ] ) : 1;

    $total = (int) $wpdb->get_var( "SELECT COUNT(*) FROM {$devices_table}" );
    $pages = max( 1, (int) ceil( $total / $page_size ) );
    if ( $page > $pages ) {
        $page = $pages;
    }

    $offset = ( $page - 1 ) * $page_size;

    $rows = $wpdb->get_results(
        $wpdb->prepare(
            "SELECT
                d.device_id_hash,
                d.user_id,
                d.username,
                d.email,
                COALESCE(NULLIF(d.device_platform, ''), (SELECT s.device_platform FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.device_platform IS NOT NULL AND s.device_platform <> '' ORDER BY s.last_seen_utc DESC LIMIT 1)) AS device_platform,
                COALESCE(NULLIF(d.device_type, ''), (SELECT s.device_type FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.device_type IS NOT NULL AND s.device_type <> '' ORDER BY s.last_seen_utc DESC LIMIT 1)) AS device_type,
                COALESCE(NULLIF(d.device_model, ''), (SELECT s.device_model FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.device_model IS NOT NULL AND s.device_model <> '' ORDER BY s.last_seen_utc DESC LIMIT 1)) AS device_model,
                COALESCE(NULLIF(d.app_version, ''), (SELECT s.app_version FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.app_version IS NOT NULL AND s.app_version <> '' ORDER BY s.last_seen_utc DESC LIMIT 1)) AS app_version,
                (SELECT s.stream_server_url FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.stream_server_url IS NOT NULL AND s.stream_server_url <> '' ORDER BY s.last_seen_utc DESC LIMIT 1) AS stream_server_url,
                (SELECT s.stream_server_is_hosted FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash AND s.stream_server_url IS NOT NULL AND s.stream_server_url <> '' ORDER BY s.last_seen_utc DESC LIMIT 1) AS stream_server_is_hosted,
                d.first_seen_utc,
                d.last_seen_utc,
                d.last_ip,
                d.is_blocked,
                d.blocked_at_utc,
                (SELECT COUNT(*) FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash) AS session_count,
                (SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, s.first_seen_utc, s.last_seen_utc)), 0) FROM {$sessions_table} s WHERE s.device_id_hash = d.device_id_hash) AS total_seconds
             FROM {$devices_table} d
             ORDER BY d.last_seen_utc DESC
             LIMIT %d OFFSET %d",
            $page_size,
            $offset
        ),
        ARRAY_A
    );

    $tz_label = joes_scanner_auth_get_site_timezone_label();

    echo '<h2>Devices</h2>';
    echo '<p class="description">Showing device timestamps in site timezone: ' . esc_html( $tz_label ) . '</p>';

    $base_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=devices' );
    joes_scanner_auth_render_pager( $base_url, $page_param, $page, $pages, array() );

    echo '<table class="widefat striped jsauth-table">';
    echo '<thead><tr>';
    echo '<th class="jsauth-col-device">Device</th>';
    echo '<th class="jsauth-col-num">Blocked</th>';
    echo '<th class="jsauth-col-account">Account</th>';
    echo '<th class="jsauth-col-platform">Platform</th>';
    echo '<th class="jsauth-col-type">Type</th>';
    echo '<th class="jsauth-col-model">Model</th>';
    echo '<th class="jsauth-col-app">App</th>';
    echo '<th class="jsauth-col-server">Server</th>';
    echo '<th class="jsauth-col-dt">First seen</th>';
    echo '<th class="jsauth-col-dt">Last seen</th>';
    echo '<th class="jsauth-col-ip">IP</th>';
    echo '<th class="jsauth-col-num">Sessions</th>';
    echo '<th class="jsauth-col-dur">Total time</th>';
    echo '<th class="jsauth-col-actions">Actions</th>';
    echo '</tr></thead><tbody>';

    if ( empty( $rows ) ) {
        echo '<tr><td colspan="14">No devices found yet.</td></tr>';
    } else {
        foreach ( $rows as $r ) {
            $device_hash  = (string) $r['device_id_hash'];
            $device_short = $device_hash ? substr( $device_hash, 0, 8 ) : '';

            $is_blocked = isset( $r['is_blocked'] ) ? (int) $r['is_blocked'] : 0;

            $user_id  = isset( $r['user_id'] ) ? (int) $r['user_id'] : 0;
            $username = isset( $r['username'] ) ? (string) $r['username'] : '';
            $email    = isset( $r['email'] ) ? (string) $r['email'] : '';

            $acct_label = $device_short !== '' ? $device_short : 'Unregistered';
            if ( $user_id > 0 ) {
                // Show just one identifier if username and email are the same.
                $base = $username ? $username : '';
                if ( $base === '' && $email ) {
                    $base = $email;
                }
                if ( $base === '' ) {
                    $base = 'User #' . $user_id;
                }

                $acct_label = $base;

                if ( $email && $username ) {
                    if ( strtolower( $email ) !== strtolower( $username ) ) {
                        $acct_label .= ' (' . $email . ')';
                    }
                }
            }

            $plat  = isset( $r['device_platform'] ) ? (string) $r['device_platform'] : '';
            $type  = isset( $r['device_type'] ) ? (string) $r['device_type'] : '';
            $model = isset( $r['device_model'] ) ? (string) $r['device_model'] : '';
            $app   = isset( $r['app_version'] ) ? (string) $r['app_version'] : '';
            $server_url  = isset( $r['stream_server_url'] ) ? (string) $r['stream_server_url'] : '';
            $server_hosted = isset( $r['stream_server_is_hosted'] ) ? (int) $r['stream_server_is_hosted'] : -1;
            $server_disp = joes_scanner_auth_format_stream_server_display( $server_url, $server_hosted );

            $first = isset( $r['first_seen_utc'] ) ? (string) $r['first_seen_utc'] : '';
            $last  = isset( $r['last_seen_utc'] ) ? (string) $r['last_seen_utc'] : '';
            $ip    = isset( $r['last_ip'] ) ? (string) $r['last_ip'] : '';

            $sess_cnt = isset( $r['session_count'] ) ? (int) $r['session_count'] : 0;
            $tot_sec  = isset( $r['total_seconds'] ) ? (int) $r['total_seconds'] : 0;

            echo '<tr>';
            echo '<td title="' . esc_attr( $device_hash ) . '">' . esc_html( $device_short ) . '</td>';
            echo '<td>' . esc_html( $is_blocked ? 'Yes' : 'No' ) . '</td>';
            echo '<td class="jsauth-wrap">' . esc_html( $acct_label ) . '</td>';
            echo '<td>' . esc_html( $plat ?: 'Unknown' ) . '</td>';
            echo '<td>' . esc_html( $type ?: 'Unknown' ) . '</td>';
            list( $model_disp, $model_trunc ) = joes_scanner_auth_truncate_table_text( $model, 25 );
            if ( $model_disp !== '' ) {
                echo '<td title="' . esc_attr( $model ) . '"><span class="jsauth-ellipsis">' . esc_html( $model_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
            echo '<td>' . esc_html( $app ) . '</td>';
            if ( ! empty( $server_disp[0] ) ) {
                list( $srv_disp, $srv_trunc ) = joes_scanner_auth_truncate_table_text( $server_disp[0], 50 );
                echo '<td title="' . esc_attr( $server_disp[1] ) . '"><span class="jsauth-ellipsis">' . esc_html( $srv_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
            echo '<td>' . esc_html( joes_scanner_auth_format_datetime_local( $first ) ) . '</td>';
            echo '<td>' . esc_html( joes_scanner_auth_format_datetime_local( $last ) ) . '</td>';
            list( $ip_disp, $ip_title, $ip_cls ) = joes_scanner_auth_format_ip_for_table( $ip );
            if ( $ip_disp !== '' ) {
                echo '<td class="jsauth-ip-cell ' . esc_attr( $ip_cls ) . '" title="' . esc_attr( $ip_title ) . '"><span class="jsauth-ellipsis">' . esc_html( $ip_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
            echo '<td>' . esc_html( number_format_i18n( $sess_cnt ) ) . '</td>';
            echo '<td>' . esc_html( joes_scanner_auth_format_duration( $tot_sec ) ) . '</td>';

            // Use admin-post endpoints so each button targets a single row deterministically.
            $admin_post_url      = admin_url( 'admin-post.php' );
            $device_action_nonce = wp_create_nonce( 'joes_scanner_auth_device_action' );

            echo '<td class="jsauth-actions">';

            // Block/unblock device.
            echo '<form method="post" action="' . esc_url( $admin_post_url ) . '" style="display:inline;">';
            echo '<input type="hidden" name="action" value="joes_scanner_auth_set_device_blocked" />';
            echo '<input type="hidden" name="nonce" value="' . esc_attr( $device_action_nonce ) . '" />';
            echo '<input type="hidden" name="device_id_hash" value="' . esc_attr( $device_hash ) . '" />';
            echo '<input type="hidden" name="blocked" value="' . esc_attr( $is_blocked ? '0' : '1' ) . '" />';
            if ( $is_blocked ) {
                echo '<button class="button button-secondary" type="submit">Unblock</button>';
            } else {
                echo '<button class="button button-secondary" type="submit">Block</button>';
            }
            echo '</form>';

            // Block IP (if present).
            if ( $ip ) {
                echo '<form method="post" action="' . esc_url( $admin_post_url ) . '" style="display:inline;" onsubmit="return confirm(\'Block IP ' . esc_js( $ip ) . '?\');">';
                echo '<input type="hidden" name="action" value="joes_scanner_auth_set_ip_blocked" />';
                echo '<input type="hidden" name="nonce" value="' . esc_attr( $device_action_nonce ) . '" />';
                echo '<input type="hidden" name="ip" value="' . esc_attr( $ip ) . '" />';
                echo '<input type="hidden" name="blocked" value="1" />';
                echo '<button class="button button-secondary" type="submit">Block IP</button>';
                echo '</form>';
            }

            // Clear device.
            echo '<form method="post" action="' . esc_url( $admin_post_url ) . '" style="display:inline;" onsubmit="return confirm(\'Clear this device and delete its sessions and saved settings?\');">';
            echo '<input type="hidden" name="action" value="joes_scanner_auth_clear_device" />';
            echo '<input type="hidden" name="nonce" value="' . esc_attr( $device_action_nonce ) . '" />';
            echo '<input type="hidden" name="scope" value="global" />';
            echo '<input type="hidden" name="user_key" value="0" />';
            echo '<input type="hidden" name="device_id_hash" value="' . esc_attr( $device_hash ) . '" />';
            echo '<button class="button button-secondary" type="submit">Clear</button>';
            echo '</form>';

            echo '</td>';

            echo '</tr>';
        }
    }

    echo '</tbody></table>';

    joes_scanner_auth_render_pager( $base_url, $page_param, $page, $pages, array() );

    $blocked = $wpdb->get_results(
        "SELECT ip, blocked_at_utc, reason FROM {$blocked_ips_tbl} ORDER BY blocked_at_utc DESC LIMIT 200",
        ARRAY_A
    );

    echo '<h3 style="margin-top:20px;">Blocked IPs</h3>';
    echo '<table class="widefat fixed striped" style="max-width: 800px;">';
    echo '<thead><tr><th>IP</th><th>Blocked at</th><th>Reason</th><th>Action</th></tr></thead><tbody>';

    if ( empty( $blocked ) ) {
        echo '<tr><td colspan="4">No blocked IPs.</td></tr>';
    } else {
        foreach ( $blocked as $b ) {
            $ip = (string) $b['ip'];
            echo '<tr>';
            list( $ip_disp, $ip_title, $ip_cls ) = joes_scanner_auth_format_ip_for_table( $ip );
            if ( $ip_disp !== '' ) {
                echo '<td class="jsauth-ip-cell ' . esc_attr( $ip_cls ) . '" title="' . esc_attr( $ip_title ) . '"><span class="jsauth-ellipsis">' . esc_html( $ip_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
            echo '<td>' . esc_html( joes_scanner_auth_format_datetime_local( (string) $b['blocked_at_utc'] ) ) . '</td>';
            echo '<td>' . esc_html( (string) $b['reason'] ) . '</td>';
            echo '<td class="jsauth-actions">';
            $admin_post_url      = admin_url( 'admin-post.php' );
            $device_action_nonce = wp_create_nonce( 'joes_scanner_auth_device_action' );
            echo '<form method="post" action="' . esc_url( $admin_post_url ) . '" style="display:inline;">';
            echo '<input type="hidden" name="action" value="joes_scanner_auth_set_ip_blocked" />';
            echo '<input type="hidden" name="nonce" value="' . esc_attr( $device_action_nonce ) . '" />';
            echo '<input type="hidden" name="ip" value="' . esc_attr( $ip ) . '" />';
            echo '<input type="hidden" name="blocked" value="0" />';
            echo '<button class="button button-secondary" type="submit">Unblock</button>';
            echo '</form>';
            echo '</td>';
            echo '</tr>';
        }
    }

    echo '</tbody></table>';
}


function joes_scanner_auth_render_active_tab() {
    global $wpdb;

    $opts = joes_scanner_auth_get_options();

    $sessions_table = joes_scanner_auth_table_sessions();
    $devices_table  = joes_scanner_auth_table_devices();

    $active_window_minutes = (int) $opts['active_window_minutes'];
    $active_cutoff_utc = gmdate( 'Y-m-d H:i:s', time() - ( $active_window_minutes * MINUTE_IN_SECONDS ) );

    $summary = $wpdb->get_row(
        $wpdb->prepare(
            "SELECT
                COUNT(*) AS active_sessions,
                COUNT(DISTINCT CONCAT(
                    COALESCE(NULLIF(CAST(s.user_id AS CHAR), '0'), NULLIF(CAST(d.user_id AS CHAR), '0'), ''), '|',
                    COALESCE(NULLIF(s.email, ''), NULLIF(d.email, ''), ''), '|',
                    COALESCE(NULLIF(s.device_id_hash, ''), '')
                )) AS active_accounts,
                AVG(TIMESTAMPDIFF(SECOND, s.first_seen_utc, s.last_seen_utc)) AS avg_seconds
             FROM {$sessions_table} s
             LEFT JOIN {$devices_table} d ON d.device_id_hash = s.device_id_hash
             WHERE s.last_seen_utc >= %s",
            $active_cutoff_utc
        ),
        ARRAY_A
    );

    $active_sessions = isset( $summary['active_sessions'] ) ? (int) $summary['active_sessions'] : 0;
    $active_accounts = isset( $summary['active_accounts'] ) ? (int) $summary['active_accounts'] : 0;
    $avg_seconds     = isset( $summary['avg_seconds'] ) ? (int) $summary['avg_seconds'] : 0;

    $tz_label = joes_scanner_auth_get_site_timezone_label();

    echo '<h2>Active</h2>';
    echo '<p class="description">Showing timestamps in site timezone: ' . esc_html( $tz_label ) . '</p>';

    echo '<p>';
    echo 'Active sessions: <strong>' . esc_html( number_format_i18n( $active_sessions ) ) . '</strong> ';
    echo 'Active accounts: <strong>' . esc_html( number_format_i18n( $active_accounts ) ) . '</strong> ';
    echo 'Average session length (active only): <strong>' . esc_html( joes_scanner_auth_format_duration( $avg_seconds ) ) . '</strong>';
    echo '</p>';

    $refresh_seconds = isset( $opts['active_tab_refresh_seconds'] ) ? (int) $opts['active_tab_refresh_seconds'] : 0;
    if ( $refresh_seconds > 0 ) {
        $ms = (int) $refresh_seconds * 1000;
        echo '<script>(function(){setTimeout(function(){try{location.reload();}catch(e){}},' . (int) $ms . ');})();</script>';
    }

    $active_rows = $wpdb->get_results(
        $wpdb->prepare(
            "SELECT
                COALESCE(NULLIF(s.user_id, 0), NULLIF(d.user_id, 0), 0) AS user_id,
                COALESCE(NULLIF(s.username, ''), NULLIF(d.username, ''), '') AS username,
                COALESCE(NULLIF(s.email, ''), NULLIF(d.email, ''), '') AS email,
                s.session_token,
                COALESCE(NULLIF(s.device_id_hash, ''), NULLIF(d.device_id_hash, ''), '') AS device_id_hash,
                COALESCE(NULLIF(s.device_platform, ''), NULLIF(d.device_platform, ''), '') AS device_platform,
                COALESCE(NULLIF(s.device_type, ''), NULLIF(d.device_type, ''), '') AS device_type,
                COALESCE(NULLIF(s.device_model, ''), NULLIF(d.device_model, ''), '') AS device_model,
                COALESCE(NULLIF(s.app_version, ''), NULLIF(d.app_version, ''), '') AS app_version,
                s.stream_server_url,
                s.stream_server_is_hosted,
                s.first_seen_utc,
                s.last_seen_utc,
                COALESCE(NULLIF(s.last_ip, ''), NULLIF(d.last_ip, ''), '') AS last_ip,
                TIMESTAMPDIFF(SECOND, s.first_seen_utc, s.last_seen_utc) AS duration_seconds
             FROM {$sessions_table} s
             LEFT JOIN {$devices_table} d ON d.device_id_hash = s.device_id_hash
             WHERE s.last_seen_utc >= %s
             ORDER BY s.first_seen_utc DESC, s.last_seen_utc DESC
             LIMIT 500",
            $active_cutoff_utc
        ),
        ARRAY_A
    );

    echo '<table class="widefat striped jsauth-table jsauth-history-accounts">';
    echo '<thead><tr>';
    echo '<th class="jsauth-col-account">Account</th>';
    echo '<th class="jsauth-col-session">Session</th>';
    echo '<th class="jsauth-col-device">Device</th>';
    echo '<th class="jsauth-col-ip">IP</th>';
    echo '<th class="jsauth-col-platform">Platform</th>';
    echo '<th class="jsauth-col-type">Type</th>';
    echo '<th class="jsauth-col-model">Model</th>';
    echo '<th class="jsauth-col-app">App</th>';
    echo '<th class="jsauth-col-server">Server</th>';
    echo '<th class="jsauth-col-dt">Session start</th>';
    echo '<th class="jsauth-col-dt">Last seen</th>';
    echo '<th class="jsauth-col-dur">Session time</th>';
    echo '</tr></thead><tbody>';

    if ( empty( $active_rows ) ) {
        echo '<tr><td colspan="12">No active sessions.</td></tr>';
    } else {
        foreach ( $active_rows as $s ) {
            $user_id  = isset( $s['user_id'] ) ? (int) $s['user_id'] : 0;
            $username = isset( $s['username'] ) ? (string) $s['username'] : '';
            $email    = isset( $s['email'] ) ? (string) $s['email'] : '';

            $label = 'Unregistered';
            if ( $user_id > 0 ) {
                $label = $username ? $username : 'User #' . $user_id;
                if ( $email && ( ! $username || strcasecmp( $email, $username ) !== 0 ) ) {
                    $label .= ' (' . $email . ')';
                }
            } elseif ( $email ) {
                $label .= ' (' . $email . ')';
            }

$session_token = isset( $s['session_token'] ) ? (string) $s['session_token'] : '';
            $session_short = $session_token ? substr( $session_token, 0, 8 ) : '';

            $device_hash = isset( $s['device_id_hash'] ) ? (string) $s['device_id_hash'] : '';
            $device_short = $device_hash ? substr( $device_hash, 0, 8 ) : '';

            $ip       = isset( $s['last_ip'] ) ? (string) $s['last_ip'] : '';
            $plat     = isset( $s['device_platform'] ) ? (string) $s['device_platform'] : '';
            $type     = isset( $s['device_type'] ) ? (string) $s['device_type'] : '';
            $model    = isset( $s['device_model'] ) ? (string) $s['device_model'] : '';
            $app      = isset( $s['app_version'] ) ? (string) $s['app_version'] : '';
            $server_url  = isset( $s['stream_server_url'] ) ? (string) $s['stream_server_url'] : '';
            $server_hosted = isset( $s['stream_server_is_hosted'] ) ? (int) $s['stream_server_is_hosted'] : -1;
            $server_disp = joes_scanner_auth_format_stream_server_display( $server_url, $server_hosted );
            $start    = isset( $s['first_seen_utc'] ) ? (string) $s['first_seen_utc'] : '';
            $last     = isset( $s['last_seen_utc'] ) ? (string) $s['last_seen_utc'] : '';
            $dur_sec  = isset( $s['duration_seconds'] ) ? (int) $s['duration_seconds'] : 0;

            echo '<tr>';
            list( $label_disp, $label_trunc ) = joes_scanner_auth_truncate_table_text( $label, 50 );
            echo '<td class="jsauth-wrap" title="' . esc_attr( $label ) . '">' . esc_html( $label_disp ) . '</td>';
            echo '<td title="' . esc_attr( $session_token ) . '">' . esc_html( $session_short ) . '</td>';
            echo '<td title="' . esc_attr( $device_hash ) . '">' . esc_html( $device_short ) . '</td>';
            list( $ip_disp, $ip_title, $ip_cls ) = joes_scanner_auth_format_ip_for_table( $ip );
            if ( $ip_disp !== '' ) {
                echo '<td class="jsauth-ip-cell ' . esc_attr( $ip_cls ) . '" title="' . esc_attr( $ip_title ) . '"><span class="jsauth-ellipsis">' . esc_html( $ip_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
            echo '<td>' . esc_html( $plat ) . '</td>';
            echo '<td>' . esc_html( $type ) . '</td>';
            list( $model_disp, $model_trunc ) = joes_scanner_auth_truncate_table_text( $model, 25 );
            if ( $model_disp !== '' ) {
                echo '<td title="' . esc_attr( $model ) . '"><span class="jsauth-ellipsis">' . esc_html( $model_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
            echo '<td>' . esc_html( $app ) . '</td>';
            if ( ! empty( $server_disp[0] ) ) {
                list( $srv_disp, $srv_trunc ) = joes_scanner_auth_truncate_table_text( $server_disp[0], 50 );
                echo '<td title="' . esc_attr( $server_disp[1] ) . '"><span class="jsauth-ellipsis">' . esc_html( $srv_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
            echo '<td>' . esc_html( joes_scanner_auth_format_datetime_local( $start ) ) . '</td>';
            echo '<td>' . esc_html( joes_scanner_auth_format_datetime_local( $last ) ) . '</td>';
            echo '<td>' . esc_html( joes_scanner_auth_format_duration( $dur_sec ) ) . '</td>';
            echo '</tr>';
        }
    }

    echo '</tbody></table>';

    $inactive_limit = isset( $opts['active_tab_inactive_sessions'] ) ? (int) $opts['active_tab_inactive_sessions'] : 0;
    if ( $inactive_limit > 0 ) {
        echo '<h3 style="margin-top:20px;">Recently inactive sessions</h3>';

        $inactive = $wpdb->get_results(
            $wpdb->prepare(
                "SELECT
                    user_id,
                    username,
                    email,
                    session_token,
                    device_id_hash,
                    device_platform,
                    device_type,
                    device_model,
                    app_version,
                    stream_server_url,
                    stream_server_is_hosted,
                    first_seen_utc,
                    last_seen_utc,
                    last_ip,
                    TIMESTAMPDIFF(SECOND, first_seen_utc, last_seen_utc) AS duration_seconds
                 FROM {$sessions_table}
                 WHERE last_seen_utc < %s
                 ORDER BY last_seen_utc DESC
                 LIMIT %d",
                $active_cutoff_utc,
                $inactive_limit
            ),
            ARRAY_A
        );



        echo '<table class="widefat striped jsauth-table jsauth-history-accounts">';
        echo '<thead><tr>';
        echo '<th>Account</th>';
        echo '<th>Session</th>';
        echo '<th>Device</th>';
        echo '<th>IP</th>';
        echo '<th>Platform</th>';
        echo '<th>Type</th>';
        echo '<th>Model</th>';
        echo '<th>App</th>';
        echo '<th>Server</th>';
        echo '<th>Session start</th>';
        echo '<th>Last seen</th>';
        echo '<th>Session time</th>';
        echo '</tr></thead><tbody>';

        if ( empty( $inactive ) ) {
            echo '<tr><td colspan="12">No inactive sessions found.</td></tr>';
        } else {
            foreach ( $inactive as $s ) {
                $user_id  = isset( $s['user_id'] ) ? (int) $s['user_id'] : 0;
                $username = isset( $s['username'] ) ? (string) $s['username'] : '';
                $email    = isset( $s['email'] ) ? (string) $s['email'] : '';

                $label = 'Unregistered';
                if ( $user_id > 0 ) {
                    $label = $username ? $username : 'User #' . $user_id;
                                        if ( $email ) {
                        if ( ! $username || strcasecmp( $email, $username ) !== 0 ) {
                            $label .= ' (' . $email . ')';
                        }
                    }
                } else {
                    if ( $email ) {
                        $label .= ' (' . $email . ')';
                    }
                }

                $session_token = isset( $s['session_token'] ) ? (string) $s['session_token'] : '';
                $session_short = $session_token ? substr( $session_token, 0, 8 ) : '';

                $device_hash = isset( $s['device_id_hash'] ) ? (string) $s['device_id_hash'] : '';
                $device_short = $device_hash ? substr( $device_hash, 0, 8 ) : '';

                $ip       = isset( $s['last_ip'] ) ? (string) $s['last_ip'] : '';
                $plat     = isset( $s['device_platform'] ) ? (string) $s['device_platform'] : '';
                $type     = isset( $s['device_type'] ) ? (string) $s['device_type'] : '';
                $model    = isset( $s['device_model'] ) ? (string) $s['device_model'] : '';
                $app      = isset( $s['app_version'] ) ? (string) $s['app_version'] : '';
                $server_url  = isset( $s['stream_server_url'] ) ? (string) $s['stream_server_url'] : '';
                $server_hosted = isset( $s['stream_server_is_hosted'] ) ? (int) $s['stream_server_is_hosted'] : -1;
                $server_disp = joes_scanner_auth_format_stream_server_display( $server_url, $server_hosted );
                $start    = isset( $s['first_seen_utc'] ) ? (string) $s['first_seen_utc'] : '';
                $last     = isset( $s['last_seen_utc'] ) ? (string) $s['last_seen_utc'] : '';
                $dur_sec  = isset( $s['duration_seconds'] ) ? (int) $s['duration_seconds'] : 0;

                echo '<tr>';
                list( $label_disp, $label_trunc ) = joes_scanner_auth_truncate_table_text( $label, 50 );
                echo '<td class="jsauth-wrap" title="' . esc_attr( $label ) . '">' . esc_html( $label_disp ) . '</td>';
                echo '<td title="' . esc_attr( $session_token ) . '">' . esc_html( $session_short ) . '</td>';
                echo '<td title="' . esc_attr( $device_hash ) . '">' . esc_html( $device_short ) . '</td>';
                list( $ip_disp, $ip_title, $ip_cls ) = joes_scanner_auth_format_ip_for_table( $ip );
            if ( $ip_disp !== '' ) {
                echo '<td class="jsauth-ip-cell ' . esc_attr( $ip_cls ) . '" title="' . esc_attr( $ip_title ) . '"><span class="jsauth-ellipsis">' . esc_html( $ip_disp ) . '</span></td>';
            } else {
                echo '<td></td>';
            }
                echo '<td>' . esc_html( $plat ) . '</td>';
                echo '<td>' . esc_html( $type ) . '</td>';
                list( $model_disp, $model_trunc ) = joes_scanner_auth_truncate_table_text( $model, 25 );
                if ( $model_disp !== '' ) {
                    echo '<td title="' . esc_attr( $model ) . '"><span class="jsauth-ellipsis">' . esc_html( $model_disp ) . '</span></td>';
                } else {
                    echo '<td></td>';
                }
                echo '<td>' . esc_html( $app ) . '</td>';
                if ( ! empty( $server_disp[0] ) ) {
                    list( $srv_disp, $srv_trunc ) = joes_scanner_auth_truncate_table_text( $server_disp[0], 50 );
                echo '<td title="' . esc_attr( $server_disp[1] ) . '"><span class="jsauth-ellipsis">' . esc_html( $srv_disp ) . '</span></td>';
                } else {
                    echo '<td></td>';
                }
                echo '<td>' . esc_html( joes_scanner_auth_format_datetime_local( $start ) ) . '</td>';
                echo '<td>' . esc_html( joes_scanner_auth_format_datetime_local( $last ) ) . '</td>';
                echo '<td>' . esc_html( joes_scanner_auth_format_duration( $dur_sec ) ) . '</td>';
                echo '</tr>';
            }
        }

        echo '</tbody></table>';
    }
}

function joes_scanner_auth_render_logs_tab() {
    global $wpdb;

    $events_table    = joes_scanner_auth_table_events();
    $app_events_tbl  = joes_scanner_auth_table_app_events();

    $tz_label = joes_scanner_auth_get_site_timezone_label();

    $limit = isset( $_GET['logs_limit'] ) ? (int) $_GET['logs_limit'] : 500;
    $limit = max( 50, min( 2000, $limit ) );

    $auth_limit = (int) ceil( $limit / 2 );
    $app_limit  = $limit - $auth_limit;

    $auth_rows = $wpdb->get_results(
        $wpdb->prepare(
            "SELECT created_at_utc, result, http_status, message, username, email, ip, device_platform, device_type, device_model, app_version, device_id_hash, session_token
             FROM {$events_table}
             ORDER BY created_at_utc DESC
             LIMIT %d",
            $auth_limit
        ),
        ARRAY_A
    );

    $app_rows = $wpdb->get_results(
        $wpdb->prepare(
            "SELECT created_at_utc, event_type, payload_json, username, email, ip, device_platform, device_type, device_model, app_version, device_id_hash, session_token
             FROM {$app_events_tbl}
             ORDER BY created_at_utc DESC
             LIMIT %d",
            $app_limit
        ),
        ARRAY_A
    );

    $lines = array();

    foreach ( (array) $auth_rows as $r ) {
        $ts_local = joes_scanner_auth_format_datetime_local( (string) ( $r['created_at_utc'] ?? '' ) );
        $acct = '';
        $u = isset( $r['username'] ) ? trim( (string) $r['username'] ) : '';
        $e = isset( $r['email'] ) ? trim( (string) $r['email'] ) : '';
        if ( $u !== '' && $e !== '' && strcasecmp( $u, $e ) === 0 ) {
            $acct = $e;
        } elseif ( $u !== '' && $e !== '' ) {
            $acct = $u . ' ' . $e;
        } elseif ( $e !== '' ) {
            $acct = $e;
        } elseif ( $u !== '' ) {
            $acct = $u;
        } else {
            $acct = 'Unregistered';
        }

        $msg = isset( $r['message'] ) ? (string) $r['message'] : '';
        $status = isset( $r['http_status'] ) ? (int) $r['http_status'] : 0;

        $parts = array();
        $parts[] = '[' . $ts_local . ']';
        $parts[] = '[auth]';
        $parts[] = '[' . (string) ( $r['result'] ?? '' ) . ']';
        $parts[] = 'account=' . $acct;
        if ( ! empty( $r['ip'] ) ) { $parts[] = 'ip=' . (string) $r['ip']; }
        if ( ! empty( $r['device_platform'] ) ) { $parts[] = 'platform=' . (string) $r['device_platform']; }
        if ( ! empty( $r['device_type'] ) ) { $parts[] = 'type=' . (string) $r['device_type']; }
        if ( ! empty( $r['device_model'] ) ) { $parts[] = 'model=' . (string) $r['device_model']; }
        if ( ! empty( $r['app_version'] ) ) { $parts[] = 'app=' . (string) $r['app_version']; }
        if ( ! empty( $r['device_id_hash'] ) ) { $parts[] = 'device=' . joes_scanner_auth_format_device_hash_short( (string) $r['device_id_hash'] ); }
        if ( ! empty( $r['session_token'] ) ) { $parts[] = 'session=' . substr( (string) $r['session_token'], 0, 8 ); }
        if ( $status > 0 ) { $parts[] = 'status=' . (string) $status; }
        if ( $msg !== '' ) { $parts[] = 'msg=' . $msg; }

        $lines[] = array(
            'ts'   => (string) ( $r['created_at_utc'] ?? '' ),
            'line' => implode( ' ', $parts ),
        );
    }

    foreach ( (array) $app_rows as $r ) {
        $ts_local = joes_scanner_auth_format_datetime_local( (string) ( $r['created_at_utc'] ?? '' ) );
        $acct = '';
        $u = isset( $r['username'] ) ? trim( (string) $r['username'] ) : '';
        $e = isset( $r['email'] ) ? trim( (string) $r['email'] ) : '';
        if ( $u !== '' && $e !== '' && strcasecmp( $u, $e ) === 0 ) {
            $acct = $e;
        } elseif ( $u !== '' && $e !== '' ) {
            $acct = $u . ' ' . $e;
        } elseif ( $e !== '' ) {
            $acct = $e;
        } elseif ( $u !== '' ) {
            $acct = $u;
        } else {
            $acct = 'Unregistered';
        }

        $payload = isset( $r['payload_json'] ) ? (string) $r['payload_json'] : '';
        if ( strlen( $payload ) > 2000 ) {
            $payload = substr( $payload, 0, 2000 ) . '...';
        }

        $parts = array();
        $parts[] = '[' . $ts_local . ']';
        $parts[] = '[app]';
        $parts[] = '[' . (string) ( $r['event_type'] ?? '' ) . ']';
        $parts[] = 'account=' . $acct;
        if ( ! empty( $r['ip'] ) ) { $parts[] = 'ip=' . (string) $r['ip']; }
        if ( ! empty( $r['device_platform'] ) ) { $parts[] = 'platform=' . (string) $r['device_platform']; }
        if ( ! empty( $r['device_type'] ) ) { $parts[] = 'type=' . (string) $r['device_type']; }
        if ( ! empty( $r['device_model'] ) ) { $parts[] = 'model=' . (string) $r['device_model']; }
        if ( ! empty( $r['app_version'] ) ) { $parts[] = 'app=' . (string) $r['app_version']; }
        if ( ! empty( $r['device_id_hash'] ) ) { $parts[] = 'device=' . joes_scanner_auth_format_device_hash_short( (string) $r['device_id_hash'] ); }
        if ( ! empty( $r['session_token'] ) ) { $parts[] = 'session=' . substr( (string) $r['session_token'], 0, 8 ); }
        if ( $payload !== '' ) { $parts[] = 'payload=' . $payload; }

        $lines[] = array(
            'ts'   => (string) ( $r['created_at_utc'] ?? '' ),
            'line' => implode( ' ', $parts ),
        );
    }

    usort(
        $lines,
        function ( $a, $b ) {
            $ta = isset( $a['ts'] ) ? strtotime( $a['ts'] . ' UTC' ) : 0;
            $tb = isset( $b['ts'] ) ? strtotime( $b['ts'] . ' UTC' ) : 0;
            if ( $ta === $tb ) { return 0; }
            return ( $ta > $tb ) ? -1 : 1;
        }
    );

    if ( count( $lines ) > $limit ) {
        $lines = array_slice( $lines, 0, $limit );
    }

    $text = '';
    foreach ( $lines as $row ) {
        $text .= (string) ( $row['line'] ?? '' ) . "\n";
    }

    $base_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=logs' );

    echo '<h2 style="margin-top: 18px;">Logs</h2>';
    echo '<p class="description">Showing timestamps in site timezone: ' . esc_html( $tz_label ) . '</p>';

    echo '<form method="get" style="margin: 10px 0 12px; display:flex; gap: 10px; align-items:end; flex-wrap: wrap;">';
    echo '<input type="hidden" name="page" value="joes-scanner-auth" />';
    echo '<input type="hidden" name="tab" value="logs" />';
    echo '<label style="display:flex; flex-direction:column; gap: 4px;">';
    echo '<span>Rows</span>';
    echo '<input type="number" name="logs_limit" min="50" max="2000" value="' . esc_attr( (string) $limit ) . '" />';
    echo '</label>';
    echo '<button class="button button-secondary" type="submit">Refresh</button>';
    echo '<button class="button button-secondary" type="button" id="jsauth-logs-selectall">Select all</button>';
    echo '</form>';

    echo '<textarea id="jsauth-logs-box" style="width:100%; max-width: 100%; height: 520px; font-family: Consolas, Monaco, monospace; font-size: 12px; line-height: 1.35;" spellcheck="false" wrap="off" readonly>' . esc_textarea( $text ) . '</textarea>';

    echo '<script>(function(){var b=document.getElementById("jsauth-logs-box");var btn=document.getElementById("jsauth-logs-selectall");if(!b||!btn){return;}btn.addEventListener("click",function(){b.focus();b.select();});})();</script>';
}

function joes_scanner_auth_render_lookups_tab() {
    if ( ! current_user_can( 'manage_options' ) ) {
        echo '<p>You do not have permission to view this page.</p>';
        return;
    }

    joes_scanner_auth_observed_triples_migrate_v1_to_v2();

    $servers_lookups = joes_scanner_auth_lookups_get_server_keys();
    $servers_observed = joes_scanner_auth_observed_triples_v2_get_server_keys();

    // Merge by server_key, prefer newest timestamp.
    $servers_map = array();
    foreach ( (array) $servers_lookups as $s ) {
        $k = isset( $s['server_key'] ) ? (string) $s['server_key'] : '';
        if ( $k === '' ) {
            continue;
        }
        $servers_map[ $k ] = array(
            'server_key'     => $k,
            'updated_at_utc'  => isset( $s['updated_at_utc'] ) ? (string) $s['updated_at_utc'] : '',
            'newest_seen_utc' => '',
        );
    }
    foreach ( (array) $servers_observed as $s ) {
        $k = isset( $s['server_key'] ) ? (string) $s['server_key'] : '';
        if ( $k === '' ) {
            continue;
        }
        if ( ! isset( $servers_map[ $k ] ) ) {
            $servers_map[ $k ] = array(
                'server_key'     => $k,
                'updated_at_utc'  => '',
                'newest_seen_utc' => isset( $s['newest_last_seen_utc'] ) ? (string) $s['newest_last_seen_utc'] : '',
            );
        } else {
            $servers_map[ $k ]['newest_seen_utc'] = isset( $s['newest_last_seen_utc'] ) ? (string) $s['newest_last_seen_utc'] : '';
        }
    }
    $servers = array_values( $servers_map );

    usort(
        $servers,
        function ( $a, $b ) {
            $au = isset( $a['updated_at_utc'] ) ? (string) $a['updated_at_utc'] : '';
            $bu = isset( $b['updated_at_utc'] ) ? (string) $b['updated_at_utc'] : '';
            $as = isset( $a['newest_seen_utc'] ) ? (string) $a['newest_seen_utc'] : '';
            $bs = isset( $b['newest_seen_utc'] ) ? (string) $b['newest_seen_utc'] : '';
            $at = $as ? $as : $au;
            $bt = $bs ? $bs : $bu;
            return strcmp( $bt, $at );
        }
    );
    $selected_server = isset( $_GET['server_key'] ) ? sanitize_text_field( (string) $_GET['server_key'] ) : '';
    $selected_server = joes_scanner_auth_lookups_normalize_server_key( $selected_server );

    if ( $selected_server === '' && ! empty( $servers ) && isset( $servers[0]['server_key'] ) ) {
        $selected_server = (string) $servers[0]['server_key'];
    }

    echo '<div class="jsauth-card">';
    echo '<h2>Server Lookups Cache</h2>';
    echo '<p class="description">This cache is used to seed new installations with receiver, site, and talkgroup lists for a server. The app can refresh and report updates when telemetry is enabled.</p>';

    echo '<form method="get" action="' . esc_url( admin_url( 'options-general.php' ) ) . '" style="margin: 10px 0 18px 0;">';
    echo '<input type="hidden" name="page" value="joes-scanner-auth" />';
    echo '<input type="hidden" name="tab" value="lookups" />';

    // Keep the View button to the right of the dropdown, while allowing the select to be wide and responsive.
    echo '<div style="display:flex; align-items:flex-end; gap: 10px; max-width: 980px;">';
    echo '<div style="flex: 1 1 auto; min-width: 520px;">';
    echo '<strong>Server</strong><br />';
    echo '<select name="server_key" style="width: 100%; max-width: 980px;">';

    if ( empty( $servers ) ) {
        echo '<option value="">No cached servers yet</option>';
    } else {
        foreach ( (array) $servers as $s ) {
            $key = isset( $s['server_key'] ) ? (string) $s['server_key'] : '';
            if ( $key === '' ) {
                continue;
            }
            $sel = ( $key === $selected_server ) ? 'selected' : '';
            $label = $key;
            $last_sync_utc = function_exists( 'joes_scanner_auth_lookups_get_last_report_utc_for_server' ) ? joes_scanner_auth_lookups_get_last_report_utc_for_server( $key ) : null;
            if ( $last_sync_utc ) {
                $label .= ' (last sync ' . joes_scanner_auth_format_datetime_local( (string) $last_sync_utc ) . ')';
            } elseif ( isset( $s['updated_at_utc'] ) && $s['updated_at_utc'] ) {
                $label .= ' (lookups updated ' . joes_scanner_auth_format_datetime_local( (string) $s['updated_at_utc'] ) . ')';
            } elseif ( isset( $s['newest_seen_utc'] ) && $s['newest_seen_utc'] ) {
                $label .= ' (observed ' . joes_scanner_auth_format_datetime_local( (string) $s['newest_seen_utc'] ) . ')';
            }
            echo '<option value="' . esc_attr( $key ) . '" ' . $sel . '>' . esc_html( $label ) . '</option>';
        }
    }

    echo '</select>';
    echo '</div>';
    echo '<div style="flex: 0 0 auto;">';
    submit_button( 'View', 'secondary', '', false );
    echo '</div>';
    echo '</div>';
    echo '</form>';

    if ( $selected_server === '' ) {
        echo '<p>No server selected.</p>';
        echo '</div>';
        return;
    }

    $row = joes_scanner_auth_lookups_get_cache_row( $selected_server );

    $by_email = ( $row && isset( $row['updated_by_email'] ) ) ? (string) $row['updated_by_email'] : '';
    $by_device = ( $row && isset( $row['updated_by_device_id_hash'] ) ) ? (string) $row['updated_by_device_id_hash'] : '';
    $updated = ( $row && isset( $row['updated_at_utc'] ) ) ? (string) $row['updated_at_utc'] : '';
    $last_sync_utc = function_exists( 'joes_scanner_auth_lookups_get_last_report_utc_for_server' ) ? joes_scanner_auth_lookups_get_last_report_utc_for_server( $selected_server ) : null;

    echo '<div style="margin: 10px 0 14px 0; padding: 10px 12px; background: #fff; border: 1px solid #dcdcde; border-radius: 8px;">';
    echo '<div><strong>Server key:</strong> ' . esc_html( $selected_server ) . '</div>';
    echo '<div><strong>Last sync received:</strong> ' . esc_html( $last_sync_utc ? joes_scanner_auth_format_datetime_local( (string) $last_sync_utc ) : 'Unknown' ) . '</div>';
    echo '<div><strong>Lookups payload updated:</strong> ' . esc_html( $updated ? joes_scanner_auth_format_datetime_local( $updated ) : 'Unknown' ) . '</div>';
    echo '<div><strong>Populated by:</strong> ';
    if ( $by_email !== '' ) {
        echo esc_html( $by_email ) . ' ';
    }
    if ( $by_device !== '' ) {
        $dev_short = joes_scanner_auth_format_device_hash_short( $by_device );
        echo '<span title="' . esc_attr( $by_device ) . '"><span class="jsauth-ellipsis">' . esc_html( $dev_short ) . '</span></span>';
    } else {
        echo esc_html( 'Unknown device' );
    }
    echo '</div>';
    echo '</div>';

    $admin_nonce = wp_create_nonce( 'joes_scanner_auth_admin_actions' );

    $render_list = function ( $title, $section, $json ) use ( $selected_server, $admin_nonce ) {
        $items = array();
        if ( is_string( $json ) && trim( $json ) !== '' ) {
            $decoded = json_decode( $json, true );
            if ( is_array( $decoded ) ) {
                $items = $decoded;
            }
        }

        echo '<div class="jsauth-card" style="margin-top: 14px;">';
        echo '<div style="display:flex; align-items:center; justify-content:space-between; gap: 10px;">';
        echo '<h3 style="margin: 0;">' . esc_html( $title ) . ' <span class="description">(' . esc_html( (string) count( $items ) ) . ')</span></h3>';

        echo '<form method="post" style="margin:0;">';
        echo '<input type="hidden" name="_wpnonce" value="' . esc_attr( $admin_nonce ) . '" />';
        echo '<input type="hidden" name="joes_scanner_auth_action" value="lookups_clear" />';
        echo '<input type="hidden" name="server_key" value="' . esc_attr( $selected_server ) . '" />';
        echo '<input type="hidden" name="section" value="' . esc_attr( $section ) . '" />';
        submit_button( 'Clear ' . $title, 'delete', 'submit', false );
        echo '</form>';

        echo '</div>';

        if ( empty( $items ) ) {
            echo '<p class="description">No items cached.</p>';
            echo '</div>';
            return;
        }

        // Receivers and sites tend to be arrays of { id, name }.
        // Talkgroups tend to be arrays of { talkgroup, alpha_tag, description, group, ... }.
        echo '<table class="widefat striped jsauth-table" style="margin-top: 10px;">';
        echo '<thead><tr>';
        echo '<th style="width:120px;">Id</th>';
        echo '<th>Name</th>';
        echo '<th style="width:180px;">Group</th>';
        echo '</tr></thead>';
        echo '<tbody>';

        $max = 2000;
        $count = 0;
        foreach ( (array) $items as $it ) {
            if ( $count >= $max ) {
                break;
            }
            if ( ! is_array( $it ) ) {
                continue;
            }

            $id = '';
            if ( isset( $it['id'] ) ) {
                $id = (string) $it['id'];
            } elseif ( isset( $it['receiverId'] ) ) {
                $id = (string) $it['receiverId'];
            } elseif ( isset( $it['siteId'] ) ) {
                $id = (string) $it['siteId'];
            } elseif ( isset( $it['talkgroup'] ) ) {
                $id = (string) $it['talkgroup'];
            } elseif ( isset( $it['tg'] ) ) {
                $id = (string) $it['tg'];
            }

            $name = '';
            if ( isset( $it['name'] ) ) {
                $name = (string) $it['name'];
            } elseif ( isset( $it['alphaTag'] ) ) {
                $name = (string) $it['alphaTag'];
            } elseif ( isset( $it['alpha_tag'] ) ) {
                $name = (string) $it['alpha_tag'];
            } elseif ( isset( $it['label'] ) ) {
                $name = (string) $it['label'];
            } elseif ( isset( $it['description'] ) ) {
                $name = (string) $it['description'];
            }

            $group = '';
            if ( isset( $it['group'] ) ) {
                $group = (string) $it['group'];
            } elseif ( isset( $it['category'] ) ) {
                $group = (string) $it['category'];
            }

            echo '<tr>';
            echo '<td>' . esc_html( $id ) . '</td>';
            echo '<td>' . esc_html( $name ) . '</td>';
            echo '<td>' . esc_html( $group ) . '</td>';
            echo '</tr>';

            $count++;
        }

        echo '</tbody>';
        echo '</table>';

        if ( count( $items ) > $max ) {
            echo '<p class="description">Showing first ' . esc_html( (string) $max ) . ' items.</p>';
        }

        echo '</div>';
    };

    // Observed receiver, site, talkgroup triples (authoritative association data) - shown first.
    $triples = joes_scanner_auth_observed_triples_v2_get_rows( $selected_server, 5000 );
    if ( ! is_array( $triples ) ) {
        $triples = array();
    }
    $triples_count = count( $triples );

    echo '<details open style="margin-top: 18px;">';
    echo '<summary style="cursor:pointer;"><strong>Observed Receiver, Site, Talkgroup</strong> <span class="description">(' . esc_html( (string) $triples_count ) . ')</span></summary>';
    echo '<div class="jsauth-card" style="margin-top: 10px;">';
    echo '<p class="description">These rows are built from the canonical v2 discovery sync reported by apps. This is the authoritative Receiver to Site to Talkgroup hierarchy used for filter seeding and sync.</p>';

    echo '<div style="display:flex; align-items:center; justify-content:space-between; gap: 10px;">';
    echo '<div><strong>Rows:</strong> ' . esc_html( (string) $triples_count ) . '</div>';
    echo '<form method="post" style="margin:0;">';
    echo '<input type="hidden" name="_wpnonce" value="' . esc_attr( $admin_nonce ) . '" />';
    echo '<input type="hidden" name="joes_scanner_auth_action" value="observed_triples_clear" />';
    echo '<input type="hidden" name="server_key" value="' . esc_attr( $selected_server ) . '" />';
    submit_button( 'Clear discovery rows for this server', 'delete', 'submit', false );
    echo '</form>';
    echo '</div>';

    if ( empty( $triples ) ) {
        echo '<p class="description">No observed rows yet for this server.</p>';
    } else {
        echo '<table class="widefat striped jsauth-table" style="margin-top: 10px;">';
    echo '<thead><tr>';
    echo '<th style="min-width: 220px;">Receiver</th>';
    echo '<th style="min-width: 220px;">Site</th>';
    echo '<th style="min-width: 260px;">Talkgroup</th>';
    echo '<th style="min-width: 110px;">Count</th>';
    echo '<th style="min-width: 180px;">Last seen</th>';
    echo '<th style="min-width: 260px;">Source</th>';
    echo '</tr></thead><tbody>';

    foreach ( $triples as $t ) {
        $rx = isset( $t['receiver_label'] ) ? (string) $t['receiver_label'] : '';
        $sx = isset( $t['site_label'] ) ? (string) $t['site_label'] : '';
        $tg = isset( $t['talkgroup_label'] ) ? (string) $t['talkgroup_label'] : '';
        $cnt = isset( $t['seen_count'] ) ? (string) $t['seen_count'] : '0';
        $last_seen_utc = isset( $t['last_seen_utc'] ) ? (string) $t['last_seen_utc'] : '';
        $last_seen = $last_seen_utc !== '' ? joes_scanner_auth_format_datetime_local( $last_seen_utc ) : '';

        $src_email = isset( $t['last_source_email'] ) ? (string) $t['last_source_email'] : '';
        $src_dev_full = isset( $t['last_source_device_hash'] ) ? (string) $t['last_source_device_hash'] : '';
        $src_dev_short = $src_dev_full !== '' ? joes_scanner_auth_format_device_hash_short( $src_dev_full ) : '';
        $src_full = '';
        if ( $src_email !== '' && $src_dev_full !== '' ) {
            $src_full = $src_email . ' device=' . $src_dev_full;
        } elseif ( $src_email !== '' ) {
            $src_full = $src_email;
        } elseif ( $src_dev_full !== '' ) {
            $src_full = $src_dev_full;
        }
        $src_disp = '';
        if ( $src_email !== '' && $src_dev_short !== '' ) {
            $src_disp = $src_email . ' device=' . $src_dev_short;
        } elseif ( $src_email !== '' ) {
            $src_disp = $src_email;
        } elseif ( $src_dev_short !== '' ) {
            $src_disp = $src_dev_short;
        }

        echo '<tr>';
        echo '<td class="jsauth-wrap">' . esc_html( $rx ) . '</td>';
        echo '<td class="jsauth-wrap">' . esc_html( $sx ) . '</td>';
        echo '<td class="jsauth-wrap">' . esc_html( $tg ) . '</td>';
        echo '<td>' . esc_html( $cnt ) . '</td>';
        echo '<td>' . esc_html( $last_seen ) . '</td>';
        if ( $src_disp !== '' ) {
            echo '<td title="' . esc_attr( $src_full ) . '"><span class="jsauth-ellipsis">' . esc_html( $src_disp ) . '</span></td>';
        } else {
            echo '<td class="jsauth-wrap">' . esc_html( 'Unknown' ) . '</td>';
        }
        echo '</tr>';
    }

        echo '</tbody></table>';
        echo '<p class="description">Sorted alphabetically by Receiver, then Site, then Talkgroup.</p>';
    }

    echo '</div>';
    echo '</details>';

    // Raw lookup catalogs (supporting data) - shown second.
        $lookup_receivers_count = 0;
    $lookup_sites_count = 0;
    $lookup_talkgroups_count = 0;

    if ( $row ) {
        $tmp = isset( $row['receivers_json'] ) ? $row['receivers_json'] : '';
        $dec = is_string( $tmp ) && trim( $tmp ) !== '' ? json_decode( $tmp, true ) : null;
        $lookup_receivers_count = is_array( $dec ) ? count( $dec ) : 0;

        $tmp = isset( $row['sites_json'] ) ? $row['sites_json'] : '';
        $dec = is_string( $tmp ) && trim( $tmp ) !== '' ? json_decode( $tmp, true ) : null;
        $lookup_sites_count = is_array( $dec ) ? count( $dec ) : 0;

        $tmp = isset( $row['talkgroups_json'] ) ? $row['talkgroups_json'] : '';
        $dec = is_string( $tmp ) && trim( $tmp ) !== '' ? json_decode( $tmp, true ) : null;
        $lookup_talkgroups_count = is_array( $dec ) ? count( $dec ) : 0;
    }

echo '<details style="margin-top: 12px;">';
    echo '<summary style="cursor:pointer;"><strong>Lookup catalogs</strong> <span class="description">(Receivers ' . esc_html( (string) $lookup_receivers_count ) . ', Sites ' . esc_html( (string) $lookup_sites_count ) . ', Talkgroups ' . esc_html( (string) $lookup_talkgroups_count ) . ')</span></summary>';
    echo '<div class="jsauth-card" style="margin-top: 10px;">';
    echo '<p class="description">These are the raw lookup lists cached per server. They are useful as supporting metadata, but they may not encode Receiver to Site to Talkgroup associations.</p>';
    if ( ! $row ) {
        echo '<p class="description">No cached lookup catalogs found for this server yet.</p>';
    } else {
        $render_list( 'Receivers', 'receivers', isset( $row['receivers_json'] ) ? $row['receivers_json'] : null );
        $render_list( 'Sites', 'sites', isset( $row['sites_json'] ) ? $row['sites_json'] : null );
        $render_list( 'Talkgroups', 'talkgroups', isset( $row['talkgroups_json'] ) ? $row['talkgroups_json'] : null );

        echo '<div style="margin-top: 14px;">';
        echo '<form method="post">';
        echo '<input type="hidden" name="_wpnonce" value="' . esc_attr( $admin_nonce ) . '" />';
        echo '<input type="hidden" name="joes_scanner_auth_action" value="lookups_clear" />';
        echo '<input type="hidden" name="server_key" value="' . esc_attr( $selected_server ) . '" />';
        echo '<input type="hidden" name="section" value="all" />';
        submit_button( 'Clear all lookups for this server', 'delete' );
        echo '</form>';
        echo '</div>';
    }
    echo '</div>';
    echo '</details>';

    echo '</div>';
}

function joes_scanner_auth_render_history_tab() {
    global $wpdb;

    $events_table   = joes_scanner_auth_table_events();
    $sessions_table = joes_scanner_auth_table_sessions();
    $devices_table  = joes_scanner_auth_table_devices();

    $since_30d = gmdate( 'Y-m-d H:i:s', time() - ( 30 * DAY_IN_SECONDS ) );

    $total_sessions = (int) $wpdb->get_var( "SELECT COUNT(*) FROM {$sessions_table}" );

    $avg_session_seconds_all = (int) $wpdb->get_var(
        "SELECT COALESCE(AVG(TIMESTAMPDIFF(SECOND, first_seen_utc, last_seen_utc)), 0) FROM {$sessions_table}"
    );

    $total_session_seconds_30d = (int) $wpdb->get_var(
        $wpdb->prepare(
            "SELECT COALESCE(SUM(TIMESTAMPDIFF(SECOND, first_seen_utc, last_seen_utc)), 0)
             FROM {$sessions_table}
             WHERE last_seen_utc >= %s",
            $since_30d
        )
    );

    $auth_total_30d = (int) $wpdb->get_var(
        $wpdb->prepare(
            "SELECT COUNT(*) FROM {$events_table} WHERE created_at_utc >= %s",
            $since_30d
        )
    );

    $auth_ok_30d = (int) $wpdb->get_var(
        $wpdb->prepare(
            "SELECT COUNT(*) FROM {$events_table} WHERE created_at_utc >= %s AND result = 'ok'",
            $since_30d
        )
    );

    $ok_rate_30d = ( $auth_total_30d > 0 ) ? ( ( $auth_ok_30d / $auth_total_30d ) * 100.0 ) : 0.0;

    $tz_label = joes_scanner_auth_get_site_timezone_label();

    echo '<h2 style="margin-top: 18px;">History</h2>';

    echo '<style>
        .jsauth-device-toggle { display: inline-block; }
        .jsauth-device-details-row td { padding: 2px 4px !important; }
        .jsauth-device-id-line { display: flex; gap: 6px; align-items: baseline; flex-wrap: wrap; margin: 4px 0 6px; }
        .jsauth-device-id-line code { word-break: break-all; }
    </style>';

    echo '<p class="description">Showing timestamps in site timezone: ' . esc_html( $tz_label ) . '</p>';

    echo '<table class="widefat fixed striped" style="max-width: 980px;"><tbody>';
    echo '<tr><td style="width: 320px;"><strong>Total sessions stored</strong></td><td>' . esc_html( $total_sessions ) . '</td></tr>';
    echo '<tr><td><strong>Average session length (all time)</strong></td><td>' . esc_html( joes_scanner_auth_format_duration( $avg_session_seconds_all ) ) . '</td></tr>';
    echo '<tr><td><strong>Total session time (last 30d)</strong></td><td>' . esc_html( joes_scanner_auth_format_duration( $total_session_seconds_30d ) ) . '</td></tr>';
    echo '<tr><td><strong>Successful auths (last 30d)</strong></td><td>' . esc_html( $auth_ok_30d ) . ' (' . esc_html( number_format_i18n( $ok_rate_30d, 1 ) ) . '% ok rate)</td></tr>';
    echo '</tbody></table>';



    echo '<h3 style="margin-top: 22px;">Accounts</h3>';
    echo '<p class="description">Accounts are sorted by newest device last seen. Expand an account to load devices, device totals, and individual sessions.</p>';

    $page_size  = 50;
    $page_param = 'history_page';
    $page       = isset( $_GET[ $page_param ] ) ? max( 1, (int) $_GET[ $page_param ] ) : 1;

    $acct_total = (int) $wpdb->get_var( "SELECT COUNT(*) FROM (SELECT COALESCE(user_id, 0) AS user_key FROM {$sessions_table} UNION SELECT COALESCE(user_id, 0) AS user_key FROM {$devices_table}) u" );
    $pages = max( 1, (int) ceil( $acct_total / $page_size ) );
    if ( $page > $pages ) {
        $page = $pages;
    }
    $offset = ( $page - 1 ) * $page_size;

    // Account list is based on sessions so it includes every account that has connected.
    // Device counts are pulled from the devices table when available, otherwise derived from sessions.
    $accounts = $wpdb->get_results(
        $wpdb->prepare(
            "SELECT
                k.user_key,
                COALESCE(s.user_id, d.user_id) AS user_id,
                COALESCE(NULLIF(s.username, ''), d.username) AS username,
                COALESCE(NULLIF(s.email, ''), d.email) AS email,
                COALESCE(d.device_count, s.session_device_count, 0) AS device_count,
                COALESCE(s.session_count, 0) AS session_count,
                COALESCE(s.total_seconds, 0) AS total_seconds,
                GREATEST(
                    COALESCE(d.newest_device_last_seen, '0000-00-00 00:00:00'),
                    COALESCE(s.newest_session_last_seen, '0000-00-00 00:00:00')
                ) AS newest_last_seen
             FROM (
                SELECT COALESCE(user_id, 0) AS user_key FROM {$sessions_table}
                UNION
                SELECT COALESCE(user_id, 0) AS user_key FROM {$devices_table}
             ) k
             LEFT JOIN (
                SELECT
                    COALESCE(user_id, 0) AS user_key,
                    MAX(user_id) AS user_id,
                    MAX(username) AS username,
                    MAX(email) AS email,
                    COUNT(DISTINCT device_id_hash) AS session_device_count,
                    COUNT(*) AS session_count,
                    SUM(TIMESTAMPDIFF(SECOND, first_seen_utc, last_seen_utc)) AS total_seconds,
                    MAX(last_seen_utc) AS newest_session_last_seen
                FROM {$sessions_table}
                GROUP BY COALESCE(user_id, 0)
             ) s ON s.user_key = k.user_key
             LEFT JOIN (
                SELECT
                    COALESCE(user_id, 0) AS user_key,
                    MAX(user_id) AS user_id,
                    MAX(username) AS username,
                    MAX(email) AS email,
                    COUNT(*) AS device_count,
                    MAX(last_seen_utc) AS newest_device_last_seen
                FROM {$devices_table}
                GROUP BY COALESCE(user_id, 0)
             ) d ON d.user_key = k.user_key
             ORDER BY newest_last_seen DESC
             LIMIT %d OFFSET %d",
            $page_size,
            $offset
        )
    );

    $base_url = admin_url( 'options-general.php?page=joes-scanner-auth&tab=history' );

    joes_scanner_auth_render_pager( $base_url, $page_param, $page, $pages, array() );

echo '<table class="widefat striped jsauth-table jsauth-history-accounts">';
    echo '<thead><tr>';
    echo '<th class="jsauth-col-account">Account</th>';
    echo '<th class="jsauth-col-num">Devices</th>';
    echo '<th class="jsauth-col-num">Sessions</th>';
    echo '<th class="jsauth-col-dur">Total time</th>';
    echo '<th class="jsauth-col-dt">Last seen</th>';
    echo '</tr></thead><tbody>';

    if ( empty( $accounts ) ) {
        echo '<tr><td colspan="5">No accounts found.</td></tr>';
    } else {
        foreach ( $accounts as $acct ) {
            $user_key = (int) $acct->user_key;

            $username = isset( $acct->username ) ? trim( (string) $acct->username ) : '';
            $email = isset( $acct->email ) ? trim( (string) $acct->email ) : '';

            // Avoid redundant account labels (for many installs the username is the email).
            if ( $username !== '' && $email !== '' && strcasecmp( $username, $email ) === 0 ) {
                $label = $email;
            } else if ( $username !== '' && $email !== '' ) {
                $label = $username . ' ' . $email;
            } else if ( $email !== '' ) {
                $label = $email;
            } else if ( $username !== '' ) {
                $label = $username;
            } else {
                $label = 'Unknown';
            }

            $acct_last_seen = ! empty( $acct->newest_last_seen ) ? joes_scanner_auth_format_datetime_local( $acct->newest_last_seen ) : '';
            $acct_total_time = joes_scanner_auth_format_duration( (int) $acct->total_seconds );

            $row_id = 'jsauth-acct-' . substr( md5( 'acct:' . (string) $user_key ), 0, 12 );
            echo '<tr class="jsauth-acct-summary-row">';
            echo '<td class="jsauth-col-account jsauth-wrap">';
            echo '<button type="button" class="button-link jsauth-expand" data-target="' . esc_attr( $row_id ) . '" data-userkey="' . esc_attr( $user_key ) . '" aria-expanded="false" aria-controls="' . esc_attr( $row_id ) . '">';
            echo '<span class="jsauth-summary-main">' . esc_html( $label ) . '</span>';
            echo '</button>';
            echo '</td>';
            echo '<td class="jsauth-col-num">' . esc_html(number_format_i18n((int) $acct->device_count)) . '</td>';
            echo '<td class="jsauth-col-num">' . esc_html(number_format_i18n((int) $acct->session_count)) . '</td>';
            echo '<td class="jsauth-col-time">' . esc_html( $acct_total_time ) . '</td>';
            echo '<td class="jsauth-col-dt">' . esc_html( $acct_last_seen ) . '</td>';
            echo '</tr>';
            echo '<tr id="' . esc_attr( $row_id ) . '" class="jsauth-acct-details-row" style="display:none;">';
            echo '<td colspan="5" class="jsauth-wrap">';
            echo '<div class="jsauth-history-body" data-loaded="0" data-userkey="' . esc_attr( $user_key ) . '"><em>Loading...</em></div>';
            echo '</td>';
            echo '</tr>';
        }
    }

    echo '</tbody></table>';

        joes_scanner_auth_render_pager( $base_url, $page_param, $page, $pages, array() );
}
