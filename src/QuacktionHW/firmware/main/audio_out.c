#include "audio_out.h"

#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "esp_check.h"
#include "esp_log.h"
#include "board.h"
#include "pcm_ring.h"

#define FRAME_SAMPLES       320
#define DMA_DRAIN_MS        120
#define RING_SECONDS        20
#define WRITE_WAIT_MS       1000

static const char *TAG = "audio_out";

extern const uint8_t quack_wav_start[] asm("_binary_quack_wav_start");
extern const uint8_t quack_wav_end[] asm("_binary_quack_wav_end");

static int32_t s_frame[FRAME_SAMPLES * BOARD_I2S_CHANNELS];

static pcm_ring_t *s_ring;
static TaskHandle_t s_task;
static SemaphoreHandle_t s_stopped;
static volatile bool s_playing;
static volatile bool s_stop_requested;
static size_t s_played;
static size_t s_overflow;

static esp_err_t write_frame(esp_codec_dev_handle_t speaker, const int16_t *samples, size_t n)
{
    for (size_t i = 0; i < n; i++) {
        int32_t s = (int32_t)samples[i] << 16;
        s_frame[2 * i] = s;
        s_frame[2 * i + 1] = s;
    }
    return esp_codec_dev_write(speaker, s_frame, n * BOARD_I2S_CHANNELS * sizeof(int32_t)) == ESP_CODEC_DEV_OK
           ? ESP_OK : ESP_FAIL;
}

static void playback_task(void *arg)
{
    esp_codec_dev_handle_t speaker = board_speaker();
    int16_t pcm[FRAME_SAMPLES];
    while (true) {
        if (!s_playing) {
            ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
            continue;
        }
        if (s_stop_requested) {
            s_playing = false;
            xSemaphoreGive(s_stopped);
            continue;
        }
        size_t bytes = pcm_ring_read(s_ring, pcm, sizeof(pcm), pdMS_TO_TICKS(20));
        if (bytes && write_frame(speaker, pcm, bytes / sizeof(int16_t)) == ESP_OK) {
            s_played += bytes;
        }
    }
}

esp_err_t audio_out_play_pcm(const int16_t *samples, size_t count)
{
    esp_codec_dev_handle_t speaker = board_speaker();
    ESP_RETURN_ON_FALSE(speaker, ESP_ERR_INVALID_STATE, TAG, "speaker not initialised");

    ESP_RETURN_ON_ERROR(board_speaker_amp(true), TAG, "amp on");
    esp_err_t err = ESP_OK;
    for (size_t offset = 0; offset < count && err == ESP_OK; offset += FRAME_SAMPLES) {
        size_t n = count - offset < FRAME_SAMPLES ? count - offset : FRAME_SAMPLES;
        err = write_frame(speaker, samples + offset, n);
    }
    vTaskDelay(pdMS_TO_TICKS(DMA_DRAIN_MS));
    board_speaker_amp(false);
    return err;
}

