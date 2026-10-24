#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "cJSON.h"
#include "esp_app_desc.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_random.h"
#include "esp_system.h"
#include "mbedtls/platform_util.h"
#include "protocomm.h"
#include "protocomm_ble.h"
#include "protocomm_security2.h"
#include "esp_srp.h"
#include "esp_dpp.h"
#include "esp_wifi.h"
#include "host/ble_hs.h"
#include "sdkconfig.h"
#include "audio_out.h"
#include "board.h"
#include "buttons.h"
#include "config_store.h"
#include "device_socket.h"
#include "identity.h"
#include "net_wifi.h"
#include "setup.h"
#include "status.h"

#define PROTOCOL_VERSION        1
#define SRP_USERNAME            "quacktion"
#define SRP_PASSWORD            "quacktion-setup"
#define SALT_LEN                16
#define APPROVAL_TIMEOUT_MS     (2 * 60 * 1000)
#define BLINK_MS                400
#define DPP_CHANNELS            "6"
#define DPP_WINDOW_MS           (2 * 60 * 1000)
#define DPP_URI_WAIT_MS         5000
#define DPP_URI_MAX             256
#define DPP_BIT_URI             BIT0
#define DPP_BIT_FAILED          BIT1
#define DPP_BIT_CONFIG          BIT2
#define DPP_BIT_UNUSABLE        BIT3
#define AUTH_WINDOW_MS          (5 * 60 * 1000)
#define SESSION_WINDOW_MS       (10 * 60 * 1000)
#define WIFI_ATTEMPT_MS         30000
#define BACKEND_ATTEMPT_MS      20000
#define IDENTIFY_INTERVAL_MS    5000
#define TICK_MS                 200
#define REBOOT_DELAY_MS         1000
#define MAX_PLAINTEXT           496
#define REQUEST_ID_MAX          36
#define CACHED_RESPONSES        8
#define SCAN_RESULTS_MAX        20

static const char *TAG = "setup";

/* c5649ba0-9be0-43aa-bfb6-8902ab366fad, little-endian. Fixed by docs/ble-protocol.md. */
static uint8_t SERVICE_UUID[16] = {
    0xad, 0x6f, 0x36, 0xab, 0x02, 0x89, 0xb6, 0xbf, 0xaa, 0x43, 0xe0, 0x9b, 0xa0, 0x9b, 0x64, 0xc5,
};
/* Company ID 0xFFFF (test, little-endian), protocol version, flags (bit 0: setup available). */
static uint8_t MANUFACTURER_DATA[] = {0xff, 0xff, PROTOCOL_VERSION, 0x01};

typedef enum {
    WIFI_UNCONFIGURED,
    WIFI_SCANNING,
    WIFI_AWAITING_DPP,
    WIFI_CONNECTING,
    WIFI_CONNECTED,
    WIFI_FAILED,
} wifi_state_t;

typedef enum {
    BACKEND_UNLINKED,
    BACKEND_CONNECTING,
    BACKEND_ONLINE,
    BACKEND_FAILED,
} backend_state_t;

static const char *WIFI_STATE_NAMES[] = {"unconfigured", "scanning", "awaiting_dpp", "connecting", "connected", "failed"};
static const char *BACKEND_STATE_NAMES[] = {"unlinked", "connecting", "online", "failed"};

typedef struct {
    char id[REQUEST_ID_MAX + 1];
    char *response;
} cached_response_t;

typedef void (*operation_t)(const cJSON *req, cJSON *resp);

static SemaphoreHandle_t s_lock;
static protocomm_t *s_pc;
static protocomm_security2_params_t s_sec2;
static char *s_salt;
static char *s_verifier;
static char s_session_id[17];
static TickType_t s_window_start;
static bool s_authenticated;
static bool s_ever_authenticated;
static bool s_approved;
static TickType_t s_auth_tick;
static uint16_t s_conn_handle;
static bool s_leds_on = true;

/* DPP: the event handler runs without s_lock (wifi.dpp.start waits for it while holding s_lock),
 * so it hands results over through s_dpp_events and these buffers. */
static EventGroupHandle_t s_dpp_events;
static bool s_dpp_active;
static TickType_t s_dpp_start;
static char s_dpp_uri[DPP_URI_MAX];
static quack_config_t s_dpp_config;
static volatile bool s_closing;

static quack_config_t s_saved;
static wifi_state_t s_wifi = WIFI_UNCONFIGURED;
static wifi_state_t s_wifi_before_scan;
static bool s_scanning;
static bool s_attempt_active;
static TickType_t s_attempt_start;
static quack_config_t s_attempt;
static char s_ssid[CONFIG_SSID_MAX * 2 + 1];
static const char *s_error;

static backend_state_t s_backend = BACKEND_UNLINKED;
static bool s_backend_pending;
static TickType_t s_backend_start;

static char s_last_request_id[REQUEST_ID_MAX + 1];
static cached_response_t s_cache[CACHED_RESPONSES];
static int s_cache_next;
static TickType_t s_last_identify;

static TickType_t elapsed_since(TickType_t t)
{
    return xTaskGetTickCount() - t;
}

static void reboot_later(void)
{
    s_closing = true;
}

/* ---------- validation helpers ---------- */

static bool utf8_valid(const uint8_t *s, size_t len)
{
    for (size_t i = 0; i < len;) {
        uint8_t c = s[i];
        size_t n = c < 0x80 ? 1 : (c & 0xe0) == 0xc0 ? 2 : (c & 0xf0) == 0xe0 ? 3 : (c & 0xf8) == 0xf0 ? 4 : 0;
        if (n == 0 || i + n > len || (c < 0x20 && n == 1)) {
            return false;
        }
        for (size_t k = 1; k < n; k++) {
            if ((s[i + k] & 0xc0) != 0x80) {
                return false;
            }
        }
        i += n;
    }
    return true;
}

