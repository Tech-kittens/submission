#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"

#define CONFIG_SSID_MAX         32
#define CONFIG_PASSWORD_MAX     64
#define CONFIG_DEVICE_ID_MAX    63
#define CONFIG_CREDENTIAL_LEN   64

typedef struct {
    uint8_t ssid[CONFIG_SSID_MAX];
    size_t ssid_len;
    char password[CONFIG_PASSWORD_MAX + 1];
    char device_id[CONFIG_DEVICE_ID_MAX + 1];
    char credential[CONFIG_CREDENTIAL_LEN + 1];
} quack_config_t;

esp_err_t config_store_init(void);

/* Saved values, with the menuconfig values as fallback for anything not saved. */
void config_load(quack_config_t *out);
bool config_has_wifi(const quack_config_t *c);
bool config_has_backend(const quack_config_t *c);
void config_wipe(quack_config_t *c);

esp_err_t config_save_wifi(const uint8_t *ssid, size_t ssid_len, const char *password);

/* Backend credentials installed during setup stay pending until the backend has accepted them. */
esp_err_t config_save_backend_pending(const char *device_id, const char *credential);
bool config_load_backend_pending(quack_config_t *out);
esp_err_t config_promote_backend_pending(void);
esp_err_t config_discard_backend_pending(void);

esp_err_t config_request_setup(void);
/* Returns true once if a setup request was recorded, and clears it. */
bool config_take_setup_request(void);
