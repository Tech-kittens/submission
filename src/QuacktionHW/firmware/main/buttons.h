#pragma once

#include "esp_err.h"

typedef struct {
    void (*on_k1_press)(void);
    void (*on_k2_hold)(void);
    void (*on_k2_press)(void);
} buttons_callbacks_t;

/* Polls K1 and K2 on the TCA9555. Callbacks run in the button task: on_k1_press on each debounced
 * press, on_k2_hold once when K2 has been held for BUTTONS_HOLD_MS, and on_k2_press when K2 is
 * released before that. */
#define BUTTONS_HOLD_MS 3000

esp_err_t buttons_start(const buttons_callbacks_t *cb);