static bool is_hex(const char *s, size_t len)
{
    for (size_t i = 0; i < len; i++) {
        if (!isxdigit((unsigned char)s[i])) {
            return false;
        }
    }
    return true;
}

static void hex_encode(const uint8_t *in, size_t len, char *out)
{
    for (size_t i = 0; i < len; i++) {
        sprintf(out + 2 * i, "%02x", in[i]);
    }
    out[2 * len] = '\0';
}

static bool hex_decode(const char *hex, uint8_t *out, size_t *out_len, size_t max)
{
    size_t len = strlen(hex);
    if (len == 0 || len % 2 || len / 2 > max || !is_hex(hex, len)) {
        return false;
    }
    for (size_t i = 0; i < len / 2; i++) {
        unsigned v;
        sscanf(hex + 2 * i, "%2x", &v);
        out[i] = (uint8_t)v;
    }
    *out_len = len / 2;
    return true;
}

static void set_display_ssid(const uint8_t *ssid, size_t len)
{
    if (utf8_valid(ssid, len)) {
        memcpy(s_ssid, ssid, len);
        s_ssid[len] = '\0';
    } else {
        hex_encode(ssid, len, s_ssid);
    }
}

/* ---------- operations (called with s_lock held) ---------- */

static void fail(cJSON *resp, const char *code)
{
    cJSON_ReplaceItemInObject(resp, "ok", cJSON_CreateFalse());
    cJSON_AddStringToObject(resp, "error", code);
}

static void op_device_info(const cJSON *req, cJSON *resp)
{
    char id[IDENTITY_ID_LEN + 1];
    if (identity_device_identifier(id) != ESP_OK) {
        fail(resp, "internal_error");
        return;
    }
    cJSON_AddNumberToObject(resp, "protocolVersion", PROTOCOL_VERSION);
    cJSON_AddStringToObject(resp, "deviceIdentifier", id);
    cJSON_AddStringToObject(resp, "model", "waveshare-esp32-s3-audio-board");
    cJSON_AddStringToObject(resp, "firmwareVersion", esp_app_get_description()->version);
    bool ready = s_wifi == WIFI_CONNECTED && s_backend == BACKEND_ONLINE;
    cJSON_AddStringToObject(resp, "setupState", ready ? "ready_to_complete" : "in_progress");
    cJSON *caps = cJSON_AddObjectToObject(resp, "capabilities");
    cJSON_AddBoolToObject(caps, "manualWifi", true);
    cJSON_AddBoolToObject(caps, "dpp", true);
    cJSON_AddBoolToObject(caps, "identify", true);
}

static void identify_task(void *arg)
{
    for (int i = 0; i < 3; i++) {
        board_leds_fill(40, 40, 0);
        vTaskDelay(pdMS_TO_TICKS(150));
        board_leds_off();
        vTaskDelay(pdMS_TO_TICKS(150));
    }
    audio_out_quack();
    status_refresh();
    vTaskDelete(NULL);
}

static void op_device_identify(const cJSON *req, cJSON *resp)
{
    TickType_t since = elapsed_since(s_last_identify);
    if (s_last_identify && since < pdMS_TO_TICKS(IDENTIFY_INTERVAL_MS)) {
        fail(resp, "rate_limited");
        cJSON_AddNumberToObject(resp, "retryAfterMs", IDENTIFY_INTERVAL_MS - pdTICKS_TO_MS(since));
        return;
    }
    s_last_identify = xTaskGetTickCount();
    if (xTaskCreate(identify_task, "identify", 4096, NULL, 5, NULL) != pdPASS) {
        fail(resp, "internal_error");
    }
}

static uint32_t window_ms(void)
{
    return s_ever_authenticated ? SESSION_WINDOW_MS : AUTH_WINDOW_MS;
}

static void op_setup_status(const cJSON *req, cJSON *resp)
{
    uint32_t elapsed = pdTICKS_TO_MS(elapsed_since(s_window_start));
    cJSON_AddStringToObject(resp, "setupSessionId", s_session_id);
    cJSON_AddNumberToObject(resp, "setupExpiresInSeconds", elapsed < window_ms() ? (window_ms() - elapsed) / 1000 : 0);
    cJSON_AddStringToObject(resp, "approval", s_approved ? "approved" : "pending");
    cJSON_AddStringToObject(resp, "wifiState", WIFI_STATE_NAMES[s_wifi]);
    cJSON_AddStringToObject(resp, "backendState", BACKEND_STATE_NAMES[s_backend]);
    if (s_ssid[0]) {
        cJSON_AddStringToObject(resp, "ssid", s_ssid);
    } else {
        cJSON_AddNullToObject(resp, "ssid");
    }
    if (s_error) {
        cJSON_AddStringToObject(resp, "errorCode", s_error);
    } else {
        cJSON_AddNullToObject(resp, "errorCode");
    }
    cJSON_AddStringToObject(resp, "lastRequestId", s_last_request_id);
}