static uint32_t le32(const uint8_t *p)
{
    return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint16_t le16(const uint8_t *p)
{
    return p[0] | (p[1] << 8);
}

esp_err_t audio_out_play_wav(const uint8_t *wav, size_t len)
{
    ESP_RETURN_ON_FALSE(len >= 12 && !memcmp(wav, "RIFF", 4) && !memcmp(wav + 8, "WAVE", 4),
                        ESP_ERR_INVALID_ARG, TAG, "not a WAV file");

    bool format_ok = false;
    size_t pos = 12;
    while (pos + 8 <= len) {
        const uint8_t *chunk = wav + pos;
        uint32_t size = le32(chunk + 4);
        const uint8_t *body = chunk + 8;
        if (size > len - pos - 8) {
            size = len - pos - 8;
        }
        if (!memcmp(chunk, "fmt ", 4) && size >= 16) {
            format_ok = le16(body) == 1 && le16(body + 2) == 1 &&
                        le32(body + 4) == BOARD_SAMPLE_RATE && le16(body + 14) == 16;
            ESP_RETURN_ON_FALSE(format_ok, ESP_ERR_NOT_SUPPORTED, TAG, "WAV must be 16 kHz 16-bit mono PCM");
        } else if (!memcmp(chunk, "data", 4)) {
            ESP_RETURN_ON_FALSE(format_ok, ESP_ERR_INVALID_ARG, TAG, "WAV data before fmt");
            return audio_out_play_pcm((const int16_t *)body, size / sizeof(int16_t));
        }
        pos += 8 + size + (size & 1);
    }
    ESP_LOGE(TAG, "WAV has no data chunk");
    return ESP_ERR_INVALID_ARG;
}

esp_err_t audio_out_quack(void)
{
    return audio_out_play_wav(quack_wav_start, quack_wav_end - quack_wav_start);
}

esp_err_t audio_out_stream_begin(void)
{
    ESP_RETURN_ON_FALSE(board_speaker(), ESP_ERR_INVALID_STATE, TAG, "speaker not initialised");
    ESP_RETURN_ON_FALSE(!s_playing, ESP_ERR_INVALID_STATE, TAG, "stream already open");
    if (!s_task) {
        s_ring = pcm_ring_create(RING_SECONDS * BOARD_SAMPLE_RATE * sizeof(int16_t));
        s_stopped = xSemaphoreCreateBinary();
        ESP_RETURN_ON_FALSE(s_ring && s_stopped, ESP_ERR_NO_MEM, TAG, "playback buffer");
        ESP_RETURN_ON_FALSE(xTaskCreatePinnedToCore(playback_task, "audio_out", 4096, NULL, 6, &s_task, 1) == pdPASS,
                            ESP_ERR_NO_MEM, TAG, "playback task");
    }
    pcm_ring_clear(s_ring);
    s_played = 0;
    s_overflow = 0;
    s_stop_requested = false;
    ESP_RETURN_ON_ERROR(board_speaker_amp(true), TAG, "amp on");
    s_playing = true;
    xTaskNotifyGive(s_task);
    return ESP_OK;
}

esp_err_t audio_out_stream_write(const void *pcm, size_t bytes)
{
    if (!s_playing) {
        return ESP_ERR_INVALID_STATE;
    }
    size_t n = pcm_ring_write(s_ring, pcm, bytes, pdMS_TO_TICKS(WRITE_WAIT_MS));
    if (n < bytes) {
        s_overflow += bytes - n;
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}

size_t audio_out_stream_clear(void)
{
    return s_ring ? pcm_ring_clear(s_ring) : 0;
}

bool audio_out_stream_busy(void)
{
    return s_playing && pcm_ring_used(s_ring) > 1;
}

esp_err_t audio_out_stream_end(uint32_t drain_ms)
{
    if (!s_playing) {
        return ESP_ERR_INVALID_STATE;
    }
    TickType_t start = xTaskGetTickCount();
    while (pcm_ring_used(s_ring) > 1 && xTaskGetTickCount() - start < pdMS_TO_TICKS(drain_ms)) {
        vTaskDelay(pdMS_TO_TICKS(20));
    }
    size_t left = pcm_ring_clear(s_ring);
    s_stop_requested = true;
    xSemaphoreTake(s_stopped, portMAX_DELAY);
    vTaskDelay(pdMS_TO_TICKS(DMA_DRAIN_MS));
    board_speaker_amp(false);

    ESP_LOGI(TAG, "Played %u ms of streamed audio", (unsigned)(s_played / (BOARD_SAMPLE_RATE * 2 / 1000)));
    if (left > 1) {
        ESP_LOGW(TAG, "Stopped with %u ms still queued", (unsigned)(left / (BOARD_SAMPLE_RATE * 2 / 1000)));
    }
    if (s_overflow) {
        ESP_LOGW(TAG, "Dropped %u bytes because the playback buffer was full", (unsigned)s_overflow);
    }
    return ESP_OK;
}
