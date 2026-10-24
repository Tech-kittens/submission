# Device protocol v1

This is a backend transport contract, **not a claim of firmware compatibility**. No codec, sample rate, channel configuration, frame duration or hardware pinout is assumed. The production audio format allowlists are empty until hardware/provider integration is validated.

## Provisioning

1. An authenticated account calls `POST /api/devices` with a unique device identifier, name and firmware version. The backend sets ownership from the JWT, never from a supplied account ID. Registration returns the device and a pairing challenge (ID, random challenge, five-minute expiry).
2. The same authenticated owner confirms `POST /api/devices/{id}/pair` with `{ "challengeId", "challenge" }`. The challenge is bound to owner/device and atomically consumed once. The device must be unpaired. The response contains a new random credential, returned exactly once.
3. The owner installs the credential on the physically controlled device using a trusted provisioning channel. **That physical channel and firmware secure storage are not implemented here.** A device identifier is not hardware attestation. This initial flow does not prove factory identity or support unauthenticated claim-by-serial.
4. The device sends the credential in the `Authorization: Device <credential>` handshake header over WSS. Do not use a query string, an account JWT, or the ElevenLabs API key.

Only hashes of challenges/credentials are persisted. Unpair revokes the credential and closes the socket; end an active associated session first. If provisioning or the response fails after consuming the challenge, obtain another challenge while unpaired. If the credential was committed but lost, unpair and pair again. Pairing state is fail-closed across crashes; it does not return the old credential. Ownership transfer requires unpair/delete by the old owner followed by registration/provisioning under the new account. A stolen credential authorizes that device until revoked; rotate by unpair/pair.

## Handshake

`GET /ws/devices/{deviceId}` must upgrade to WebSocket, supply the device authorization header and request `Sec-WebSocket-Protocol: quacktion.v1`. Authentication precedes accepting the connection. The account must remain active. Device credentials are rechecked at connection acceptance and every five seconds thereafter.

A reconnect atomically replaces and aborts the previous connection on the single supported transport host. A replaced connection cannot remove the new registry entry. Reconnect explicitly repeats session association and audio negotiation; it does not restart the meeting or cancel outstanding tasks.
Device disconnects leave the active session available for reconnection. Active sessions close after the configured `Sessions:InactiveTimeoutSeconds` without transcript activity (default 1800 seconds; set to 0 to disable).

## Session-scoped audio connection

For the session-scoped proxy, authenticate `POST /api/sessions/start` using `Authorization: Device <credential>` and send `{ "deviceId", "requestId", "name" }`. `Idempotency-Key` may be used instead of the body request ID. The response contains `sessionId`, `status: "Ready"`, `webSocketUrl`, `connectionCredential` and `expiresInSeconds`. Retry with the same request ID before connecting to recover a Ready session; the server rotates the credential. The opaque credential is short-lived, stored only as a hash, bound to that session/device and consumed atomically by one WebSocket connection. The session ID is never a credential.

Connect to the returned `/ws/sessions/{sessionId}/audio` URL with `Authorization: Session <connectionCredential>` and `Sec-WebSocket-Protocol: quacktion.audio.v1`. The provider connection is established and initialized before the server reports the session Active. Then send `audio.start` with protocol version 1 and the configured input/output formats, followed by raw binary frames. Raw device bytes are Base64 wrapped in ElevenLabs Conversational AI `user_audio_chunk` events. Provider `audio` event data is Base64 decoded and forwarded as binary frames. Do not send text and audio for the same utterance. Use `session.end` to finish. A closed or consumed connection credential cannot be reused; after a transport disconnect this session is ended and firmware starts a new idempotent session. The legacy `/ws/devices/{deviceId}` control protocol remains available.

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
{"version":1,"type":"audio.start","audio":{"protocolVersion":1,"input":{"codec":"<agreed-codec>","sampleRate":12345,"channels":1},"output":{"codec":"<agreed-codec>","sampleRate":12345,"channels":1}}}
{"version":1,"type":"audio.stop"}
{"version":1,"type":"session.end","requestId":"unique-request-id"}
```

Wake-word firmware can create and start a session in one authenticated request. The wake word itself is local and must be excluded from the prompt:

```json
{"version":1,"type":"session.start","requestId":"unique-request-id","payload":{"initialPrompt":"What is the current status of our sprint?","promptFormat":"text"}}
```

The backend creates the session, connects the configured ElevenLabs agent, and returns `session.started` with the Quacktion session ID. Repeating a `requestId` returns the same session. A start failure returns `session.start_failed` with that request ID. `promptFormat: "audio"` reserves the same lifecycle for audio-only initial utterances; after `session.started`, firmware negotiates audio with `audio.start` and streams the utterance as binary frames. It must not also send a text transcription of those same frames. End with `{"version":1,"type":"session.end","requestId":"unique-request-id"}`.

The format values above are placeholders, **not supported hardware settings**. Both directions must be explicitly allowed in backend configuration and accepted by the provider adapter. No transcoding is performed.

`StartSession` can start an existing Created session or attach to an Active session. Its owner and device must match the authenticated connection. It cannot create a session for a supplied account. `AudioStart` requires this association, an Active session, and successful format negotiation. Binary data before that is rejected. `AudioStop` cancels the current stream and discards queued input; a later AudioStart renegotiates. `EndSession` tears down voice resources and closes the associated socket. It preserves all Quacktions.

Backend controls include `DeviceConnected`, `Heartbeat`, `StartSession`, `AudioStart`, `AudioStop`, and `Error`. `Error.Code` gives a stable reason such as `audio_not_started`, `unsupported_audio_format`, `message_too_large`, or a not-configured integration code. Protocol violations end the connection. Outgoing binary messages carry provider-adapted audio. Oversize provider output is rejected rather than blindly forwarded.

Provider adapters must serialize writes to their own transport, implement cancellation, reject unsupported formats, and map transcripts with real source-event IDs. Speaker identity remains null when unknown. The backend stores no raw audio. Audio buffers exist only for the bounded active connection lifetime.

ElevenLabs event/audio transport is implemented in the backend. Input and output audio must exactly match the provider’s negotiated codec, rate and channel count because this integration does not transcode. Firmware playback behavior and codec selection still require hardware validation. Test-only echo adapters must never be registered in production.
