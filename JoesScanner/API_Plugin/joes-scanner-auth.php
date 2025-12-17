<?php
/**
 * Plugin Name: Joe's Scanner Auth API
 * Description: REST endpoint and admin settings for authenticating Joe's Scanner app users against WordPress + SureCart subscriptions.
 * Version: 2.0.0
 * Author: Joe's Scanner
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

/**
 * Retrieve plugin options with sane defaults and legacy normalization.
 */
function joes_scanner_auth_get_options() {
    $stored = get_option( 'joes_scanner_auth_options', array() );

    if ( ! is_array( $stored ) ) {
        return array(
            'allowed_price_ids' => array(),
            'allowed_statuses'  => 'active,trialing,past_due',
        );
    }

    if ( ! isset( $stored['allowed_price_ids'] ) || ! is_array( $stored['allowed_price_ids'] ) ) {
        $stored['allowed_price_ids'] = array();
    }

    if ( ! isset( $stored['allowed_statuses'] ) || ! is_string( $stored['allowed_statuses'] ) ) {
        $stored['allowed_statuses'] = 'active,trialing,past_due';
    }

    return $stored;
}

/**
 * Returns an array of allowed SureCart price IDs as strings.
 */
function joes_scanner_auth_get_allowed_price_ids() {
    $opts = joes_scanner_auth_get_options();

    $ids = isset( $opts['allowed_price_ids'] ) && is_array( $opts['allowed_price_ids'] )
        ? $opts['allowed_price_ids']
        : array();

    $clean = array();
    foreach ( $ids as $id ) {
        $id = trim( (string) $id );
        if ( $id !== '' ) {
            $clean[] = $id;
        }
    }

    return array_values( array_unique( $clean ) );
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

    if ( empty( $parts ) ) {
        $parts = array( 'active', 'trialing', 'past_due' );
    }

    return $parts;
}

/**
 * Sanitize options before saving.
 */
function joes_scanner_auth_sanitize_options( $input ) {
    $output = joes_scanner_auth_get_options();

    // Allowed statuses from checkboxes.
    if ( isset( $input['allowed_statuses'] ) && is_array( $input['allowed_statuses'] ) ) {
        $statuses = array();

        foreach ( $input['allowed_statuses'] as $status ) {
            $status = strtolower( trim( sanitize_text_field( $status ) ) );
            if ( $status !== '' ) {
                $statuses[] = $status;
            }
        }

        $statuses                   = array_values( array_unique( $statuses ) );
        $output['allowed_statuses'] = implode( ',', $statuses );
    } elseif ( isset( $input['allowed_statuses'] ) && ! is_array( $input['allowed_statuses'] ) ) {
        $output['allowed_statuses'] = sanitize_text_field( $input['allowed_statuses'] );
    }

    // Allowed price IDs from checkboxes.
    if ( isset( $input['allowed_price_ids'] ) && is_array( $input['allowed_price_ids'] ) ) {
        $ids = array();
        foreach ( $input['allowed_price_ids'] as $id ) {
            $id = trim( sanitize_text_field( (string) $id ) );
            if ( $id !== '' ) {
                $ids[] = $id;
            }
        }
        $output['allowed_price_ids'] = array_values( array_unique( $ids ) );
    } else {
        $output['allowed_price_ids'] = array();
    }

    return $output;
}

/**
 * Section description callback.
 */
function joes_scanner_auth_section_main_cb() {
    echo '<p>Configure which SureCart subscription prices and statuses count as a valid scanner subscription.</p>';
}

/**
 * Fetch subscription prices from SureCart using sc_get_product + WP_Query.
 *
 * Returns an array like:
 * [
 *   [ "id" => "uuid", "label" => "Product Name - $X - every month" ],
 *   ...
 * ]
 */
