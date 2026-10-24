# Quacktion BLE setup protocol v1

The contract between the Quacktion app and the duck firmware for Bluetooth setup. Behaviour and
requirements come from [onboarding.md](onboarding.md); this document fixes the wire format.
Items marked **[review]** need a decision from the app or backend side.

## 1. Overview

- The duck advertises only in **setup mode**: first boot with no saved configuration, or after the
  setup button is held for 3 s. Setup mode lasts at most 5 minutes, then the duck stops advertising
  and reboots into normal mode. Bluetooth is never on in normal mode.
- Transport is Espressif's provisioning framework (`protocomm` over BLE, NimBLE host) with
  **Security 2** (SRP6a key exchange, AES-256-GCM). Espressif's Android and iOS provisioning
  libraries implement this transport and handshake, including custom endpoints.
- Every operation is one encrypted request/response exchange on its own GATT characteristic.
  Everything except `proto-ver` and the handshake requires a completed Security 2 session.
- **The user approves the app by pressing the setup button (K2) on the duck.** Until then the app
  can only read device info and status and make the duck identify itself. Changing Wi-Fi or
  installing credentials requires approval (section 4).
- One app connection at a time. No OS-level pairing or bonding is needed or used.

## 2. Discovery

### Advertisement

| Field | Value |
| --- | --- |
| Complete 128-bit service UUID | `c5649ba0-9be0-43aa-bfb6-8902ab366fad` |
| Flags | LE General Discoverable, BR/EDR not supported |

Scan response:

| Field | Value |
| --- | --- |
| Complete local name | `Quacktion-XXXX`, where `XXXX` is the 4-character display suffix (uppercase hex) |
| Manufacturer specific data | company ID `0xFFFF` **[review]**, then byte 0 = protocol version (`0x01`), byte 1 = flags (bit 0: setup available) |

`0xFFFF` is the Bluetooth SIG test company ID; a shipped product needs an assigned ID.

The app filters scans by the service UUID and shows the local name. The name is a label for the
user, not proof of identity. Advertisements never contain credentials, account IDs, the setup
code or the DPP URI.

### Identifiers

| Concept | Where it comes from | Stable |
| --- | --- | --- |
| `deviceIdentifier` | Firmware, from the chip **[review: source, see section 10]** | Across reboots, Wi-Fi changes and (proposed) factory reset |
| `deviceId` | Backend, returned by pairing, sent with `backend.configure` | Until unpaired |
| BLE address | Random per setup window | No; never use it as an identity |
| Display suffix | Last 4 hex characters of `deviceIdentifier` | Same as `deviceIdentifier` |

## 3. GATT layout

One primary service. Each endpoint is a characteristic with read and write permission and a
Characteristic User Description descriptor (`0x2901`) holding the endpoint name, so the app can
discover endpoints by name instead of hardcoding UUIDs. Characteristic UUIDs are the service UUID
with characters 5 to 8 replaced by the endpoint's 16-bit ID.

| Endpoint name | 16-bit ID | Characteristic UUID | Needs session |
| --- | --- | --- | --- |
| `prov-session` | `0xFF51` | `c564ff51-9be0-43aa-bfb6-8902ab366fad` | Handshake only |
| `proto-ver` | `0xFF53` | `c564ff53-9be0-43aa-bfb6-8902ab366fad` | No |
| `device.info` | `0xFF60` | `c564ff60-9be0-43aa-bfb6-8902ab366fad` | Yes |
| `device.identify` | `0xFF61` | `c564ff61-9be0-43aa-bfb6-8902ab366fad` | Yes |
| `setup.status` | `0xFF62` | `c564ff62-9be0-43aa-bfb6-8902ab366fad` | Yes |
| `wifi.scan` | `0xFF63` | `c564ff63-9be0-43aa-bfb6-8902ab366fad` | Yes |
| `wifi.configure` | `0xFF64` | `c564ff64-9be0-43aa-bfb6-8902ab366fad` | Yes |
| `wifi.dpp.start` | `0xFF65` | `c564ff65-9be0-43aa-bfb6-8902ab366fad` | Yes |
| `wifi.dpp.cancel` | `0xFF66` | `c564ff66-9be0-43aa-bfb6-8902ab366fad` | Yes |
| `backend.configure` | `0xFF67` | `c564ff67-9be0-43aa-bfb6-8902ab366fad` | Yes |
| `setup.complete` | `0xFF68` | `c564ff68-9be0-43aa-bfb6-8902ab366fad` | Yes |
| `setup.cancel` | `0xFF69` | `c564ff69-9be0-43aa-bfb6-8902ab366fad` | Yes **[review: addition]** |

