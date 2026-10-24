#include "board.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_check.h"
#include "esp_log.h"
#include "esp_codec_dev_defaults.h"
#include "esp_io_expander_tca95xx_16bit.h"
#include "led_strip.h"

#define I2C_PORT        0
#define GPIO_I2C_SDA    GPIO_NUM_11
#define GPIO_I2C_SCL    GPIO_NUM_10

#define I2S_PORT        I2S_NUM_1
#define GPIO_I2S_MCLK   GPIO_NUM_12
#define GPIO_I2S_BCLK   GPIO_NUM_13
#define GPIO_I2S_WS     GPIO_NUM_14
#define GPIO_I2S_DIN    GPIO_NUM_15
#define GPIO_I2S_DOUT   GPIO_NUM_16

#define GPIO_LED_DATA   GPIO_NUM_38

#define SPEAKER_VOLUME  90
#define MIC_GAIN_DB     30.0f
#define MIC_ADC_LANES   4

static const char *TAG = "board";

static i2c_master_bus_handle_t s_i2c;
static esp_io_expander_handle_t s_expander;
static i2s_chan_handle_t s_i2s_tx;
static i2s_chan_handle_t s_i2s_rx;
static esp_codec_dev_handle_t s_speaker;
static esp_codec_dev_handle_t s_mic;
static led_strip_handle_t s_leds;

static esp_err_t i2c_init(void)
{
    const i2c_master_bus_config_t cfg = {
        .i2c_port = I2C_PORT,
        .sda_io_num = GPIO_I2C_SDA,
        .scl_io_num = GPIO_I2C_SCL,
        .clk_source = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        .flags.enable_internal_pullup = true,
    };
    return i2c_new_master_bus(&cfg, &s_i2c);
}

static esp_err_t expander_init(void)
{
    ESP_RETURN_ON_ERROR(esp_io_expander_new_i2c_tca95xx_16bit(s_i2c, ESP_IO_EXPANDER_I2C_TCA9555_ADDRESS_000, &s_expander),
                        TAG, "TCA9555 not found");
    ESP_RETURN_ON_ERROR(esp_io_expander_set_dir(s_expander,
                                                IO_EXPANDER_PIN_NUM_0 | IO_EXPANDER_PIN_NUM_1 | IO_EXPANDER_PIN_NUM_5 |
                                                IO_EXPANDER_PIN_NUM_6 | BOARD_EXIO_PA_EN,
                                                IO_EXPANDER_OUTPUT), TAG, "expander outputs");
    ESP_RETURN_ON_ERROR(esp_io_expander_set_level(s_expander, BOARD_EXIO_PA_EN, 0), TAG, "PA off");
    return esp_io_expander_set_dir(s_expander,
                                   IO_EXPANDER_PIN_NUM_2 | BOARD_EXIO_K1 | BOARD_EXIO_K2 | BOARD_EXIO_K3,
                                   IO_EXPANDER_INPUT);
}

static esp_err_t i2s_init(void)
{
    i2s_chan_config_t chan_cfg = I2S_CHANNEL_DEFAULT_CONFIG(I2S_PORT, I2S_ROLE_MASTER);
    chan_cfg.auto_clear = true;
    ESP_RETURN_ON_ERROR(i2s_new_channel(&chan_cfg, &s_i2s_tx, &s_i2s_rx), TAG, "I2S channels");

    const i2s_std_config_t std_cfg = {
        .clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(BOARD_SAMPLE_RATE),
        .slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(BOARD_I2S_SLOT_BITS, I2S_SLOT_MODE_STEREO),
        .gpio_cfg = {
            .mclk = GPIO_I2S_MCLK,
            .bclk = GPIO_I2S_BCLK,
            .ws = GPIO_I2S_WS,
            .dout = GPIO_I2S_DOUT,
            .din = GPIO_I2S_DIN,
        },
    };
    ESP_RETURN_ON_ERROR(i2s_channel_init_std_mode(s_i2s_tx, &std_cfg), TAG, "I2S tx mode");
    ESP_RETURN_ON_ERROR(i2s_channel_init_std_mode(s_i2s_rx, &std_cfg), TAG, "I2S rx mode");
    ESP_RETURN_ON_ERROR(i2s_channel_enable(s_i2s_tx), TAG, "I2S tx enable");
    return i2s_channel_enable(s_i2s_rx);
}

