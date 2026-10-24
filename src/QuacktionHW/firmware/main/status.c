#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "board.h"
#include "status.h"

#define ERROR_BLINKS    3
#define ERROR_BLINK_MS  250

typedef struct {
    uint8_t r, g, b;
} rgb_t;

static const rgb_t COLOURS[] = {
    [STATUS_BOOT]       = {40, 30, 0},
    [STATUS_CONNECTING] = {0, 0, 40},
    [STATUS_IDLE]       = {0, 10, 0},
    [STATUS_LISTENING]  = {0, 30, 30},
    [STATUS_SPEAKING]   = {30, 0, 40},
    [STATUS_MUTED]      = {40, 0, 0},
    [STATUS_SETUP]      = {25, 25, 25},
};

static SemaphoreHandle_t s_lock;
static status_t s_current = STATUS_BOOT;
static bool s_shown;

static void lock(void)
{
    if (!s_lock) {
        s_lock = xSemaphoreCreateMutex();
    }
    xSemaphoreTake(s_lock, portMAX_DELAY);
}

void status_set(status_t status)
{
    lock();
    if (!s_shown || status != s_current) {
        s_current = status;
        s_shown = true;
        board_leds_fill(COLOURS[status].r, COLOURS[status].g, COLOURS[status].b);
    }
    xSemaphoreGive(s_lock);
}

void status_refresh(void)
{
    lock();
    board_leds_fill(COLOURS[s_current].r, COLOURS[s_current].g, COLOURS[s_current].b);
    xSemaphoreGive(s_lock);
}

void status_show_error(void)
{
    lock();
    for (int i = 0; i < ERROR_BLINKS; i++) {
        board_leds_fill(40, 0, 0);
        vTaskDelay(pdMS_TO_TICKS(ERROR_BLINK_MS));
        board_leds_off();
        vTaskDelay(pdMS_TO_TICKS(ERROR_BLINK_MS));
    }
    board_leds_fill(COLOURS[s_current].r, COLOURS[s_current].g, COLOURS[s_current].b);
    xSemaphoreGive(s_lock);
}
