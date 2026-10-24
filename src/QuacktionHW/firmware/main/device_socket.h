#pragma once

#include <stdbool.h>
#include "esp_err.h"

typedef enum {
    DEVICE_SOCKET_STOPPED,
    DEVICE_SOCKET_CONNECTING,
    DEVICE_SOCKET_ONLINE,
    DEVICE_SOCKET_REJECTED,
} device_socket_state_t;

/* Keeps the backend device socket (/ws/devices/{deviceId}) open for online status: sends a
 * heartbeat every 15 s and reconnects with backoff when it drops. */
esp_err_t device_socket_start(const char *backend_url, const char *device_id, const char *device_credential);
void device_socket_stop(void);
device_socket_state_t device_socket_state(void);
bool device_socket_online(void);
