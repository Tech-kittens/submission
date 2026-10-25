# Device protocol v1

This is the backend transport contract. Current defaults support mono PCM16 input at 16 kHz and output at 16 or 24 kHz. Hardware latency and compatibility still need validation on the duck. [Meeting segments and mode controls](transcription-protocol.md) share this connection.

## Provisioning

1. An authenticated account calls `POST /api/devices` with a unique device identifier, name and firmware version. The backend sets ownership from the JWT, never from a supplied account ID. Registration returns the device and a pairing challenge (ID, random challenge, five-minute expiry).
2. The same authenticated owner confirms `POST /api/devices/{id}/pair` with `{ "challengeId", "challenge" }`. The challenge is bound to owner/device and atomically consumed once. The device must be unpaired. The response contains a new random credential, returned exactly once.
3. The owner installs the credential on the physically controlled device using a trusted provisioning channel. **That physical channel and firmware secure storage are not implemented here.** A device identifier is not hardware attestation. This initial flow does not prove factory identity or support unauthenticated claim-by-serial.
4. The device sends the credential in the `Authorization: Device <credential>` handshake header over WSS. Do not use a query string, an account JWT, or the Gemini Live API key.

Only hashes of challenges/credentials are persisted. Unpair revokes the credential and closes the socket; end an active associated session first. If provisioning or the response fails after consuming the challenge, obtain another challenge while unpaired. If the credential was committed but lost, unpair and pair again. Pairing state is fail-closed across crashes; it does not return the old credential. Ownership transfer requires unpair/delete by the old owner followed by registration/provisioning under the new account. A stolen credential authorizes that device until revoked; rotate by unpair/pair.

## Handshake

`GET /ws/devices/{deviceId}` must upgrade to WebSocket, supply the device authorization header and request `Sec-WebSocket-Protocol: quacktion.v1`. Authentication precedes accepting the connection. The account must remain active. Device credentials are rechecked at connection acceptance and every five seconds thereafter.

A reconnect atomically replaces and aborts the previous connection on the single supported transport host. A replaced connection cannot remove the new registry entry. Reconnect explicitly repeats session association and audio negotiation; it does not restart the meeting or cancel outstanding tasks.
Device disconnects leave the active session available for reconnection. Active sessions close after the configured `Sessions:InactiveTimeoutSeconds` without transcript activity (default 1800 seconds; set to 0 to disable).

## Session-scoped audio connection

For a session-scoped audio stream, authenticate `POST /api/sessions/start` using either `Authorization: Bearer <accessToken>` for a direct account session or `Authorization: Device <deviceCredential>` to associate the paired device. Send `{ "requestId", "name" }`; the device credential identifies the device, so no device ID is sent. `Idempotency-Key` may be used instead of the body request ID. The response contains `sessionId`, `status: "Ready"`, `webSocketUrl` and `expiresInSeconds`. Retry with the same request ID before connecting. The session ID is never a credential.

Connect to the returned `/ws/sessions/{sessionId}/audio` URL using either the owning account's `Authorization: Bearer <accessToken>` or the matching device's `Authorization: Device <deviceCredential>`, plus `Sec-WebSocket-Protocol: quacktion.audio.v1`. The provider is initialized before the server reports the session Active. Send `audio.start`, wait for `audio.ready`, then send raw binary frames in the negotiated format. Do not send text and audio for the same utterance. Use `session.end` to finish. After a session socket disconnect, create a new session with a new idempotency key. Outside meeting mode, firmware can avoid a new TLS connection by using the persistent device socket flow below.

## Framing


- Text messages are UTF-8 JSON controls. `version: 1` is required. Control names are case-sensitive; JSON property matching is case-insensitive. New session/audio controls and their responses use lower-case dotted names and camel-case fields; the legacy PascalCase controls remain accepted.
- Binary messages are audio payloads only, in the negotiated input/output formats. There is no unannounced header/codec inference.
- Maximum logical message size is 65,536 bytes, including all fragments. Fragments of different message types cannot be combined. There is a 45-second receive/assembly timeout and a 10-second send timeout.
- Input and output channels each hold at most 16 complete messages. Writers await capacity; slow peers are bounded by timeouts. This is backpressure, not unbounded buffering or silent audio dropping while active.
- Send a `Heartbeat` control at least every 20 seconds. Idle transport timeout is 45 seconds. Other traffic also counts as transport activity. TCP/WebSocket keepalives supplement the application heartbeat.

Examples (device → backend):

```json
{"version":1,"type":"heartbeat"}
{"version":1,"type":"session.start","requestId":"unique-request-id","payload":{"initialPrompt":"What is the current status of our sprint?","promptFormat":"text"}}
{"version":1,"type":"audio.start","audio":{"protocolVersion":1,"input":{"codec":"pcm_16000","sampleRate":16000,"channels":1},"output":{"codec":"pcm_16000","sampleRate":16000,"channels":1}}}
{"version":1,"type":"audio.stop"}
{"version":1,"type":"session.end","requestId":"unique-request-id"}
```

Wake-word firmware can create and start a session in one authenticated request. The wake word itself is local and must be excluded from the prompt:

```json
{"version":1,"type":"session.start","requestId":"unique-request-id","payload":{"initialPrompt":"What is the current status of our sprint?","promptFormat":"text"}}
```

The backend creates the session, connects the configured Gemini Live model, and returns `session.started` with the Quacktion session ID. Repeating a `requestId` returns the same session. A start failure returns `session.start_failed` with that request ID. `promptFormat: "audio"` reserves the same lifecycle for audio-only initial utterances; after `session.started`, firmware negotiates audio with `audio.start` and streams the utterance as binary frames. It must not also send a text transcription of those same frames. End with `{"version":1,"type":"session.end","requestId":"unique-request-id"}`.

Use `pcm_16000`, 16000 Hz, mono in both directions on current hardware. These formats are enabled by default. The backend resamples Gemini output from 24 kHz to 16 kHz.

`StartSession` can start an existing Created session or attach to an Active session. Its owner and device must match the authenticated connection. It cannot create a session for a supplied account. `AudioStart` requires this association, an Active session, and successful format negotiation. Binary data before that is rejected. `AudioStop` cancels the current stream and discards queued input; a later AudioStart renegotiates. `EndSession` tears down voice resources and sends `session.ended`, preserving this device socket and all Quacktions. A new wake starts a new session on the same connection. Lowercase `audio.start` receives `audio.ready`; PascalCase `AudioStart` retains its legacy response.

Legacy backend controls include `DeviceConnected`, `Heartbeat`, `StartSession`, `AudioStart`, `AudioStop`, and `Error`. `Error.Code` gives a stable reason such as `audio_not_started`, `unsupported_audio_format`, or `message_too_large`. Legacy framing violations end the connection; meeting control validation errors return lowercase `error` and allow recovery. Outgoing binary messages carry provider-adapted audio. Oversize provider output is rejected rather than blindly forwarded.

Provider adapters must serialize writes to their own transport, implement cancellation, reject unsupported formats, and map transcripts with real source-event IDs. Speaker identity remains null when unknown. The backend stores no raw audio. Audio buffers exist only for the bounded active connection lifetime.

Gemini Live transport preserves the device binary/control protocol. Input and output remain PCM16 little-endian, mono, 16 kHz on the existing ESP32 firmware. The backend resamples Gemini’s native 24 kHz output. No firmware format change is required; physical hardware must still be validated.