static const char *auth_name(wifi_auth_mode_t auth, bool *supported)
{
    *supported = true;
    switch (auth) {
    case WIFI_AUTH_OPEN: return "open";
    case WIFI_AUTH_WPA2_PSK: return "wpa2_psk";
    case WIFI_AUTH_WPA_WPA2_PSK: return "wpa_wpa2_psk";
    case WIFI_AUTH_WPA3_PSK: return "wpa3_psk";
    case WIFI_AUTH_WPA2_WPA3_PSK: return "wpa2_wpa3_psk";
    default: break;
    }
    *supported = false;
    switch (auth) {
    case WIFI_AUTH_WEP: return "wep";
    case WIFI_AUTH_WPA_PSK: return "wpa_psk";
    case WIFI_AUTH_OWE: return "owe";
    case WIFI_AUTH_WPA2_ENTERPRISE: return "wpa2_enterprise";
    case WIFI_AUTH_WPA3_ENTERPRISE:
    case WIFI_AUTH_WPA2_WPA3_ENTERPRISE:
    case WIFI_AUTH_WPA3_ENT_192: return "wpa3_enterprise";
    default: return "unknown";
    }
}

static int compare_rssi(const void *a, const void *b)
{
    return ((const wifi_ap_record_t *)b)->rssi - ((const wifi_ap_record_t *)a)->rssi;
}

/* Strongest first, one entry per SSID, hidden networks dropped. */
static size_t scan_list(wifi_ap_record_t *aps)
{
    size_t n = net_wifi_scan_results(aps, SCAN_RESULTS_MAX);
    qsort(aps, n, sizeof(*aps), compare_rssi);
    size_t out = 0;
    for (size_t i = 0; i < n; i++) {
        size_t len = strnlen((const char *)aps[i].ssid, sizeof(aps[i].ssid));
        if (len == 0) {
            continue;
        }
        bool dup = false;
        for (size_t k = 0; k < out && !dup; k++) {
            dup = strncmp((const char *)aps[k].ssid, (const char *)aps[i].ssid, sizeof(aps[i].ssid)) == 0;
        }
        if (!dup) {
            aps[out++] = aps[i];
        }
    }
    return out;
}

static void op_wifi_scan(const cJSON *req, cJSON *resp)
{
    const cJSON *action = cJSON_GetObjectItemCaseSensitive(req, "action");
    if (!cJSON_IsString(action)) {
        fail(resp, "invalid_request");
        return;
    }
    if (strcmp(action->valuestring, "start") == 0) {
        if (s_scanning || s_attempt_active || s_dpp_active) {
            fail(resp, "busy");
            return;
        }
        if (net_wifi_scan_start() != ESP_OK) {
            fail(resp, "busy");
            return;
        }
        s_scanning = true;
        s_wifi_before_scan = s_wifi;
        s_wifi = WIFI_SCANNING;
        strlcpy(s_last_request_id, cJSON_GetObjectItem(req, "id")->valuestring, sizeof(s_last_request_id));
        return;
    }
    if (strcmp(action->valuestring, "results") != 0) {
        fail(resp, "invalid_request");
        return;
    }
    if (s_scanning) {
        fail(resp, "busy");
        return;
    }

    const cJSON *offset_item = cJSON_GetObjectItemCaseSensitive(req, "offset");
    int offset = cJSON_IsNumber(offset_item) ? offset_item->valueint : 0;
    wifi_ap_record_t *aps = calloc(SCAN_RESULTS_MAX, sizeof(*aps));
    if (!aps) {
        fail(resp, "internal_error");
        return;
    }
    size_t total = scan_list(aps);
    cJSON_AddNumberToObject(resp, "total", total);
    cJSON *next = cJSON_AddNullToObject(resp, "next");
    cJSON *networks = cJSON_AddArrayToObject(resp, "networks");
    for (size_t i = offset < 0 ? 0 : offset; i < total; i++) {
        cJSON *net = cJSON_CreateObject();
        size_t len = strnlen((const char *)aps[i].ssid, sizeof(aps[i].ssid));
        if (utf8_valid(aps[i].ssid, len)) {
            cJSON_AddStringToObject(net, "ssid", (const char *)aps[i].ssid);
        } else {
            char hex[CONFIG_SSID_MAX * 2 + 1];
            hex_encode(aps[i].ssid, len, hex);
            cJSON_AddStringToObject(net, "ssidHex", hex);
        }
        bool supported;
        cJSON_AddNumberToObject(net, "rssi", aps[i].rssi);
        cJSON_AddStringToObject(net, "auth", auth_name(aps[i].authmode, &supported));
        cJSON_AddBoolToObject(net, "supported", supported);
        cJSON_AddItemToArray(networks, net);

        char *probe = cJSON_PrintUnformatted(resp);
        size_t size = probe ? strlen(probe) + 24 : MAX_PLAINTEXT + 1;
        cJSON_free(probe);
        if (size > MAX_PLAINTEXT) {
            cJSON_DeleteItemFromArray(networks, cJSON_GetArraySize(networks) - 1);
            cJSON_ReplaceItemInObject(resp, "next", cJSON_CreateNumber(i));
            break;
        }
    }
    (void)next;
    free(aps);
}

static void start_wifi_attempt(const quack_config_t *target)
{
    s_attempt = *target;
    s_attempt_active = true;
    s_attempt_start = xTaskGetTickCount();
    s_error = NULL;
    s_wifi = WIFI_CONNECTING;
    set_display_ssid(target->ssid, target->ssid_len);
    net_wifi_connect(target->ssid, target->ssid_len, target->password, false);
}

static void dpp_stop(void)
{
    if (!s_dpp_active) {
        return;
    }
    esp_supp_dpp_stop_listen();
    esp_supp_dpp_deinit();
    s_dpp_active = false;
    mbedtls_platform_zeroize(s_dpp_uri, sizeof(s_dpp_uri));
}

