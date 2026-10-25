# Quacktion conversations: frontend and speaker integration

This guide describes how the Flutter app creates and displays voice conversations, and how ESP32-S3 firmware uses the same backend session/audio service. The backend owns session state, account/device authorization, provider connections, and transcripts. Neither client connects to ElevenLabs directly.

## System shape

```text
Flutter app ── account Bearer token ──┐
                                     ├─ POST /api/sessions/start
Speaker ───── paired Device token ───┘          │
                                                └─ sessionId + one-use connection credential (speaker flow)
Both clients open /ws/sessions/{id}/audio ────── backend ───── ElevenLabs agent
Both clients receive/send PCM16 audio on that socket
Flutter loads transcript/history over REST and receives live notifications over SignalR
```

The app and speaker use the same session-audio protocol, but authenticate differently:

| Client | Create session | Authenticate audio WebSocket | Ownership |
| --- | --- | --- | --- |
| Flutter app | `Authorization: Bearer <account access token>` | Same account Bearer token | Session belongs to the signed-in account; no device ID |
| Speaker | `Authorization: Device <device credential>` | `Authorization: Session <one-use connection credential>` | Backend resolves owner and device from the paired device credential |

Never send the account token to firmware. Never send either device credential to the app. The connection credential is short-lived and single-use; the session ID is an identifier, not a secret.

## Flutter implementation

The app composition root in `lib/app/dependencies.dart` builds `BackendApi`, `ApiSessionRepository`, `SignalRSessionEventClient`, and `SessionAudioTransport`. `AppShell` passes the voice-controller factory to the conversation screen.

The relevant responsibilities are:

- `lib/features/conversations/data/session_repository.dart` loads session lists, individual sessions, and transcripts from REST. It converts transcript speaker/source fields into chat messages.
- `lib/features/conversations/data/session_event_client.dart` connects to `/hubs/sessions` with the current account access token, joins the viewed session, and forwards status/transcript events. REST refresh recovers missed events.
- `lib/features/conversations/application/conversation_controller.dart` owns the selected conversation, history, transcript refresh, and event subscriptions. Starting voice adds the returned backend session to history and selects it.
- `lib/features/conversations/data/session_audio_transport.dart` creates the audio session, opens the authenticated WebSocket, negotiates PCM, sends microphone frames, plays binary replies, handles controls, and closes the session.
- `lib/features/voice/application/voice_controller.dart` coordinates transport state, mute, error/status text, and microphone-level data for the UI.
- `lib/features/conversations/data/phone_audio_engine.dart` exposes the Flutter method/event channels implemented in `android/app/src/main/kotlin/com/example/quacktion_frontend/MainActivity.kt`.
- `lib/features/conversations/presentation/widgets/conversation_voice_controls.dart` binds voice lifecycle to the selected chat/screen. The reusable voice widget displays the inline waveform, mute, and end controls.

### App voice startup

1. User presses **Start conversation**. The app generates an idempotency key and calls `POST /api/sessions/start` with the account Bearer token and a conversation name.
2. The backend returns `sessionId`, `status: Ready`, `webSocketUrl`, and `expiresInSeconds`. It does not return a one-time connection credential for the Bearer flow.
3. The app opens the exact returned URL with `Authorization: Bearer <access token>` and WebSocket subprotocol `quacktion.audio.v1`.
4. On `session.started`, the app sends `audio.start` with the configured input/output formats. It waits for `audio.ready` before opening the microphone.
5. Android records PCM16 mono frames and sends them as WebSocket **binary** messages. Server binary messages are PCM replies and are played through Android audio output.
6. The app receives transcript/status events through SignalR and refreshes REST transcript/history as needed. Audio does not travel through SignalR.
7. Mute stops sending microphone frames but keeps the session open. End sends `session.end`, waits for `session.ended` (with a bounded timeout), stops capture/playback, and closes the socket.

Control messages are UTF-8 JSON. Audio messages are raw PCM bytes: never wrap audio in JSON or Base64 on the client. `audio.clear` means immediately discard queued, unplayed reply audio. `session.start_failed`, `session.failed`, and `error` are failures; show the error and create a new session for another attempt.

### Conversation history and continuity

History/status/transcript are account-scoped backend data. The UI opens the most recently updated conversation; it does not make a new session until voice starts. SignalR is best-effort, so refetch session/transcript after reconnect or missed events.

Starting on the app and starting on the speaker currently creates distinct sessions associated with the same account. Their saved sessions and transcripts appear in the account's history, but the current audio WebSocket session is not transferred between clients. A consumed connection credential cannot be reused, and a dropped socket ends that session. True mid-session handoff requires an explicit backend handoff/reconnect feature; do not emulate it by reusing the session ID or credentials.

## Speaker firmware integration

The current hardware checkout already contains the session client split across `QuacktionHW/firmware/main/session.c`, `session_audio.c`, and `app.c`. Use these as the implementation reference when extending or reviewing firmware; keep the HTTP/session logic separate from audio capture/playback and the existing device-presence socket.