static esp_err_t speaker_init(void)
{
    audio_codec_i2s_cfg_t i2s_cfg = {
        .port = I2S_PORT,
        .tx_handle = s_i2s_tx,
    };
    const audio_codec_data_if_t *data_if = audio_codec_new_i2s_data(&i2s_cfg);

    audio_codec_i2c_cfg_t i2c_cfg = {
        .port = I2C_PORT,
        .addr = ES8311_CODEC_DEFAULT_ADDR,
        .bus_handle = s_i2c,
    };
    const audio_codec_ctrl_if_t *ctrl_if = audio_codec_new_i2c_ctrl(&i2c_cfg);
    const audio_codec_gpio_if_t *gpio_if = audio_codec_new_gpio();
    ESP_RETURN_ON_FALSE(data_if && ctrl_if && gpio_if, ESP_ERR_NO_MEM, TAG, "codec interfaces");

    es8311_codec_cfg_t es8311_cfg = {
        .codec_mode = ESP_CODEC_DEV_WORK_MODE_DAC,
        .ctrl_if = ctrl_if,
        .gpio_if = gpio_if,
        .pa_pin = -1,
        .use_mclk = false,
    };
    const audio_codec_if_t *codec_if = es8311_codec_new(&es8311_cfg);
    ESP_RETURN_ON_FALSE(codec_if, ESP_ERR_NOT_FOUND, TAG, "ES8311 not found");

    esp_codec_dev_cfg_t dev_cfg = {
        .dev_type = ESP_CODEC_DEV_TYPE_OUT,
        .codec_if = codec_if,
        .data_if = data_if,
    };
    s_speaker = esp_codec_dev_new(&dev_cfg);
    ESP_RETURN_ON_FALSE(s_speaker, ESP_ERR_NO_MEM, TAG, "speaker device");

    esp_codec_dev_sample_info_t fs = {
        .sample_rate = BOARD_SAMPLE_RATE,
        .channel = BOARD_I2S_CHANNELS,
        .bits_per_sample = BOARD_I2S_SLOT_BITS,
    };
    ESP_RETURN_ON_FALSE(esp_codec_dev_open(s_speaker, &fs) == ESP_CODEC_DEV_OK, ESP_FAIL, TAG, "speaker open");
    ESP_RETURN_ON_FALSE(esp_codec_dev_set_out_vol(s_speaker, SPEAKER_VOLUME) == ESP_CODEC_DEV_OK, ESP_FAIL, TAG, "volume");
    return ESP_OK;
}

