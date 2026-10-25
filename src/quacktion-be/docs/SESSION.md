# ESP32-S3 session and audio handoff

This document is the client contract for assistant audio. A client authenticates with a device credential or account bearer token and exchanges audio with the backend over a session socket; firmware can also reuse its persistent device socket. The backend owns session lifecycle and connects to Gemini Live. Clients never receive Gemini Live credentials.

Related backend references: [device protocol](device-protocol.md), [authentication and pairing](../AUTH.md), [audio simulator](../tools/Quacktion.AudioSimulator/Program.cs).

## Device provisioning

An account owner creates and pairs a device through the account API. Firmware uses its paired device credential for both session creation and audio; apps use their account bearer token. Do not put credentials in source control, logs, URLs, or normal control messages.

Session creation and its audio WebSocket accept either an account access token with the `Bearer` authorization scheme or a paired device credential with the `Device` scheme. Bearer sessions stream directly between the authenticated client and provider without a device. Device-authenticated sessions are associated with that paired device.

## Create a session

After the wake word and initial utterance have been captured, the firmware sends:

```http
POST /api/sessions/start
Authorization: Device <deviceCredential>
Idempotency-Key: <unique-request-id>
Content-Type: application/json
```

```json
{
  "name": "ESP32-S3 voice session"
}
```

`Idempotency-Key` is preferred; alternatively supply `requestId` in the JSON body. Use a unique value for each intended session and reuse it only to retry that same request. The initial prompt is not part of this endpoint: the device sends microphone audio after opening the WebSocket. If audio includes the wake phrase, remove the wake phrase locally before sending the user utterance. Do not send a separate text transcription of the same audio.

Successful response (`200`):

```json
{
  "sessionId": "<quacktion-session-id>",
  "status": "Ready",
  "webSocketUrl": "wss://api.example.com/ws/sessions/<session-id>/audio",
  "expiresInSeconds": 60
}
```

Apps may use `Authorization: Bearer <accessToken>` instead. For firmware, the server derives the owner and device from the device credential, so no device ID is sent. **Device authentication is the supported design on the session socket.** No `connectionCredential` is returned and `Authorization: Session` is not supported. `expiresInSeconds` is the remaining window in which to connect. A retry while Ready returns the same session without extending its deadline.

Expired Ready sessions fail with `connection_window_expired` and release the device. Maintenance runs every ten seconds; a new start also repairs an expired, terminal or missing session reservation immediately. Use a new idempotency key after expiry. The default connection window is 60 seconds.

The service enforces one active session per device. An already consumed or started idempotency key returns a conflict; start a new session with a new key after the previous session finishes.

## Open the audio WebSocket

Connect to the exact `webSocketUrl` returned by the API. Convert the scheme only if necessary for the client library (`wss` for HTTPS deployments, `ws` for local HTTP development).

```http
GET /ws/sessions/<session-id>/audio
Authorization: Device <deviceCredential>
Sec-WebSocket-Protocol: quacktion.audio.v1
Connection: Upgrade
Upgrade: websocket
```

Authenticate the WebSocket with either the owning account's `Authorization: Bearer <accessToken>` or the matching device's `Authorization: Device <deviceCredential>`. The backend verifies the account or device owns the session, atomically claims the pending connection, connects to Gemini Live and waits for `setupComplete`. Only then does it persist the backend provider-connection ID, transition the session to `Active`, and send:

```json
{
  "version": 1,
  "type": "session.started",
  "requestId": "<same-idempotency-key>",
  "payload": { "sessionId": "<quacktion-session-id>", "status": "Active" }
}
```

The next device message must be the audio format negotiation below. The backend gives the device no provider URL, signed URL, API key, or backend provider-connection ID.

If initialization fails, the socket sends `session.start_failed` with the request ID, session ID and a stable error `code`, then closes. The session is persisted as Failed. Do not retry the same failed session; resolve the provider/configuration issue and create a new session.

## Negotiate audio and stream

Before sending audio, send one JSON text message:

