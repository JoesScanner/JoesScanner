<?php
/*
//|--------------------------------------------------------------------------//| Options//|--------------------------------------------------------------------------*/

function joes_scanner_auth_normalize_version_string( $version ) {
    $version = trim( (string) $version );
    if ( $version === '' ) {
        return '';
    }

    $version = str_replace( array( '+', '-', '_' ), '.', $version );
    $version = preg_replace( '/[^0-9.]/', '', $version );
    $version = preg_replace( '/\.{2,}/', '.', $version );
    $version = trim( (string) $version, '.' );

    if ( $version === '' ) {
        return '';
    }

    $parts = explode( '.', $version );
    $clean = array();
    foreach ( $parts as $part ) {
        if ( $part === '' ) {
            continue;
        }
        $clean[] = (string) intval( $part, 10 );
    }

    return implode( '.', $clean );
}

function joes_scanner_auth_compare_versions( $left, $right ) {
    $left  = joes_scanner_auth_normalize_version_string( $left );
    $right = joes_scanner_auth_normalize_version_string( $right );

    if ( $left === $right ) {
        return 0;
    }

    $left_parts  = $left === '' ? array() : explode( '.', $left );
    $right_parts = $right === '' ? array() : explode( '.', $right );
    $max = max( count( $left_parts ), count( $right_parts ) );

    for ( $i = 0; $i < $max; $i++ ) {
        $l = isset( $left_parts[ $i ] ) ? (int) $left_parts[ $i ] : 0;
        $r = isset( $right_parts[ $i ] ) ? (int) $right_parts[ $i ] : 0;
        if ( $l < $r ) {
            return -1;
        }
        if ( $l > $r ) {
            return 1;
        }
    }

    return 0;
}

function joes_scanner_auth_check_minimum_app_version( $app_version ) {
    $opts = joes_scanner_auth_get_options();
    $minimum = isset( $opts['minimum_app_version'] ) ? joes_scanner_auth_normalize_version_string( (string) $opts['minimum_app_version'] ) : '';
    $current = joes_scanner_auth_normalize_version_string( $app_version );

    if ( $minimum === '' ) {
        return array(
            'ok'                  => true,
            'minimum_app_version' => '',
            'app_version'         => $current,
        );
    }

    if ( $current === '' ) {
        return array(
            'ok'                  => false,
            'error'               => 'app_version_required',
            'message'             => 'This version of Joe\'s Scanner is no longer supported. Please update the app to at least version ' . $minimum . '.',
            'minimum_app_version' => $minimum,
            'app_version'         => '',
        );
    }

    if ( joes_scanner_auth_compare_versions( $current, $minimum ) < 0 ) {
        return array(
            'ok'                  => false,
            'error'               => 'app_version_too_old',
            'message'             => 'This version of Joe\'s Scanner is no longer supported. Please update the app to at least version ' . $minimum . '.',
            'minimum_app_version' => $minimum,
            'app_version'         => $current,
        );
    }

    return array(
        'ok'                  => true,
        'minimum_app_version' => $minimum,
        'app_version'         => $current,
    );
}

/**
 * Normalize the SureCart subscription statuses accepted by the plugin.
 *
 * Checkbox submissions arrive as an array, while internal option updates and
 * older plugin versions may provide a comma-separated string. Supporting both
 * forms prevents an internal SureCart sync from collapsing a multi-status
 * selection back to only "active".
 */
function joes_scanner_auth_normalize_allowed_statuses_value( $raw, $fallback_to_active = true ) {
    $allowed = function_exists( 'joes_scanner_auth_get_supported_subscription_statuses' )
        ? array_keys( joes_scanner_auth_get_supported_subscription_statuses() )
        : array( 'active', 'trialing', 'past_due', 'incomplete', 'unpaid', 'canceled', 'completed' );

    $values = is_array( $raw ) ? $raw : array( $raw );
    $parts  = array();

    foreach ( $values as $value ) {
        if ( ! is_scalar( $value ) ) {
            continue;
        }

        // A full option update can contain the stored CSV representation.
        foreach ( preg_split( '/\s*,\s*/', strtolower( trim( (string) $value ) ) ) as $status_key ) {
            $status_key = function_exists( 'sanitize_text_field' )
                ? sanitize_text_field( $status_key )
                : preg_replace( '/[^a-z0-9_\-]/', '', $status_key );

            if ( $status_key !== '' && in_array( $status_key, $allowed, true ) ) {
                $parts[] = $status_key;
            }
        }
    }

    $parts = array_values( array_unique( $parts ) );

    if ( empty( $parts ) && $fallback_to_active ) {
        $parts = array( 'active' );
    }

    return $parts;
}

/**
 * Retrieve plugin options with sane defaults and legacy normalization.
 */