static esp_err_t mic_init(void)
{
    audio_codec_i2s_cfg_t i2s_cfg = {
        .port = I2S_PORT,
        .rx_handle = s_i2s_rx,
    };
    const audio_codec_data_if_t *data_if = audio_codec_new_i2s_data(&i2s_cfg);

    audio_codec_i2c_cfg_t i2c_cfg = {
        .port = I2C_PORT,
        .addr = ES7210_CODEC_DEFAULT_ADDR,
        .bus_handle = s_i2c,
    };
    const audio_codec_ctrl_if_t *ctrl_if = audio_codec_new_i2c_ctrl(&i2c_cfg);
    ESP_RETURN_ON_FALSE(data_if && ctrl_if, ESP_ERR_NO_MEM, TAG, "codec interfaces");

    es7210_codec_cfg_t es7210_cfg = {
        .ctrl_if = ctrl_if,
        .mic_selected = ES7210_SEL_MIC1 | ES7210_SEL_MIC2 | ES7210_SEL_MIC3 | ES7210_SEL_MIC4,
    };
    const audio_codec_if_t *codec_if = es7210_codec_new(&es7210_cfg);
    ESP_RETURN_ON_FALSE(codec_if, ESP_ERR_NOT_FOUND, TAG, "ES7210 not found");

    esp_codec_dev_cfg_t dev_cfg = {
        .dev_type = ESP_CODEC_DEV_TYPE_IN,
        .codec_if = codec_if,
        .data_if = data_if,
    };
    s_mic = esp_codec_dev_new(&dev_cfg);
    ESP_RETURN_ON_FALSE(s_mic, ESP_ERR_NO_MEM, TAG, "mic device");

    esp_codec_dev_sample_info_t fs = {
        .sample_rate = BOARD_SAMPLE_RATE,
        .channel = BOARD_I2S_CHANNELS,
        .bits_per_sample = BOARD_I2S_SLOT_BITS,
    };
    ESP_RETURN_ON_FALSE(esp_codec_dev_open(s_mic, &fs) == ESP_CODEC_DEV_OK, ESP_FAIL, TAG, "mic open");
    for (int ch = 0; ch < MIC_ADC_LANES; ch++) {
        ESP_RETURN_ON_FALSE(esp_codec_dev_set_in_channel_gain(s_mic, ESP_CODEC_DEV_MAKE_CHANNEL_MASK(ch), MIC_GAIN_DB)
                            == ESP_CODEC_DEV_OK, ESP_FAIL, TAG, "mic gain");
    }
    return ESP_OK;
}

static esp_err_t leds_init(void)
{
    const led_strip_config_t strip_cfg = {
        .strip_gpio_num = GPIO_LED_DATA,
        .max_leds = BOARD_LED_COUNT,
        .led_model = LED_MODEL_WS2812,
        .color_component_format = LED_STRIP_COLOR_COMPONENT_FMT_RGB,
    };
    const led_strip_rmt_config_t rmt_cfg = {
        .clk_src = RMT_CLK_SRC_DEFAULT,
        .resolution_hz = 10 * 1000 * 1000,
    };
    ESP_RETURN_ON_ERROR(led_strip_new_rmt_device(&strip_cfg, &rmt_cfg, &s_leds), TAG, "LED ring");
    return led_strip_clear(s_leds);
}

esp_err_t board_init(void)
{
    ESP_RETURN_ON_ERROR(i2c_init(), TAG, "I2C init failed");
    ESP_RETURN_ON_ERROR(expander_init(), TAG, "TCA9555 init failed");
    ESP_RETURN_ON_ERROR(i2s_init(), TAG, "I2S init failed");
    ESP_RETURN_ON_ERROR(speaker_init(), TAG, "ES8311 init failed");
    ESP_RETURN_ON_ERROR(mic_init(), TAG, "ES7210 init failed");
    ESP_RETURN_ON_ERROR(leds_init(), TAG, "LED init failed");
    ESP_LOGI(TAG, "Board ready");
    return ESP_OK;
}

i2c_master_bus_handle_t board_i2c_bus(void)
{
    return s_i2c;
}

esp_io_expander_handle_t board_expander(void)
{
    return s_expander;
}

i2s_chan_handle_t board_i2s_rx(void)
{
    return s_i2s_rx;
}

esp_codec_dev_handle_t board_speaker(void)
{
    return s_speaker;
}

esp_codec_dev_handle_t board_mic(void)
{
    return s_mic;
}

esp_err_t board_speaker_amp(bool on)
{
    esp_err_t err = esp_io_expander_set_level(s_expander, BOARD_EXIO_PA_EN, on);
    vTaskDelay(pdMS_TO_TICKS(10));
    return err;
}

esp_err_t board_leds_fill(uint8_t r, uint8_t g, uint8_t b)
{
    for (int i = 0; i < BOARD_LED_COUNT; i++) {
        ESP_RETURN_ON_ERROR(led_strip_set_pixel(s_leds, i, r, g, b), TAG, "LED pixel");
    }
    return led_strip_refresh(s_leds);
}

esp_err_t board_leds_off(void)
{
    return led_strip_clear(s_leds);
}
