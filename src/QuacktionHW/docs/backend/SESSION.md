# ESP32-S3 session and audio handoff

This document is the device-side implementation contract for session-based audio proxying. The ESP32-S3 detects the wake word locally, authenticates with its paired device credential, opens one session-scoped WebSocket, and exchanges audio with the backend. The backend owns account/session lifecycle and connects to ElevenLabs. Firmware must never receive or store ElevenLabs credentials.

Related backend references: [device protocol](device-protocol.md), [authentication and pairing](../AUTH.md), [audio simulator](../tools/Quacktion.AudioSimulator/Program.cs).

## Device provisioning

An account owner creates and pairs the device through the account API. Pairing returns a random 64-character hexadecimal device credential once; the backend stores only its hash. Deliver the credential to firmware through a trusted provisioning process and keep it in protected device storage. Do not put it in source control, logs, URLs, or normal control messages.

The device credential uses the `Device` authorization scheme for the session creation HTTP request. The separate session connection credential uses the `Session` scheme for a single WebSocket handshake. They are not interchangeable.

## Create a session

After the wake word and initial utterance have been captured, the firmware sends:

```http
POST /api/sessions/start
Authorization: Device <device-credential>
Idempotency-Key: <unique-request-id>
Content-Type: application/json
```

```json
{
  "deviceId": "<paired-device-id>",
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
  "connectionCredential": "<one-time-secret>",
  "expiresInSeconds": 60
}
```

The connection credential is short-lived (default 60 seconds, configurable up to 300), bound to this session and device, and stored server-side only as a hash. The session ID is an identifier, never an authentication secret. A credential is consumed once when the WebSocket authenticates. A retry with the same idempotency key while the session is still Ready returns the same session and rotates the credential; discard any earlier response credential.

The service enforces one active session per device. An already consumed or started idempotency key returns a conflict; start a new session with a new key after the previous session finishes.

## Open the audio WebSocket

Connect to the exact `webSocketUrl` returned by the API. Convert the scheme only if necessary for the client library (`wss` for HTTPS deployments, `ws` for local HTTP development).

```http
GET /ws/sessions/<session-id>/audio
Authorization: Session <connectionCredential>
Sec-WebSocket-Protocol: quacktion.audio.v1
Connection: Upgrade
Upgrade: websocket
```

The backend atomically consumes the credential, verifies the session/device association, connects to ElevenLabs and waits for its conversation initialization metadata. Only then does it persist the provider conversation ID, transition the session to `Active`, and send:

```json
{
  "version": 1,
  "type": "session.started",
  "requestId": "<same-idempotency-key>",
  "payload": { "sessionId": "<quacktion-session-id>", "status": "Active" }
}
```

The next device message must be the audio format negotiation below. The backend gives the device no provider URL, signed URL, API key, or provider conversation ID.

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

The values above are examples only. Obtain the actual input and output codec, sample rate and channel count from the firmware/provider integration owner. The backend accepts only exact formats configured in both its input/output allowlists and accepted by the initialized ElevenLabs conversation. Allowlists are empty by default. The backend does not resample or transcode.

Wait for `audio.ready` before transmitting microphone data. Then send raw audio bytes as WebSocket **binary** messages. Do not wrap audio bytes in JSON or Base64 on the device; the backend Base64 encodes them into the documented ElevenLabs Conversational AI `user_audio_chunk` event. Provider `audio` event payloads are decoded by the backend and delivered to the device as binary messages.

Use short frames for low latency and keep audio format/sample alignment valid. A single logical WebSocket message can be fragmented by the transport; the backend reassembles fragments. Maximum logical text or binary message size is 65,536 bytes. Input and output buffering is bounded to 16 messages, so firmware should not build an unbounded local queue when either connection is slow.

An ElevenLabs interruption causes the backend to discard queued provider audio and send this text control; firmware should immediately clear audio that has not yet been played:

```json
{ "version": 1, "type": "audio.clear" }
```

## End, failures and reconnect

End cleanly with:

```json
{ "version": 1, "type": "session.end", "requestId": "<unique-end-request-id>" }
```

The backend closes the provider conversation, persists `Completed` (or `Failed` if shutdown fails), releases the device reservation and responds with `session.ended`. A signed-in web client can also end the session through `POST /api/sessions/{sessionId}/end`; the active device socket receives the same control notification.

Provider disconnects produce `session.failed` and terminate the connection. A device transport disconnect also closes the current session; this endpoint's one-use connection credential does not support reconnecting to that session. Reconnect by creating a new session with a new idempotency key. Do not try to reuse the old connection credential.

WebSocket control messages are UTF-8 JSON with `version: 1`; audio uses binary messages only. The session audio endpoint supports `audio.start`, `session.end`, and server controls such as `session.started`, `audio.ready`, `audio.clear`, `session.start_failed`, `session.failed`, and `error`. It does not use application heartbeat messages; the WebSocket implementation's native ping/pong keepalive is used. Treat provider/network failures as session failures and surface a reconnect prompt to the user.

## Local simulator

The repository includes a PCM16 WAV client that performs the HTTP start, WebSocket authentication, audio negotiation, binary upload, binary download, WAV save, and clean end:

```bash
dotnet run --project tools/Quacktion.AudioSimulator -- http://localhost:5188 <device-id> <device-credential> input.wav output.wav [input-codec] [output-codec] [output-rate] [output-channels]
```

It supports PCM 16-bit WAV input/output only and does not resample. The device must already be created and paired. For live provider tests, configure `ElevenLabs__AgentId`, `ElevenLabs__ApiKey` for private agents, and exact `Audio__AllowedInputFormats__...` / `Audio__AllowedOutputFormats__...` values. Never log the device credential, session connection credential or audio contents.

The provider events follow the official [ElevenLabs Conversational AI WebSocket API](https://elevenlabs.io/docs/eleven-agents/api-reference/eleven-agents/websocket) and [client events](https://elevenlabs.io/docs/eleven-agents/customization/events/client-events). This is the conversational-agent endpoint, not the text-to-speech-only WebSocket API.
