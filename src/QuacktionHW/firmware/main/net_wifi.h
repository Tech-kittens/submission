#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"
#include "esp_wifi_types.h"

typedef enum {
    NET_WIFI_IDLE,
    NET_WIFI_CONNECTING,
    NET_WIFI_CONNECTED,
    NET_WIFI_FAILED,
} net_wifi_state_t;

typedef enum {
    NET_WIFI_FAIL_NONE,
    NET_WIFI_FAIL_NOT_FOUND,
    NET_WIFI_FAIL_WRONG_PASSWORD,
    NET_WIFI_FAIL_OTHER,
} net_wifi_fail_t;

/* Starts the station with nothing to join. Wi-Fi settings are kept in RAM only. */
esp_err_t net_wifi_init(void);

/* Joins a network, replacing any current one. With keep_retrying false, a wrong password or a
 * missing network ends the attempt in NET_WIFI_FAILED instead of retrying forever. */
esp_err_t net_wifi_connect(const uint8_t *ssid, size_t ssid_len, const char *password, bool keep_retrying);
void net_wifi_disconnect(void);
net_wifi_state_t net_wifi_state(void);
net_wifi_fail_t net_wifi_fail_reason(void);
bool net_wifi_wait_connected(uint32_t timeout_ms);

esp_err_t net_wifi_scan_start(void);
bool net_wifi_scan_done(void);
/* Copies up to max results of the last finished scan. */
size_t net_wifi_scan_results(wifi_ap_record_t *out, size_t max);
