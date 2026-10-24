#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "cJSON.h"
#include "esp_crt_bundle.h"
#include "esp_log.h"
#include "esp_websocket_client.h"
#include "mbedtls/platform_util.h"
#include "session_audio.h"

#define SUBPROTOCOL          "quacktion.audio.v1"
#define AUDIO_FORMAT         "{\"codec\":\"pcm_16000\",\"sampleRate\":16000,\"channels\":1}"
#define TEXT_MAX             4096
#define STARTED_TIMEOUT_MS   20000
#define READY_TIMEOUT_MS     10000
#define ENDED_TIMEOUT_MS     10000
#define SEND_TIMEOUT_MS      5000

#define BIT_STARTED   BIT0
#define BIT_READY     BIT1
#define BIT_ENDED     BIT2
#define BIT_FAILED    BIT3
#define BIT_CLOSED    BIT4

#define WS_OP_CONT    0x0
#define WS_OP_TEXT    0x1
#define WS_OP_BINARY  0x2

static const char *TAG = "session_ws";

struct session_audio {
    esp_websocket_client_handle_t client;
    session_audio_callbacks_t cb;
    EventGroupHandle_t events;
    char text[TEXT_MAX + 1];
    size_t text_len;
    bool text_overflow;
    uint8_t message_op;
    size_t bytes_sent;
    size_t bytes_received;
    size_t clears;
};

static const char *payload_string(const cJSON *root, const char *name)
{
    const cJSON *item = cJSON_GetObjectItemCaseSensitive(root, name);
    if (!cJSON_IsString(item)) {
        const cJSON *payload = cJSON_GetObjectItemCaseSensitive(root, "payload");
        item = cJSON_GetObjectItemCaseSensitive(payload, name);
    }
    return cJSON_IsString(item) ? item->valuestring : NULL;
}

static void handle_control(session_audio_t *a)
{
    cJSON *root = cJSON_Parse(a->text);
    const cJSON *type_item = cJSON_GetObjectItemCaseSensitive(root, "type");
    if (!cJSON_IsString(type_item)) {
        ESP_LOGW(TAG, "Ignoring control without a type");
        cJSON_Delete(root);
        return;
    }
    const char *type = type_item->valuestring;
    const char *code = payload_string(root, "code");

    if (strcmp(type, "session.started") == 0) {
        const char *status = payload_string(root, "status");
        ESP_LOGI(TAG, "session.started (status %s)", status ? status : "?");
        xEventGroupSetBits(a->events, BIT_STARTED);
    } else if (strcmp(type, "audio.ready") == 0) {
        ESP_LOGI(TAG, "audio.ready");
        xEventGroupSetBits(a->events, BIT_READY);
    } else if (strcmp(type, "audio.clear") == 0) {
        a->clears++;
        ESP_LOGI(TAG, "audio.clear");
        if (a->cb.on_clear) {
            a->cb.on_clear(a->cb.ctx);
        }
    } else if (strcmp(type, "session.ended") == 0) {
        ESP_LOGI(TAG, "session.ended");
        xEventGroupSetBits(a->events, BIT_ENDED);
    } else if (strcmp(type, "session.start_failed") == 0 || strcmp(type, "session.failed") == 0 ||
               strcmp(type, "error") == 0) {
        ESP_LOGE(TAG, "%s (code %s)", type, code ? code : "none");
        xEventGroupSetBits(a->events, BIT_FAILED);
    } else {
        ESP_LOGW(TAG, "Unhandled control %s", type);
    }
    cJSON_Delete(root);
}

static void on_data(session_audio_t *a, const esp_websocket_event_data_t *d)
{
    if (d->op_code == WS_OP_TEXT || d->op_code == WS_OP_BINARY) {
        a->message_op = d->op_code;
        if (d->payload_offset == 0) {
            a->text_len = 0;
            a->text_overflow = false;
        }
    } else if (d->op_code != WS_OP_CONT) {
        return;
    }

    if (a->message_op == WS_OP_BINARY) {
        a->bytes_received += d->data_len;
        if (a->cb.on_audio && d->data_len > 0) {
            a->cb.on_audio((const uint8_t *)d->data_ptr, d->data_len, a->cb.ctx);
        }
        return;
    }
    if (a->message_op != WS_OP_TEXT) {
        return;
    }

    if (a->text_len + d->data_len > TEXT_MAX) {
        a->text_overflow = true;
    } else {
        memcpy(a->text + a->text_len, d->data_ptr, d->data_len);
        a->text_len += d->data_len;
    }
    bool frame_done = d->payload_offset + d->data_len >= d->payload_len;
    if (frame_done && d->fin) {
        if (a->text_overflow) {
            ESP_LOGW(TAG, "Dropped oversized control message");
        } else {
            a->text[a->text_len] = '\0';
            handle_control(a);
        }
        a->text_len = 0;
        a->text_overflow = false;
    }
}

static void on_event(void *arg, esp_event_base_t base, int32_t id, void *event_data)
{
    session_audio_t *a = arg;
    const esp_websocket_event_data_t *d = event_data;
    switch (id) {
    case WEBSOCKET_EVENT_CONNECTED:
        ESP_LOGI(TAG, "Connected");
        break;
    case WEBSOCKET_EVENT_DATA:
        on_data(a, d);
        break;
    case WEBSOCKET_EVENT_ERROR:
        ESP_LOGE(TAG, "Socket error (type %d, handshake status %d)",
                 d->error_handle.error_type, d->error_handle.esp_ws_handshake_status_code);
        xEventGroupSetBits(a->events, BIT_CLOSED);
        break;
    case WEBSOCKET_EVENT_DISCONNECTED:
    case WEBSOCKET_EVENT_CLOSED:
        ESP_LOGI(TAG, "Closed");
        xEventGroupSetBits(a->events, BIT_CLOSED);
        break;
    default:
        break;
    }
}