static void restore_previous_wifi(void)
{
    if (config_has_wifi(&s_saved)) {
        net_wifi_connect(s_saved.ssid, s_saved.ssid_len, s_saved.password, true);
        set_display_ssid(s_saved.ssid, s_saved.ssid_len);
        s_wifi = WIFI_CONNECTING;
    } else {
        net_wifi_disconnect();
        s_ssid[0] = '\0';
        s_wifi = WIFI_UNCONFIGURED;
    }
}

static void op_wifi_configure(const cJSON *req, cJSON *resp)
{
    if (s_attempt_active || s_scanning) {
        fail(resp, "busy");
        return;
    }
    dpp_stop();
    quack_config_t target = {0};
    const cJSON *ssid = cJSON_GetObjectItemCaseSensitive(req, "ssid");
    const cJSON *ssid_hex = cJSON_GetObjectItemCaseSensitive(req, "ssidHex");
    const cJSON *password = cJSON_GetObjectItemCaseSensitive(req, "password");
    if (cJSON_IsString(ssid)) {
        target.ssid_len = strlen(ssid->valuestring);
        if (target.ssid_len == 0 || target.ssid_len > CONFIG_SSID_MAX) {
            fail(resp, "invalid_ssid");
            return;
        }
        memcpy(target.ssid, ssid->valuestring, target.ssid_len);
    } else if (!cJSON_IsString(ssid_hex) ||
               !hex_decode(ssid_hex->valuestring, target.ssid, &target.ssid_len, CONFIG_SSID_MAX)) {
        fail(resp, "invalid_ssid");
        return;
    }
    const char *pw = cJSON_IsString(password) ? password->valuestring : "";
    size_t pw_len = strlen(pw);
    bool pw_ok = pw_len == 0 || (pw_len >= 8 && pw_len <= 63) || (pw_len == 64 && is_hex(pw, 64));
    if (!pw_ok) {
        fail(resp, "invalid_password");
        return;
    }
    strlcpy(target.password, pw, sizeof(target.password));
    strlcpy(s_last_request_id, cJSON_GetObjectItem(req, "id")->valuestring, sizeof(s_last_request_id));
    ESP_LOGI(TAG, "Trying new Wi-Fi network");
    start_wifi_attempt(&target);
    config_wipe(&target);
}

static void on_dpp_event(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    if (id == WIFI_EVENT_DPP_URI_READY) {
        const wifi_event_dpp_uri_ready_t *e = data;
        strlcpy(s_dpp_uri, e->uri, sizeof(s_dpp_uri));
        xEventGroupSetBits(s_dpp_events, DPP_BIT_URI);
    } else if (id == WIFI_EVENT_DPP_CFG_RECVD) {
        const wifi_event_dpp_config_received_t *e = data;
        for (int i = 0; i < e->total_conf; i++) {
            const esp_dpp_config_data_t *row = &e->configs[i];
            if (row->akm == ESP_DPP_AKM_DPP || row->ssid_len == 0 || row->ssid_len > CONFIG_SSID_MAX ||
                row->password_len == 0 || row->password_len > CONFIG_PASSWORD_MAX) {
                continue;
            }
            config_wipe(&s_dpp_config);
            memcpy(s_dpp_config.ssid, row->ssid, row->ssid_len);
            s_dpp_config.ssid_len = row->ssid_len;
            memcpy(s_dpp_config.password, row->password, row->password_len);
            xEventGroupSetBits(s_dpp_events, DPP_BIT_CONFIG);
            return;
        }
        xEventGroupSetBits(s_dpp_events, DPP_BIT_UNUSABLE);
    } else if (id == WIFI_EVENT_DPP_FAILED) {
        xEventGroupSetBits(s_dpp_events, DPP_BIT_FAILED);
    }
}

static void op_wifi_dpp(const cJSON *req, cJSON *resp)
{
    if (s_attempt_active || s_scanning || s_dpp_active) {
        fail(resp, "busy");
        return;
    }
    /* The radio has to sit on the listen channel, so drop any current network until DPP ends. */
    net_wifi_disconnect();
    xEventGroupClearBits(s_dpp_events, DPP_BIT_URI | DPP_BIT_FAILED | DPP_BIT_CONFIG | DPP_BIT_UNUSABLE);

    bool ok = esp_supp_dpp_init() == ESP_OK;
    if (ok && esp_supp_dpp_bootstrap_gen(DPP_CHANNELS, DPP_BOOTSTRAP_QR_CODE, NULL, NULL) != ESP_OK) {
        esp_supp_dpp_deinit();
        ok = false;
    }
    if (ok) {
        EventBits_t bits = xEventGroupWaitBits(s_dpp_events, DPP_BIT_URI | DPP_BIT_FAILED, pdFALSE, pdFALSE,
                                               pdMS_TO_TICKS(DPP_URI_WAIT_MS));
        ok = (bits & DPP_BIT_URI) && esp_supp_dpp_start_listen() == ESP_OK;
        if (!ok) {
            esp_supp_dpp_deinit();
        }
    }
    if (!ok) {
        ESP_LOGW(TAG, "DPP could not start");
        restore_previous_wifi();
        fail(resp, "dpp_unavailable");
        return;
    }

    s_dpp_active = true;
    s_dpp_start = xTaskGetTickCount();
    s_wifi = WIFI_AWAITING_DPP;
    s_ssid[0] = '\0';
    s_error = NULL;
    strlcpy(s_last_request_id, cJSON_GetObjectItem(req, "id")->valuestring, sizeof(s_last_request_id));
    ESP_LOGI(TAG, "DPP listening on channel %s", DPP_CHANNELS);
    cJSON_AddStringToObject(resp, "uri", s_dpp_uri);
    cJSON_AddNumberToObject(resp, "expiresInSeconds", DPP_WINDOW_MS / 1000);
}