function joes_scanner_auth_get_options() {
    $stored = get_option( JOES_SCANNER_AUTH_OPTIONS_KEY, array() );

    if ( ! is_array( $stored ) ) {
        $stored = array();
    }

    if ( ! isset( $stored['allowed_price_ids'] ) || ! is_array( $stored['allowed_price_ids'] ) ) {
        $stored['allowed_price_ids'] = array();
    }

    if ( ! isset( $stored['price_levels'] ) || ! is_array( $stored['price_levels'] ) ) {
        $stored['price_levels'] = array();
    }

    if ( ! isset( $stored['subscription_item_levels'] ) || ! is_array( $stored['subscription_item_levels'] ) ) {
        $stored['subscription_item_levels'] = array();
    }

    if ( ! isset( $stored['surecart_known_subscription_items'] ) || ! is_array( $stored['surecart_known_subscription_items'] ) ) {
        $stored['surecart_known_subscription_items'] = array();
    }

    if ( ! isset( $stored['surecart_subscription_price_map'] ) || ! is_array( $stored['surecart_subscription_price_map'] ) ) {
        $stored['surecart_subscription_price_map'] = array();
    }

    if ( ! isset( $stored['surecart_subscription_product_map'] ) || ! is_array( $stored['surecart_subscription_product_map'] ) ) {
        $stored['surecart_subscription_product_map'] = array();
    }

    $stored['allowed_statuses'] = implode(
        ',',
        joes_scanner_auth_normalize_allowed_statuses_value(
            isset( $stored['allowed_statuses'] ) ? $stored['allowed_statuses'] : array(),
            true
        )
    );

    if ( ! isset( $stored['surecart_enabled'] ) ) {
        $stored['surecart_enabled'] = 1;
    }

    $stored['surecart_enabled'] = ( (int) $stored['surecart_enabled'] === 1 ) ? 1 : 0;

    if ( ! isset( $stored['surecart_auto_sync_prices'] ) ) {
        $stored['surecart_auto_sync_prices'] = 1;
    }


    if ( ! isset( $stored['surecart_known_price_ids'] ) || ! is_array( $stored['surecart_known_price_ids'] ) ) {
        $stored['surecart_known_price_ids'] = array();
    }

    if ( ! isset( $stored['surecart_last_detected_utc'] ) || ! is_string( $stored['surecart_last_detected_utc'] ) ) {
        $stored['surecart_last_detected_utc'] = '';
    }


    // Normalize price levels (price ID -> integer level).
    $clean_levels = array();
    foreach ( (array) $stored['price_levels'] as $k => $v ) {
        $k = trim( (string) $k );
        if ( $k === '' ) {
            continue;
        }
        $lvl = is_numeric( $v ) ? (int) $v : 0;
        $clean_levels[ $k ] = $lvl;
    }
    $stored['price_levels'] = $clean_levels;

    $clean_item_levels = array();
    foreach ( (array) $stored['subscription_item_levels'] as $k => $v ) {
        $k = trim( (string) $k );
        if ( $k === '' ) {
            continue;
        }
        $clean_item_levels[ $k ] = is_numeric( $v ) ? max( 0, min( 99, (int) $v ) ) : 0;
    }
    $stored['subscription_item_levels'] = $clean_item_levels;

    $clean_known_items = array();
    foreach ( (array) $stored['surecart_known_subscription_items'] as $item_id ) {
        $item_id = trim( (string) $item_id );
        if ( $item_id !== '' ) {
            $clean_known_items[] = $item_id;
        }
    }
    $stored['surecart_known_subscription_items'] = array_values( array_unique( $clean_known_items ) );

    $clean_price_map = array();
    foreach ( (array) $stored['surecart_subscription_price_map'] as $item_id => $price_ids ) {
        $item_id = trim( (string) $item_id );
        if ( $item_id === '' ) {
            continue;
        }
        $bucket = array();
        foreach ( (array) $price_ids as $price_id ) {
            $price_id = trim( (string) $price_id );
            if ( $price_id !== '' ) {
                $bucket[] = $price_id;
            }
        }
        $bucket = array_values( array_unique( $bucket ) );
        sort( $bucket );
        $clean_price_map[ $item_id ] = ! empty( $bucket ) ? $bucket : array( $item_id );
    }
    $stored['surecart_subscription_price_map'] = $clean_price_map;

    $clean_product_map = array();
    foreach ( (array) $stored['surecart_subscription_product_map'] as $item_id => $product_id ) {
        $item_id    = trim( (string) $item_id );
        $product_id = trim( (string) $product_id );
        if ( $item_id !== '' && $product_id !== '' ) {
            $clean_product_map[ $item_id ] = $product_id;
        }
    }
    $stored['surecart_subscription_product_map'] = $clean_product_map;

    if ( empty( $stored['subscription_item_levels'] ) && ! empty( $stored['price_levels'] ) && ! empty( $stored['surecart_subscription_price_map'] ) ) {
        foreach ( (array) $stored['surecart_subscription_price_map'] as $item_id => $price_ids ) {
            $best_level = 0;
            foreach ( (array) $price_ids as $price_id ) {
                $price_id = trim( (string) $price_id );
                if ( $price_id !== '' && isset( $stored['price_levels'][ $price_id ] ) ) {
                    $best_level = max( $best_level, (int) $stored['price_levels'][ $price_id ] );
                }
            }
            if ( $best_level > 0 ) {
                $stored['subscription_item_levels'][ $item_id ] = $best_level;
            }
        }
    }

    // If legacy allowed_price_ids exist but no explicit level has been set, treat allowed prices as level 1 (standard).
    if ( empty( $stored['price_levels'] ) && isset( $stored['allowed_price_ids'] ) && is_array( $stored['allowed_price_ids'] ) ) {
        foreach ( (array) $stored['allowed_price_ids'] as $legacy_id ) {
            $legacy_id = trim( (string) $legacy_id );
            if ( $legacy_id === '' ) {
                continue;
            }
            $stored['price_levels'][ $legacy_id ] = 1;
        }
    }




    // Connection tracking defaults.
    if ( ! isset( $stored['active_window_minutes'] ) || ! is_numeric( $stored['active_window_minutes'] ) ) {
        $stored['active_window_minutes'] = 5;
    }

    if ( ! isset( $stored['event_retention_days'] ) || ! is_numeric( $stored['event_retention_days'] ) ) {
        $stored['event_retention_days'] = 90;
    }

    if ( ! isset( $stored['session_retention_days'] ) || ! is_numeric( $stored['session_retention_days'] ) ) {
        $stored['session_retention_days'] = 30;
    }

    if ( ! isset( $stored['comms_retention_days'] ) || ! is_numeric( $stored['comms_retention_days'] ) ) {
        $stored['comms_retention_days'] = 90;
    }

    if ( ! isset( $stored['minimum_app_version'] ) || ! is_string( $stored['minimum_app_version'] ) ) {
        $stored['minimum_app_version'] = '';
    }


    // Automatic backups.
    // Legacy fields: backup_frequency, backup_custom_minutes.
    if ( ! isset( $stored['backup_frequency'] ) || ! is_string( $stored['backup_frequency'] ) ) {
        // off | hourly | every6hours | every12hours | daily | weekly | custom
        $stored['backup_frequency'] = 'daily';
    }

    if ( ! isset( $stored['backup_keep_count'] ) || ! is_numeric( $stored['backup_keep_count'] ) ) {
        $stored['backup_keep_count'] = 14;
    }

    if ( ! isset( $stored['backup_custom_minutes'] ) || ! is_numeric( $stored['backup_custom_minutes'] ) ) {
        // Used when backup_frequency is set to custom. Value is in minutes.
        $stored['backup_custom_minutes'] = 1440;
    }

    // New recurrence fields used by the single picker UI.
    if ( ! isset( $stored['backup_recur_enabled'] ) ) {
        $stored['backup_recur_enabled'] = 0;
    }
    $stored['backup_recur_enabled'] = ( (int) $stored['backup_recur_enabled'] === 1 ) ? 1 : 0;

    if ( ! isset( $stored['backup_recur_every'] ) || ! is_numeric( $stored['backup_recur_every'] ) ) {
        $stored['backup_recur_every'] = 1;
    }

    if ( ! isset( $stored['backup_recur_unit'] ) || ! is_string( $stored['backup_recur_unit'] ) ) {
        $stored['backup_recur_unit'] = 'day';
    }

    if ( ! isset( $stored['backup_recur_weekdays'] ) || ! is_array( $stored['backup_recur_weekdays'] ) ) {
        $stored['backup_recur_weekdays'] = array();
    }

    if ( ! isset( $stored['backup_recur_end_type'] ) || ! is_string( $stored['backup_recur_end_type'] ) ) {
        $stored['backup_recur_end_type'] = 'never';
    }

    if ( ! isset( $stored['backup_recur_end_on'] ) || ! is_string( $stored['backup_recur_end_on'] ) ) {
        $stored['backup_recur_end_on'] = '';
    }

    if ( ! isset( $stored['backup_recur_end_after'] ) || ! is_numeric( $stored['backup_recur_end_after'] ) ) {
        $stored['backup_recur_end_after'] = 0;
    }

    if ( ! isset( $stored['backup_recur_anchor_local'] ) || ! is_string( $stored['backup_recur_anchor_local'] ) ) {
        // Local anchor datetime string, used for weekly weekday schedules.
        $stored['backup_recur_anchor_local'] = '';
    }

    if ( ! isset( $stored['backup_recur_occurrences'] ) || ! is_numeric( $stored['backup_recur_occurrences'] ) ) {
        $stored['backup_recur_occurrences'] = 0;
    }

    if ( ! isset( $stored['backup_recur_next_run_ts'] ) || ! is_numeric( $stored['backup_recur_next_run_ts'] ) ) {
        $stored['backup_recur_next_run_ts'] = 0;
    }



    $stored['active_window_minutes']   = max( 1, min( 120, (int) $stored['active_window_minutes'] ) );
    $stored['event_retention_days']    = max( 1, min( 3650, (int) $stored['event_retention_days'] ) );
    $stored['session_retention_days']  = max( 1, min( 3650, (int) $stored['session_retention_days'] ) );
    $stored['comms_retention_days']    = max( 0, min( 3650, (int) $stored['comms_retention_days'] ) );
    $stored['minimum_app_version']     = function_exists( 'joes_scanner_auth_normalize_version_string' )
        ? joes_scanner_auth_normalize_version_string( (string) $stored['minimum_app_version'] )
        : preg_replace( '/[^0-9.]/', '', (string) $stored['minimum_app_version'] );


    $allowed_backup = array( 'off', 'hourly', 'every6hours', 'every12hours', 'daily', 'weekly', 'custom' );
    $stored['backup_frequency'] = strtolower( trim( (string) $stored['backup_frequency'] ) );
    if ( ! in_array( $stored['backup_frequency'], $allowed_backup, true ) ) {
        $stored['backup_frequency'] = 'daily';
    }

    $stored['backup_keep_count']    = max( 1, min( 200, (int) $stored['backup_keep_count'] ) );
    $stored['backup_custom_minutes'] = max( 1, min( 525600, (int) $stored['backup_custom_minutes'] ) ); // 1 min .. 365 days

    // Normalize new recurrence fields.
    $stored['backup_recur_every'] = max( 1, min( 525600, (int) $stored['backup_recur_every'] ) );
    $stored['backup_recur_unit']  = strtolower( trim( (string) $stored['backup_recur_unit'] ) );
    $allowed_units = array( 'minute', 'hour', 'day', 'week', 'month' );
    if ( ! in_array( $stored['backup_recur_unit'], $allowed_units, true ) ) {
        $stored['backup_recur_unit'] = 'day';
    }

    $stored['backup_recur_end_type'] = strtolower( trim( (string) $stored['backup_recur_end_type'] ) );
    $allowed_end = array( 'never', 'on', 'after' );
    if ( ! in_array( $stored['backup_recur_end_type'], $allowed_end, true ) ) {
        $stored['backup_recur_end_type'] = 'never';
    }

    $stored['backup_recur_end_on'] = trim( (string) $stored['backup_recur_end_on'] );
    if ( $stored['backup_recur_end_on'] !== '' && ! preg_match( '/^\d{4}-\d{2}-\d{2}$/', $stored['backup_recur_end_on'] ) ) {
        $stored['backup_recur_end_on'] = '';
    }

    $stored['backup_recur_end_after'] = max( 0, min( 1000000, (int) $stored['backup_recur_end_after'] ) );

    $wd = array();
    foreach ( (array) $stored['backup_recur_weekdays'] as $w ) {
        $w = (int) $w;
        if ( $w >= 0 && $w <= 6 ) {
            $wd[] = $w;
        }
    }
    $wd = array_values( array_unique( $wd ) );
    sort( $wd );
    $stored['backup_recur_weekdays'] = $wd;

    $stored['backup_recur_anchor_local'] = trim( (string) $stored['backup_recur_anchor_local'] );
    $stored['backup_recur_occurrences']  = max( 0, min( 1000000, (int) $stored['backup_recur_occurrences'] ) );
    $stored['backup_recur_next_run_ts']  = max( 0, (int) $stored['backup_recur_next_run_ts'] );

    // If the new recurrence UI has never been configured, map legacy backup_frequency into the new fields.
    if ( $stored['backup_recur_anchor_local'] === '' && $stored['backup_recur_next_run_ts'] === 0 && $stored['backup_recur_enabled'] === 0 ) {
        $legacy = $stored['backup_frequency'];
        if ( $legacy === 'off' || $legacy === '' ) {
            $stored['backup_recur_enabled'] = 0;
        } else {
            $stored['backup_recur_enabled'] = 1;
            if ( $legacy === 'hourly' ) {
                $stored['backup_recur_every'] = 1;
                $stored['backup_recur_unit']  = 'hour';
            } elseif ( $legacy === 'every6hours' ) {
                $stored['backup_recur_every'] = 6;
                $stored['backup_recur_unit']  = 'hour';
            } elseif ( $legacy === 'every12hours' ) {
                $stored['backup_recur_every'] = 12;
                $stored['backup_recur_unit']  = 'hour';
            } elseif ( $legacy === 'weekly' ) {
                $stored['backup_recur_every'] = 1;
                $stored['backup_recur_unit']  = 'week';
                // Default to the current day until user picks days.
                $stored['backup_recur_weekdays'] = array( (int) gmdate( 'w' ) );
            } elseif ( $legacy === 'custom' ) {
                $stored['backup_recur_every'] = max( 1, (int) $stored['backup_custom_minutes'] );
                $stored['backup_recur_unit']  = 'minute';
            } else {
                // daily default
                $stored['backup_recur_every'] = 1;
                $stored['backup_recur_unit']  = 'day';
            }
        }
    }

    // Active tab display defaults.
    if ( ! isset( $stored['active_tab_inactive_sessions'] ) || ! is_numeric( $stored['active_tab_inactive_sessions'] ) ) {
        $stored['active_tab_inactive_sessions'] = 25;
    }

    $stored['active_tab_inactive_sessions'] = max( 0, min( 500, (int) $stored['active_tab_inactive_sessions'] ) );



    if ( ! isset( $stored['active_tab_refresh_seconds'] ) || ! is_numeric( $stored['active_tab_refresh_seconds'] ) ) {
        $stored['active_tab_refresh_seconds'] = 0;
    }

    $stored['active_tab_refresh_seconds'] = max( 0, min( 600, (int) $stored['active_tab_refresh_seconds'] ) );
return $stored;
}

