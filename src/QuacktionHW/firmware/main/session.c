#include <stdio.h>
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "cJSON.h"
#include "esp_crt_bundle.h"
#include "esp_http_client.h"
#include "esp_log.h"
#include "esp_random.h"
#include "mbedtls/platform_util.h"
#include "session.h"

#define START_PATH        "/api/sessions/start"
#define MAX_ATTEMPTS      3
#define TIMEOUT_MS        15000
#define RESPONSE_MAX      2048

static const char *TAG = "session";

void session_new_request_id(char out[37])
{
    uint8_t b[16];
    esp_fill_random(b, sizeof(b));
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    snprintf(out, 37, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
             b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
             b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
}

static bool copy_string(const cJSON *root, const char *name, char *dst, size_t dst_len)
{
    const cJSON *item = cJSON_GetObjectItemCaseSensitive(root, name);
    if (!cJSON_IsString(item) || strlen(item->valuestring) >= dst_len) {
        return false;
    }
    strcpy(dst, item->valuestring);
    return true;
}

static void log_error_body(int status, const char *body)
{
    cJSON *root = cJSON_Parse(body);
    const cJSON *code = root ? cJSON_GetObjectItemCaseSensitive(root, "code") : NULL;
    const cJSON *title = root ? cJSON_GetObjectItemCaseSensitive(root, "title") : NULL;
    ESP_LOGE(TAG, "Start failed: HTTP %d%s%s", status,
             cJSON_IsString(code) ? ", code " : cJSON_IsString(title) ? ", " : "",
             cJSON_IsString(code) ? code->valuestring : cJSON_IsString(title) ? title->valuestring : "");
    cJSON_Delete(root);
}

static esp_err_t parse_ready(const char *body, session_t *out)
{
    cJSON *root = cJSON_Parse(body);
    if (!root) {
        ESP_LOGE(TAG, "Start response is not JSON");
        return ESP_ERR_INVALID_RESPONSE;
    }

    esp_err_t err = ESP_OK;
    char status[16] = "";
    const cJSON *expires = cJSON_GetObjectItemCaseSensitive(root, "expiresInSeconds");
    if (!copy_string(root, "sessionId", out->session_id, sizeof(out->session_id)) ||
        !copy_string(root, "status", status, sizeof(status)) ||
        !copy_string(root, "webSocketUrl", out->web_socket_url, sizeof(out->web_socket_url)) ||
        !copy_string(root, "connectionCredential", out->connection_credential, sizeof(out->connection_credential))) {
        ESP_LOGE(TAG, "Start response is missing fields");
        err = ESP_ERR_INVALID_RESPONSE;
    } else if (strcmp(status, "Ready") != 0) {
        ESP_LOGE(TAG, "Session %s has status %s, expected Ready", out->session_id, status);
        err = ESP_ERR_INVALID_STATE;
    } else {
        out->expires_in_seconds = cJSON_IsNumber(expires) ? expires->valueint : 0;
    }

    cJSON *cred = cJSON_GetObjectItemCaseSensitive(root, "connectionCredential");
    if (cJSON_IsString(cred)) {
        mbedtls_platform_zeroize(cred->valuestring, strlen(cred->valuestring));
    }
    cJSON_Delete(root);
    return err;
}

static esp_err_t post_once(const session_config_t *cfg, const char *url, const char *body,
                           session_t *out, bool *retryable)
{
    *retryable = false;
    char auth[160];
    if (snprintf(auth, sizeof(auth), "Device %s", cfg->device_credential) >= (int)sizeof(auth)) {
        return ESP_ERR_INVALID_SIZE;
    }

    esp_http_client_config_t http = {
        .url = url,
        .method = HTTP_METHOD_POST,
        .timeout_ms = TIMEOUT_MS,
        .crt_bundle_attach = esp_crt_bundle_attach,
        .disable_auto_redirect = true,
    };
    esp_http_client_handle_t client = esp_http_client_init(&http);
    if (!client) {
        mbedtls_platform_zeroize(auth, sizeof(auth));
        return ESP_ERR_NO_MEM;
    }
    esp_http_client_set_header(client, "Authorization", auth);
    esp_http_client_set_header(client, "Idempotency-Key", out->idempotency_key);
    esp_http_client_set_header(client, "Content-Type", "application/json");
    esp_http_client_set_header(client, "Accept", "application/json");
    mbedtls_platform_zeroize(auth, sizeof(auth));

    char *response = calloc(1, RESPONSE_MAX + 1);
    esp_err_t err = response ? ESP_OK : ESP_ERR_NO_MEM;
    int body_len = strlen(body);

    if (err == ESP_OK) {
        err = esp_http_client_open(client, body_len);
    }
    if (err == ESP_OK && esp_http_client_write(client, body, body_len) != body_len) {
        err = ESP_FAIL;
    }
    if (err == ESP_OK && esp_http_client_fetch_headers(client) < 0) {
        err = ESP_FAIL;
    }

    int total = 0;
    if (err == ESP_OK) {
        int n = 0;
        while (total < RESPONSE_MAX && (n = esp_http_client_read(client, response + total, RESPONSE_MAX - total)) > 0) {
            total += n;
        }
        if (n < 0) {
            err = ESP_FAIL;
        }
    }

    if (err != ESP_OK && err != ESP_ERR_NO_MEM) {
        ESP_LOGW(TAG, "Request to backend failed: %s", esp_err_to_name(err));
        *retryable = true;
    } else if (err == ESP_OK) {
        int status = esp_http_client_get_status_code(client);
        if (status == 200) {
            err = parse_ready(response, out);
        } else {
            log_error_body(status, response);
            *retryable = status >= 500;
            err = ESP_FAIL;
        }
    }

    if (response) {
        mbedtls_platform_zeroize(response, total);
        free(response);
    }
    esp_http_client_cleanup(client);
    return err;
}

esp_err_t session_start(const session_config_t *cfg, session_t *out)
{
    memset(out, 0, sizeof(*out));
    if (!cfg->backend_url[0] || !cfg->device_id[0] || !cfg->device_credential[0]) {
        ESP_LOGE(TAG, "Backend URL, device ID or device credential not configured");
        return ESP_ERR_INVALID_ARG;
    }

    size_t base_len = strlen(cfg->backend_url);
    while (base_len && cfg->backend_url[base_len - 1] == '/') {
        base_len--;
    }
    char url[256];
    if (snprintf(url, sizeof(url), "%.*s" START_PATH, (int)base_len, cfg->backend_url) >= (int)sizeof(url)) {
        return ESP_ERR_INVALID_SIZE;
    }

    cJSON *req = cJSON_CreateObject();
    cJSON_AddStringToObject(req, "deviceId", cfg->device_id);
    cJSON_AddStringToObject(req, "name", "Quacktion duck session");
    char *body = cJSON_PrintUnformatted(req);
    cJSON_Delete(req);
    if (!body) {
        return ESP_ERR_NO_MEM;
    }

    session_new_request_id(out->idempotency_key);
    ESP_LOGI(TAG, "Starting session (key %s)", out->idempotency_key);

    esp_err_t err = ESP_FAIL;
    for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
        bool retryable;
        err = post_once(cfg, url, body, out, &retryable);
        if (err == ESP_OK || !retryable || attempt == MAX_ATTEMPTS) {
            break;
        }
        ESP_LOGW(TAG, "Retrying with the same key (attempt %d of %d)", attempt + 1, MAX_ATTEMPTS);
        vTaskDelay(pdMS_TO_TICKS(1000 * attempt));
    }
    cJSON_free(body);

    if (err == ESP_OK) {
        ESP_LOGI(TAG, "Session %s Ready, credential expires in %d s", out->session_id, out->expires_in_seconds);
    } else {
        session_clear(out);
    }
    return err;
}

void session_clear(session_t *s)
{
    mbedtls_platform_zeroize(s, sizeof(*s));
}
