# Device meeting protocol

Implemented from the firmware handoff dated 2026-10-24. The original proposal was unavailable. These examples define the backend wire format to compare with `real-backend-integration` before enabling `QUACK_F8_PROTOCOL` on hardware.

## Transport and routing

Reuse `/ws/devices/{deviceId}`, `quacktion.v1`, and `Authorization: Device <credential>`, alongside the existing heartbeat. Text controls are JSON with protocol version 1 and camel-case fields. Meeting fields may be top-level or inside `payload`. Invalid meeting controls return `error` without closing the connection.

Outside meeting mode, assistant audio reuses this socket: `session.start` → `session.started` → `audio.start` → `audio.ready` → binary PCM → `session.end` → `session.ended`. The device socket remains open for the next wake. No new device TLS connection is needed.

During meetings, inbound binary data belongs exclusively to the open segment. Run simultaneous assistant conversations on `/ws/sessions/{sessionId}/audio`, sending microphone audio to both sockets. No audio is copied implicitly. Legacy assistant starts on the meeting socket receive `use_session_audio_socket`; meeting negotiation while legacy assistant audio is running receives `assistant_audio_active`. Finish legacy audio before entering meeting mode. These rules preserve routing without binary headers.

## App endpoints and modes

All endpoints require the owning account's Bearer token:

| Request | Result |
| --- | --- |
| `POST /api/meetings`, `{"name":"Planning","recordingConsent":true}` | Bearer-authenticated phone recording; no device ID required (`/api/meetings/start` is an alias) |
| `POST /api/meetings`, `{"name":"Planning","recordingConsent":true,"deviceId":"<owned-device>"}` | Bearer-authenticated duck recording; send `mode.set: meeting` |
| `POST /api/devices/{id}/meetings` | Existing hardware-targeted route; still requires authentication and ownership |
| `POST /api/meetings/{id}/end` | End meeting; request assistant mode unless physically muted |
| `POST /api/devices/{id}/mode`, `{"mode":"off"}` | Request `off`, `assistant`, or `meeting`; other modes end the active meeting |
| `GET /api/meetings`, `GET /api/meetings/{id}` | Owned meetings |
| `GET /api/meetings/{id}/segments` | Ordered segment transcripts/metadata |
| `GET /api/meetings/{id}/events` | Wake/backlog metadata |

Lists accept `offset` and `limit` (default 50). Mode requests are asynchronous; `GET /api/devices/{id}` exposes `mode`, `requestedMode`, and `mutedByUser`. Starting a duck recording requires a connected, paired, unmuted device. Phone recordings use the Bearer-authenticated meeting endpoint without a device. Meeting IDs are distinct from assistant session IDs. A meeting does not reserve the assistant slot.

```json
{"version":1,"type":"mode.set","mode":"meeting"}
{"version":1,"type":"mode.changed","mode":"meeting"}
```

Firmware reports `mode.changed` on every change and reconnect. Boot default is `assistant`. An active meeting survives reconnect; an unmuted device reporting assistant mode on initial connection is asked to return to meeting mode. Once meeting mode has been acknowledged on that connection, switching the device back to assistant mode ends the meeting. An ended meeting reporting meeting mode is asked to return to assistant mode.

An unsolicited off report, `reason: "button"`/`"muted"`, `mutedByUser: true`, or device error `muted_by_user` latches physical mute. Remote meeting/assistant requests then return `409 muted_by_user`. Only a device report of a non-off mode clears the latch. A remote off request acknowledged without physical mute indicators can be reversed remotely. Invalid modes return `error: invalid_mode`.

## Negotiation

On entering meeting mode and after reconnect:

```json
{"version":1,"type":"transcribe.start","format":"pcm_16000","frameMs":40,"preRollMs":300,"hangoverMs":500}
{"version":1,"type":"transcribe.ready","format":"pcm_16000","frameMs":40,"meetingId":"<id>"}
```

Input is signed PCM16 little-endian, mono, 16 kHz. Frames are exactly 1,280 bytes / 40 ms. Other formats/frame durations receive `unsupported_audio_format`. Pre-roll range is 0–1000 ms; hangover is 0–5000 ms. Firmware defaults are 300/500 ms. Audio must follow `transcribe.ready` and `segment.start`.

## Segments and persistence

```json
{"version":1,"type":"segment.start","segmentId":"boot123-9","startMs":null,"startUptimeMs":12000,"clockSource":"none","preRollMs":300,"backlog":false,"continuesSegmentId":null}
```

Send binary frames, then:

```json
{"version":1,"type":"segment.end","segmentId":"boot123-9","frames":25,"bytes":32000,"reason":"silence"}
```

Accepted reasons: `silence`, `max_length`, `muted`, `mode`, `disconnect`, `overflow`. Maximum segment size: 750 frames / 960,000 bytes / 30 seconds. Incorrect counts return `segment_count_mismatch`; invalid frame size/duration returns `invalid_segment_audio`. Invalid segment buffers are discarded and a new segment can recover. Partial valid audio at disconnect is saved with actual counts and reason `disconnect`.

`segment.end` may omit `segmentId`, in which case it ends the sole open segment. If provided, the ID must match.