There is one mismatch to correct when updating the current firmware: [session.c](../../QuacktionHW/firmware/main/session.c) still includes `deviceId` in the `/api/sessions/start` JSON body. The current backend derives that ID from `Authorization: Device ...` and its start-request DTO has no `deviceId` field. Remove that JSON property; keep the configured ID for routes such as `/ws/devices/{deviceId}` that explicitly require it. The checked-in hardware `docs/backend/SESSION.md` also describes the older body shape, so follow the current backend controller/DTO and this note for session creation.

### Speaker startup and authorization

1. Obtain the paired `deviceId` and device credential through the trusted pairing/provisioning flow. Store the credential in protected device storage. Do not hard-code, log, or commit real credentials.
2. When Wi-Fi is online and the device is paired, maintain the existing `GET /ws/devices/{deviceId}` connection with `Authorization: Device <credential>` and `quacktion.v1`. This reports that the speaker is online and supports device control. It is **not** the session audio stream.
3. On wake-word/button activation, capture the user's utterance locally. Exclude the wake phrase. Create a random request/idempotency key for this intended session.
4. POST `/api/sessions/start` over HTTPS with `Authorization: Device <device credential>`, `Idempotency-Key: <request id>`, and a body such as `{"name":"Quacktion duck session"}`. The backend resolves the paired device ID and owning account from the authenticated device credential; do not send `deviceId` in this request body.
5. Require a `Ready` response with `sessionId`, `webSocketUrl`, `connectionCredential`, and `expiresInSeconds`. Keep the connection credential only long enough to open the WebSocket, then zeroize it. If retrying an ambiguous POST, reuse its idempotency key; a retry may rotate the returned one-time credential, so discard the earlier credential.
6. Connect to the exact returned URL with `Authorization: Session <connectionCredential>` and subprotocol `quacktion.audio.v1`. Do not use the device credential or account token as this WebSocket credential.
7. Wait for `session.started`, send `audio.start` with the agreed formats, then wait for `audio.ready`. Only then send PCM input and play server binary replies.
8. On `audio.clear`, flush unplayed speaker audio immediately. On end, send `session.end` with a unique request ID and wait for `session.ended`. On start/provider failure, close and start a new session with a new idempotency key after resolving the failure.

An audio WebSocket is session-scoped and one-use. It has no application heartbeat; use WebSocket ping/pong. If it drops, do not reconnect with the consumed credential or assume the session can resume. Create a new session. Continue to use the separate device socket for online presence.

### Audio format and real-time behavior

The current app and speaker examples use `pcm_16000`: signed PCM16, 16,000 samples/second, mono. This is a deployment agreement, not an automatic conversion. The backend does not resample; input and output formats must both exactly match its allowlists and the initialized agent/provider format. If the device microphone/I2S path is stereo or uses another rate, convert to mono/16 kHz PCM16 before sending, or coordinate a different format across firmware, app, provider, and backend.

Send short frames at real-time pace. Do not buffer an entire utterance and burst it after `audio.ready`; keep any pre-ready wake-word utterance buffer bounded. Keep WebSocket logical messages within 65,536 bytes and apply backpressure—the backend queues at most 16 messages in either direction. Playback must consume server binary frames in order. Do not log audio payloads.

### Firmware integration checklist

- Keep device credentials and one-use session credentials out of serial logs and source control; clear temporary copies after use.
- Use ESP-IDF certificate verification (`esp_crt_bundle`) for HTTPS/WSS. Permit insecure `http/ws` only for local development.
- Use a new idempotency key per intended session; reuse it only to retry that same POST.
- Do not send a transcription and the corresponding audio utterance together.
- Handle fragmented WebSocket messages correctly and distinguish text controls from binary audio.
- End sessions on the intended user/device action and on unrecoverable transport failure. Surface failures through the device status/LED behavior.
- Keep audio-path failures separate from device-presence status; losing an audio session should not erase pairing or Wi-Fi configuration.
- Validate microphone levels, playback, echo behavior, audio clearing, and stop/restart on the physical speaker. Unit tests cannot validate I2S routing or acoustic behavior.

## Verification and source references

Frontend checks: `flutter analyze`, `flutter test`, and `flutter build apk --debug`. Verify a real conversation only with a backend configured for the chosen audio formats and a working agent; no mock reply should be used as a fallback.

Firmware session acceptance: log the session ID/status without secrets, observe `session.started` then `audio.ready`, confirm the device sends and receives binary audio, and verify clean `session.ended`. Use the backend session/history/transcript API to confirm persistence.

Protocol source of truth:

- Backend app flow: [FE-SESSION.md](../../QuacktionBE/docs/FE-SESSION.md)
- Backend session/audio wire protocol: [SESSION.md](../../QuacktionBE/docs/SESSION.md)
- Firmware implementation notes: [hardware SESSION.md](../../QuacktionHW/docs/backend/SESSION.md) and [firmware instructions](../../QuacktionHW/firmware/CLAUDE.md)

If these local instructions disagree with backend protocol documentation, resolve the discrepancy before changing a client. The ElevenLabs API key belongs only on the backend.