static void op_wifi_dpp_cancel(const cJSON *req, cJSON *resp)
{
    if (s_dpp_active) {
        ESP_LOGI(TAG, "DPP cancelled");
        dpp_stop();
        restore_previous_wifi();
    }
}

static void update_dpp(void)
{
    if (!s_dpp_active) {
        return;
    }
    EventBits_t bits = xEventGroupGetBits(s_dpp_events);
    if (bits & DPP_BIT_CONFIG) {
        xEventGroupClearBits(s_dpp_events, DPP_BIT_CONFIG);
        dpp_stop();
        ESP_LOGI(TAG, "Wi-Fi received from the phone over DPP");
        start_wifi_attempt(&s_dpp_config);
        config_wipe(&s_dpp_config);
    } else if (bits & DPP_BIT_UNUSABLE) {
        xEventGroupClearBits(s_dpp_events, DPP_BIT_UNUSABLE);
        dpp_stop();
        ESP_LOGW(TAG, "DPP gave no network the duck can use");
        restore_previous_wifi();
        s_wifi = WIFI_FAILED;
        s_error = "dpp_failed";
    } else if (elapsed_since(s_dpp_start) > pdMS_TO_TICKS(DPP_WINDOW_MS)) {
        dpp_stop();
        ESP_LOGW(TAG, "DPP expired");
        restore_previous_wifi();
        s_wifi = WIFI_FAILED;
        s_error = "dpp_timeout";
    } else if (bits & DPP_BIT_FAILED) {
        xEventGroupClearBits(s_dpp_events, DPP_BIT_FAILED);
        ESP_LOGW(TAG, "DPP exchange failed, listening again");
        esp_supp_dpp_start_listen();
    }
}

static bool valid_device_id(const char *id)
{
    size_t len = strlen(id);
    if (len == 0 || len > CONFIG_DEVICE_ID_MAX) {
        return false;
    }
    for (size_t i = 0; i < len; i++) {
        char c = id[i];
        if (!(isalnum((unsigned char)c) || c == '-' || c == '_' || c == '.')) {
            return false;
        }
    }
    return true;
}

static void op_backend_configure(const cJSON *req, cJSON *resp)
{
    const cJSON *device_id = cJSON_GetObjectItemCaseSensitive(req, "deviceId");
    const cJSON *credential = cJSON_GetObjectItemCaseSensitive(req, "credential");
    if (!cJSON_IsString(device_id) || !cJSON_IsString(credential) || !valid_device_id(device_id->valuestring) ||
        strlen(credential->valuestring) != CONFIG_CREDENTIAL_LEN ||
        !is_hex(credential->valuestring, CONFIG_CREDENTIAL_LEN)) {
        fail(resp, "invalid_credential");
        return;
    }
    if (config_save_backend_pending(device_id->valuestring, credential->valuestring) != ESP_OK) {
        fail(resp, "storage_failed");
        return;
    }
    strlcpy(s_last_request_id, cJSON_GetObjectItem(req, "id")->valuestring, sizeof(s_last_request_id));
    ESP_LOGI(TAG, "Backend credential stored for device %s", device_id->valuestring);
    device_socket_stop();
    s_backend_pending = true;
    s_backend = BACKEND_CONNECTING;
    s_backend_start = xTaskGetTickCount();
    s_error = NULL;
}

static void op_setup_complete(const cJSON *req, cJSON *resp)
{
    if (s_wifi != WIFI_CONNECTED || s_backend != BACKEND_ONLINE) {
        fail(resp, "not_ready");
        return;
    }
    ESP_LOGI(TAG, "Setup complete");
    cJSON_AddNumberToObject(resp, "rebootInMs", REBOOT_DELAY_MS);
    reboot_later();
}

static void op_setup_cancel(const cJSON *req, cJSON *resp)
{
    ESP_LOGI(TAG, "Setup cancelled");
    config_discard_backend_pending();
    cJSON_AddNumberToObject(resp, "rebootInMs", REBOOT_DELAY_MS);
    reboot_later();
}

/* ---------- protocomm endpoint plumbing ---------- */

static char *cached(const char *id)
{
    for (int i = 0; i < CACHED_RESPONSES; i++) {
        if (s_cache[i].response && strcmp(s_cache[i].id, id) == 0) {
            return s_cache[i].response;
        }
    }
    return NULL;
}

static void remember(const char *id, const char *response)
{
    cached_response_t *slot = &s_cache[s_cache_next];
    s_cache_next = (s_cache_next + 1) % CACHED_RESPONSES;
    free(slot->response);
    strlcpy(slot->id, id, sizeof(slot->id));
    slot->response = strdup(response);
}

static char *error_response(const char *id, const char *code)
{
    cJSON *resp = cJSON_CreateObject();
    cJSON_AddNumberToObject(resp, "v", 1);
    if (id) {
        cJSON_AddStringToObject(resp, "id", id);
    } else {
        cJSON_AddNullToObject(resp, "id");
    }
    cJSON_AddFalseToObject(resp, "ok");
    cJSON_AddStringToObject(resp, "error", code);
    char *out = cJSON_PrintUnformatted(resp);
    cJSON_Delete(resp);
    return out;
}

typedef struct {
    const char *name;
    uint16_t uuid;
    operation_t op;
    bool before_approval;
} endpoint_def_t;