Gemini Flash transcribes each segment via the audio-understanding API, independently of the Live assistant. MongoDB stores transcript, counts, timestamps, owner, meeting and continuation IDs. Raw PCM stays bounded in memory and is not persisted. Transcription retries 429/5xx responses up to twice within a 60-second budget. A failed transcription is stored with `failureCode` and reported as `error`; replaying the segment can retry it. A backend process crash can lose in-memory audio. Durable audio recovery requires firmware replay; the supplied contract has no segment acknowledgement.

Identity is `(deviceId, segmentId)`. Identical completed replay does not create another transcript or call Gemini again. Changed audio/metadata for a completed identity receives `segment_conflict`. A disconnected partial segment can be resent with the same prefix and additional frames.

`bootId` is the prefix before the last hyphen; the suffix is an unsigned sequence number. Ordering is `bootId`, then `startUptimeMs`, then segment ID for ties. Sequence number is not time order. `startMs` is always null when `clockSource` is `none`; backend receipt time is recorded separately as `createdAt`.

## Backlog, continuations and wake

Buffered segments may upload at up to 4x real time; the server does not pace ingestion. It processes one segment at a time and awaits transcription/storage before reading the next control. Backpressure holds reads, with no unbounded segment queue or silent loss on queue saturation. Memory is bounded by one segment plus WebSocket message buffers. Processing time does not consume the receive/assembly deadline.

After reconnect, negotiate again, report loss, then send backlog segments:

```json
{"version":1,"type":"backlog.dropped","reason":"overflow","frames":25,"bytes":32000,"segments":1,"durationMs":1000,"bootId":"boot123"}
{"version":1,"type":"segment.start","segmentId":"boot123-1","startMs":null,"startUptimeMs":13000,"clockSource":"none","preRollMs":0,"backlog":true,"continuesSegmentId":"boot123-9"}
{"version":1,"type":"wake","segmentId":"boot123-1","outcome":"session","startUptimeMs":13200}
```

`continuesSegmentId` links the predecessor in the same boot/meeting, even when delivery is out of order. Consumers join by identity then order by uptime; individual segment transcripts remain available so loss is visible. Wake outcomes are `session` and `ignored_busy`; optional `sessionId` links a known assistant session. Markers can precede their segment. The backend stores them without silently removing speech.

Mute loss uses `reason: "muted"` and amounts only. Stored backlog fields are `reason`, `frames`, `bytes`, `segments`, `durationMs`, `bootId`; extra fields are discarded. Wake fields are `segmentId`, `outcome`, `sessionId`, `startMs`, `startUptimeMs`, `bootId`. Events expose receipt time and `payloadJson`.

Flush the current meeting's backlog before starting another meeting: the supplied segment framing has no meeting ID to distinguish old backlog after an app starts a new meeting.

## Deferred features

Unknown controls in the recognized `mode.*`, `transcribe.*`, `segment.*`, `notify.*`, and `confirm.*` families receive `unsupported_feature` without disconnecting. `notify.done` is implemented as described below; confirmation execution remains deferred. Late confirmation answers have no effect and cannot unmute a device.

`device.status` refreshes `lastSeenAt`. A 60-second status interval is sufficient for optional telemetry, but it does not replace heartbeat at least every 20 seconds or the 45-second transport idle limit.


## 9. Notifications (firmware contract)

The backend sends notifications on the authenticated existing device socket. Every JSON control contains `version: 1`. Prefer payload fields:

```json
{"version":1,"type":"notify.play","requestId":"brief-123","payload":{"notifyId":"brief-123","kind":"quack","audio":false,"expiresInSeconds":60}}
```

Kinds: `quack` (white), `action_done` (green), `needs_confirmation` (yellow pulse), `failed` (red/double quack). Briefing offers always use `quack`; approval notifications never replace the authenticated approval endpoint. IDs are at most 48 characters. Firmware queues at most four notifications; backend permits one unacknowledged notification per device.

For spoken notification audio, set `audio: true`, then send:

```json
{"version":1,"type":"notify.audio.start","payload":{"notifyId":"brief-123","audio":{"codec":"pcm_16000","sampleRate":16000,"channels":1}}}
```

Send raw mono 16-bit little-endian PCM at 16 kHz, up to 4,096 bytes per binary frame, at most 320,000 bytes/10 seconds total. Always finish an opened stream on a usable connection:

```json
{"version":1,"type":"notify.audio.end","payload":{"notifyId":"brief-123"}}
```

The backend serializes this entire sequence through the single outbound socket writer. It cannot interleave with conversational binary audio. Firmware waits for assistant speech to be quiet before playback; notifications do not interrupt replies.

## 10. Playback acknowledgement

```json
{"version":1,"type":"notify.done","requestId":"brief-123","payload":{"notifyId":"brief-123","outcome":"played","atMs":1792901005306,"atUptimeMs":92454,"clockSource":"sntp"}}
```

`played` acknowledges playback. `silent` acknowledges a muted device and must not be retried. `expired` and `dropped` permit bounded retry only while relevant. Acknowledgements are correlated to device ownership, notification ID and the current transport connection. Duplicate acknowledgements have no effect. None of these outcomes means the user accepted a briefing.

The notification protocol does not start a microphone session. The existing Gemini Live audio session captures any response. Expired offers and offers from another connection cannot interpret a later bare affirmative as acceptance.