function joes_scanner_auth_get_all_subscription_prices() {
    $results = array();

    if ( function_exists( 'sc_get_product' ) && class_exists( 'WP_Query' ) ) {
        $query = new WP_Query(
            array(
                'post_type'      => 'sc_product',
                'post_status'    => 'publish',
                'posts_per_page' => 200,
                'no_found_rows'  => true,
                'fields'         => 'ids',
            )
        );

        if ( $query->posts ) {
            foreach ( $query->posts as $post_id ) {
                $product_data = sc_get_product( $post_id );

                $product_name      = 'Product ' . $post_id;
                $product_recurring = false;
                $prices            = array();

                if ( is_array( $product_data ) ) {
                    if ( isset( $product_data['name'] ) ) {
                        $product_name = (string) $product_data['name'];
                    } elseif ( isset( $product_data['post']['post_title'] ) ) {
                        $product_name = (string) $product_data['post']['post_title'];
                    }

                    if ( isset( $product_data['raw_product_sample']['recurring'] ) ) {
                        $product_recurring = (bool) $product_data['raw_product_sample']['recurring'];
                    }

                    if (
                        isset( $product_data['raw_product_sample']['prices']['data'] ) &&
                        is_array( $product_data['raw_product_sample']['prices']['data'] )
                    ) {
                        $prices = $product_data['raw_product_sample']['prices']['data'];
                    } elseif (
                        isset( $product_data['active_prices'] ) &&
                        is_array( $product_data['active_prices'] )
                    ) {
                        $prices = $product_data['active_prices'];
                    }
                } elseif ( is_object( $product_data ) ) {
                    if ( isset( $product_data->name ) ) {
                        $product_name = (string) $product_data->name;
                    } elseif ( isset( $product_data->post->post_title ) ) {
                        $product_name = (string) $product_data->post->post_title;
                    }

                    if ( isset( $product_data->raw_product_sample->recurring ) ) {
                        $product_recurring = (bool) $product_data->raw_product_sample->recurring;
                    }

                    if (
                        isset( $product_data->raw_product_sample->prices->data ) &&
                        is_array( $product_data->raw_product_sample->prices->data )
                    ) {
                        $prices = $product_data->raw_product_sample->prices->data;
                    } elseif (
                        isset( $product_data->active_prices ) &&
                        is_array( $product_data->active_prices )
                    ) {
                        $prices = $product_data->active_prices;
                    }
                }

                if ( ! $prices ) {
                    continue;
                }

                foreach ( $prices as $price ) {
                    if ( is_array( $price ) ) {
                        $id                     = isset( $price['id'] ) ? (string) $price['id'] : '';
                        $pname                  = isset( $price['name'] ) ? (string) $price['name'] : '';
                        $display                = isset( $price['display_amount'] ) ? (string) $price['display_amount'] : '';
                        $billing                = isset( $price['interval_text'] ) ? (string) $price['interval_text'] : '';
                        $recurring_interval     = isset( $price['recurring_interval'] ) ? $price['recurring_interval'] : null;
                        $recurring_interval_cnt = isset( $price['recurring_interval_count'] ) ? $price['recurring_interval_count'] : null;
                        $recurring_period_cnt   = isset( $price['recurring_period_count'] ) ? $price['recurring_period_count'] : null;
                    } elseif ( is_object( $price ) ) {
                        $id                     = isset( $price->id ) ? (string) $price->id : '';
                        $pname                  = isset( $price->name ) ? (string) $price->name : '';
                        $display                = isset( $price->display_amount ) ? (string) $price->display_amount : '';
                        $billing                = isset( $price->interval_text ) ? (string) $price->interval_text : '';
                        $recurring_interval     = isset( $price->recurring_interval ) ? $price->recurring_interval : null;
                        $recurring_interval_cnt = isset( $price->recurring_interval_count ) ? $price->recurring_interval_count : null;
                        $recurring_period_cnt   = isset( $price->recurring_period_count ) ? $price->recurring_period_count : null;
                    } else {
                        continue;
                    }

                    if ( $id === '' ) {
                        continue;
                    }

                    // Treat as subscription if product is recurring OR price has recurring fields.
                    $price_recurring = (
                        ! empty( $recurring_interval ) ||
                        ! empty( $recurring_interval_cnt ) ||
                        ! empty( $recurring_period_cnt )
                    );

                    if ( ! $product_recurring && ! $price_recurring ) {
                        // Skip one-time prices.
                        continue;
                    }

                    $label_parts = array( $product_name );

                    if ( $pname !== '' && $pname !== 'price' ) {
                        $label_parts[] = $pname;
                    }

                    if ( $display !== '' ) {
                        $label_parts[] = $display;
                    }

                    if ( $billing !== '' ) {
                        $label_parts[] = $billing;
                    }

                    $results[ $id ] = array(
                        'id'    => $id,
                        'label' => implode( ' - ', $label_parts ),
                    );
                }
            }
        }

        if ( ! empty( $results ) ) {
            return array_values( $results );
        }
    }

    return array();
}

