#pragma once

#include <stddef.h>
#include "freertos/FreeRTOS.h"

/* Single-producer, single-consumer byte ring in PSRAM that either side can clear at any time. */
typedef struct pcm_ring pcm_ring_t;

pcm_ring_t *pcm_ring_create(size_t capacity);
size_t pcm_ring_write(pcm_ring_t *r, const void *data, size_t len, TickType_t wait);
/* Returns an even number of bytes so callers always get whole 16-bit samples. */
size_t pcm_ring_read(pcm_ring_t *r, void *dst, size_t len, TickType_t wait);
size_t pcm_ring_clear(pcm_ring_t *r);
size_t pcm_ring_used(pcm_ring_t *r);
