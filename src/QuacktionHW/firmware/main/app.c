#include "esp_bt.h"
#include "esp_check.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "sdkconfig.h"
#include "board.h"
#include "audio_in.h"
#include "audio_out.h"
#include "buttons.h"
#include "config_store.h"
#include "device_socket.h"
#include "net_wifi.h"
#include "session.h"
#include "session_audio.h"
#include "setup.h"
#include "status.h"

#define WIFI_TIMEOUT_MS         60000
#define FRAME_MS                40
#define FRAME_SAMPLES           (BOARD_SAMPLE_RATE * FRAME_MS / 1000)
#define REPLY_DRAIN_MS          20000
#define IDLE_POLL_MS            100
#define MUTE_SETTLE_MS          300
/* Above the WebSocket task (priority 5) so mic sends get the client lock between received frames. */
#define CONVERSATION_PRIORITY   6

static const char *TAG = "app";

static int16_t s_frame[FRAME_SAMPLES];
static volatile bool s_muted;
static quack_config_t s_config;

static void on_k1_press(void)
{
    bool mute = !s_muted;
    if (esp_codec_dev_set_in_mute(board_mic(), mute) != ESP_CODEC_DEV_OK) {
        ESP_LOGE(TAG, "Could not %s the mics", mute ? "mute" : "unmute");
        return;
    }
    s_muted = mute;
    if (mute) {
        audio_in_wake_enable(false);
        status_set(STATUS_MUTED);
        vTaskDelay(pdMS_TO_TICKS(MUTE_SETTLE_MS));
        ESP_LOGI(TAG, "Muted (mic level now %.1f dB)", audio_in_level_db());
    } else {
        ESP_LOGI(TAG, "Unmuted");
    }
}

static void on_k2_hold(void)
{
    ESP_LOGI(TAG, "Setup button held, restarting into setup mode");
    config_request_setup();
    esp_restart();
}

static void on_reply_audio(const uint8_t *data, size_t len, void *ctx)
{
    if (audio_out_stream_write(data, len) == ESP_ERR_NO_MEM) {
        ESP_LOGW(TAG, "Playback buffer full, reply audio dropped");
    }
}

static void on_reply_clear(void *ctx)
{
    size_t dropped = audio_out_stream_clear();
    ESP_LOGI(TAG, "Dropped %u ms of unplayed reply", (unsigned)(dropped / (BOARD_SAMPLE_RATE * 2 / 1000)));
}

/* Sends buffered and live mic audio at real-time pace until the conversation goes quiet. */
static esp_err_t stream_until_quiet(session_audio_t *audio)
{
    const uint32_t idle_ms = CONFIG_QUACK_SESSION_IDLE_SECONDS * 1000;
    const int64_t max_us = (int64_t)CONFIG_QUACK_SESSION_MAX_SECONDS * 1000000;
    const int64_t start_us = esp_timer_get_time();
    int64_t last_playback_us = start_us;
    TickType_t next = xTaskGetTickCount();

    while (session_audio_alive(audio)) {
        vTaskDelayUntil(&next, pdMS_TO_TICKS(FRAME_MS));
        if (s_muted) {
            ESP_LOGI(TAG, "Muted, ending conversation");
            return ESP_OK;
        }
        size_t got = audio_in_stream_read(s_frame, FRAME_SAMPLES, pdMS_TO_TICKS(200));
        if (got < FRAME_SAMPLES) {
            ESP_LOGE(TAG, "Mic stream stalled");
            return ESP_ERR_TIMEOUT;
        }
        ESP_RETURN_ON_ERROR(session_audio_send(audio, s_frame, got), TAG, "send");

        int64_t now = esp_timer_get_time();
        bool speaking = audio_out_stream_busy();
        status_set(speaking ? STATUS_SPEAKING : STATUS_LISTENING);
        if (speaking) {
            last_playback_us = now;
        }
        uint32_t since_playback_ms = (uint32_t)((now - last_playback_us) / 1000);
        uint32_t since_speech_ms = audio_in_ms_since_speech();
        if (since_playback_ms > idle_ms && since_speech_ms > idle_ms) {
            ESP_LOGI(TAG, "Quiet for %d s, ending conversation", CONFIG_QUACK_SESSION_IDLE_SECONDS);
            return ESP_OK;
        }
        if (now - start_us > max_us) {
            ESP_LOGW(TAG, "Conversation reached %d s, ending", CONFIG_QUACK_SESSION_MAX_SECONDS);
            return ESP_OK;
        }
    }
    ESP_LOGE(TAG, "Session socket closed by the backend");
    return ESP_FAIL;
}

