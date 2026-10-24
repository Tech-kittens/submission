#include <string.h>
#include "esp_check.h"
#include "esp_log.h"
#include "mbedtls/platform_util.h"
#include "nvs.h"
#include "nvs_flash.h"
#include "sdkconfig.h"
#include "config_store.h"

#define NAMESPACE       "quack"
#define KEY_SSID        "wifi_ssid"
#define KEY_PASSWORD    "wifi_pass"
#define KEY_DEVICE_ID   "dev_id"
#define KEY_CREDENTIAL  "dev_cred"
#define KEY_PEND_ID     "pend_id"
#define KEY_PEND_CRED   "pend_cred"
#define KEY_SETUP       "setup_req"

static const char *TAG = "config";

esp_err_t config_store_init(void)
{
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_RETURN_ON_ERROR(nvs_flash_erase(), TAG, "nvs erase");
        err = nvs_flash_init();
    }
    return err;
}

static bool get_str(nvs_handle_t h, const char *key, char *out, size_t out_size)
{
    size_t len = out_size;
    return nvs_get_str(h, key, out, &len) == ESP_OK && len > 1;
}

void config_load(quack_config_t *out)
{
    memset(out, 0, sizeof(*out));
    nvs_handle_t h;
    bool have_nvs = nvs_open(NAMESPACE, NVS_READONLY, &h) == ESP_OK;

    size_t ssid_len = sizeof(out->ssid);
    if (have_nvs && nvs_get_blob(h, KEY_SSID, out->ssid, &ssid_len) == ESP_OK && ssid_len > 0) {
        out->ssid_len = ssid_len;
        get_str(h, KEY_PASSWORD, out->password, sizeof(out->password));
    } else if (strlen(CONFIG_QUACK_WIFI_SSID) <= CONFIG_SSID_MAX) {
        out->ssid_len = strlen(CONFIG_QUACK_WIFI_SSID);
        memcpy(out->ssid, CONFIG_QUACK_WIFI_SSID, out->ssid_len);
        strlcpy(out->password, CONFIG_QUACK_WIFI_PASSWORD, sizeof(out->password));
    }

    if (!(have_nvs && get_str(h, KEY_DEVICE_ID, out->device_id, sizeof(out->device_id)) &&
          get_str(h, KEY_CREDENTIAL, out->credential, sizeof(out->credential)))) {
        strlcpy(out->device_id, CONFIG_QUACK_DEVICE_ID, sizeof(out->device_id));
        strlcpy(out->credential, CONFIG_QUACK_DEVICE_CREDENTIAL, sizeof(out->credential));
    }
    if (have_nvs) {
        nvs_close(h);
    }
}

bool config_has_wifi(const quack_config_t *c)
{
    return c->ssid_len > 0;
}

bool config_has_backend(const quack_config_t *c)
{
    return c->device_id[0] && c->credential[0];
}

void config_wipe(quack_config_t *c)
{
    mbedtls_platform_zeroize(c, sizeof(*c));
}

static esp_err_t open_rw(nvs_handle_t *h)
{
    return nvs_open(NAMESPACE, NVS_READWRITE, h);
}

esp_err_t config_save_wifi(const uint8_t *ssid, size_t ssid_len, const char *password)
{
    nvs_handle_t h;
    ESP_RETURN_ON_ERROR(open_rw(&h), TAG, "open");
    esp_err_t err = nvs_set_blob(h, KEY_SSID, ssid, ssid_len);
    if (err == ESP_OK) {
        err = nvs_set_str(h, KEY_PASSWORD, password);
    }
    if (err == ESP_OK) {
        err = nvs_commit(h);
    }
    nvs_close(h);
    return err;
}

esp_err_t config_save_backend_pending(const char *device_id, const char *credential)
{
    nvs_handle_t h;
    ESP_RETURN_ON_ERROR(open_rw(&h), TAG, "open");
    esp_err_t err = nvs_set_str(h, KEY_PEND_ID, device_id);
    if (err == ESP_OK) {
        err = nvs_set_str(h, KEY_PEND_CRED, credential);
    }
    if (err == ESP_OK) {
        err = nvs_commit(h);
    }
    nvs_close(h);
    return err;
}

bool config_load_backend_pending(quack_config_t *out)
{
    nvs_handle_t h;
    if (nvs_open(NAMESPACE, NVS_READONLY, &h) != ESP_OK) {
        return false;
    }
    bool ok = get_str(h, KEY_PEND_ID, out->device_id, sizeof(out->device_id)) &&
              get_str(h, KEY_PEND_CRED, out->credential, sizeof(out->credential));
    nvs_close(h);
    return ok;
}

esp_err_t config_promote_backend_pending(void)
{
    quack_config_t pending = {0};
    ESP_RETURN_ON_FALSE(config_load_backend_pending(&pending), ESP_ERR_NOT_FOUND, TAG, "nothing pending");
    nvs_handle_t h;
    esp_err_t err = open_rw(&h);
    if (err == ESP_OK) {
        err = nvs_set_str(h, KEY_DEVICE_ID, pending.device_id);
        if (err == ESP_OK) {
            err = nvs_set_str(h, KEY_CREDENTIAL, pending.credential);
        }
        if (err == ESP_OK) {
            nvs_erase_key(h, KEY_PEND_ID);
            nvs_erase_key(h, KEY_PEND_CRED);
            err = nvs_commit(h);
        }
        nvs_close(h);
    }
    config_wipe(&pending);
    return err;
}

esp_err_t config_discard_backend_pending(void)
{
    nvs_handle_t h;
    ESP_RETURN_ON_ERROR(open_rw(&h), TAG, "open");
    nvs_erase_key(h, KEY_PEND_ID);
    nvs_erase_key(h, KEY_PEND_CRED);
    esp_err_t err = nvs_commit(h);
    nvs_close(h);
    return err;
}

esp_err_t config_request_setup(void)
{
    nvs_handle_t h;
    ESP_RETURN_ON_ERROR(open_rw(&h), TAG, "open");
    esp_err_t err = nvs_set_u8(h, KEY_SETUP, 1);
    if (err == ESP_OK) {
        err = nvs_commit(h);
    }
    nvs_close(h);
    return err;
}

bool config_take_setup_request(void)
{
    nvs_handle_t h;
    if (open_rw(&h) != ESP_OK) {
        return false;
    }
    uint8_t requested = 0;
    bool found = nvs_get_u8(h, KEY_SETUP, &requested) == ESP_OK && requested;
    if (found) {
        nvs_erase_key(h, KEY_SETUP);
        nvs_commit(h);
    }
    nvs_close(h);
    return found;
}
