#include "audio_in.h"

#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "freertos/stream_buffer.h"
#include "freertos/task.h"
#include "esp_afe_config.h"
#include "esp_afe_sr_models.h"
#include "esp_check.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "model_path.h"
#include "sdkconfig.h"
#include "board.h"

/* ES7210 lane order in each frame, from the Waveshare demo's esp_get_feed_data */
#define INPUT_FORMAT        "RMNM"
#define ADC_LANES           4
#define SETTLE_READS        5
#define STREAM_SECONDS      10
#define STREAM_BYTES        (STREAM_SECONDS * BOARD_SAMPLE_RATE * sizeof(int16_t))
#define BIT_WAKE            BIT0

static const char *TAG = "audio_in";

static const esp_afe_sr_iface_t *s_afe;
static esp_afe_sr_data_t *s_afe_data;
static EventGroupHandle_t s_events;
static StreamBufferHandle_t s_stream;
static StaticStreamBuffer_t s_stream_struct;
static volatile bool s_streaming;
static volatile bool s_wake_enabled;
static volatile bool s_wake_applied;
static volatile int64_t s_last_speech_us;
static volatile uint32_t s_dropped_samples;
static vad_state_t s_logged_vad = VAD_SILENCE;
static volatile float s_level_db;

static void feed_task(void *arg)
{
    esp_codec_dev_handle_t mic = board_mic();
    int chunk = s_afe->get_feed_chunksize(s_afe_data);
    size_t bytes = chunk * ADC_LANES * sizeof(int16_t);
    int16_t *buf = heap_caps_malloc(bytes, MALLOC_CAP_SPIRAM);
    assert(buf);

    for (int i = 0; i < SETTLE_READS; i++) {
        esp_codec_dev_read(mic, buf, bytes);
    }
    while (true) {
        if (esp_codec_dev_read(mic, buf, bytes) != ESP_CODEC_DEV_OK) {
            ESP_LOGE(TAG, "Mic read failed");
            vTaskDelay(pdMS_TO_TICKS(100));
            continue;
        }
        s_afe->feed(s_afe_data, buf);
    }
}

static void begin_stream(void)
{
    xStreamBufferReset(s_stream);
    s_dropped_samples = 0;
    s_last_speech_us = esp_timer_get_time();
    s_streaming = true;
}

static void push_stream(const int16_t *data, int bytes)
{
    size_t sent = xStreamBufferSend(s_stream, data, bytes, 0);
    if (sent < (size_t)bytes) {
        s_dropped_samples += (bytes - sent) / sizeof(int16_t);
    }
}

static void fetch_task(void *arg)
{
    while (true) {
        if (s_wake_enabled != s_wake_applied) {
            s_wake_applied = s_wake_enabled;
            if (s_wake_applied) {
                s_afe->enable_wakenet(s_afe_data);
            } else {
                s_afe->disable_wakenet(s_afe_data);
            }
        }

        afe_fetch_result_t *res = s_afe->fetch(s_afe_data);
        if (!res || res->ret_value == ESP_FAIL) {
            ESP_LOGE(TAG, "AFE fetch failed");
            continue;
        }
        s_level_db = res->data_volume;
        /* Background talk is also speech to the VAD; only near, loud speech keeps a conversation open. */
        if (res->vad_state == VAD_SPEECH && res->data_volume >= CONFIG_QUACK_SPEECH_MIN_DB) {
            s_last_speech_us = esp_timer_get_time();
        }
        if (s_streaming && res->vad_state != s_logged_vad) {
            ESP_LOGD(TAG, "Voice %s (level %.1f dB)", res->vad_state == VAD_SPEECH ? "started" : "stopped",
                     res->data_volume);
        }
        s_logged_vad = res->vad_state;

        bool woke = (res->raw_data_channels == 1 && res->wakeup_state == WAKENET_DETECTED) ||
                    (res->raw_data_channels > 1 && res->wakeup_state == WAKENET_CHANNEL_VERIFIED);
        if (woke && s_wake_applied && !s_streaming) {
            ESP_LOGI(TAG, "Wake word detected (channel %d)", res->trigger_channel_id);
            s_wake_enabled = false;
            s_wake_applied = false;
            s_afe->disable_wakenet(s_afe_data);
            begin_stream();
            xEventGroupSetBits(s_events, BIT_WAKE);
            continue;
        }

        if (s_streaming) {
            push_stream(res->data, res->data_size);
        }
    }
}