/**
 * Core helper: given a SureCart price ID, break its label into:
 *  - full:  "Joe's Scanner - Subscription - $6 - every month"
 *  - plan:  "Subscription"
 *  - price: "$6 - every month"
 */
function joes_scanner_auth_get_label_parts_for_price( $price_id ) {
    $price_id = (string) $price_id;
    if ( $price_id === '' ) {
        return array(
            'full'  => '',
            'plan'  => '',
            'price' => '',
        );
    }

    // Uses the same normalized labels as the settings UI.
    $full_label = joes_scanner_auth_get_price_label_by_id( $price_id );
    if ( $full_label === '' ) {
        return array(
            'full'  => '',
            'plan'  => '',
            'price' => '',
        );
    }

    $parts = array_map( 'trim', explode( ' - ', $full_label ) );
    $count = count( $parts );

    $plan  = '';
    $price = '';

    if ( $count >= 4 ) {
        // Example: [ "Joe's Scanner", "Subscription", "$6", "every month" ]
        $plan  = $parts[ $count - 3 ];
        $price = implode( ' - ', array_slice( $parts, -2 ) );
    } elseif ( $count === 3 ) {
        // Example: [ "Subscription", "$6", "every month" ]
        $plan  = $parts[0];
        $price = implode( ' - ', array_slice( $parts, -2 ) );
    } elseif ( $count === 2 ) {
        // Example: [ "Subscription", "$6/month" ]
        $plan  = $parts[0];
        $price = $parts[1];
    } elseif ( $count === 1 ) {
        $plan  = $parts[0];
        $price = $parts[0];
    }

    return array(
        'full'  => $full_label,
        'plan'  => $plan,
        'price' => $price,
    );
}

/**
 * Price text for the API, for example "$6 - every month".
 */
function joes_scanner_auth_get_price_label_for_api( $price_id ) {
    $parts = joes_scanner_auth_get_label_parts_for_price( $price_id );
    return $parts['price'];
}

/**
 * Plan name for the API, for example "Subscription" or "Subscription Annual".
 */
function joes_scanner_auth_get_plan_label_for_api( $price_id ) {
    $parts = joes_scanner_auth_get_label_parts_for_price( $price_id );
    return $parts['plan'];
}

/**
 * Given a SureCart price ID, return a human-friendly label if known.
 * Falls back to the raw ID if no label is found.
 */
function joes_scanner_auth_get_price_label_by_id( $price_id ) {
    $price_id = (string) $price_id;
    if ( $price_id === '' ) {
        return '';
    }

    static $label_map = null;

    if ( $label_map === null ) {
        $label_map = array();
        $prices    = joes_scanner_auth_get_all_subscription_prices();
        foreach ( $prices as $price ) {
            if ( isset( $price['id'], $price['label'] ) ) {
                $pid               = (string) $price['id'];
                $label_map[ $pid ] = (string) $price['label'];
            }
        }
    }

    if ( isset( $label_map[ $price_id ] ) ) {
        return $label_map[ $price_id ];
    }

    // Fallback: return the raw ID so the app still gets something.
    return $price_id;
}

