#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"
#include "session.h"

typedef struct session_audio session_audio_t;

/* Called from the WebSocket task. on_audio may receive any byte count, not only whole samples. */
typedef struct {
    void (*on_audio)(const uint8_t *data, size_t len, void *ctx);
    void (*on_clear)(void *ctx);
    void *ctx;
} session_audio_callbacks_t;

/* Connects with the session's one-time credential and returns once audio.ready arrives. */
esp_err_t session_audio_open(const session_t *s, const session_audio_callbacks_t *cb, session_audio_t **out);
esp_err_t session_audio_send(session_audio_t *a, const int16_t *pcm, size_t samples);
bool session_audio_alive(session_audio_t *a);
/* Sends session.end, waits for session.ended, then closes and frees the connection. */
esp_err_t session_audio_end(session_audio_t *a);
