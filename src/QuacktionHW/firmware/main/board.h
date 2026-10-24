#pragma once

#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"
#include "driver/i2c_master.h"
#include "driver/i2s_std.h"
#include "esp_codec_dev.h"
#include "esp_io_expander.h"

#define BOARD_SAMPLE_RATE      16000
#define BOARD_I2S_CHANNELS     2
#define BOARD_I2S_SLOT_BITS    32

#define BOARD_LED_COUNT        7

#define BOARD_EXIO_PA_EN       IO_EXPANDER_PIN_NUM_8
#define BOARD_EXIO_K1          IO_EXPANDER_PIN_NUM_9
#define BOARD_EXIO_K2          IO_EXPANDER_PIN_NUM_10
#define BOARD_EXIO_K3          IO_EXPANDER_PIN_NUM_11

esp_err_t board_init(void);

i2c_master_bus_handle_t board_i2c_bus(void);
esp_io_expander_handle_t board_expander(void);
i2s_chan_handle_t board_i2s_rx(void);
esp_codec_dev_handle_t board_speaker(void);
esp_codec_dev_handle_t board_mic(void);

esp_err_t board_speaker_amp(bool on);

esp_err_t board_leds_fill(uint8_t r, uint8_t g, uint8_t b);
esp_err_t board_leds_off(void);