static esp_err_t endpoint(uint32_t session_id, const uint8_t *inbuf, ssize_t inlen,
                          uint8_t **outbuf, ssize_t *outlen, void *priv)
{
    const endpoint_def_t *ep = priv;
    cJSON *req = inlen > 0 && inlen <= MAX_PLAINTEXT ? cJSON_ParseWithLength((const char *)inbuf, inlen) : NULL;
    const cJSON *v = cJSON_GetObjectItemCaseSensitive(req, "v");
    const cJSON *id = cJSON_GetObjectItemCaseSensitive(req, "id");
    bool id_ok = cJSON_IsString(id) && id->valuestring[0] && strlen(id->valuestring) <= REQUEST_ID_MAX;
    const char *id_str = id_ok ? id->valuestring : NULL;

    xSemaphoreTake(s_lock, portMAX_DELAY);
    char *text = NULL;
    bool owned = true;
    if (!req || !id_ok) {
        text = error_response(id_str, "invalid_request");
    } else if (!cJSON_IsNumber(v) || v->valueint != 1) {
        text = error_response(id_str, "unsupported_version");
    } else if (s_closing) {
        text = error_response(id_str, "setup_expired");
    } else if (!ep->before_approval && !s_approved) {
        text = error_response(id_str, "approval_required");
    } else if ((text = cached(id_str)) != NULL) {
        owned = false;
    } else {
        cJSON *resp = cJSON_CreateObject();
        cJSON_AddNumberToObject(resp, "v", 1);
        cJSON_AddStringToObject(resp, "id", id_str);
        cJSON_AddTrueToObject(resp, "ok");
        ep->op(req, resp);
        text = cJSON_PrintUnformatted(resp);
        cJSON_Delete(resp);
        if (text && strlen(text) > MAX_PLAINTEXT) {
            cJSON_free(text);
            text = error_response(id_str, "internal_error");
        }
        if (text) {
            remember(id_str, text);
        }
    }

    esp_err_t err = ESP_ERR_NO_MEM;
    if (text) {
        *outlen = strlen(text);
        *outbuf = malloc(*outlen);
        if (*outbuf) {
            memcpy(*outbuf, text, *outlen);
            err = ESP_OK;
        }
        if (owned) {
            cJSON_free(text);
        }
    }
    xSemaphoreGive(s_lock);
    if (req) {
        cJSON_Delete(req);
    }
    return err;
}

static const endpoint_def_t ENDPOINTS[] = {
    {"device.info", 0xff60, op_device_info, true},
    {"device.identify", 0xff61, op_device_identify, true},
    {"setup.status", 0xff62, op_setup_status, true},
    {"wifi.scan", 0xff63, op_wifi_scan, false},
    {"wifi.configure", 0xff64, op_wifi_configure, false},
    {"wifi.dpp.start", 0xff65, op_wifi_dpp, false},
    {"wifi.dpp.cancel", 0xff66, op_wifi_dpp_cancel, false},
    {"backend.configure", 0xff67, op_backend_configure, false},
    {"setup.complete", 0xff68, op_setup_complete, false},
    {"setup.cancel", 0xff69, op_setup_cancel, false},
};
#define ENDPOINT_COUNT (sizeof(ENDPOINTS) / sizeof(ENDPOINTS[0]))

static void on_protocomm_event(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    xSemaphoreTake(s_lock, portMAX_DELAY);
    if (base == PROTOCOMM_SECURITY_SESSION_EVENT && id == PROTOCOMM_SECURITY_SESSION_SETUP_OK) {
        ESP_LOGI(TAG, "App session ready; press the setup button to approve it");
        s_authenticated = true;
        s_ever_authenticated = true;
        s_approved = false;
        s_auth_tick = xTaskGetTickCount();
    } else if (base == PROTOCOMM_SECURITY_SESSION_EVENT && id == PROTOCOMM_SECURITY_SESSION_CREDENTIALS_MISMATCH) {
        ESP_LOGW(TAG, "App used the wrong setup password");
    } else if (base == PROTOCOMM_TRANSPORT_BLE_EVENT && id == PROTOCOMM_TRANSPORT_BLE_CONNECTED) {
        s_conn_handle = ((const protocomm_ble_event_t *)data)->conn_handle;
        ESP_LOGI(TAG, "App connected");
    } else if (base == PROTOCOMM_TRANSPORT_BLE_EVENT && id == PROTOCOMM_TRANSPORT_BLE_DISCONNECTED) {
        ESP_LOGI(TAG, "App disconnected");
        s_authenticated = false;
        s_approved = false;
    }
    xSemaphoreGive(s_lock);
}

/* ---------- setup window ---------- */

