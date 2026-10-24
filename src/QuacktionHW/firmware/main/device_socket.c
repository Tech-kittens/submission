#include <stdio.h>
#include <string.h>
#include <strings.h>
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "cJSON.h"
#include "esp_check.h"
#include "esp_crt_bundle.h"
#include "esp_log.h"
#include "esp_websocket_client.h"
#include "mbedtls/platform_util.h"
#include "device_socket.h"

#define SUBPROTOCOL             "quacktion.v1"
#define HEARTBEAT               "{\"version\":1,\"type\":\"heartbeat\"}"
#define HEARTBEAT_MS            15000
#define SEND_TIMEOUT_MS         5000
#define RETRY_MIN_MS            2000
#define RETRY_MAX_MS            60000
#define REJECTED_RETRY_MS       60000
#define TEXT_MAX                1024
#define WS_OP_TEXT              0x1

static const char *TAG = "device_ws";

static esp_websocket_client_handle_t s_client;
static volatile device_socket_state_t s_state = DEVICE_SOCKET_STOPPED;
static SemaphoreHandle_t s_lock;
static bool s_heartbeat_started;
static int s_retry_ms = RETRY_MIN_MS;
static char s_text[TEXT_MAX + 1];
static size_t s_text_len;

static void handle_control(const char *json)
{
    cJSON *root = cJSON_Parse(json);
    const cJSON *type = cJSON_GetObjectItemCaseSensitive(root, "type");
    if (cJSON_IsString(type)) {
        const cJSON *code = cJSON_GetObjectItemCaseSensitive(root, "code");
        if (!cJSON_IsString(code)) {
            code = cJSON_GetObjectItemCaseSensitive(cJSON_GetObjectItemCaseSensitive(root, "payload"), "code");
        }
        if (strcasecmp(type->valuestring, "error") == 0) {
            ESP_LOGE(TAG, "Backend error (code %s)", cJSON_IsString(code) ? code->valuestring : "none");
        } else if (strcasecmp(type->valuestring, "heartbeat") != 0) {
            ESP_LOGI(TAG, "Backend control %s", type->valuestring);
        }
    }
    cJSON_Delete(root);
}

static void on_data(const esp_websocket_event_data_t *d)
{
    if (d->op_code != WS_OP_TEXT && d->op_code != 0) {
        return;
    }
    if (d->payload_offset == 0 && d->op_code == WS_OP_TEXT) {
        s_text_len = 0;
    }
    if (s_text_len + d->data_len <= TEXT_MAX) {
        memcpy(s_text + s_text_len, d->data_ptr, d->data_len);
        s_text_len += d->data_len;
    }
    if (d->fin && d->payload_offset + d->data_len >= d->payload_len) {
        s_text[s_text_len] = '\0';
        handle_control(s_text);
        s_text_len = 0;
    }
}

static int s_pending_retry_ms;

/* An ERROR event is followed by DISCONNECTED; the delay chosen for the error must survive it. */
static void set_offline(int retry_ms)
{
    if (s_state != DEVICE_SOCKET_REJECTED) {
        s_state = DEVICE_SOCKET_CONNECTING;
    }
    s_pending_retry_ms = retry_ms;
    esp_websocket_client_set_reconnect_timeout(s_client, retry_ms);
}

static int next_backoff_ms(void)
{
    int retry = s_retry_ms;
    s_retry_ms = s_retry_ms * 2 > RETRY_MAX_MS ? RETRY_MAX_MS : s_retry_ms * 2;
    return retry;
}

static void on_event(void *arg, esp_event_base_t base, int32_t id, void *event_data)
{
    const esp_websocket_event_data_t *d = event_data;
    switch (id) {
    case WEBSOCKET_EVENT_CONNECTED:
        ESP_LOGI(TAG, "Device socket online");
        s_state = DEVICE_SOCKET_ONLINE;
        s_retry_ms = RETRY_MIN_MS;
        s_pending_retry_ms = 0;
        esp_websocket_client_set_reconnect_timeout(s_client, RETRY_MIN_MS);
        break;
    case WEBSOCKET_EVENT_DATA:
        on_data(d);
        break;
    case WEBSOCKET_EVENT_ERROR: {
        int status = d->error_handle.esp_ws_handshake_status_code;
        if (status == 401 || status == 403) {
            ESP_LOGE(TAG, "Backend rejected the device credential (HTTP %d), retrying in %d s",
                     status, REJECTED_RETRY_MS / 1000);
            s_state = DEVICE_SOCKET_REJECTED;
            set_offline(REJECTED_RETRY_MS);
        } else {
            s_state = DEVICE_SOCKET_CONNECTING;
            int retry = next_backoff_ms();
            ESP_LOGW(TAG, "Device socket error (type %d, HTTP %d), retrying in %d s",
                     d->error_handle.error_type, status, retry / 1000);
            set_offline(retry);
        }
        break;
    }
    case WEBSOCKET_EVENT_DISCONNECTED:
    case WEBSOCKET_EVENT_CLOSED:
        if (s_state == DEVICE_SOCKET_ONLINE) {
            ESP_LOGW(TAG, "Device socket dropped, reconnecting");
            s_state = DEVICE_SOCKET_CONNECTING;
        }
        set_offline(s_pending_retry_ms ? s_pending_retry_ms : next_backoff_ms());
        break;
    default:
        break;
    }
}