function joes_scanner_auth_is_surecart_enabled() {
    $opts = joes_scanner_auth_get_options();
    return isset( $opts['surecart_enabled'] ) && (int) $opts['surecart_enabled'] === 1;
}

function joes_scanner_auth_field_surecart_auto_sync_prices() {
    $opts = joes_scanner_auth_get_options();
    $val  = isset( $opts['surecart_auto_sync_prices'] ) && (int) $opts['surecart_auto_sync_prices'] === 1 ? 1 : 0;

    echo '<label>';
    echo '<input type="hidden" name="joes_scanner_auth_options[surecart_auto_sync_prices]" value="0" />';
    echo '<input type="checkbox" name="joes_scanner_auth_options[surecart_auto_sync_prices]" value="1" ' . checked( 1, $val, false ) . ' /> ';
    echo 'Automatically keep Allowed SureCart prices in sync with all recurring subscription prices found in SureCart.';
    echo '</label>';
    echo '<p class="description">When enabled, newly added recurring prices will be allowed automatically without needing to manually check them here.</p>';
}



/**
 * Returns mapping of subscription item IDs to integer subscription levels.
 * Level 0 = no access, Level 1 = standard, Level 2 = premium.
 */
function joes_scanner_auth_get_subscription_item_levels() {
    $opts = joes_scanner_auth_get_options();
    $levels = isset( $opts['subscription_item_levels'] ) && is_array( $opts['subscription_item_levels'] ) ? $opts['subscription_item_levels'] : array();

    $clean = array();
    foreach ( (array) $levels as $k => $v ) {
        $k = trim( (string) $k );
        if ( $k === '' ) {
            continue;
        }
        $clean[ $k ] = max( 0, min( 99, is_numeric( $v ) ? (int) $v : 0 ) );
    }

    return $clean;
}