/**
 * Field renderer for allowed price IDs as checkbox list.
 */
function joes_scanner_auth_allowed_price_ids_render() {
    $options          = joes_scanner_auth_get_options();
    $selected_ids     = isset( $options['allowed_price_ids'] ) && is_array( $options['allowed_price_ids'] )
        ? $options['allowed_price_ids']
        : array();
    $available_prices = joes_scanner_auth_get_all_subscription_prices();

    if ( empty( $available_prices ) ) {
        echo '<p>No SureCart subscription prices could be detected. Make sure SureCart is active and you have products with subscription prices.</p>';
        return;
    }

    echo '<p>Select which SureCart prices should grant access to the Joe\'s Scanner app.</p>';

    foreach ( $available_prices as $price ) {
        if ( ! isset( $price['id'], $price['label'] ) ) {
            continue;
        }

        $id      = (string) $price['id'];
        $label   = (string) $price['label'];
        $checked = in_array( $id, $selected_ids, true ) ? 'checked="checked"' : '';
        ?>
        <label>
            <input
                type="checkbox"
                name="joes_scanner_auth_options[allowed_price_ids][]"
                value="<?php echo esc_attr( $id ); ?>"
                <?php echo $checked; ?>
            />
            <?php echo esc_html( $label ); ?>
        </label>
        <br />
        <?php
    }
}

/**
 * Field renderer for allowed statuses as checkbox list.
 */
function joes_scanner_auth_allowed_statuses_render() {
    $current = joes_scanner_auth_get_allowed_statuses();

    $all_statuses = array(
        'active'   => 'Active',
        'trialing' => 'Trialing',
        'past_due' => 'Past due',
        'canceled' => 'Canceled',
    );
    ?>
    <p>Select which subscription statuses are considered valid for scanner access.</p>
    <?php
    foreach ( $all_statuses as $slug => $label ) {
        $checked = in_array( $slug, $current, true ) ? 'checked="checked"' : '';
        ?>
        <label>
            <input
                type="checkbox"
                name="joes_scanner_auth_options[allowed_statuses][]"
                value="<?php echo esc_attr( $slug ); ?>"
                <?php echo $checked; ?>
            />
            <?php echo esc_html( $label ); ?>
        </label>
        <br />
        <?php
    }
    ?>
    <p class="description">
        If none are selected, the default is Active, Trialing, and Past due.
    </p>
    <?php
}

/**
 * Options page markup.
 */
function joes_scanner_auth_options_page() {
    if ( ! current_user_can( 'manage_options' ) ) {
        return;
    }
    ?>
    <div class="wrap">
        <h1>Joe's Scanner Auth</h1>
        <form action="options.php" method="post">
            <?php
            settings_fields( 'joes_scanner_auth' );
            do_settings_sections( 'joes_scanner_auth' );
            submit_button();
            ?>
        </form>

        <hr />
        <h2>How to use</h2>
        <ol>
            <li>Ensure SureCart is active and you have subscription-type products and prices configured.</li>
            <li>Use the checkboxes above to select which prices should grant access to the Joe's Scanner app.</li>
            <li>Use the status checkboxes to choose which subscription statuses count as valid.</li>
            <li>Save changes. The /wp-json/joes-scanner/v1/auth endpoint will now require one of the selected prices with an allowed status.</li>
        </ol>
    </div>
    <?php
}

/**
 * Admin settings hooks.
 */
if ( is_admin() ) {
    add_action( 'admin_menu', 'joes_scanner_auth_add_admin_menu' );
    add_action( 'admin_init', 'joes_scanner_auth_settings_init' );
}

/**
 * Adds a settings page under Settings > Joe's Scanner Auth.
 */
function joes_scanner_auth_add_admin_menu() {
    add_options_page(
        "Joe's Scanner Auth",
        "Joe's Scanner Auth",
        'manage_options',
        'joes-scanner-auth',
        'joes_scanner_auth_options_page'
    );
}