Firmware byte order for the service UUID (`protocomm_ble_config_t.service_uuid`, little-endian):
`ad 6f 36 ab 02 89 b6 bf aa 43 e0 9b a0 9b 64 c5`.

### Exchange model

1. The app **writes** the request to the endpoint's characteristic.
2. After the write is acknowledged, the app **reads** the same characteristic to get the response.

There are no notifications. The app polls `setup.status` (section 6) for progress.

### Size limits and fragmentation

- A request or response is at most **512 bytes** on the wire. After Security 2 adds its 16-byte
  tag, that leaves **496 bytes of plaintext**.
- Values longer than the negotiated ATT MTU are carried by standard long writes (prepared writes)
  and long reads (read blob); the BLE stacks on Android, iOS and the duck handle this. Request a
  larger MTU after connecting (Android `requestMtu(517)`); the protocol must also work at the
  default 23-byte MTU, and both must be tested.
- Results that can exceed 496 bytes (`wifi.scan`) are paged at the application level.

## 4. Security 2 session and button approval

### Why a button and not a code

The duck has no screen, so there is no good way to show a user a secret code. Instead, like
Bluetooth headphones, it relies on a physical action: the person setting it up presses the setup
button on the duck to approve the connected app.

- **Encryption:** Security 2 still runs, with a **fixed, published password** (below). It is not a
  secret and authenticates nothing; it only sets up AES-256-GCM so nobody listening can read the
  Wi-Fi password or the device credential.
- **Authorisation:** the button press. The duck accepts configuration only from the one app that
  was connected when its button was pressed.
- **Accepted risk:** someone within Bluetooth range who connects first, or who actively intercepts
  the link during the few minutes of setup, could be the one approved. The user would notice: their
  app would not get approval, and setup would fail. This replaces onboarding.md's "random setup
  code" and "no universal hardcoded password" requirements, by team decision.

### Credentials

- SRP6a username: **`quacktion`**. Password: **`quacktion-setup`**. The same for every duck.
- The duck still generates a fresh salt and verifier per setup window.

### Approval

1. After the handshake, `setup.status` shows `"approval": "pending"` and the duck blinks white.
   The app shows: "Press the button on your duck to continue."
2. The user **briefly presses K2** (the same button that is held for 3 s to start setup). The duck
   stops blinking, and `setup.status` shows `"approval": "approved"`.
3. The approval belongs to that BLE connection. If the app disconnects, the next connection must be
   approved again.
4. If no approval arrives within **2 minutes** of the handshake, the duck disconnects the app, so a
   stranger's phone cannot hold the only connection slot.

Before approval only `device.info`, `device.identify` and `setup.status` work. Every other
operation returns `approval_required`. The app should offer `device.identify` while waiting, so the
user knows which duck to press.

### Handshake

`proto-ver` (unencrypted) returns:

```json
{"prov":{"ver":"v1.1","sec_ver":2,"sec_patch_ver":1,"cap":[]},"quacktion":{"protocolVersion":1}}
```

`sec_patch_ver` 1 means the AES-GCM nonce's last 4 bytes are a big-endian counter that advances
after every encrypted message in either direction; a client that assumes patch version 0 fails
from the second message on. The app checks `prov.sec_ver == 2` and
`quacktion.protocolVersion == 1`, then runs the standard
Security 2 handshake on `prov-session` (protobuf `S2SessionCmd0/Resp0/Cmd1/Resp1` from ESP-IDF
`components/protocomm/proto/sec2.proto`). Espressif's app libraries do this when given the
username and password above.

- Only one session exists at a time; a second central cannot connect.

### Keeping the session in sync

AES-GCM uses a message counter that advances on every encrypted request and response on both
sides. If any write or read fails, times out, or the link drops, the counters may no longer match.
The app must then **disconnect, reconnect and redo the handshake** before retrying. A new connection
needs a new button press.

## 5. Message format