/**
 * Backward-compatible derived price map based on the selected subscription items.
 */
function joes_scanner_auth_get_price_levels() {
    $levels = joes_scanner_auth_get_subscription_item_levels();
    $opts   = joes_scanner_auth_get_options();
    $map    = isset( $opts['surecart_subscription_price_map'] ) && is_array( $opts['surecart_subscription_price_map'] ) ? $opts['surecart_subscription_price_map'] : array();

    $derived = array();
    foreach ( $levels as $item_id => $lvl ) {
        foreach ( (array) ( isset( $map[ $item_id ] ) ? $map[ $item_id ] : array() ) as $price_id ) {
            $price_id = trim( (string) $price_id );
            if ( $price_id !== '' ) {
                $derived[ $price_id ] = max( 0, min( 99, (int) $lvl ) );
            }
        }
    }

    return $derived;
}

/**
 * Returns an array of allowed subscription item IDs.
 */
function joes_scanner_auth_get_allowed_subscription_item_ids() {
    $levels = joes_scanner_auth_get_subscription_item_levels();
    $allowed = array();
    foreach ( $levels as $item_id => $lvl ) {
        if ( (int) $lvl > 0 ) {
            $allowed[] = (string) $item_id;
        }
    }
    $allowed = array_values( array_unique( $allowed ) );
    sort( $allowed );
    return $allowed;
}

/**
 * Returns an array of allowed SureCart price IDs derived from the selected subscription items.
 * This is kept for backward compatibility with older code paths and debug output.
 */
function joes_scanner_auth_get_allowed_price_ids() {
    $levels = joes_scanner_auth_get_price_levels();

    $allowed = array();
    foreach ( $levels as $id => $lvl ) {
        if ( (int) $lvl > 0 ) {
            $allowed[] = (string) $id;
        }
    }

    $allowed = array_values( array_unique( $allowed ) );
    sort( $allowed );
    return $allowed;
}



function joes_scanner_auth_value_to_array_deep( $value ) {
    if ( is_array( $value ) ) {
        return $value;
    }

    if ( is_object( $value ) ) {
        $json = wp_json_encode( $value );
        if ( is_string( $json ) && $json != '' ) {
            $decoded = json_decode( $json, true );
            if ( is_array( $decoded ) ) {
                return $decoded;
            }
        }
    }

    return array();
}

function joes_scanner_auth_collect_possible_product_refs( $value, $depth = 0 ) {
    $refs = array();
    if ( $depth > 4 ) {
        return $refs;
    }

    if ( is_scalar( $value ) || is_null( $value ) ) {
        $token = trim( (string) $value );
        if ( $token !== '' ) {
            $refs[] = $token;
        }
        return array_values( array_unique( $refs ) );
    }

    $arr = joes_scanner_auth_value_to_array_deep( $value );
    if ( empty( $arr ) ) {
        return $refs;
    }

    foreach ( array( 'id', 'product_id', 'productId', 'sc_id', 'surecart_id', 'post_id', 'wp_id' ) as $key ) {
        if ( isset( $arr[ $key ] ) && ( is_scalar( $arr[ $key ] ) || is_null( $arr[ $key ] ) ) ) {
            $token = trim( (string) $arr[ $key ] );
            if ( $token !== '' ) {
                $refs[] = $token;
            }
        }
    }

    foreach ( array( 'product', 'product_data', 'product_model', 'raw_product_sample', 'post' ) as $nested_key ) {
        if ( isset( $arr[ $nested_key ] ) ) {
            $refs = array_merge( $refs, joes_scanner_auth_collect_possible_product_refs( $arr[ $nested_key ], $depth + 1 ) );
        }
    }

    return array_values( array_unique( $refs ) );
}

function joes_scanner_auth_get_surecart_product_ref_to_post_id_map() {
    static $map = null;
    if ( is_array( $map ) ) {
        return $map;
    }

    $map = array();
    if ( ! function_exists( 'sc_get_product' ) || ! class_exists( 'WP_Query' ) ) {
        return $map;
    }

    $query = new WP_Query(
        array(
            'post_type'      => 'sc_product',
            'post_status'    => 'publish',
            'posts_per_page' => 500,
            'no_found_rows'  => true,
            'fields'         => 'ids',
        )
    );

    foreach ( (array) $query->posts as $post_id ) {
        $post_id = (int) $post_id;
        if ( $post_id <= 0 ) {
            continue;
        }

        $map[ (string) $post_id ] = $post_id;

        try {
            $product_data = sc_get_product( $post_id );
        } catch ( \Throwable $e ) {
            $product_data = null;
        }

        foreach ( joes_scanner_auth_collect_possible_product_refs( $product_data ) as $ref ) {
            $ref = trim( (string) $ref );
            if ( $ref !== '' ) {
                $map[ $ref ] = $post_id;
            }
        }
    }

    return $map;
}