/**
 * Registers the settings, section, and fields.
 */
function joes_scanner_auth_settings_init() {
    register_setting(
        'joes_scanner_auth',
        'joes_scanner_auth_options',
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
        'joes_scanner_auth'
    );

    add_settings_field(
        'joes_scanner_auth_allowed_price_ids',
        'Allowed SureCart prices',
        'joes_scanner_auth_allowed_price_ids_render',
        'joes_scanner_auth',
        'joes_scanner_auth_section_main'
    );

    add_settings_field(
        'joes_scanner_auth_allowed_statuses',
        'Allowed subscription statuses',
        'joes_scanner_auth_allowed_statuses_render',
        'joes_scanner_auth',
        'joes_scanner_auth_section_main'
    );
}

/**
 * REST API registration.
 */
add_action(
    'rest_api_init',
    function () {
        register_rest_route(
            'joes-scanner/v1',
            '/auth',
            array(
                'methods'             => 'POST',
                'callback'            => 'joes_scanner_auth_endpoint',
                'permission_callback' => '__return_true',
            )
        );

        register_rest_route(
            'joes-scanner/v1',
            '/debug-surecart',
            array(
                'methods'             => 'GET',
                'callback'            => 'joes_scanner_auth_debug_surecart',
                'permission_callback' => '__return_true',
            )
        );

        register_rest_route(
            'joes-scanner/v1',
            '/debug-account',
            array(
                'methods'             => 'GET',
                'callback'            => 'joes_scanner_auth_debug_account',
                'permission_callback' => '__return_true',
            )
        );
    }
);

/**
 * POST /wp-json/joes-scanner/v1/auth
 */