static esp_err_t converse(void)
{
    const session_config_t cfg = {
        .backend_url = CONFIG_QUACK_BACKEND_URL,
        .device_id = s_config.device_id,
        .device_credential = s_config.credential,
    };
    session_t session;
    ESP_RETURN_ON_ERROR(session_start(&cfg, &session), TAG, "session start");

    esp_err_t err = audio_out_stream_begin();
    if (err != ESP_OK) {
        session_clear(&session);
        return err;
    }

    const session_audio_callbacks_t cb = {
        .on_audio = on_reply_audio,
        .on_clear = on_reply_clear,
    };
    session_audio_t *audio;
    err = session_audio_open(&session, &cb, &audio);
    session_clear(&session);
    if (err != ESP_OK) {
        audio_out_stream_end(0);
        return err;
    }

    status_set(STATUS_LISTENING);
    esp_err_t stream_err = stream_until_quiet(audio);
    audio_out_stream_end(stream_err == ESP_OK && !s_muted ? REPLY_DRAIN_MS : 0);
    err = session_audio_end(audio);
    return stream_err != ESP_OK ? stream_err : err;
}

static void conversation_task(void *arg)
{
    status_set(STATUS_CONNECTING);
    ESP_ERROR_CHECK(net_wifi_init());
    net_wifi_connect(s_config.ssid, s_config.ssid_len, s_config.password, true);
    if (!net_wifi_wait_connected(WIFI_TIMEOUT_MS)) {
        ESP_LOGW(TAG, "Wi-Fi not connected yet; it keeps retrying in the background");
    }
    if (device_socket_start(CONFIG_QUACK_BACKEND_URL, s_config.device_id, s_config.credential) != ESP_OK) {
        ESP_LOGE(TAG, "Device socket not started; the backend will show this duck offline");
    }

    bool listening = false;
    while (true) {
        if (s_muted) {
            if (listening) {
                audio_in_wake_enable(false);
                listening = false;
            }
            status_set(STATUS_MUTED);
            vTaskDelay(pdMS_TO_TICKS(IDLE_POLL_MS));
            continue;
        }
        if (!listening) {
            status_set(STATUS_IDLE);
            audio_in_wake_enable(true);
            listening = true;
            ESP_LOGI(TAG, "Listening for \"Hi ESP\"");
        }
        if (!audio_in_wait_wake(pdMS_TO_TICKS(IDLE_POLL_MS))) {
            continue;
        }
        listening = false;
        if (s_muted) {
            audio_in_stream_end();
            continue;
        }

        status_set(STATUS_CONNECTING);
        audio_out_quack();
        esp_err_t err = converse();
        audio_in_stream_end();
        ESP_LOGI(TAG, "Conversation %s", err == ESP_OK ? "ended cleanly" : "failed");
        if (err != ESP_OK) {
            status_show_error();
        }
    }
}

void app_main(void)
{
    ESP_ERROR_CHECK(board_init());
    status_set(STATUS_BOOT);
    ESP_ERROR_CHECK(config_store_init());

    config_load(&s_config);
    bool requested = config_take_setup_request();
    if (requested || !config_has_wifi(&s_config) || !config_has_backend(&s_config)) {
        ESP_LOGI(TAG, "Starting setup mode (%s)", requested ? "requested" : "not set up yet");
        config_wipe(&s_config);
        ESP_LOGI(TAG, "Quack");
        audio_out_quack();
        setup_run();
    }

    /* Normal mode never uses Bluetooth; give its reserved memory back to the heap. */
    ESP_ERROR_CHECK(esp_bt_mem_release(ESP_BT_MODE_BLE));
    ESP_ERROR_CHECK(audio_in_start());

    ESP_LOGI(TAG, "Quack");
    ESP_ERROR_CHECK(audio_out_quack());

    const buttons_callbacks_t cb = {.on_k1_press = on_k1_press, .on_k2_hold = on_k2_hold};
    ESP_ERROR_CHECK(buttons_start(&cb));
    xTaskCreate(conversation_task, "conversation", 8192, NULL, CONVERSATION_PRIORITY, NULL);
}