All endpoint payloads (inside the encryption) are UTF-8 JSON objects.

Request:

```json
{"v":1,"id":"<request id>", "...": "operation fields"}
```

Success response:

```json
{"v":1,"id":"<same id>","ok":true, "...": "result fields"}
```

Error response:

```json
{"v":1,"id":"<same id>","ok":false,"error":"<error code>"}
```

- `v` is the payload version; a duck rejects other versions with `unsupported_version`.
- `id` is chosen by the app: 1 to 36 ASCII characters, unique per intended operation. A UUID is fine.
- Unknown fields are ignored.

### Request IDs and retries

- The duck remembers the responses to the last **8** request IDs in the current setup window. A
  request that repeats a known `id` returns the stored response and is **not executed again**.
- To retry after a transport failure: reconnect, redo the handshake, resend with the **same** `id`.
- To start a new attempt deliberately (for example a different password), use a **new** `id`.
- Long-running operations (`wifi.scan`, `wifi.configure`, `wifi.dpp.start`, `backend.configure`)
  return as soon as they are accepted; their outcome is read from `setup.status`. Starting one while
  the same kind is already running returns `busy`; nothing runs twice.

## 6. Operations

### device.info

Request: `{"v":1,"id":"…"}`

Response:

```json
{
  "v": 1, "id": "…", "ok": true,
  "protocolVersion": 1,
  "deviceIdentifier": "<stable identifier>",
  "model": "waveshare-esp32-s3-audio-board",
  "firmwareVersion": "0.2.0",
  "setupState": "in_progress",
  "capabilities": {"manualWifi": true, "dpp": true, "identify": true}
}
```

`setupState` is `in_progress` or `ready_to_complete`. `model` is **[review]**: onboarding.md says
`waveshare-esp32-s3-ai-speaker`, but the board is the ESP32-S3-AUDIO-Board.

### device.identify

Request: `{"v":1,"id":"…"}`. The duck flashes its LEDs and quacks.

Response: `{"v":1,"id":"…","ok":true}`, or `rate_limited` with `"retryAfterMs": <n>` when called
again within 5 s.

### setup.status

Request: `{"v":1,"id":"…"}`

Response:

```json
{
  "v": 1, "id": "…", "ok": true,
  "setupSessionId": "<random per setup window>",
  "setupExpiresInSeconds": 212,
  "approval": "approved",
  "wifiState": "connecting",
  "backendState": "unlinked",
  "ssid": "Home Wi-Fi",
  "errorCode": null,
  "lastRequestId": "<id of the last long-running request>"
}
```

| `wifiState` | Meaning |
| --- | --- |
| `unconfigured` | No saved network |
| `scanning` | A scan is running |
| `awaiting_dpp` | DPP is listening for the phone |
| `connecting` | Joining a network |
| `connected` | Joined and got an IP address |
| `failed` | Last attempt failed; see `errorCode` |

| `backendState` | Meaning |
| --- | --- |
| `unlinked` | No device credential stored |
| `connecting` | Opening the device socket |
| `online` | Backend accepted the credential and the device socket is open |
| `failed` | Last attempt failed; see `errorCode` |

`ssid` is the network being joined or joined, never a password. `errorCode` is the code for the
most recent failure, or `null`. Poll about once a second while something is in progress. After
reconnecting, the app reads `setup.status` to recover the current state; nothing restarts.

An IP address does not mean the backend is reachable, and an unreachable backend does not mean the
Wi-Fi password is wrong; the two states are reported separately for that reason.

### wifi.scan

Start a scan: `{"v":1,"id":"…","action":"start"}` → `{"v":1,"id":"…","ok":true}`. `wifiState`
becomes `scanning`, then returns to its previous value when results are ready.

Read results: `{"v":1,"id":"…","action":"results","offset":0}` →

```json
{
  "v": 1, "id": "…", "ok": true,
  "total": 14, "next": 6,
  "networks": [
    {"ssid": "Home Wi-Fi", "rssi": -48, "auth": "wpa2_wpa3_psk", "supported": true},
    {"ssidHex": "e4b8ade69687", "rssi": -71, "auth": "wpa2_enterprise", "supported": false}
  ]
}
```

- Results are sorted by signal strength and deduplicated by SSID. Each page holds as many networks
  as fit in 496 bytes. `next` is the offset of the next page, or `null` at the end.