esp_err_t audio_in_start(void)
{
    ESP_RETURN_ON_FALSE(board_mic(), ESP_ERR_INVALID_STATE, TAG, "mic not initialised");

    srmodel_list_t *models = esp_srmodel_init("model");
    ESP_RETURN_ON_FALSE(models, ESP_ERR_NOT_FOUND, TAG, "no speech models in the model partition");
    afe_config_t *cfg = afe_config_init(INPUT_FORMAT, models, AFE_TYPE_SR, AFE_MODE_LOW_COST);
    ESP_RETURN_ON_FALSE(cfg, ESP_FAIL, TAG, "afe config");
    cfg->vad_init = true;
    cfg->memory_alloc_mode = AFE_MEMORY_ALLOC_MORE_PSRAM;
    s_afe = esp_afe_handle_from_config(cfg);
    s_afe_data = s_afe->create_from_config(cfg);
    afe_config_free(cfg);
    ESP_RETURN_ON_FALSE(s_afe_data, ESP_ERR_NO_MEM, TAG, "afe create");
    s_afe->print_pipeline(s_afe_data);
    s_afe->disable_wakenet(s_afe_data);

    uint8_t *storage = heap_caps_malloc(STREAM_BYTES + 1, MALLOC_CAP_SPIRAM);
    s_events = xEventGroupCreate();
    ESP_RETURN_ON_FALSE(storage && s_events, ESP_ERR_NO_MEM, TAG, "stream buffer");
    s_stream = xStreamBufferCreateStatic(STREAM_BYTES, 1, storage, &s_stream_struct);

    ESP_RETURN_ON_FALSE(xTaskCreatePinnedToCore(feed_task, "afe_feed", 8192, NULL, 5, NULL, 0) == pdPASS,
                        ESP_ERR_NO_MEM, TAG, "feed task");
    ESP_RETURN_ON_FALSE(xTaskCreatePinnedToCore(fetch_task, "afe_fetch", 8192, NULL, 5, NULL, 1) == pdPASS,
                        ESP_ERR_NO_MEM, TAG, "fetch task");
    return ESP_OK;
}

void audio_in_wake_enable(bool enable)
{
    if (enable) {
        xEventGroupClearBits(s_events, BIT_WAKE);
    }
    s_wake_enabled = enable;
}

bool audio_in_wait_wake(TickType_t wait)
{
    return xEventGroupWaitBits(s_events, BIT_WAKE, pdTRUE, pdTRUE, wait) & BIT_WAKE;
}

size_t audio_in_stream_read(int16_t *dst, size_t max_samples, TickType_t wait)
{
    size_t got = 0;
    size_t want = max_samples * sizeof(int16_t);
    TickType_t start = xTaskGetTickCount();
    while (got < want) {
        TickType_t elapsed = xTaskGetTickCount() - start;
        if (elapsed >= wait) {
            break;
        }
        got += xStreamBufferReceive(s_stream, (uint8_t *)dst + got, want - got, wait - elapsed);
    }
    return got / sizeof(int16_t);
}

void audio_in_stream_end(void)
{
    s_streaming = false;
    if (s_dropped_samples) {
        ESP_LOGW(TAG, "Dropped %lu mic samples while streaming", (unsigned long)s_dropped_samples);
    }
}

uint32_t audio_in_ms_since_speech(void)
{
    return (uint32_t)((esp_timer_get_time() - s_last_speech_us) / 1000);
}

float audio_in_level_db(void)
{
    return s_level_db;
}
