#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_check.h"
#include "esp_log.h"
#include "board.h"
#include "buttons.h"

#define POLL_MS         30
#define STABLE_POLLS    2

static const char *TAG = "buttons";

typedef struct {
    uint32_t pin;
    bool pressed;
    bool candidate;
    int stable;
    TickType_t pressed_at;
    bool hold_fired;
} button_t;

static buttons_callbacks_t s_cb;

/* Returns true on the poll where a debounced press starts. */
static bool debounce(button_t *b, uint32_t levels)
{
    bool now = !(levels & b->pin);
    if (now != b->candidate) {
        b->candidate = now;
        b->stable = 0;
        return false;
    }
    if (b->stable < STABLE_POLLS && ++b->stable == STABLE_POLLS && b->candidate != b->pressed) {
        b->pressed = b->candidate;
        if (b->pressed) {
            b->pressed_at = xTaskGetTickCount();
            b->hold_fired = false;
            return true;
        }
    }
    return false;
}

static void button_task(void *arg)
{
    esp_io_expander_handle_t expander = board_expander();
    button_t k1 = {.pin = BOARD_EXIO_K1};
    button_t k2 = {.pin = BOARD_EXIO_K2};

    while (true) {
        vTaskDelay(pdMS_TO_TICKS(POLL_MS));
        uint32_t levels;
        if (esp_io_expander_get_level(expander, BOARD_EXIO_K1 | BOARD_EXIO_K2, &levels) != ESP_OK) {
            continue;
        }
        if (debounce(&k1, levels) && s_cb.on_k1_press) {
            s_cb.on_k1_press();
        }
        bool k2_was_pressed = k2.pressed;
        debounce(&k2, levels);
        if (k2_was_pressed && !k2.pressed && !k2.hold_fired && s_cb.on_k2_press) {
            s_cb.on_k2_press();
        }
        if (k2.pressed && !k2.hold_fired &&
            xTaskGetTickCount() - k2.pressed_at >= pdMS_TO_TICKS(BUTTONS_HOLD_MS)) {
            k2.hold_fired = true;
            if (s_cb.on_k2_hold) {
                s_cb.on_k2_hold();
            }
        }
    }
}

esp_err_t buttons_start(const buttons_callbacks_t *cb)
{
    ESP_RETURN_ON_FALSE(board_expander(), ESP_ERR_INVALID_STATE, TAG, "expander not initialised");
    s_cb = *cb;
    BaseType_t ok = xTaskCreate(button_task, "buttons", 3072, NULL, 4, NULL);
    return ok == pdPASS ? ESP_OK : ESP_ERR_NO_MEM;
}