function joes_scanner_auth_endpoint( $request ) {
    try {
        $params   = $request->get_json_params();
        $username = isset( $params['username'] ) ? sanitize_user( $params['username'] ) : '';
        $password = isset( $params['password'] ) ? (string) $params['password'] : '';

        if ( $username === '' || $password === '' ) {
            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'missing_credentials',
                    'message' => 'Username and password are required.',
                ),
                400
            );
        }

        $user = wp_authenticate( $username, $password );

        if ( is_wp_error( $user ) ) {
            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'invalid_credentials',
                    'message' => 'Invalid username or password.',
                ),
                401
            );
        }

        if ( ! class_exists( '\SureCart\Models\Customer' ) || ! class_exists( '\SureCart\Models\Subscription' ) ) {
            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'surecart_not_available',
                    'message' => 'SureCart models are not available on this site.',
                ),
                500
            );
        }

        $email          = $user->user_email;
        $allowed_prices = joes_scanner_auth_get_allowed_price_ids();
        $allowed_status = joes_scanner_auth_get_allowed_statuses();

        if ( empty( $allowed_prices ) ) {
            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'no_allowed_prices_configured',
                    'message' => 'No allowed SureCart price IDs are configured in Joe\'s Scanner Auth settings.',
                ),
                500
            );
        }

        $customer = \SureCart\Models\Customer::where(
            array(
                'email' => $email,
            )
        )->first();

        $active              = false;
        $status              = 'none';
        $level_label         = null;
        $expires_at_utc      = null;
        $trial_ends_at_utc   = null;
        $price_id_used       = null;
        $price_label_for_api = null;
        $period_end_at_utc   = null;

        if ( $customer ) {
            $subscriptions = \SureCart\Models\Subscription::where(
                array(
                    'customer_ids[]' => $customer->id,
                )
            )->get();

            $best_subscription        = null;
            $best_status_priority     = -1;
            $best_plan_priority       = -1;
            $best_end_timestamp       = 0;

            foreach ( $subscriptions as $subscription ) {
                $sub_status   = isset( $subscription->status ) ? strtolower( (string) $subscription->status ) : '';
                $sub_price_id = isset( $subscription->price_id ) ? (string) $subscription->price_id : '';

                if ( $sub_price_id === '' ) {
                    continue;
                }

                if ( ! in_array( $sub_price_id, $allowed_prices, true ) ) {
                    continue;
                }

                if ( ! in_array( $sub_status, $allowed_status, true ) ) {
                    continue;
                }

                // Status priority: trialing > active > past_due > others.
                switch ( $sub_status ) {
                    case 'trialing':
                        $status_priority = 3;
                        break;
                    case 'active':
                        $status_priority = 2;
                        break;
                    case 'past_due':
                        $status_priority = 1;
                        break;
                    default:
                        $status_priority = 0;
                        break;
                }

                // Plan label for selection: "Subscription" vs "Subscription Annual".
                if ( function_exists( 'joes_scanner_auth_get_plan_label_for_api' ) ) {
                    $plan_label = joes_scanner_auth_get_plan_label_for_api( $sub_price_id );
                } else {
                    $plan_label = joes_scanner_auth_get_price_label_by_id( $sub_price_id );
                }

                $plan_label_lower = strtolower( (string) $plan_label );

                // Plan priority: prefer non-annual plans over annual when status is the same.
                $plan_priority = ( strpos( $plan_label_lower, 'annual' ) !== false ) ? 0 : 1;

                // Use the later of current period end or trial end as an end timestamp for comparison.
                $end_ts = 0;
                if ( ! empty( $subscription->current_period_end_at ) ) {
                    $end_ts = max( $end_ts, (int) $subscription->current_period_end_at );
                }
                if ( ! empty( $subscription->trial_end_at ) ) {
                    $end_ts = max( $end_ts, (int) $subscription->trial_end_at );
                }

                if (
                    $status_priority > $best_status_priority ||
                    ( $status_priority === $best_status_priority && $plan_priority > $best_plan_priority ) ||
                    ( $status_priority === $best_status_priority && $plan_priority === $best_plan_priority && $end_ts > $best_end_timestamp )
                ) {
                    $best_subscription    = $subscription;
                    $best_status_priority = $status_priority;
                    $best_plan_priority   = $plan_priority;
                    $best_end_timestamp   = $end_ts;
                }
            }

            if ( $best_subscription ) {
                $active        = true;
                $status        = isset( $best_subscription->status ) ? strtolower( (string) $best_subscription->status ) : '';
                $price_id_used = isset( $best_subscription->price_id ) ? (string) $best_subscription->price_id : null;

                if ( $price_id_used ) {
                    // Plan label: "Subscription" or "Subscription Annual".
                    if ( function_exists( 'joes_scanner_auth_get_plan_label_for_api' ) ) {
                        $level_label = joes_scanner_auth_get_plan_label_for_api( $price_id_used );
                    } else {
                        $level_label = joes_scanner_auth_get_price_label_by_id( $price_id_used );
                    }

                    // Price label: "$6 - every month".
                    if ( function_exists( 'joes_scanner_auth_get_price_label_for_api' ) ) {
                        $price_label_for_api = joes_scanner_auth_get_price_label_for_api( $price_id_used );
                    } else {
                        $price_label_for_api = $level_label;
                    }
                }

                // Resolve raw end and trial strings from the chosen subscription.
                $raw_current_end = null;
                if ( ! empty( $best_subscription->current_period_end_at_date_time ) ) {
                    $raw_current_end = (string) $best_subscription->current_period_end_at_date_time;
                } elseif ( ! empty( $best_subscription->current_period_end_at_date ) ) {
                    $raw_current_end = (string) $best_subscription->current_period_end_at_date;
                } elseif ( ! empty( $best_subscription->current_period_end_at ) ) {
                    $raw_current_end = (string) $best_subscription->current_period_end_at;
                }

                $raw_trial_end = null;
                if ( ! empty( $best_subscription->trial_end_at_date_time ) ) {
                    $raw_trial_end = (string) $best_subscription->trial_end_at_date_time;
                } elseif ( ! empty( $best_subscription->trial_end_at_date ) ) {
                    $raw_trial_end = (string) $best_subscription->trial_end_at_date;
                } elseif ( ! empty( $best_subscription->trial_end_at ) ) {
                    $raw_trial_end = (string) $best_subscription->trial_end_at;
                }

                // expires_at_utc: raw current period end.
                if ( $raw_current_end ) {
                    $ts = is_numeric( $raw_current_end ) ? (int) $raw_current_end : strtotime( $raw_current_end );
                    if ( $ts ) {
                        $expires_at_utc = gmdate( 'c', $ts );
                    }
                }

                // trial_ends_at_utc: raw trial end.
                if ( $raw_trial_end ) {
                    $ts = is_numeric( $raw_trial_end ) ? (int) $raw_trial_end : strtotime( $raw_trial_end );
                    if ( $ts ) {
                        $trial_ends_at_utc = gmdate( 'c', $ts );
                    }
                }

                // period_end_at_utc: trial end while trialing, otherwise period end.
                $raw_period = ( $status === 'trialing' )
                    ? ( $raw_trial_end ?: $raw_current_end )
                    : ( $raw_current_end ?: $raw_trial_end );

                if ( $raw_period ) {
                    $ts = is_numeric( $raw_period ) ? (int) $raw_period : strtotime( $raw_period );
                    if ( $ts ) {
                        $period_end_at_utc = gmdate( 'c', $ts );
                    }
                }
            }
        }

        if ( ! $active ) {
            return new WP_REST_Response(
                array(
                    'ok'      => false,
                    'error'   => 'no_active_subscription',
                    'message' => 'No active scanner subscription found for this account.',
                    'user'    => array(
                        'id'           => $user->ID,
                        'email'        => $email,
                        'display_name' => $user->display_name,
                    ),
                    'subscription' => array(
                        'active'        => false,
                        'status'        => $status,
                        'level'         => $level_label,
                        'price_id'      => $price_label_for_api,
                        'price_guid'    => $price_id_used,
                        'expires_at'    => $expires_at_utc,
                        'trial_ends_at' => $trial_ends_at_utc,
                        'period_end_at' => $period_end_at_utc,
                    ),
                    'server_time_utc' => gmdate( 'c' ),
                ),
                403
            );
        }

        return new WP_REST_Response(
            array(
                'ok'   => true,
                'user' => array(
                    'id'           => $user->ID,
                    'email'        => $email,
                    'display_name' => $user->display_name,
                ),
                'subscription' => array(
                    'active'        => true,
                    'status'        => $status,
                    // Plan label ("Subscription" or "Subscription Annual").
                    'level'         => $level_label,
                    // Price text ("$6 - every month").
                    'price_id'      => $price_label_for_api,
                    // Raw SureCart price GUID for internal use.
                    'price_guid'    => $price_id_used,
                    'expires_at'    => $expires_at_utc,
                    'trial_ends_at' => $trial_ends_at_utc,
                    'period_end_at' => $period_end_at_utc,
                ),
                'server_time_utc' => gmdate( 'c' ),
            ),
            200
        );
    } catch ( \Throwable $e ) {
        $msg = sprintf(
            '[JSAuthB] Internal error while validating subscription in %s:%d - %s',
            $e->getFile(),
            $e->getLine(),
            $e->getMessage()
        );

        error_log( "Joe's Scanner Auth internal error: " . $msg );

        return new WP_REST_Response(
            array(
                'ok'      => false,
                'error'   => 'internal_error',
                'message' => $msg,
                'debug'   => array(
                    'file' => $e->getFile(),
                    'line' => $e->getLine(),
                ),
            ),
            500
        );
    }
}