static esp_err_t start_protocomm(void)
{
    int verifier_len = 0;
    esp_err_t err = esp_srp_gen_salt_verifier(SRP_USERNAME, strlen(SRP_USERNAME), SRP_PASSWORD,
                                              strlen(SRP_PASSWORD), &s_salt, SALT_LEN, &s_verifier,
                                              &verifier_len);
    if (err != ESP_OK) {
        return err;
    }
    s_sec2 = (protocomm_security2_params_t){
        .salt = s_salt, .salt_len = SALT_LEN, .verifier = s_verifier, .verifier_len = verifier_len,
    };

    s_pc = protocomm_new();
    if (!s_pc) {
        return ESP_ERR_NO_MEM;
    }
    ESP_ERROR_CHECK(protocomm_set_security(s_pc, "prov-session", &protocomm_security2, &s_sec2));
    char version[128];
    snprintf(version, sizeof(version),
             "{\"prov\":{\"ver\":\"v1.1\",\"sec_ver\":2,\"sec_patch_ver\":%d,\"cap\":[]},"
             "\"quacktion\":{\"protocolVersion\":%d}}",
             protocomm_security2.patch_ver, PROTOCOL_VERSION);
    ESP_ERROR_CHECK(protocomm_set_version(s_pc, "proto-ver", version));
    for (size_t i = 0; i < ENDPOINT_COUNT; i++) {
        ESP_ERROR_CHECK(protocomm_add_endpoint(s_pc, ENDPOINTS[i].name, endpoint, (void *)&ENDPOINTS[i]));
    }

    static protocomm_ble_name_uuid_t lookup[ENDPOINT_COUNT + 2];
    lookup[0] = (protocomm_ble_name_uuid_t){"prov-session", 0xff51};
    lookup[1] = (protocomm_ble_name_uuid_t){"proto-ver", 0xff53};
    for (size_t i = 0; i < ENDPOINT_COUNT; i++) {
        lookup[i + 2] = (protocomm_ble_name_uuid_t){ENDPOINTS[i].name, ENDPOINTS[i].uuid};
    }
    protocomm_ble_config_t ble = {
        .manufacturer_data = MANUFACTURER_DATA,
        .manufacturer_data_len = sizeof(MANUFACTURER_DATA),
        .nu_lookup_count = ENDPOINT_COUNT + 2,
        .nu_lookup = lookup,
    };
    memcpy(ble.service_uuid, SERVICE_UUID, sizeof(SERVICE_UUID));
    char suffix[IDENTITY_SUFFIX_LEN + 1];
    ESP_ERROR_CHECK(identity_display_suffix(suffix));
    snprintf(ble.device_name, sizeof(ble.device_name), "Quacktion-%s", suffix);

    ESP_ERROR_CHECK(esp_event_handler_register(PROTOCOMM_SECURITY_SESSION_EVENT, ESP_EVENT_ANY_ID,
                                               on_protocomm_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(PROTOCOMM_TRANSPORT_BLE_EVENT, ESP_EVENT_ANY_ID,
                                               on_protocomm_event, NULL));
    err = protocomm_ble_start(s_pc, &ble);
    if (err == ESP_OK) {
        ESP_LOGI(TAG, "Advertising as %s", ble.device_name);
    }
    return err;
}

static void stop_protocomm(void)
{
    if (s_pc) {
        protocomm_ble_stop(s_pc);
        protocomm_delete(s_pc);
        s_pc = NULL;
    }
}

/* ---------- background state machine ---------- */

static void update_wifi(void)
{
    if (s_scanning && net_wifi_scan_done()) {
        s_scanning = false;
        s_wifi = s_wifi_before_scan;
    }
    if (s_dpp_active) {
        return;
    }
    net_wifi_state_t net = net_wifi_state();
    if (s_attempt_active) {
        bool timed_out = elapsed_since(s_attempt_start) > pdMS_TO_TICKS(WIFI_ATTEMPT_MS);
        if (net == NET_WIFI_CONNECTED) {
            s_attempt_active = false;
            if (config_save_wifi(s_attempt.ssid, s_attempt.ssid_len, s_attempt.password) == ESP_OK) {
                s_saved.ssid_len = s_attempt.ssid_len;
                memcpy(s_saved.ssid, s_attempt.ssid, s_attempt.ssid_len);
                strlcpy(s_saved.password, s_attempt.password, sizeof(s_saved.password));
                ESP_LOGI(TAG, "New Wi-Fi network saved");
            }
            config_wipe(&s_attempt);
            s_wifi = WIFI_CONNECTED;
        } else if (net == NET_WIFI_FAILED || timed_out) {
            net_wifi_fail_t reason = net_wifi_fail_reason();
            s_error = timed_out ? "wifi_timeout"
                    : reason == NET_WIFI_FAIL_WRONG_PASSWORD ? "wrong_password"
                    : reason == NET_WIFI_FAIL_NOT_FOUND ? "network_not_found" : "wifi_timeout";
            ESP_LOGW(TAG, "Wi-Fi attempt failed: %s", s_error);
            s_attempt_active = false;
            config_wipe(&s_attempt);
            s_wifi = WIFI_FAILED;
            if (config_has_wifi(&s_saved)) {
                net_wifi_connect(s_saved.ssid, s_saved.ssid_len, s_saved.password, true);
                set_display_ssid(s_saved.ssid, s_saved.ssid_len);
            } else {
                net_wifi_disconnect();
                s_ssid[0] = '\0';
            }
        }
        return;
    }
    if (s_scanning || s_wifi == WIFI_FAILED) {
        if (s_wifi == WIFI_FAILED && net == NET_WIFI_CONNECTED) {
            s_wifi = WIFI_CONNECTED;
        }
        return;
    }
    if (config_has_wifi(&s_saved)) {
        s_wifi = net == NET_WIFI_CONNECTED ? WIFI_CONNECTED : WIFI_CONNECTING;
    }
}

static void update_backend(void)
{
    quack_config_t creds = {0};
    bool have = s_backend_pending ? config_load_backend_pending(&creds) : config_has_backend(&s_saved);
    if (!s_backend_pending && have) {
        creds = s_saved;
    }
    if (!have) {
        config_wipe(&creds);
        return;
    }

    device_socket_state_t sock = device_socket_state();
    if (sock == DEVICE_SOCKET_STOPPED && s_wifi == WIFI_CONNECTED && s_backend != BACKEND_FAILED) {
        if (device_socket_start(CONFIG_QUACK_BACKEND_URL, creds.device_id, creds.credential) == ESP_OK) {
            s_backend = BACKEND_CONNECTING;
            s_backend_start = xTaskGetTickCount();
        } else {
            s_backend = BACKEND_FAILED;
            s_error = "backend_error";
        }
    } else if (sock == DEVICE_SOCKET_ONLINE && s_backend != BACKEND_ONLINE) {
        if (s_backend_pending) {
            if (config_promote_backend_pending() == ESP_OK) {
                s_backend_pending = false;
                strlcpy(s_saved.device_id, creds.device_id, sizeof(s_saved.device_id));
                strlcpy(s_saved.credential, creds.credential, sizeof(s_saved.credential));
            } else {
                s_error = "storage_failed";
            }
        }
        s_backend = BACKEND_ONLINE;
        if (s_error && (strcmp(s_error, "backend_unreachable") == 0)) {
            s_error = NULL;
        }
        ESP_LOGI(TAG, "Backend online");
    } else if (sock == DEVICE_SOCKET_REJECTED) {
        device_socket_stop();
        s_backend = BACKEND_FAILED;
        s_error = "credential_rejected";
        ESP_LOGW(TAG, "Backend rejected the device credential");
    } else if (sock == DEVICE_SOCKET_CONNECTING && s_backend == BACKEND_CONNECTING &&
               elapsed_since(s_backend_start) > pdMS_TO_TICKS(BACKEND_ATTEMPT_MS)) {
        s_backend = BACKEND_FAILED;
        s_error = "backend_unreachable";
        ESP_LOGW(TAG, "Backend not reachable yet");
    } else if (sock == DEVICE_SOCKET_ONLINE) {
        s_backend = BACKEND_ONLINE;
    }
    config_wipe(&creds);
}

static void approve(void)
{
    xSemaphoreTake(s_lock, portMAX_DELAY);
    if (s_authenticated && !s_approved) {
        s_approved = true;
        ESP_LOGI(TAG, "App approved with the setup button");
    }
    xSemaphoreGive(s_lock);
}

/* Blinks while an app waits for approval, otherwise shows the setup colour. */
static void show_approval(bool waiting)
{
    bool on = !waiting || (xTaskGetTickCount() / pdMS_TO_TICKS(BLINK_MS)) % 2 == 0;
    if (on != s_leds_on) {
        s_leds_on = on;
        if (on) {
            status_refresh();
        } else {
            board_leds_off();
        }
    }
}

static void on_k2_hold(void)
{
    ESP_LOGI(TAG, "Setup button held, restarting setup");
    config_request_setup();
    esp_restart();
}

void setup_run(void)
{
    s_lock = xSemaphoreCreateMutex();
    status_set(STATUS_SETUP);
    config_load(&s_saved);
    snprintf(s_session_id, sizeof(s_session_id), "%08lx%08lx", (unsigned long)esp_random(), (unsigned long)esp_random());

    ESP_ERROR_CHECK(net_wifi_init());
    if (config_has_wifi(&s_saved)) {
        net_wifi_connect(s_saved.ssid, s_saved.ssid_len, s_saved.password, true);
        set_display_ssid(s_saved.ssid, s_saved.ssid_len);
        s_wifi = WIFI_CONNECTING;
    }
    if (config_has_backend(&s_saved)) {
        s_backend = BACKEND_CONNECTING;
        s_backend_start = xTaskGetTickCount();
    }

    s_dpp_events = xEventGroupCreate();
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, WIFI_EVENT_DPP_URI_READY, on_dpp_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, WIFI_EVENT_DPP_CFG_RECVD, on_dpp_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, WIFI_EVENT_DPP_FAILED, on_dpp_event, NULL));

    const buttons_callbacks_t cb = {.on_k2_hold = on_k2_hold, .on_k2_press = approve};
    ESP_ERROR_CHECK(buttons_start(&cb));

    s_window_start = xTaskGetTickCount();
    if (start_protocomm() != ESP_OK) {
        ESP_LOGE(TAG, "Could not start BLE setup");
        status_show_error();
        esp_restart();
    }

    while (true) {
        vTaskDelay(pdMS_TO_TICKS(TICK_MS));
        xSemaphoreTake(s_lock, portMAX_DELAY);
        if (!s_closing) {
            update_dpp();
            update_wifi();
            update_backend();
        }
        bool expired = elapsed_since(s_window_start) > pdMS_TO_TICKS(window_ms());
        bool closing = s_closing;
        bool waiting = s_authenticated && !s_approved;
        bool approval_timed_out = waiting && elapsed_since(s_auth_tick) > pdMS_TO_TICKS(APPROVAL_TIMEOUT_MS);
        if (approval_timed_out) {
            s_auth_tick = xTaskGetTickCount();
        }
        uint16_t conn = s_conn_handle;
        xSemaphoreGive(s_lock);

        show_approval(waiting && !closing);
        if (approval_timed_out) {
            ESP_LOGW(TAG, "App not approved within %d s, disconnecting it", APPROVAL_TIMEOUT_MS / 1000);
            ble_gap_terminate(conn, BLE_ERR_REM_USER_CONN_TERM);
        }

        if (closing || expired) {
            if (expired) {
                ESP_LOGW(TAG, "Setup window expired");
            }
            vTaskDelay(pdMS_TO_TICKS(REBOOT_DELAY_MS));
            xSemaphoreTake(s_lock, portMAX_DELAY);
            dpp_stop();
            xSemaphoreGive(s_lock);
            stop_protocomm();
            device_socket_stop();
            if (!closing) {
                config_discard_backend_pending();
            }
            quack_config_t now;
            config_load(&now);
            bool complete = config_has_wifi(&now) && config_has_backend(&now);
            config_wipe(&now);
            if (complete) {
                esp_restart();
            }
            ESP_LOGI(TAG, "Not set up; hold K2 for 3 s to start setup again");
            board_leds_off();
            vTaskSuspend(NULL);
        }
    }
}