```json
{
  "version": 1,
  "type": "audio.start",
  "audio": {
    "protocolVersion": 1,
    "input":  { "codec": "pcm_16000", "sampleRate": 16000, "channels": 1 },
    "output": { "codec": "pcm_16000", "sampleRate": 16000, "channels": 1 }
  }
}
```

Use the illustrated `pcm_16000`, 16000 Hz, mono formats with current hardware. Gemini receives PCM16 at 16 kHz; its 24 kHz output is low-pass filtered and resampled by the backend. Clients that support 24 kHz may instead negotiate output `pcm_24000`, 24000 Hz, mono. Both output formats are enabled by default.

Wait for `audio.ready` before sending raw PCM16 little-endian **binary** frames, ideally 20–40 ms each. The backend Base64 encodes input inside Gemini `realtimeInput.audio` with `mimeType: "audio/pcm;rate=16000"`. It decodes Gemini `serverContent.modelTurn.parts[].inlineData` and streams raw PCM binary output at the negotiated rate.

Use short frames for low latency and keep audio format/sample alignment valid. A single logical WebSocket message can be fragmented by the transport; the backend reassembles fragments. Maximum logical text or binary message size is 65,536 bytes. Input and output buffering is bounded to 16 messages, so firmware should not build an unbounded local queue when either connection is slow.

The input queue waits for capacity without dropping frames or closing because it is full. There is no real-time pacing gate: buffered 40 ms frames can be uploaded at 3x, then live frames at real time. This confirms backend transport behavior, not Gemini's turn detection at accelerated speed; that still needs a real-provider/hardware trial. Provider send failures now surface as session failures instead of silently losing microphone audio.

For the lowest setup latency outside meeting mode, reuse `/ws/devices/{deviceId}`: send `session.start` with `payload.promptFormat: "audio"`, wait for `session.started`, negotiate `audio.start`, wait for `audio.ready`, then send binary frames. `session.end` returns `session.ended` and leaves this device socket open for the next wake. See [meeting framing and coexistence](transcription-protocol.md).

An Gemini Live interruption causes the backend to discard queued provider audio and send this text control; firmware should immediately clear audio that has not yet been played:

```json
{ "version": 1, "type": "audio.clear" }
```

## End, failures and reconnect

End cleanly with:

```json
{ "version": 1, "type": "session.end", "requestId": "<unique-end-request-id>" }
```

The backend closes the provider conversation, persists `Completed` (or `Failed` if shutdown fails), releases the device reservation and responds with `session.ended`. A signed-in web client can also end the session through `POST /api/sessions/{sessionId}/end`; the active device socket receives the same control notification.

An orderly provider close produces `session.ended`. Unexpected provider disconnects that exhaust reconnection produce `session.failed`; both terminate the session audio connection after sending the terminal control. A transport disconnect also closes the current session; the session permits one audio WebSocket connection during the configured connection window. Reconnect by creating a new session with a new idempotency key.

WebSocket control messages are UTF-8 JSON with `version: 1`; audio uses binary messages only. The session audio endpoint supports `audio.start`, `session.end`, and server controls such as `session.started`, `audio.ready`, `audio.clear`, `session.start_failed`, `session.failed`, and `error`. It does not use application heartbeat messages; the WebSocket implementation's native ping/pong keepalive is used. Treat provider/network failures as session failures and surface a reconnect prompt to the user.

## Local simulator

The repository includes a PCM16 WAV client that performs the HTTP start, WebSocket authentication, audio negotiation, binary upload, binary download, WAV save, and clean end:

```bash
dotnet run --project tools/Quacktion.AudioSimulator -- http://localhost:5188 <access-token> input.wav output.wav [input-codec] [output-codec] [output-rate] [output-channels]
```

It supports PCM16 WAV input/output. Set `Gemini__ApiKey` server-side. Input must be mono 16 kHz. The backend supports 16 kHz resampled or 24 kHz native output. Never log credentials or meeting audio.

The provider contract follows the [Gemini Live API wire reference](https://ai.google.dev/api/live). See [Gemini configuration, tools and deployment](GEMINI.md).