static void heartbeat_task(void *arg)
{
    while (true) {
        vTaskDelay(pdMS_TO_TICKS(HEARTBEAT_MS));
        xSemaphoreTake(s_lock, portMAX_DELAY);
        if (s_client && s_state == DEVICE_SOCKET_ONLINE && esp_websocket_client_is_connected(s_client)) {
            int len = strlen(HEARTBEAT);
            if (esp_websocket_client_send_text(s_client, HEARTBEAT, len, pdMS_TO_TICKS(SEND_TIMEOUT_MS)) != len) {
                ESP_LOGW(TAG, "Heartbeat send failed");
            }
        }
        xSemaphoreGive(s_lock);
    }
}

static esp_err_t build_url(const char *backend_url, const char *device_id, char *out, size_t out_len)
{
    const char *scheme;
    const char *rest;
    if (strncmp(backend_url, "https://", 8) == 0) {
        scheme = "wss://";
        rest = backend_url + 8;
    } else if (strncmp(backend_url, "http://", 7) == 0) {
        scheme = "ws://";
        rest = backend_url + 7;
    } else {
        return ESP_ERR_INVALID_ARG;
    }
    size_t rest_len = strlen(rest);
    while (rest_len && rest[rest_len - 1] == '/') {
        rest_len--;
    }
    for (const char *c = device_id; *c; c++) {
        if (!((*c >= '0' && *c <= '9') || (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') ||
              *c == '-' || *c == '_' || *c == '.')) {
            return ESP_ERR_INVALID_ARG;
        }
    }
    int n = snprintf(out, out_len, "%s%.*s/ws/devices/%s", scheme, (int)rest_len, rest, device_id);
    return n > 0 && (size_t)n < out_len ? ESP_OK : ESP_ERR_INVALID_SIZE;
}

esp_err_t device_socket_start(const char *backend_url, const char *device_id, const char *device_credential)
{
    if (!s_lock) {
        s_lock = xSemaphoreCreateMutex();
        ESP_RETURN_ON_FALSE(s_lock, ESP_ERR_NO_MEM, TAG, "lock");
    }
    ESP_RETURN_ON_FALSE(!s_client, ESP_ERR_INVALID_STATE, TAG, "already started");
    ESP_RETURN_ON_FALSE(backend_url[0] && device_id[0] && device_credential[0], ESP_ERR_INVALID_ARG, TAG,
                        "backend URL, device ID or device credential not configured");

    char url[256];
    ESP_RETURN_ON_ERROR(build_url(backend_url, device_id, url, sizeof(url)), TAG, "device socket URL");

    char headers[160];
    ESP_RETURN_ON_FALSE(snprintf(headers, sizeof(headers), "Authorization: Device %s\r\n", device_credential) <
                        (int)sizeof(headers), ESP_ERR_INVALID_SIZE, TAG, "credential too long");

    esp_websocket_client_config_t cfg = {
        .uri = url,
        .subprotocol = SUBPROTOCOL,
        .headers = headers,
        .crt_bundle_attach = esp_crt_bundle_attach,
        .reconnect_timeout_ms = RETRY_MIN_MS,
        .enable_close_reconnect = true,
        .network_timeout_ms = 10000,
        .buffer_size = 1024,
        .task_prio = 4,
        .task_stack = 6144,
    };
    s_client = esp_websocket_client_init(&cfg);
    mbedtls_platform_zeroize(headers, sizeof(headers));
    ESP_RETURN_ON_FALSE(s_client, ESP_ERR_NO_MEM, TAG, "client init");
    esp_websocket_register_events(s_client, WEBSOCKET_EVENT_ANY, on_event, NULL);

    if (!s_heartbeat_started) {
        ESP_RETURN_ON_FALSE(xTaskCreate(heartbeat_task, "device_hb", 3072, NULL, 3, NULL) == pdPASS,
                            ESP_ERR_NO_MEM, TAG, "heartbeat task");
        s_heartbeat_started = true;
    }
    s_state = DEVICE_SOCKET_CONNECTING;
    s_retry_ms = RETRY_MIN_MS;
    s_pending_retry_ms = 0;
    ESP_LOGI(TAG, "Connecting device socket for device %s", device_id);
    return esp_websocket_client_start(s_client);
}

void device_socket_stop(void)
{
    if (!s_lock) {
        return;
    }
    xSemaphoreTake(s_lock, portMAX_DELAY);
    if (s_client) {
        esp_websocket_client_destroy(s_client);
        s_client = NULL;
    }
    s_state = DEVICE_SOCKET_STOPPED;
    xSemaphoreGive(s_lock);
}

device_socket_state_t device_socket_state(void)
{
    return s_state;
}

bool device_socket_online(void)
{
    return s_state == DEVICE_SOCKET_ONLINE;
}
