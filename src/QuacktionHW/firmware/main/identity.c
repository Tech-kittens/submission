#include <ctype.h>
#include <stdio.h>
#include <string.h>
#include "esp_check.h"
#include "esp_mac.h"
#include "psa/crypto.h"
#include "identity.h"

#define HASH_HEX_CHARS  12

static const char *TAG = "identity";

esp_err_t identity_device_identifier(char out[IDENTITY_ID_LEN + 1])
{
    uint8_t mac[6];
    ESP_RETURN_ON_ERROR(esp_efuse_mac_get_default(mac), TAG, "factory MAC");
    ESP_RETURN_ON_FALSE(psa_crypto_init() == PSA_SUCCESS, ESP_FAIL, TAG, "psa init");

    uint8_t hash[32];
    size_t hash_len;
    ESP_RETURN_ON_FALSE(psa_hash_compute(PSA_ALG_SHA_256, mac, sizeof(mac), hash, sizeof(hash), &hash_len) ==
                        PSA_SUCCESS, ESP_FAIL, TAG, "hash");

    int n = snprintf(out, IDENTITY_ID_LEN + 1, "quack-");
    for (int i = 0; i < HASH_HEX_CHARS / 2; i++) {
        n += snprintf(out + n, IDENTITY_ID_LEN + 1 - n, "%02x", hash[i]);
    }
    return ESP_OK;
}

esp_err_t identity_display_suffix(char out[IDENTITY_SUFFIX_LEN + 1])
{
    char id[IDENTITY_ID_LEN + 1];
    ESP_RETURN_ON_ERROR(identity_device_identifier(id), TAG, "identifier");
    for (int i = 0; i < IDENTITY_SUFFIX_LEN; i++) {
        out[i] = (char)toupper((unsigned char)id[IDENTITY_ID_LEN - IDENTITY_SUFFIX_LEN + i]);
    }
    out[IDENTITY_SUFFIX_LEN] = '\0';
    return ESP_OK;
}