- `ssid` is present when the name is valid UTF-8; otherwise `ssidHex` holds the raw bytes.
- `auth` is one of `open`, `wpa_psk`, `wpa2_psk`, `wpa_wpa2_psk`, `wpa3_psk`, `wpa2_wpa3_psk`,
  `wpa2_enterprise`, `wpa3_enterprise`, `wep`, `owe`, `unknown`. `supported` is false for
  enterprise, WEP, WPA (version 1 only), OWE and unknown networks.
- The duck only sees 2.4 GHz networks.

### wifi.configure

Request:

```json
{"v":1,"id":"…","ssid":"Home Wi-Fi","password":"…","hidden":false}
```

- Use `ssidHex` instead of `ssid` for names that are not valid UTF-8. Set `hidden` to true for a
  network typed in by hand.
- SSID: 1 to 32 bytes. Password: empty for open networks, otherwise 8 to 63 characters or exactly
  64 hex digits.

Response: `{"v":1,"id":"…","ok":true}` once accepted. Then `wifiState` goes to `connecting`, then
`connected` or `failed`.

- The previous working network stays saved until the new one has connected. If the new one fails,
  the duck goes back to the previous network (if any).
- The password is never echoed in any response or log, and no endpoint returns saved passwords.
- Starting a configure while DPP is active cancels DPP first.

### wifi.dpp.start

Android only: offer it when `capabilities.dpp` is true and `WifiManager.isEasyConnectSupported()`.

Request: `{"v":1,"id":"…"}`

Response:

```json
{"v":1,"id":"…","ok":true,"uri":"DPP:C:81/6;M:…;K:…;;","expiresInSeconds":120}
```

- The app passes `uri` to Android's Easy Connect flow
  (`Settings.ACTION_PROCESS_WIFI_EASY_CONNECT_URI`). The URI is never shown as a QR code.
- `wifiState` is `awaiting_dpp` while the duck listens, then `connecting`, then `connected` or
  `failed`. Android returning to the app does not mean the duck connected; wait for `setup.status`.
- The duck listens on channel 6 **[review: channel list]**.
- The bootstrap key stays on the duck and is valid only for this attempt.

### wifi.dpp.cancel

Request: `{"v":1,"id":"…"}` → `{"v":1,"id":"…","ok":true}`. Stops listening and invalidates the
URI. `wifiState` returns to its previous value. Calling it when DPP is not running is not an error.

### backend.configure

Request:

```json
{"v":1,"id":"…","deviceId":"<backend device id>","credential":"<64 hex characters>"}
```

The app gets these from `POST /api/devices/{id}/pair` and sends them straight to the duck without
storing or logging the credential.

Response: `{"v":1,"id":"…","ok":true}` only after the duck has written both values to storage.
Then, once Wi-Fi is `connected`, `backendState` goes to `connecting`, then `online` or `failed`.

- The credential is never echoed or logged and no endpoint returns it.
- If storing fails (`storage_failed`), the backend cannot re-issue the same credential: the app
  follows the unpair/re-pair flow from the backend docs.
- A rejected credential (`credential_rejected`) is an account-linking problem, not a Wi-Fi one.

### setup.complete

Request: `{"v":1,"id":"…"}`

- Allowed only when `wifiState` is `connected` and `backendState` is `online`; otherwise
  `not_ready`.
- Response: `{"v":1,"id":"…","ok":true,"rebootInMs":1000}`. The duck then stops advertising,
  closes the connection and reboots into normal mode.
- The app shows Ready only after its own backend device-status check confirms the duck is online.

### setup.cancel

Request: `{"v":1,"id":"…"}` → `{"v":1,"id":"…","ok":true,"rebootInMs":1000}`.

Ends setup without completing. Configuration that was already working stays; anything not yet
proven is discarded. The duck reboots into normal mode, or waits unconfigured if it has nothing
saved.

## 7. Error codes

Stable values of `error` (in error responses) and `errorCode` (in `setup.status`). The app maps
them to its own user-facing text and never shows the raw code.