function joes_scanner_auth_get_surecart_price_product_post_id( $price_id ) {
    $price_id = trim( (string) $price_id );
    if ( $price_id === '' ) {
        return 0;
    }

    $detected_items = joes_scanner_auth_get_detected_subscription_items();
    foreach ( (array) $detected_items as $item ) {
        if ( empty( $item['post_id'] ) || empty( $item['price_ids'] ) || ! is_array( $item['price_ids'] ) ) {
            continue;
        }
        foreach ( (array) $item['price_ids'] as $known_price_id ) {
            if ( trim( (string) $known_price_id ) === $price_id ) {
                return (int) $item['post_id'];
            }
        }
    }

    $price_obj = null;
    if ( class_exists( '\SureCart\Models\Price' ) ) {
        try {
            if ( method_exists( '\SureCart\Models\Price', 'find' ) ) {
                $price_obj = \SureCart\Models\Price::find( $price_id );
            }
        } catch ( \Throwable $e ) {
        }
        if ( ! $price_obj ) {
            try {
                if ( method_exists( '\SureCart\Models\Price', 'where' ) ) {
                    $price_obj = \SureCart\Models\Price::where( array( 'id' => $price_id ) )->first();
                }
            } catch ( \Throwable $e ) {
            }
        }
    }

    $product_candidates = array();
    foreach ( joes_scanner_auth_collect_possible_product_refs( $price_obj ) as $ref ) {
        $ref = trim( (string) $ref );
        if ( $ref !== '' && $ref !== $price_id ) {
            $product_candidates[] = $ref;
        }
    }

    $product_candidates = array_values( array_unique( $product_candidates ) );
    $product_map        = joes_scanner_auth_get_surecart_product_ref_to_post_id_map();

    foreach ( $product_candidates as $candidate ) {
        if ( isset( $product_map[ $candidate ] ) ) {
            return (int) $product_map[ $candidate ];
        }
        if ( is_numeric( $candidate ) && isset( $product_map[ (string) (int) $candidate ] ) ) {
            return (int) $product_map[ (string) (int) $candidate ];
        }
    }

    return 0;
}

/**
 * Resolve the stable SureCart product ID associated with a price, including
 * historical price versions retained by existing subscriptions.
 */
function joes_scanner_auth_get_surecart_price_product_id( $price_id ) {
    static $cache = array();

    $price_id = trim( (string) $price_id );
    if ( $price_id === '' ) {
        return '';
    }

    if ( array_key_exists( $price_id, $cache ) ) {
        return $cache[ $price_id ];
    }

    $cache[ $price_id ] = '';

    if ( ! class_exists( '\SureCart\Models\Price' ) ) {
        return '';
    }

    $price = null;
    try {
        if ( method_exists( '\SureCart\Models\Price', 'find' ) ) {
            $price = \SureCart\Models\Price::find( $price_id );
        }
    } catch ( \Throwable $e ) {
        $price = null;
    }

    if ( ! $price || is_wp_error( $price ) ) {
        try {
            if ( method_exists( '\SureCart\Models\Price', 'where' ) ) {
                $query = \SureCart\Models\Price::where( array( 'ids[]' => array( $price_id ), 'limit' => 1 ) );
                $price = is_object( $query ) && method_exists( $query, 'first' ) ? $query->first() : null;
            }
        } catch ( \Throwable $e ) {
            $price = null;
        }
    }

    $product = null;
    if ( is_object( $price ) && isset( $price->product ) ) {
        $product = $price->product;
    } elseif ( is_array( $price ) && isset( $price['product'] ) ) {
        $product = $price['product'];
    }

    if ( is_scalar( $product ) ) {
        $cache[ $price_id ] = trim( (string) $product );
    } elseif ( is_object( $product ) && isset( $product->id ) ) {
        $cache[ $price_id ] = trim( (string) $product->id );
    } elseif ( is_array( $product ) && isset( $product['id'] ) ) {
        $cache[ $price_id ] = trim( (string) $product['id'] );
    }

    return $cache[ $price_id ];
}

function joes_scanner_auth_resolve_subscription_item_id_for_price_id( $price_id ) {
    $price_id = trim( (string) $price_id );
    if ( $price_id === '' ) {
        return '';
    }

    $opts = joes_scanner_auth_get_options();
    $map  = isset( $opts['surecart_subscription_price_map'] ) && is_array( $opts['surecart_subscription_price_map'] ) ? $opts['surecart_subscription_price_map'] : array();

    // Exact mapping is always authoritative.
    foreach ( (array) $map as $item_id => $price_ids ) {
        foreach ( (array) $price_ids as $mapped_price_id ) {
            if ( trim( (string) $mapped_price_id ) === $price_id ) {
                return (string) $item_id;
            }
        }
    }

    $levels = joes_scanner_auth_get_subscription_item_levels();
    if ( array_key_exists( $price_id, $levels ) ) {
        return $price_id;
    }

    // SureCart version-controls price IDs. Existing subscriptions can retain
    // an older price ID after the current product price changes. Match both
    // prices through their stable product ID.
    $price_product_id = joes_scanner_auth_get_surecart_price_product_id( $price_id );
    if ( $price_product_id !== '' ) {
        $product_map = isset( $opts['surecart_subscription_product_map'] ) && is_array( $opts['surecart_subscription_product_map'] )
            ? $opts['surecart_subscription_product_map']
            : array();

        $best_item_id = '';
        $best_level   = -1;

        foreach ( (array) $product_map as $item_id => $product_id ) {
            if ( trim( (string) $product_id ) !== $price_product_id ) {
                continue;
            }

            $item_level = array_key_exists( $item_id, $levels ) ? (int) $levels[ $item_id ] : 0;
            if ( $item_level > $best_level ) {
                $best_item_id = (string) $item_id;
                $best_level   = $item_level;
            }
        }

        if ( $best_item_id !== '' ) {
            return $best_item_id;
        }
    }

    // Compatibility fallback for configurations saved before product IDs were
    // stored directly.
    $post_id = joes_scanner_auth_get_surecart_price_product_post_id( $price_id );
    if ( $post_id > 0 ) {
        $detected_items = joes_scanner_auth_get_detected_subscription_items();
        $best_item_id   = '';
        $best_level     = -1;

        foreach ( (array) $detected_items as $item ) {
            $item_id = isset( $item['id'] ) ? trim( (string) $item['id'] ) : '';
            if ( $item_id === '' || (int) ( isset( $item['post_id'] ) ? $item['post_id'] : 0 ) !== $post_id ) {
                continue;
            }

            $item_level = array_key_exists( $item_id, $levels ) ? (int) $levels[ $item_id ] : 0;
            if ( $item_level > $best_level ) {
                $best_item_id = $item_id;
                $best_level   = $item_level;
            }
        }

        if ( $best_item_id !== '' ) {
            return $best_item_id;
        }
    }

    return '';
}

