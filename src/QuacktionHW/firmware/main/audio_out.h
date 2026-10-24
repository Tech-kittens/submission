#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"

esp_err_t audio_out_play_pcm(const int16_t *samples, size_t count);
esp_err_t audio_out_play_wav(const uint8_t *wav, size_t len);
esp_err_t audio_out_quack(void);

/* Streamed mono 16-bit PCM at BOARD_SAMPLE_RATE. Do not mix with audio_out_play_* while a stream is open. */
esp_err_t audio_out_stream_begin(void);
esp_err_t audio_out_stream_write(const void *pcm, size_t bytes);
size_t audio_out_stream_clear(void);
bool audio_out_stream_busy(void);
/* Waits up to drain_ms for queued audio to finish, then stops and turns the amp off. */
esp_err_t audio_out_stream_end(uint32_t drain_ms);