/**
 * Debug endpoint for /wp-json/joes-scanner/v1/debug-account
 */
function joes_scanner_auth_debug_account( $request ) {
    $email = $request->get_param( 'email' );
    $email = $email ? sanitize_email( $email ) : '';

    if ( $email === '' ) {
        return new WP_REST_Response(
            array(
                'ok'      => false,
                'error'   => 'missing_email',
                'message' => 'Provide an email query parameter, for example ?email=nate@natesnetwork.com',
            ),
            400
        );
    }

    $out = array(
        'ok'               => true,
        'email'            => $email,
        'wp_user'          => null,
        'sc_customer'      => null,
        'sc_subscriptions' => array(),
        'plugin_config'    => array(
            'allowed_price_ids' => joes_scanner_auth_get_allowed_price_ids(),
            'allowed_statuses'  => joes_scanner_auth_get_allowed_statuses(),
        ),
    );

    // 1) WordPress user.
    $wp_user = get_user_by( 'email', $email );
    if ( $wp_user instanceof WP_User ) {
        $out['wp_user'] = json_decode( wp_json_encode( $wp_user ), true );
    }

    // If SureCart models are not available, we still return WP user info.
    if ( ! class_exists( '\SureCart\Models\Customer' ) || ! class_exists( '\SureCart\Models\Subscription' ) ) {
        $out['surecart_available'] = false;
        return new WP_REST_Response( $out, 200 );
    }

    $out['surecart_available'] = true;

    // 2) SureCart customer by email.
    $customer = \SureCart\Models\Customer::where(
        array(
            'email' => $email,
        )
    )->first();

    if ( $customer ) {
        $out['sc_customer'] = json_decode( wp_json_encode( $customer ), true );

        // 3) All SureCart subscriptions for this customer.
        $subscriptions = \SureCart\Models\Subscription::where(
            array(
                'customer_ids[]' => $customer->id,
            )
        )->get();

        $subs_out = array();

        foreach ( $subscriptions as $subscription ) {
            $sub_arr = json_decode( wp_json_encode( $subscription ), true );

            // Figure out the raw price ID from the subscription.
            $price_id = '';
            if ( isset( $sub_arr['price_id'] ) && $sub_arr['price_id'] !== '' ) {
                $price_id = (string) $sub_arr['price_id'];
            } elseif ( isset( $sub_arr['price'] ) && $sub_arr['price'] !== '' ) {
                $price_id = (string) $sub_arr['price'];
            }

            if ( $price_id !== '' ) {
                // Human readable price text.
                if ( function_exists( 'joes_scanner_auth_get_price_label_for_api' ) ) {
                    $price_label = joes_scanner_auth_get_price_label_for_api( $price_id );
                } else {
                    $price_label = joes_scanner_auth_get_price_label_by_id( $price_id );
                }

                $sub_arr['price']      = $price_label;
                $sub_arr['price_id']   = $price_id;
                $sub_arr['price_text'] = $price_label;
            }

            $subs_out[] = $sub_arr;
        }

        $out['sc_subscriptions'] = $subs_out;
    }

    return new WP_REST_Response( $out, 200 );
}

/**
 * Debug endpoint implementation for /wp-json/joes-scanner/v1/debug-surecart
 */
function joes_scanner_auth_debug_surecart() {
    $out = array(
        'products'          => array(),
        'products_found'    => 0,
        'normalized_prices' => array(),
    );

    try {
        if ( function_exists( 'sc_get_product' ) && class_exists( 'WP_Query' ) ) {
            $query = new WP_Query(
                array(
                    'post_type'      => 'sc_product',
                    'post_status'    => 'publish',
                    'posts_per_page' => 50,
                    'no_found_rows'  => true,
                    'fields'         => 'ids',
                )
            );

            $out['products_found'] = count( $query->posts );
            foreach ( $query->posts as $post_id ) {
                $prod              = sc_get_product( $post_id );
                $out['products'][] = json_decode( wp_json_encode( $prod ), true );
            }
        }
    } catch ( \Throwable $e ) {
        $out['products_error'] = $e->getMessage();
    }

    try {
        $out['normalized_prices'] = joes_scanner_auth_get_all_subscription_prices();
    } catch ( \Throwable $e ) {
        $out['prices_error'] = $e->getMessage();
    }

    return $out;
}
