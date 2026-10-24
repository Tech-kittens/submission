# Quacktion: voice duck firmware

Voice-assistant duck. The board cleans mic audio, detects the wake word, starts a
backend session, streams speech over the session WebSocket, and plays the agent's
reply audio. The backend owns sessions and connects to the ElevenLabs agent; the
duck never sees provider credentials (see `../docs/backend/SESSION.md`).

## Hardware: Waveshare ESP32-S3-AUDIO-Board

- SoC: ESP32-S3R8, dual-core 240 MHz, 8 MB PSRAM, 16 MB flash
- Mics: dual mic array -> ES7210 ADC (I2S in)
- Speaker: ES8311 codec + amp -> speaker header (I2S out)
- 7x RGB LEDs (WS2812-type ring), PCF85063 RTC, TF card slot
- TCA9555 I2C GPIO expander: pins named EXIO0-EXIO7 are on the expander, NOT ESP32 GPIOs
- Buttons: RESET, BOOT, K1-K3
- USB-C is a USB-to-UART bridge (CH343): serial only, no native USB/OTG/JTAG

### Pin map

| Signal | Pin |
| --- | --- |
| I2C SDA (ES8311, ES7210, TCA9555, RTC) | GPIO11 |
| I2C SCL | GPIO10 |
| I2S MCLK | GPIO12 |
| I2S BCLK | GPIO13 |
| I2S WS/LRCK | GPIO14 |
| I2S DIN (mic data from ES7210) | GPIO15 |
| I2S DOUT (speaker data to ES8311) | GPIO16 |
| TF card MISO / MOSI / SCK | GPIO41 / GPIO42 / GPIO40 |
| TF card CS | EXIO3 |
| RGB LED data (7x WS2812) | GPIO38 (`bsp_board.h` `LED_STRIP_GPIO_PIN`) |
| Speaker amp enable | EXIO8, high = on (`audio_driver.c` `Audio_PA_EN`) |
| K1 / K2 / K3 buttons | EXIO9 / EXIO10 / EXIO11, active low (`button_driver.c`; Arduino demo `Button_Driver.cpp`) |

Never guess a pin. If it is not in this table, read the reference demo source.

## Reference code (known to build and run on this board)

Waveshare demo, ESP-IDF version:
`C:\Users\kzee\Desktop\Quacktion\ESP32-S3-AUDIO-Board-Demo\ESP32-S3-AUDIO-Board-Demo\ESP-IDF\factory_01`

- `main/hardeware_driver/bsp_board.c`: I2C, I2S, ES8311 + ES7210 setup (patched: `i2s_port_t` -> `int`)
- `main/speech_det_driver/mic_speech.c`: ESP-SR audio front-end + WakeNet
- `main/tca9555_driver`, `main/rgb_led_driver`, `main/button_driver`
- `partitions.csv`: includes the `model` partition ESP-SR needs. Copy it.
- Feeds the audio front-end at 16 kHz, 2 channels, 32-bit slots (`bsp_i2s_init(I2S_NUM_1, 16000, 2, 32)`). Match this.

Reuse the same managed components the demo uses: `espressif/esp_codec_dev`,
`espressif/esp-sr`, `espressif/esp_io_expander_tca95xx_16bit`, `espressif/led_strip`.

## Toolchain rules

- ESP-IDF **6.1** only. Target `esp32s3`. Flash method UART. Windows host.
- Use only the current driver APIs: `driver/i2s_std.h` / `driver/i2s_tdm.h`, `driver/i2c_master.h`.
  Never use legacy `driver/i2s.h`, `driver/i2c.h` or `i2s_port_t`; IDF 6 dropped them.
- Most examples online target IDF 5.x. Expect to adapt them; say which IDF version a snippet assumes.
- Host needs `PYTHONUTF8=1` or the ESP-SR model packing script crashes.
- Build and flash only: `idf.py build`, `idf.py -p COM6 flash`.
  **Never run `idf.py monitor`**: it never exits. The human runs the monitor and pastes logs.