| Code | Where | Meaning |
| --- | --- | --- |
| `invalid_request` | any | Malformed JSON or missing or invalid field |
| `approval_required` | all but `device.info`, `device.identify`, `setup.status` | The button on the duck has not been pressed for this connection |
| `unsupported_version` | any | `v` is not 1 |
| `busy` | long-running ops | The same kind of operation is already running |
| `rate_limited` | `device.identify` | Called again too soon; see `retryAfterMs` |
| `not_ready` | `setup.complete` | Wi-Fi not connected or backend not online |
| `setup_expired` | any | The setup window has closed |
| `invalid_ssid` | `wifi.configure` | SSID empty or longer than 32 bytes |
| `invalid_password` | `wifi.configure` | Password length or format not allowed |
| `unsupported_auth` | `wifi.configure`, status | Enterprise, WEP or unknown security |
| `network_not_found` | status | No 2.4 GHz network with that name answered |
| `wrong_password` | status | The network rejected the password |
| `no_ip_address` | status | Joined but got no IP address (DHCP failed) |
| `wifi_timeout` | status | No connection within 30 s |
| `scan_failed` | status | The scan could not run |
| `dpp_unavailable` | `wifi.dpp.start` | DPP cannot start on this duck right now |
| `dpp_failed` | status | The phone and duck could not complete DPP |
| `dpp_timeout` | status | No DPP exchange before the URI expired |
| `invalid_credential` | `backend.configure` | Device ID or credential has the wrong format |
| `storage_failed` | `backend.configure` | Could not save the credential |
| `backend_unreachable` | status | Could not reach the backend over this network |
| `credential_rejected` | status | The backend refused the device credential |
| `backend_error` | status | The backend returned an unexpected error |
| `internal_error` | any | Unexpected firmware error |

## 8. App flow (reference)

1. User holds the setup button for 3 s (or powers on a new duck); the LEDs turn white.
2. App scans for the service UUID and lists `Quacktion-XXXX` devices.
3. User picks one; app reads `proto-ver` and runs the Security 2 handshake with the fixed password.
4. App shows "Press the button on your duck" and polls `setup.status`; it may call
   `device.identify` so the right duck flashes and quacks.
5. User briefly presses the button; `approval` becomes `approved`.
6. `device.info` → `deviceIdentifier` → backend `POST /api/devices`, then `/pair`.
7. `backend.configure` with the returned `deviceId` and credential.
8. Wi-Fi: `wifi.dpp.start` on supported Android phones, else `wifi.scan` + `wifi.configure`.
9. Poll `setup.status` until `wifiState` is `connected` and `backendState` is `online`.
10. Confirm online through the backend, then `setup.complete`.

Steps 7 and 8 may run in either order; `backendState` waits for Wi-Fi.

## 9. Testing checklist

- Default 23-byte MTU and a large MTU both work, including a full `wifi.scan` page and a DPP URI.
- A link drop mid-exchange: reconnect, re-handshake, retry with the same `id`; nothing runs twice.
- Before the button press, configuration operations return `approval_required`; after it they work.
- An app that is never approved is disconnected after 2 minutes; a reconnect needs a new press.
- `wifi.configure` with a wrong password reports `wrong_password` and the previous network still works.
- Wi-Fi connected but backend down reports `backend_unreachable`, not a Wi-Fi error.
- `setup.complete` before both are ready returns `not_ready`.
- Two ducks in setup mode: identify affects only the selected one.

## 10. Open questions [review]

- **`deviceIdentifier` source.** Proposal: `quack-` plus 12 hex characters of SHA-256 of the
  chip's factory MAC from eFuse, so it survives reflashing and factory reset and does not expose
  the MAC directly. Not an authenticity proof.
- **Factory reset** gesture, and confirmation that it keeps `deviceIdentifier`.
- **Setup button**: K2 proposed (K1 is mute).
- **Manufacturer company ID** for shipping (`0xFFFF` is test-only).
- **`setup.cancel`**: added here; onboarding.md lists cancel as an exit but no operation for it.
- **Setup window length while the app is connected**: 5 minutes total may be too short for DPP
  plus pairing. Proposal: 5 minutes to authenticate, then up to 10 minutes while a session is open.
- **DPP listen channels**: channel 6 only, or 1, 6 and 11 (slower to find, more robust).
- **`model` string**: see `device.info`.
- **App platform**: which BLE library the app uses (native Espressif libraries, a React Native or
  Flutter wrapper, or a custom Security 2 implementation).
