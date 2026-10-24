#include <stdlib.h>
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "freertos/semphr.h"
#include "esp_heap_caps.h"
#include "pcm_ring.h"

#define BIT_DATA  BIT0
#define BIT_SPACE BIT1

struct pcm_ring {
    uint8_t *buf;
    size_t capacity;
    size_t head;
    size_t used;
    SemaphoreHandle_t lock;
    EventGroupHandle_t events;
};

pcm_ring_t *pcm_ring_create(size_t capacity)
{
    pcm_ring_t *r = calloc(1, sizeof(*r));
    if (!r) {
        return NULL;
    }
    r->buf = heap_caps_malloc(capacity, MALLOC_CAP_SPIRAM);
    r->lock = xSemaphoreCreateMutex();
    r->events = xEventGroupCreate();
    if (!r->buf || !r->lock || !r->events) {
        heap_caps_free(r->buf);
        if (r->lock) {
            vSemaphoreDelete(r->lock);
        }
        if (r->events) {
            vEventGroupDelete(r->events);
        }
        free(r);
        return NULL;
    }
    r->capacity = capacity;
    return r;
}

static size_t copy_in(pcm_ring_t *r, const uint8_t *src, size_t len)
{
    size_t n = r->capacity - r->used;
    n = len < n ? len : n;
    size_t tail = (r->head + r->used) % r->capacity;
    size_t first = r->capacity - tail < n ? r->capacity - tail : n;
    memcpy(r->buf + tail, src, first);
    memcpy(r->buf, src + first, n - first);
    r->used += n;
    return n;
}

static size_t copy_out(pcm_ring_t *r, uint8_t *dst, size_t len)
{
    size_t n = (r->used < len ? r->used : len) & ~(size_t)1;
    size_t first = r->capacity - r->head < n ? r->capacity - r->head : n;
    memcpy(dst, r->buf + r->head, first);
    memcpy(dst + first, r->buf, n - first);
    r->head = (r->head + n) % r->capacity;
    r->used -= n;
    return n;
}

size_t pcm_ring_write(pcm_ring_t *r, const void *data, size_t len, TickType_t wait)
{
    const uint8_t *src = data;
    size_t done = 0;
    TickType_t start = xTaskGetTickCount();
    while (true) {
        xSemaphoreTake(r->lock, portMAX_DELAY);
        size_t n = copy_in(r, src + done, len - done);
        if (done + n < len) {
            xEventGroupClearBits(r->events, BIT_SPACE);
        }
        xSemaphoreGive(r->lock);
        if (n) {
            xEventGroupSetBits(r->events, BIT_DATA);
        }
        done += n;
        TickType_t elapsed = xTaskGetTickCount() - start;
        if (done == len || elapsed >= wait) {
            return done;
        }
        xEventGroupWaitBits(r->events, BIT_SPACE, pdFALSE, pdTRUE, wait - elapsed);
    }
}

size_t pcm_ring_read(pcm_ring_t *r, void *dst, size_t len, TickType_t wait)
{
    TickType_t start = xTaskGetTickCount();
    while (true) {
        xSemaphoreTake(r->lock, portMAX_DELAY);
        size_t n = copy_out(r, dst, len);
        if (n == 0) {
            xEventGroupClearBits(r->events, BIT_DATA);
        }
        xSemaphoreGive(r->lock);
        if (n) {
            xEventGroupSetBits(r->events, BIT_SPACE);
            return n;
        }
        TickType_t elapsed = xTaskGetTickCount() - start;
        if (elapsed >= wait) {
            return 0;
        }
        xEventGroupWaitBits(r->events, BIT_DATA, pdFALSE, pdTRUE, wait - elapsed);
    }
}

size_t pcm_ring_clear(pcm_ring_t *r)
{
    xSemaphoreTake(r->lock, portMAX_DELAY);
    size_t dropped = r->used;
    r->head = 0;
    r->used = 0;
    xEventGroupClearBits(r->events, BIT_DATA);
    xSemaphoreGive(r->lock);
    xEventGroupSetBits(r->events, BIT_SPACE);
    return dropped;
}

size_t pcm_ring_used(pcm_ring_t *r)
{
    xSemaphoreTake(r->lock, portMAX_DELAY);
    size_t used = r->used;
    xSemaphoreGive(r->lock);
    return used;
}
