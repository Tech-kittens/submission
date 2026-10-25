# Meeting Mode: client integration guide

This guide is for mobile and web clients using the Quacktion backend. A phone can record a meeting with its own microphone. Hardware can record through the ESP32 device protocol. The backend owns transcription and analysis for both.

Production API base URL: `https://quackback.ofneill.com`. Mobile and web clients use the signed-in user's bearer token:

```http
Authorization: Bearer <accessToken>
Content-Type: application/json
```

Meeting records belong to the authenticated account. Handle `401` through the normal token-refresh flow. A `404` can mean the meeting does not exist or is not owned by this user.

Hardware can call the meeting management routes with `Authorization: Device <device credential>`. The backend resolves the account and device from the credential; a device ID alone is not authentication. On `POST /api/meetings/start`, hardware may omit `deviceId` from the body, and any supplied ID must match its credential. The device-specific `/api/devices/{deviceId}/meetings` and `/api/devices/{deviceId}/mode` routes require that the path ID match the authenticated device when using device credentials. Both credential types have the same meeting read, update, end, and delete operations for their account. Phone audio upload requires a bearer token.

## Start flow and consent

1. Show a clear recording and transcription notice before start. The owner must confirm that participants have received appropriate notice and consent has been obtained. Keep the confirmation separate from simply tapping “Start” or selecting a meeting type.
2. Start a standalone phone meeting without `deviceId`:

   ```http
   POST /api/meetings/start

   {"name":"Release planning","type":"Planning","recordingConsent":true}
   ```

3. Capture signed 16-bit little-endian mono PCM at 16 kHz. Upload consecutive chunks of at most 30 seconds to `POST /api/meetings/{id}/audio/segments?startMs=0` with `Content-Type: application/octet-stream` and the bearer token. Each chunk must contain whole 40 ms frames (1,280 bytes per frame). Set `startMs` to the chunk's offset from the beginning of the recording in milliseconds; reuse the same offset and bytes when retrying. Wait for a successful response for every chunk before ending. A repeated upload is idempotent; different audio at the same offset returns `409 segment_conflict`.

To record through paired hardware, choose a connected, unmuted device from `GET /api/devices` and start with:

   ```http
   POST /api/meetings/start

   {"deviceId":"<device-id>","name":"Release planning","type":"Planning","recordingConsent":true}
   ```

   The existing `POST /api/devices/{deviceId}/meetings` accepts the same body except that `deviceId` is in the path. Valid types are `General`, `Standup`, `Planning`, `Brainstorming`, `DecisionMaking`, `Retrospective`, `ClientMeeting`, `Presentation`, and `Interview`.

Save the returned meeting `id`. For hardware recording, the backend requests meeting mode from the device; show a pending state until the device reports `mode: "meeting"`. For phone recording, show the recording indicator while capturing and uploading microphone audio.

`recordingConsent: true` is required. Missing consent returns `400 recording_consent_required`. An offline, unpaired, or physically muted device cannot start a hardware meeting. Physical mute must be cleared at the device. Starting the same device's active meeting returns that meeting instead of creating another one.

The spoken `start_meeting` tool also requires an explicit affirmative consent argument. A client should not interpret casual speech as consent or start a recording on the user's behalf.

## During the meeting

Read `GET /api/meetings/{id}/status` periodically while the meeting screen is visible. It returns `status`, `recordingStatus`, `recordingBytes`, `finalTranscriptStatus`, `analysisFailureCode`, and `processedAt`. For hardware meetings, use `GET /api/devices/{deviceId}` to confirm the actual mode and mute state. Poll at a modest interval; the backend does not expose a dedicated meeting push subscription.

The useful read endpoints are:

| Route | Client use |
| --- | --- |
| `GET /api/meetings?offset=0&limit=50` | Meeting history |
| `GET /api/meetings/{id}` | Meeting details and lifecycle |
| `GET /api/meetings/{id}/status` | Processing and recording progress |
| `GET /api/meetings/{id}/transcript?offset=0&limit=50` | Transcript, provisional before final processing and final turns afterward |
| `GET /api/meetings/{id}/segments?offset=0&limit=50` | Device segment metadata, provisional text, and any failure codes |
| `GET /api/meetings/{id}/events?offset=0&limit=50` | Device wake and backlog-loss events |
| `GET /api/meetings/{id}/insights?offset=0&limit=50` | Incremental meeting insights |
| `GET /api/meetings/{id}/speakers` | Current anonymous speaker labels and meeting-specific names |

