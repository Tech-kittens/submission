#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "esp_check.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "mbedtls/platform_util.h"
#include "net_wifi.h"

#define CONNECTED_BIT       BIT0
#define SCAN_DONE_BIT       BIT1
#define RETRY_DELAY_MS      1000
#define MAX_FAILED_TRIES    3
#define SCAN_MAX            20

static const char *TAG = "wifi";

static EventGroupHandle_t s_events;
static wifi_config_t s_target;
static volatile bool s_have_target;
static volatile bool s_switching;
static volatile bool s_keep_retrying;
static volatile net_wifi_state_t s_state = NET_WIFI_IDLE;
static volatile net_wifi_fail_t s_fail;
static int s_failed_tries;
static wifi_ap_record_t s_scan[SCAN_MAX];
static uint16_t s_scan_count;

static net_wifi_fail_t classify(uint8_t reason)
{
    switch (reason) {
    case WIFI_REASON_NO_AP_FOUND:
    case WIFI_REASON_NO_AP_FOUND_W_COMPATIBLE_SECURITY:
    case WIFI_REASON_NO_AP_FOUND_IN_AUTHMODE_THRESHOLD:
    case WIFI_REASON_NO_AP_FOUND_IN_RSSI_THRESHOLD:
        return NET_WIFI_FAIL_NOT_FOUND;
    case WIFI_REASON_AUTH_FAIL:
    case WIFI_REASON_4WAY_HANDSHAKE_TIMEOUT:
    case WIFI_REASON_HANDSHAKE_TIMEOUT:
        return NET_WIFI_FAIL_WRONG_PASSWORD;
    default:
        return NET_WIFI_FAIL_OTHER;
    }
}

static void start_target(void)
{
    s_failed_tries = 0;
    s_fail = NET_WIFI_FAIL_NONE;
    s_state = NET_WIFI_CONNECTING;
    esp_wifi_set_config(WIFI_IF_STA, &s_target);
    esp_wifi_connect();
}

static void on_event(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        const wifi_event_sta_disconnected_t *e = data;
        xEventGroupClearBits(s_events, CONNECTED_BIT);
        if (s_switching) {
            s_switching = false;
            start_target();
            return;
        }
        if (!s_have_target) {
            s_state = NET_WIFI_IDLE;
            return;
        }
        net_wifi_fail_t fail = classify(e->reason);
        ESP_LOGW(TAG, "Disconnected (reason %d)", e->reason);
        if (!s_keep_retrying && fail != NET_WIFI_FAIL_OTHER && ++s_failed_tries >= MAX_FAILED_TRIES) {
            s_fail = fail;
            s_state = NET_WIFI_FAILED;
            s_have_target = false;
            return;
        }
        s_state = NET_WIFI_CONNECTING;
        vTaskDelay(pdMS_TO_TICKS(RETRY_DELAY_MS));
        if (s_have_target) {
            esp_wifi_connect();
        }
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_SCAN_DONE) {
        s_scan_count = SCAN_MAX;
        if (esp_wifi_scan_get_ap_records(&s_scan_count, s_scan) != ESP_OK) {
            s_scan_count = 0;
        }
        xEventGroupSetBits(s_events, SCAN_DONE_BIT);
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        const ip_event_got_ip_t *e = data;
        ESP_LOGI(TAG, "Got IP " IPSTR, IP2STR(&e->ip_info.ip));
        s_state = NET_WIFI_CONNECTED;
        xEventGroupSetBits(s_events, CONNECTED_BIT);
    }
}

esp_err_t net_wifi_init(void)
{
    ESP_RETURN_ON_ERROR(esp_netif_init(), TAG, "netif init");
    ESP_RETURN_ON_ERROR(esp_event_loop_create_default(), TAG, "event loop");
    esp_netif_create_default_wifi_sta();

    s_events = xEventGroupCreate();
    ESP_RETURN_ON_FALSE(s_events, ESP_ERR_NO_MEM, TAG, "event group");

    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    ESP_RETURN_ON_ERROR(esp_wifi_init(&init), TAG, "wifi init");
    ESP_RETURN_ON_ERROR(esp_wifi_set_storage(WIFI_STORAGE_RAM), TAG, "wifi storage");
    ESP_RETURN_ON_ERROR(esp_event_handler_instance_register(WIFI_EVENT, ESP_EVENT_ANY_ID, on_event, NULL, NULL),
                        TAG, "wifi handler");
    ESP_RETURN_ON_ERROR(esp_event_handler_instance_register(IP_EVENT, IP_EVENT_STA_GOT_IP, on_event, NULL, NULL),
                        TAG, "ip handler");
    ESP_RETURN_ON_ERROR(esp_wifi_set_mode(WIFI_MODE_STA), TAG, "wifi mode");
    return esp_wifi_start();
}

esp_err_t net_wifi_connect(const uint8_t *ssid, size_t ssid_len, const char *password, bool keep_retrying)
{
    size_t pass_len = strlen(password);
    ESP_RETURN_ON_FALSE(ssid_len > 0 && ssid_len <= sizeof(s_target.sta.ssid) &&
                        pass_len < sizeof(s_target.sta.password), ESP_ERR_INVALID_ARG, TAG,
                        "Invalid Wi-Fi SSID or password length");

    bool was_active = s_have_target || s_state == NET_WIFI_CONNECTED;
    mbedtls_platform_zeroize(&s_target, sizeof(s_target));
    memcpy(s_target.sta.ssid, ssid, ssid_len);
    memcpy(s_target.sta.password, password, pass_len);
    s_target.sta.threshold.authmode = pass_len ? WIFI_AUTH_WPA2_PSK : WIFI_AUTH_OPEN;
    s_target.sta.sae_pwe_h2e = WPA3_SAE_PWE_BOTH;
    s_target.sta.scan_method = WIFI_ALL_CHANNEL_SCAN;
    s_keep_retrying = keep_retrying;
    s_have_target = true;

    ESP_LOGI(TAG, "Connecting to \"%.*s\"", (int)ssid_len, (const char *)ssid);
    if (was_active) {
        s_switching = true;
        if (esp_wifi_disconnect() != ESP_OK) {
            s_switching = false;
            start_target();
        }
    } else {
        start_target();
    }
    return ESP_OK;
}

void net_wifi_disconnect(void)
{
    s_have_target = false;
    s_switching = false;
    esp_wifi_disconnect();
    mbedtls_platform_zeroize(&s_target, sizeof(s_target));
}

net_wifi_state_t net_wifi_state(void)
{
    return s_state;
}

net_wifi_fail_t net_wifi_fail_reason(void)
{
    return s_fail;
}

bool net_wifi_wait_connected(uint32_t timeout_ms)
{
    return xEventGroupWaitBits(s_events, CONNECTED_BIT, pdFALSE, pdTRUE, pdMS_TO_TICKS(timeout_ms)) & CONNECTED_BIT;
}

esp_err_t net_wifi_scan_start(void)
{
    xEventGroupClearBits(s_events, SCAN_DONE_BIT);
    wifi_scan_config_t cfg = {.show_hidden = false};
    return esp_wifi_scan_start(&cfg, false);
}

bool net_wifi_scan_done(void)
{
    return xEventGroupGetBits(s_events) & SCAN_DONE_BIT;
}

size_t net_wifi_scan_results(wifi_ap_record_t *out, size_t max)
{
    size_t n = s_scan_count < max ? s_scan_count : max;
    memcpy(out, s_scan, n * sizeof(*out));
    return n;
}