- The human checks anything audible or visible (quacks, LEDs). Ask them to confirm each "done when".

## Memory

- Large buffers (audio ring buffers, WebSocket buffers) go in PSRAM: `heap_caps_malloc(size, MALLOC_CAP_SPIRAM)`.
- Bluetooth runs only in setup mode, never in normal mode. A normal boot never initialises it and
  releases its memory (`esp_bt_mem_release`).
- The ESP-SR pipeline (audio front-end, WakeNet) must never run while Bluetooth is on. Short clips
  played over plain I2S during setup (identify quack) are allowed.
- Wi-Fi and BLE share the 2.4 GHz radio. DPP listening during an open BLE session must be tested on
  the board.
- Normal mode holds two TLS connections during a conversation (device socket + session socket);
  keep their buffers in PSRAM.

## Onboarding (BLE setup)

Summary only; `../docs/onboarding.md` is the source of truth. The exact BLE contract (UUIDs,
payloads, errors) lives in `../docs/ble-protocol.md`, written in F7a.

Boot flow:
1. Read Wi-Fi config, device ID and device credential from NVS.
2. If all exist and no setup request is pending: normal mode. Never start Bluetooth; connect
   Wi-Fi, open the device socket, run audio.
3. If any are missing (first boot), or a setup request is pending: setup mode.
4. Holding the setup button for 3 s in normal mode records a one-shot setup request and reboots,
   so Bluetooth never starts in a boot that has run ESP-SR.

Setup mode:
- Setup button TBD (see Open questions). K1 is reserved for mute. Proposal: K2 (EXIO10). In the
  demo, K1/K3 are volume up/down and K2 is play/pause, so K2 stays free if K1/K3 ever become volume.
  The buttons are on the TCA9555, so poll them over I2C (the demo polls every 100 ms).
- Advertise for at most 5 minutes with a distinct LED pattern. Exit on completion, cancel or
  timeout; on cancel or timeout with a saved config, reboot into normal mode.
- Entering setup mode never erases saved Wi-Fi or backend credentials. Factory reset is a
  separate gesture (TBD).

Discovery:
- Advertise a dedicated 128-bit Quacktion service UUID (generate once, record it in
  `../docs/ble-protocol.md`), local name `Quacktion-XXXX`, and compact service data (protocol
  version, setup available) if it fits.
- Never advertise credentials, account IDs, the setup code or the DPP URI.
- Keep these separate: stable `deviceIdentifier`, backend `deviceId`, BLE address, display suffix.
  Never use the BLE address as an identity.

Security:
- Espressif provisioning framework: `protocomm` over BLE (built into ESP-IDF 6.1) with the NimBLE
  host, not Bluedroid. ESP-IDF 6.1 no longer ships `wifi_provisioning`; the `network_provisioning`
  component is not used because its Wi-Fi flow applies new credentials immediately.
- Security 2 (SRP6a + AES-GCM) for encryption only, with the fixed published password from
  `../docs/ble-protocol.md`. Salt and verifier are generated on-device per window.
