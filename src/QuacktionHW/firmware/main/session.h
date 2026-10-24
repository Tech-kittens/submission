#pragma once

#include "esp_err.h"

typedef struct {
    const char *backend_url;
    const char *device_id;
    const char *device_credential;
} session_config_t;

typedef struct {
    char idempotency_key[37];
    char session_id[64];
    char web_socket_url[256];
    char connection_credential[128];
    int expires_in_seconds;
} session_t;

void session_new_request_id(char out[37]);
esp_err_t session_start(const session_config_t *cfg, session_t *out);
void session_clear(session_t *s);