function joes_scanner_auth_get_tier_level_for_subscription_item_id( $item_id ) {
    $item_id = trim( (string) $item_id );
    if ( $item_id === '' ) {
        return 0;
    }

    $levels = joes_scanner_auth_get_subscription_item_levels();
    return isset( $levels[ $item_id ] ) ? (int) $levels[ $item_id ] : 0;
}

/**
 * Lookup subscription level for a given SureCart price id.
 * Returns 0..2.
 */
function joes_scanner_auth_get_tier_level_for_price_id( $price_id ) {
    $item_id = joes_scanner_auth_resolve_subscription_item_id_for_price_id( $price_id );
    if ( $item_id !== '' ) {
        return joes_scanner_auth_get_tier_level_for_subscription_item_id( $item_id );
    }

    return 0;
}

// Lookup tier level for a given SureCart price id.



/**
 * Normalize a SureCart price identifier so that minor formatting differences do not break matching.
 * - Trims whitespace
 * - If value contains '/', uses the last path segment
 * - If value starts with 'price_', strips that prefix
 */
function joes_scanner_auth_normalize_price_token( $value ) {
    $value = trim( (string) $value );
    if ( $value === '' ) {
        return '';
    }

    if ( strpos( $value, '/' ) !== false ) {
        $parts = explode( '/', $value );
        $tail  = trim( (string) end( $parts ) );
        if ( $tail !== '' ) {
            $value = $tail;
        }
    }

    if ( stripos( $value, 'price_' ) === 0 ) {
        $value = substr( $value, 6 );
    }

    return trim( (string) $value );
}


function joes_scanner_auth_get_detected_subscription_price_ids() {
    $prices = joes_scanner_auth_get_all_subscription_prices();
    $ids = array();
    foreach ( (array) $prices as $p ) {
        if ( is_array( $p ) && isset( $p['id'] ) ) {
            $id = trim( (string) $p['id'] );
            if ( $id !== '' ) {
                $ids[] = $id;
            }
        }
    }
    $ids = array_values( array_unique( $ids ) );
    sort( $ids );
    return $ids;
}

function joes_scanner_auth_get_detected_subscription_items() {
    $items = function_exists( 'joes_scanner_auth_get_all_subscription_items' ) ? joes_scanner_auth_get_all_subscription_items() : array();
    $normalized = array();

    foreach ( (array) $items as $item ) {
        if ( ! is_array( $item ) || empty( $item['id'] ) ) {
            continue;
        }

        $item_id = trim( (string) $item['id'] );
        if ( $item_id === '' ) {
            continue;
        }

        $price_ids = array();
        foreach ( (array) ( isset( $item['price_ids'] ) ? $item['price_ids'] : array() ) as $price_id ) {
            $price_id = trim( (string) $price_id );
            if ( $price_id !== '' ) {
                $price_ids[] = $price_id;
            }
        }

        $price_ids = array_values( array_unique( $price_ids ) );
        sort( $price_ids );

        $normalized[] = array(
            'id'         => $item_id,
            'label'      => isset( $item['label'] ) ? (string) $item['label'] : $item_id,
            'price_ids'  => $price_ids,
            'post_id'    => isset( $item['post_id'] ) ? (int) $item['post_id'] : 0,
            'product_id' => isset( $item['product_id'] ) ? trim( (string) $item['product_id'] ) : '',
        );
    }

    usort(
        $normalized,
        function ( $a, $b ) {
            return strcasecmp( (string) $a['label'], (string) $b['label'] );
        }
    );

    return $normalized;
}