- Authorisation is a short press of the setup button (K2) by the user, which approves the one app
  connected at that moment (team decision, replacing onboarding.md's random setup code). Before
  approval only `device.info`, `device.identify` and `setup.status` work. Approval ends with the
  connection; an unapproved app is disconnected after 2 minutes. One connection at a time.

Custom endpoints (logical names from `../docs/onboarding.md`):

| Endpoint | Purpose |
| --- | --- |
| `device.info` | Identity, protocol version, capabilities |
| `device.identify` | Flash and quack, rate-limited |
| `setup.status` | Current setup snapshot |
| `wifi.scan` | List usable networks |
| `wifi.configure` | Manual network config |
| `wifi.dpp.start` | Start DPP and return its URI |
| `wifi.dpp.cancel` | Stop an active DPP attempt |
| `backend.configure` | Install device ID and device credential |
| `setup.complete` | Finish setup and close provisioning |

Wi-Fi:
- Option A, DPP enrollee: reference `$IDF_PATH/examples/wifi/wifi_easy_connect/dpp-enrollee`
  (`CONFIG_ESP_WIFI_DPP_SUPPORT`). Generate a QR-code bootstrap and send the DPP URI to the app over
  the authenticated BLE session, never as a QR image. The bootstrap private key stays on the
  device. Stop listening on cancel, timeout, or before switching to manual.
- Option B, manual: `wifi.scan` + `wifi.configure`, including hidden SSIDs; personal
  authentication modes only. Validate lengths; never echo, log or return passwords.
- Keep the previous working Wi-Fi config until the new one has connected; persist only on
  success. No overlapping scans, DPP listeners or connection attempts.

Backend:
- `backend.configure` stores the device ID and device credential in NVS (encrypted NVS if
  practical) and acknowledges only after they are persisted. Never log or echo credentials or
  passwords.

Status:
- `setup.status` is readable at any time, so the app can recover after a BLE reconnect, and it
  reports Wi-Fi and backend state separately:
  - `wifiState`: `unconfigured`, `scanning`, `awaiting_dpp`, `connecting`, `connected`, `failed`.
  - `backendState`: `unlinked`, `connecting`, `online`, `failed`.
- A Wi-Fi failure, an unreachable backend and a rejected credential are different error codes.

Completion:
- Ready only when Wi-Fi is saved, the credential is stored, and the device socket (F3c) is online.
- Then close provisioning, stop advertising and reboot into normal mode.

## Build order (one step at a time; do not start the next until "done when" is confirmed)

- **F1 Board bring-up**: I2C, TCA9555, ES8311, LED ring; play a quack clip embedded in flash.
  Done when the duck quacks on boot.
- **F2 Mic capture**: ES7210 over I2S, print audio levels to serial.
  Done when levels jump as you talk.
- **F3a Wi-Fi + session start**: connect with credentials from `menuconfig`. Kconfig options
  `QUACK_WIFI_SSID` / `QUACK_WIFI_PASSWORD` / `QUACK_BACKEND_URL` / `QUACK_DEVICE_ID` /
  `QUACK_DEVICE_CREDENTIAL` (development fallback only, never committed with real values).
  Call `POST /api/sessions/start`.
  Done when the log shows a `Ready` session ID (no secrets printed).
- **F3b Session audio socket**: open the session WebSocket with the headers in "Protocol", complete
  `session.started` -> `audio.start` -> `audio.ready`, stream mic PCM, end with `session.end`.
  Done when the backend shows an `Active` session receiving audio and it ends cleanly.
- **F3c Device socket**: `GET /ws/devices/{deviceId}` with `Authorization: Device <credential>`
  and `Sec-WebSocket-Protocol: quacktion.v1`. Send a heartbeat at least every 20 s; reconnect on
  drop.
  Done when the backend reports the device online.
- **F4 Playback**: binary frames from the session socket -> PSRAM ring buffer -> I2S out.
  On `audio.clear`, drop all unplayed audio immediately.
  Done when the agent's reply plays.
- **F5 Wake word**: ESP-SR front-end (playback as echo reference) + WakeNet `wn9_hiesp`.
  On wake: quack, start a session, buffer the spoken request in PSRAM from the moment of
  detection (so the wake word itself is excluded) until `audio.ready`, then send it at
  real-time pace and keep streaming.
  Done when "Hi ESP" plus a question gets a spoken answer.
- **F6 Mute and states**: K1 pressed while running sends `session.end` and mutes the ES7210 over
  I2C; LED colours for idle / connecting / listening / speaking / muted (red); errors shown in red.
- **M2 milestone**: the duck answers a question. Onboarding (F7) starts only after M2.
- **F7a BLE contract**: write `../docs/ble-protocol.md` BEFORE implementing: service and
  characteristic UUIDs, endpoint payloads, auth per endpoint, size limits and fragmentation,
  request IDs and retries, error codes. This lets the app be built in parallel.
  Done when the frontend teammate has reviewed it.
- **F7b Setup mode + manual Wi-Fi**: Security 2 with the code printed to serial; `device.info`,
  `setup.status`, `wifi.scan`, `wifi.configure`, `backend.configure`, `setup.complete`.
  Done when the app sets up a freshly erased duck end to end and it shows online.
- **F7c Identify**: `device.identify` flashes and quacks, rate-limited.
- **F7d Spoken setup code**: dropped at the team's request.
- **F7f Button approval**: replace the random setup code with a fixed Security 2 password plus a K2
  press to approve the connected app.
  Done when configuration is refused before the press and works after it.
- **F7e DPP over BLE**: `wifi.dpp.start` / `wifi.dpp.cancel`, falling back to manual.
  Done when a compatible Android phone shares Wi-Fi.

## Protocol (summary; `../docs/backend/SESSION.md` is the source of truth)

Sessions use the session-scoped flow below. From `../docs/backend/device-protocol.md`, use only
the `/ws/devices/{deviceId}` connection and heartbeat for online status (F3c); do not start sessions
or stream audio over it.

1. `POST /api/sessions/start` with `Authorization: Device <device credential>`,
   `Idempotency-Key: <random key>`, body `{"deviceId": "...", "name": "..."}`.
   Response: `sessionId`, `status: "Ready"`, `webSocketUrl`, `connectionCredential`
   (one-time, expires in 60 s).
2. Open a WebSocket to the exact `webSocketUrl` with `Authorization: Session <connectionCredential>`
   and `Sec-WebSocket-Protocol: quacktion.audio.v1`.
3. Wait for `session.started`.
4. Send `audio.start` (protocol version 1, input and output formats), then wait for `audio.ready`.
5. Binary PCM both ways: mic audio up, agent reply audio down.
6. End with `session.end`; the backend replies `session.ended`.

Also handle: `session.start_failed` (session is Failed; start a new one, do not retry it),
`session.failed`, `error`, and `audio.clear` (drop unplayed audio).
All JSON controls carry `"version": 1`; audio is binary messages only, never JSON or Base64.
This socket has no application heartbeat; rely on WebSocket ping/pong.

## Backend rules

- Never log, print or commit the device credential or the connection credential.
- Audio format: propose `pcm_16000` (16 kHz, mono, 16-bit) for both input and output.
  The backend allowlist must include it; the backend does not convert formats.
  The mic front-end runs 32-bit stereo internally; convert to 16-bit mono before sending.
- Max message size is 65,536 bytes, and the backend buffers at most 16 messages,
  so never send faster than real time.
- One active session per device. Use a new random `Idempotency-Key` per session; reuse it only to
  retry the same start request.
- After any WebSocket disconnect the session is over: start a new session with a new key and
  never reuse a connection credential.
- Use the ESP-IDF certificate bundle (`esp_crt_bundle`) for HTTPS/WSS; allow plain http/ws only
  for local development.

## Open questions

- Session end policy: silence timeout, button, or the agent ends it?
- Session-scoped vs legacy device socket for long meetings: a Wi-Fi drop ends a session-scoped
  session.
- Who supplies speaker identity?
- Confirm `pcm_16000` is allowlisted on the backend and set as the ElevenLabs agent output format.
- Which button enters setup mode (proposal: K2)?
- What is the factory reset gesture?
- Does factory reset preserve `deviceIdentifier`?
- Source of the stable `deviceIdentifier` (e.g. derived from the chip's eFuse MAC)?
- Backend URL reachable from the duck's Wi-Fi during development (not localhost)?
