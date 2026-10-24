#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "freertos/FreeRTOS.h"
#include "esp_err.h"

/* Starts the ESP-SR front end (echo cancelling + WakeNet + VAD). The wake word starts disabled. */
esp_err_t audio_in_start(void);

void audio_in_wake_enable(bool enable);
/* Blocks until the wake word is heard. From that moment the cleaned mic audio is buffered for
 * audio_in_stream_read until audio_in_stream_end. */
bool audio_in_wait_wake(TickType_t wait);

size_t audio_in_stream_read(int16_t *dst, size_t max_samples, TickType_t wait);
void audio_in_stream_end(void);

/* Milliseconds since the voice activity detector last heard speech. */
uint32_t audio_in_ms_since_speech(void);

/* Level of the latest cleaned output frame in dBFS. */
float audio_in_level_db(void);