function joes_scanner_auth_sync_allowed_prices_from_surecart( $reason = '' ) {
    if ( ! joes_scanner_auth_is_surecart_enabled() ) {
        return;
    }

    $opts = joes_scanner_auth_get_options();
    $auto = isset( $opts['surecart_auto_sync_prices'] ) && (int) $opts['surecart_auto_sync_prices'] === 1;

    $detected_items = joes_scanner_auth_get_detected_subscription_items();
    if ( empty( $detected_items ) ) {
        return;
    }

    $detected_ids      = array();
    $current_map       = array();
    $product_map_after = array();

    foreach ( $detected_items as $item ) {
        $item_id = (string) $item['id'];
        $detected_ids[] = $item_id;

        $price_ids = isset( $item['price_ids'] ) && is_array( $item['price_ids'] ) ? $item['price_ids'] : array();
        if ( empty( $price_ids ) ) {
            $price_ids = array( $item_id );
        }
        $current_map[ $item_id ] = array_values( array_unique( array_map( 'strval', $price_ids ) ) );
        sort( $current_map[ $item_id ] );

        $product_id = isset( $item['product_id'] ) ? trim( (string) $item['product_id'] ) : '';
        if ( $product_id === '' ) {
            $product_id = joes_scanner_auth_get_surecart_price_product_id( $item_id );
        }
        if ( $product_id !== '' ) {
            $product_map_after[ $item_id ] = $product_id;
        }
    }

    $detected_ids = array_values( array_unique( $detected_ids ) );
    sort( $detected_ids );

    $known = isset( $opts['surecart_known_subscription_items'] ) && is_array( $opts['surecart_known_subscription_items'] ) ? $opts['surecart_known_subscription_items'] : array();
    $known = array_values( array_unique( array_map( 'strval', $known ) ) );
    sort( $known );

    $levels_before      = isset( $opts['subscription_item_levels'] ) && is_array( $opts['subscription_item_levels'] ) ? $opts['subscription_item_levels'] : array();
    $map_before         = isset( $opts['surecart_subscription_price_map'] ) && is_array( $opts['surecart_subscription_price_map'] ) ? $opts['surecart_subscription_price_map'] : array();
    $product_map_before = isset( $opts['surecart_subscription_product_map'] ) && is_array( $opts['surecart_subscription_product_map'] ) ? $opts['surecart_subscription_product_map'] : array();

    // Backfill stable product IDs for legacy configured price IDs. This lets a
    // new current price inherit the old configured access level.
    foreach ( (array) $levels_before as $old_item_id => $old_level ) {
        $old_item_id = trim( (string) $old_item_id );
        if ( $old_item_id === '' || isset( $product_map_before[ $old_item_id ] ) ) {
            continue;
        }

        $old_product_id = joes_scanner_auth_get_surecart_price_product_id( $old_item_id );
        if ( $old_product_id !== '' ) {
            $product_map_before[ $old_item_id ] = $old_product_id;
        }
    }

    $levels_after = array();
    foreach ( $detected_ids as $item_id ) {
        if ( array_key_exists( $item_id, $levels_before ) ) {
            $levels_after[ $item_id ] = max( 0, min( 99, is_numeric( $levels_before[ $item_id ] ) ? (int) $levels_before[ $item_id ] : 0 ) );
            continue;
        }

        $migrated_level = -1;
        $new_product_id = isset( $product_map_after[ $item_id ] ) ? (string) $product_map_after[ $item_id ] : '';
        if ( $new_product_id !== '' ) {
            foreach ( (array) $levels_before as $old_item_id => $old_level ) {
                $old_product_id = isset( $product_map_before[ $old_item_id ] ) ? trim( (string) $product_map_before[ $old_item_id ] ) : '';
                if ( $old_product_id !== '' && hash_equals( $new_product_id, $old_product_id ) ) {
                    $candidate_level = max( 0, min( 99, is_numeric( $old_level ) ? (int) $old_level : 0 ) );
                    $migrated_level = max( $migrated_level, $candidate_level );
                }
            }
        }

        if ( $migrated_level >= 0 ) {
            $levels_after[ $item_id ] = $migrated_level;
        } else {
            $levels_after[ $item_id ] = $auto ? 1 : 0;
        }
    }

    $new_ids = array_values( array_diff( $detected_ids, $known ) );
    sort( $new_ids );

    // Derive compatibility fields from the new in-memory maps. The prior code
    // called getters here, which reread the old option before update_option().
    $derived_price_levels = array();
    foreach ( $levels_after as $item_id => $level ) {
        foreach ( (array) ( isset( $current_map[ $item_id ] ) ? $current_map[ $item_id ] : array( $item_id ) ) as $price_id ) {
            $price_id = trim( (string) $price_id );
            if ( $price_id !== '' ) {
                $derived_price_levels[ $price_id ] = (int) $level;
            }
        }
    }

    $allowed_price_ids = array();
    foreach ( $derived_price_levels as $price_id => $level ) {
        if ( (int) $level > 0 ) {
            $allowed_price_ids[] = (string) $price_id;
        }
    }
    $allowed_price_ids = array_values( array_unique( $allowed_price_ids ) );
    sort( $allowed_price_ids );

    $need_update =
        ( $detected_ids !== $known ) ||
        ( $levels_after !== $levels_before ) ||
        ( $current_map !== $map_before ) ||
        ( $product_map_after !== $product_map_before ) ||
        ( $derived_price_levels !== ( isset( $opts['price_levels'] ) ? $opts['price_levels'] : array() ) ) ||
        ( $allowed_price_ids !== ( isset( $opts['allowed_price_ids'] ) ? $opts['allowed_price_ids'] : array() ) );

    if ( ! $need_update ) {
        return;
    }

    $opts['surecart_known_subscription_items'] = $detected_ids;
    $opts['surecart_subscription_price_map']   = $current_map;
    $opts['surecart_subscription_product_map'] = $product_map_after;
    $opts['surecart_last_detected_utc']        = gmdate( 'Y-m-d H:i:s' );
    $opts['subscription_item_levels']          = $levels_after;
    $opts['price_levels']                      = $derived_price_levels;
    $opts['allowed_price_ids']                 = $allowed_price_ids;

    update_option( JOES_SCANNER_AUTH_OPTIONS_KEY, $opts, false );

    if ( function_exists( 'joes_scanner_auth_log_admin_event' ) ) {
        joes_scanner_auth_log_admin_event(
            'surecart_prices_synced',
            array(
                'reason'         => (string) $reason,
                'detected_count' => count( $detected_ids ),
                'new_count'      => count( $new_ids ),
                'auto_sync'      => $auto ? 1 : 0,
            )
        );
    }
}

/**
 * Returns an array of allowed subscription statuses in lowercase.
 */
function joes_scanner_auth_get_allowed_statuses() {

    $opts = joes_scanner_auth_get_options();
    $raw  = isset( $opts['allowed_statuses'] ) ? $opts['allowed_statuses'] : '';

    if ( is_array( $raw ) ) {
        $parts = $raw;
    } else {
        $parts = array_filter(
            array_map(
                'trim',
                explode( ',', strtolower( (string) $raw ) )
            ),
            'strlen'
        );
    }

    $parts = array_map( 'strtolower', $parts );
    $parts = array_values( array_unique( $parts ) );

    $allowed = function_exists( 'joes_scanner_auth_get_supported_subscription_statuses' )
        ? array_keys( joes_scanner_auth_get_supported_subscription_statuses() )
        : array( 'active', 'trialing', 'past_due', 'incomplete', 'unpaid', 'canceled', 'completed' );

    $parts = array_values( array_intersect( $parts, $allowed ) );

    if ( empty( $parts ) ) {
        $parts = array( 'active' );
    }

    return $parts;
}

/**
 * Sanitize options before saving.
 */