Paged responses have `{ "items": [...], "offset": 0, "limit": 50 }`; `limit` must be 1–100. The transcript response changes shape when `finalTranscriptStatus` becomes `Ready`: before then its items are provisional `MeetingSegment` records; afterward they are `MeetingTranscriptTurn` records. Parse using the status and use the item's `id` as its stable key. Do not treat a provisional transcript, inferred speaker label, or incomplete recording as verified. A null speaker ID means unknown. Timestamp fields can be null, and no provider confidence value is guaranteed.

For a phone meeting, the app uploads microphone audio chunks but does not send transcript text. For a hardware meeting, the ESP32 and backend handle audio. Normal Quacktion actions and MCP approval screens continue to work during a meeting.

## End flow

Call `POST /api/meetings/{id}/end` after the final phone audio chunk is uploaded, or when the hardware owner chooses to end. The endpoint is idempotent. It returns promptly with the meeting moving into `Ending`; final transcription and analysis run afterward. Update the UI immediately to “Meeting ended; preparing report” and keep polling `/status` until `Completed` or `Failed`. Ending a hardware meeting requests the device's normal assistant mode; it does not end the device connection or disable ordinary voice use. The spoken `end_meeting` tool invokes the same lifecycle for hardware meetings.

Treat `Created`, `Active`, `Ending`, `Processing`, `Completed`, `Failed`, `Cancelled`, and `Interrupted` as distinct status values. `RecordingStatus`, `FinalTranscriptStatus`, and `AnalysisFailureCode` give more precise integrity and processing information. If processing fails, show the failure state and any available transcript; do not show a report as complete. Repeated end taps should reuse the same request and screen state.

## Report and speaker UI

When analysis exists, read `GET /api/meetings/{id}/analysis`. Its structured fields include `summary`, `decisions`, `actionItems`, `risks`, `unresolvedQuestions`, `suggestedQuacktions`, `assessments`, `scorecard`, and `metrics`. The focused routes `/scorecard`, `/action-items`, and `/decisions` return the corresponding parts. Before analysis is ready these routes can return `404`; use `/status` to decide when to retry.

Each finding can include `evidenceSegmentIds`, a `status`, and optional owner or deadline. Link evidence back to transcript items where possible. Display missing owners, deadlines, evidence, and scores as unavailable; do not fill them in on the client. A category can have a null score or be marked `notApplicable`. The overall score measures the meeting process, not individuals. Suggested Quacktions are proposals only: do not execute calendar, email, or other external operations without the existing authenticated permission and approval flow.

`GET /api/meetings/{id}/speakers` returns `{ "speakerId": "...", "speakerName": null }` entries. To label a speaker for this meeting:

```http
PATCH /api/meetings/{id}/speakers/{speakerId}

{"speakerName":"Sarah"}
```

This changes meeting labels only; it does not enrol a voice or authenticate Sarah. Before final transcript processing, a specific provisional segment can also be corrected with `PATCH /api/meetings/{id}/segments/{segmentId}/speaker` and `{ "speakerId": "...", "speakerName": "Sarah" }`. Treat speaker IDs from final file chunks as anonymous labels that may not identify the same person across the entire meeting.

The owner can download the finished WAV from `GET /api/meetings/{id}/audio` when `recordingStatus` is `Ready`, using the bearer token. `DELETE /api/meetings/{id}` removes a completed or failed meeting and its stored data. Put deletion behind a clear confirmation in the client.

## Client states to test

- Consent declined: no start request; recording indicator remains off.
- Device offline, unpaired, or physically muted: show the backend error and an actionable device state.
- Start accepted but device mode has not changed yet: show pending, then active only after the device reports meeting mode.
- Device reconnect or transcript lag: retain the meeting ID, refetch status and paged transcript, and show any segment failure or backlog-loss event.
- End requested twice: one meeting and one report; ordinary assistant mode remains available.
- Analysis pending or failed: show processing or failure, without invented report content.
- Speaker unknown or corrected: retain anonymous labeling where identity is uncertain.
- Account switch: clear cached meeting content before loading the next account; never show another account's transcript or recording.

Voice enrolment and stable biometric owner recognition are **not currently exposed** as client APIs. Do not add a “learn my voice” button until a separately consented enrolment backend exists.

Backend and device details: [MEETING-MODE.md](MEETING-MODE.md) and [transcription-protocol.md](transcription-protocol.md).
