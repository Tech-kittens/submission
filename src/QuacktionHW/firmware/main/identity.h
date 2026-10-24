#pragma once

#include "esp_err.h"

#define IDENTITY_ID_LEN       18
#define IDENTITY_SUFFIX_LEN   4

/* "quack-" plus 12 hex characters of SHA-256 over the factory MAC in eFuse. */
esp_err_t identity_device_identifier(char out[IDENTITY_ID_LEN + 1]);
/* Last 4 characters of the device identifier, uppercase, for the BLE name. */
esp_err_t identity_display_suffix(char out[IDENTITY_SUFFIX_LEN + 1]);