static EventBits_t wait_for(session_audio_t *a, EventBits_t want, uint32_t timeout_ms)
{
    return xEventGroupWaitBits(a->events, want | BIT_FAILED | BIT_CLOSED, pdFALSE, pdFALSE,
                               pdMS_TO_TICKS(timeout_ms));
}

static esp_err_t send_text(session_audio_t *a, const char *json)
{
    int len = strlen(json);
    int sent = esp_websocket_client_send_text(a->client, json, len, pdMS_TO_TICKS(SEND_TIMEOUT_MS));
    return sent == len ? ESP_OK : ESP_FAIL;
}

static void destroy(session_audio_t *a)
{
    if (a->client) {
        esp_websocket_client_destroy(a->client);
    }
    if (a->events) {
        vEventGroupDelete(a->events);
    }
    free(a);
}

esp_err_t session_audio_open(const session_t *s, const session_audio_callbacks_t *cb, session_audio_t **out)
{
    *out = NULL;
    session_audio_t *a = calloc(1, sizeof(*a));
    if (!a) {
        return ESP_ERR_NO_MEM;
    }
    if (cb) {
        a->cb = *cb;
    }
    a->events = xEventGroupCreate();

    char headers[192];
    if (snprintf(headers, sizeof(headers), "Authorization: Session %s\r\n", s->connection_credential) >=
        (int)sizeof(headers)) {
        destroy(a);
        return ESP_ERR_INVALID_SIZE;
    }

    esp_websocket_client_config_t cfg = {
        .uri = s->web_socket_url,
        .subprotocol = SUBPROTOCOL,
        .headers = headers,
        .crt_bundle_attach = esp_crt_bundle_attach,
        .disable_auto_reconnect = true,
        .buffer_size = 2048,
        .task_prio = 5,
        .task_stack = 6144,
        .network_timeout_ms = 10000,
    };
    a->client = esp_websocket_client_init(&cfg);
    mbedtls_platform_zeroize(headers, sizeof(headers));
    if (!a->events || !a->client) {
        destroy(a);
        return ESP_ERR_NO_MEM;
    }
    esp_websocket_register_events(a->client, WEBSOCKET_EVENT_ANY, on_event, a);

    ESP_LOGI(TAG, "Opening audio socket for session %s", s->session_id);
    esp_err_t err = esp_websocket_client_start(a->client);
    EventBits_t bits = err == ESP_OK ? wait_for(a, BIT_STARTED, STARTED_TIMEOUT_MS) : 0;
    if (!(bits & BIT_STARTED) || (bits & (BIT_FAILED | BIT_CLOSED))) {
        ESP_LOGE(TAG, "No session.started");
        destroy(a);
        return ESP_FAIL;
    }

    err = send_text(a, "{\"version\":1,\"type\":\"audio.start\",\"audio\":{\"protocolVersion\":1,"
                       "\"input\":" AUDIO_FORMAT ",\"output\":" AUDIO_FORMAT "}}");
    bits = err == ESP_OK ? wait_for(a, BIT_READY, READY_TIMEOUT_MS) : 0;
    if (!(bits & BIT_READY) || (bits & (BIT_FAILED | BIT_CLOSED))) {
        ESP_LOGE(TAG, "No audio.ready");
        destroy(a);
        return ESP_FAIL;
    }

    *out = a;
    return ESP_OK;
}

bool session_audio_alive(session_audio_t *a)
{
    return !(xEventGroupGetBits(a->events) & (BIT_FAILED | BIT_CLOSED | BIT_ENDED));
}

esp_err_t session_audio_send(session_audio_t *a, const int16_t *pcm, size_t samples)
{
    if (!session_audio_alive(a)) {
        return ESP_ERR_INVALID_STATE;
    }
    int len = samples * sizeof(int16_t);
    int sent = esp_websocket_client_send_bin(a->client, (const char *)pcm, len, pdMS_TO_TICKS(SEND_TIMEOUT_MS));
    if (sent != len) {
        ESP_LOGE(TAG, "Audio send failed");
        return ESP_FAIL;
    }
    a->bytes_sent += len;
    return ESP_OK;
}

esp_err_t session_audio_end(session_audio_t *a)
{
    esp_err_t err = ESP_FAIL;
    if (session_audio_alive(a)) {
        char request_id[37];
        session_new_request_id(request_id);
        char msg[96];
        snprintf(msg, sizeof(msg), "{\"version\":1,\"type\":\"session.end\",\"requestId\":\"%s\"}", request_id);
        if (send_text(a, msg) == ESP_OK && (wait_for(a, BIT_ENDED, ENDED_TIMEOUT_MS) & BIT_ENDED)) {
            err = ESP_OK;
        } else {
            ESP_LOGE(TAG, "No session.ended");
        }
    }
    ESP_LOGI(TAG, "Sent %u bytes, received %u bytes, %u audio.clear",
             (unsigned)a->bytes_sent, (unsigned)a->bytes_received, (unsigned)a->clears);
    if (esp_websocket_client_is_connected(a->client)) {
        esp_websocket_client_close(a->client, pdMS_TO_TICKS(2000));
    }
    destroy(a);
    return err;
}