function joes_scanner_auth_sanitize_options( $input ) {
    $output = joes_scanner_auth_get_options();

    $output['allowed_price_ids'] = array();

    // Only change status handling when this field was part of the submitted
    // form, or when a full internal option update explicitly includes it.
    // Other settings tabs share this sanitize callback and must preserve the
    // existing status selection.
    $statuses_submitted = array_key_exists( 'allowed_statuses', (array) $input )
        || ! empty( $input['allowed_statuses_present'] );

    if ( $statuses_submitted ) {
        $status_parts = joes_scanner_auth_normalize_allowed_statuses_value(
            array_key_exists( 'allowed_statuses', (array) $input ) ? $input['allowed_statuses'] : array(),
            true
        );
        $output['allowed_statuses'] = implode( ',', $status_parts );
    }

if ( isset( $input['subscription_item_levels'] ) && is_array( $input['subscription_item_levels'] ) ) {
    $levels = array();
    foreach ( $input['subscription_item_levels'] as $k => $v ) {
        $k = trim( sanitize_text_field( (string) $k ) );
        if ( $k === '' ) {
            continue;
        }
        $levels[ $k ] = is_numeric( $v ) ? max( 0, min( 99, (int) $v ) ) : 0;
    }
    $output['subscription_item_levels'] = $levels;
} else {
    $output['subscription_item_levels'] = isset( $output['subscription_item_levels'] ) && is_array( $output['subscription_item_levels'] ) ? $output['subscription_item_levels'] : array();
}

// Connection tracking.

    if ( isset( $input['active_window_minutes'] ) ) {
        $output['active_window_minutes'] = max( 1, min( 120, (int) $input['active_window_minutes'] ) );
    }

    if ( isset( $input['event_retention_days'] ) ) {
        $output['event_retention_days'] = max( 1, min( 3650, (int) $input['event_retention_days'] ) );
    }

    if ( isset( $input['session_retention_days'] ) ) {
        $output['session_retention_days'] = max( 1, min( 3650, (int) $input['session_retention_days'] ) );
    }

    if ( isset( $input['comms_retention_days'] ) ) {
        // 0 disables comms pruning.
        $output['comms_retention_days'] = max( 0, min( 3650, (int) $input['comms_retention_days'] ) );
    }

    if ( isset( $input['active_tab_inactive_sessions'] ) ) {
        $output['active_tab_inactive_sessions'] = max( 0, min( 500, (int) $input['active_tab_inactive_sessions'] ) );
    }

    if ( isset( $input['active_tab_refresh_seconds'] ) ) {
        $output['active_tab_refresh_seconds'] = max( 0, min( 600, (int) $input['active_tab_refresh_seconds'] ) );
    }

    if ( array_key_exists( 'minimum_app_version', (array) $input ) ) {
        $output['minimum_app_version'] = function_exists( 'joes_scanner_auth_normalize_version_string' )
            ? joes_scanner_auth_normalize_version_string( (string) $input['minimum_app_version'] )
            : preg_replace( '/[^0-9.]/', '', (string) $input['minimum_app_version'] );
    }

    // Automatic backups.
    // Legacy selector (still accepted for backward compatibility).
    if ( isset( $input['backup_frequency'] ) ) {
        $freq = strtolower( trim( sanitize_text_field( (string) $input['backup_frequency'] ) ) );
        $allowed_backup = array( 'off', 'hourly', 'every6hours', 'every12hours', 'daily', 'weekly', 'custom' );
        $output['backup_frequency'] = in_array( $freq, $allowed_backup, true ) ? $freq : 'daily';
    }

    if ( isset( $input['backup_keep_count'] ) ) {
        $output['backup_keep_count'] = max( 1, min( 200, (int) $input['backup_keep_count'] ) );
    }

    if ( isset( $input['backup_custom_minutes'] ) ) {
        $output['backup_custom_minutes'] = max( 1, min( 525600, (int) $input['backup_custom_minutes'] ) );
    }

    // New single picker recurrence fields.
    if ( isset( $input['backup_recur_enabled'] ) ) {
        $output['backup_recur_enabled'] = ( (int) $input['backup_recur_enabled'] === 1 ) ? 1 : 0;
    } else {
        $output['backup_recur_enabled'] = 0;
    }

    if ( isset( $input['backup_recur_every'] ) ) {
        $output['backup_recur_every'] = max( 1, min( 525600, (int) $input['backup_recur_every'] ) );
    }

    if ( isset( $input['backup_recur_unit'] ) ) {
        $unit = strtolower( trim( sanitize_text_field( (string) $input['backup_recur_unit'] ) ) );
        $allowed_units = array( 'minute', 'hour', 'day', 'week', 'month' );
        $output['backup_recur_unit'] = in_array( $unit, $allowed_units, true ) ? $unit : 'day';
    }

    if ( isset( $input['backup_recur_weekdays'] ) && is_array( $input['backup_recur_weekdays'] ) ) {
        $wd = array();
        foreach ( $input['backup_recur_weekdays'] as $w ) {
            $w = (int) $w;
            if ( $w >= 0 && $w <= 6 ) {
                $wd[] = $w;
            }
        }
        $wd = array_values( array_unique( $wd ) );
        sort( $wd );
        $output['backup_recur_weekdays'] = $wd;
    } else {
        $output['backup_recur_weekdays'] = array();
    }

    if ( isset( $input['backup_recur_end_type'] ) ) {
        $end = strtolower( trim( sanitize_text_field( (string) $input['backup_recur_end_type'] ) ) );
        $allowed_end = array( 'never', 'on', 'after' );
        $output['backup_recur_end_type'] = in_array( $end, $allowed_end, true ) ? $end : 'never';
    }

    if ( isset( $input['backup_recur_end_on'] ) ) {
        $end_on = trim( sanitize_text_field( (string) $input['backup_recur_end_on'] ) );
        if ( $end_on !== '' && preg_match( '/^\d{4}-\d{2}-\d{2}$/', $end_on ) ) {
            $output['backup_recur_end_on'] = $end_on;
        } else {
            $output['backup_recur_end_on'] = '';
        }
    }

    if ( isset( $input['backup_recur_end_after'] ) ) {
        $output['backup_recur_end_after'] = max( 0, min( 1000000, (int) $input['backup_recur_end_after'] ) );
    }
    if ( isset( $input['surecart_enabled'] ) ) {
        $output['surecart_enabled'] = ( (int) $input['surecart_enabled'] === 1 ) ? 1 : 0;
    }

    if ( isset( $input['surecart_auto_sync_prices'] ) ) {
        $output['surecart_auto_sync_prices'] = ( (int) $input['surecart_auto_sync_prices'] === 1 ) ? 1 : 0;
    }

    // Keep legacy fields in sync for older debug and compatibility paths.
    $price_map = isset( $output['surecart_subscription_price_map'] ) && is_array( $output['surecart_subscription_price_map'] ) ? $output['surecart_subscription_price_map'] : array();
    $derived_price_levels = array();
    foreach ( (array) $output['subscription_item_levels'] as $item_id => $lvl ) {
        foreach ( (array) ( isset( $price_map[ $item_id ] ) ? $price_map[ $item_id ] : array() ) as $price_id ) {
            $price_id = trim( (string) $price_id );
            if ( $price_id !== '' ) {
                $derived_price_levels[ $price_id ] = max( 0, min( 99, (int) $lvl ) );
            }
        }
    }
    $output['price_levels'] = $derived_price_levels;

    $allowed = array();
    foreach ( $derived_price_levels as $price_id => $lvl ) {
        if ( (int) $lvl > 0 ) {
            $allowed[] = (string) $price_id;
        }
    }
    $allowed = array_values( array_unique( $allowed ) );
    sort( $allowed );
    $output['allowed_price_ids'] = $allowed;

    // Note: Auto sync is handled by joes_scanner_auth_sync_allowed_prices_from_surecart().
    // Do not overwrite admin selections during option saves.

    return $output;
}
