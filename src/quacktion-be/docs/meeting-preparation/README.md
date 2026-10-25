# Native meeting preparation

Calendar MCP polling persists event occurrences in `meeting_preparations`. Independent workers claim calendar sync, Argon research, memory indexing, briefing schedules and notification delivery using MongoDB leases and idempotent IDs. Provider requests never run in the live audio receive loop.

Research uses the existing `GeminiMeetingIntelligence` reasoning client and configured `Gemini:ReasoningModel`. It retrieves owner-filtered Atlas Search and Vector Search results, verifies returned evidence IDs, and saves typed research, source references, revision history and user clarification answers. Refreshes preserve clarification answers. A research failure retries with backoff, then exposes `ResearchFailed` after eight attempts.

## Configuration

Features remain opt-in. Configure:

- `MeetingPreparation__Enabled=true` and `MeetingPreparation__MemoryEnabled=true`.
- `MeetingPreparation__CalendarListTool` and `CalendarRequiredScopes` using the actual discovered Google Calendar MCP tool/schema. The read tool must support expanded instances, cancellations and pagination. Existing MCP grants and tool allowlists remain authoritative.
- Existing Gemini API credentials and reasoning model settings.
- `MeetingPreparation__EmbeddingModel=gemini-embedding-001`, `EmbeddingDimensions=768` (or a matching provisioned dimension).
- `MeetingPreparation__NativeRankFusion=true` (default): inspect MongoDB build version and try native fusion on 8+. Unsupported stages/features fall back to application reciprocal rank fusion over both real Atlas retrieval branches. Search/index failures remain errors, never fabricated empty history.
- `MeetingPreparation__SpokenNotifications=true` (default), `NotificationTtsModel=gemini-2.5-flash-preview-tts`, `NotificationVoice=Kore`. TTS uses the existing Gemini HTTP client and PCM resampler. Unsupported audio or provider failure falls back to a quack; it does not activate implicit voice acceptance.
- Optional Gmail context requires the user's `GmailEnabled` preference and configured discovered read-only Gmail MCP tool/scopes.

Authenticated users enable preparation using `PUT /api/meeting-preparation/preferences` with `enabled`, `messagingOptIn`, `briefingMinutes` (default 30), retention and exclusions. `messagingOptIn` is retained for preference compatibility and now means duck notifications. Register calendars using `POST /api/meeting-preparation/calendars` with `connectionId` and `calendarId`. Google account identity, calendar ID and expanded event ID identify each occurrence.

Provision the definitions in `atlas-text-index.json` and `atlas-vector-index.json` on the actual Atlas deployment. Run `verify-atlas.js` with mongosh; it reports the server version, queryable indexes and real feature probes. `PROVISION_MEETING_SEARCH=1` permits creating missing indexes. Ordinary MongoDB cannot validate Atlas Search. Embedding dimensions and model metadata must match the index; changing the model requires reindexing.

## Persistence and APIs

Existing collections: `meeting_memory`, `meeting_preparations`, `meeting_preparation_preferences`, `calendar_sync_states`. New collection: `device_notifications`, with device/status/retry, owner/date and retention TTL indexes. Preparation research adds a status/retry/lease index. Existing occurrence uniqueness, scheduling and retention indexes remain.

- `GET /api/meetings/upcoming`
- `GET /api/meetings/{id}/preparation`
- `POST /api/meetings/{id}/prepare` queues research
- `POST /api/meeting-memory/search`
- `GET /api/notifications`
- `GET /api/notifications/{id}`
- `POST /api/notifications/{id}/dismiss`

All routes use existing authenticated ownership checks. There is no public endpoint for sending arbitrary notification text/audio. Preparation IDs are calendar occurrence IDs, distinct from recording IDs; the existing preparation-link endpoint connects recordings to occurrences.

## Device delivery

The existing `/ws/devices/{deviceId}` socket and `quacktion.v1` subprotocol carry `notify.play`, optional `notify.audio.start`, raw PCM frames and `notify.audio.end`. Every control has version 1 and a payload. A batch occupies one item in the existing outbound channel; the sole socket writer sends the whole batch before any normal audio frame. PCM is mono signed 16-bit little-endian, 16 kHz, at most 320,000 bytes/10 seconds, in frames of at most 4,096 bytes. Notification IDs are at most 48 ASCII characters.

Delivery allows one in-flight notification per device, waits for assistant audio to be quiet, and bounds retries to five attempts. Disconnected deliveries remain durable until expiry. Reconnect retries unacknowledged attempts; acknowledged notifications are not repeated. Cancellation, calendar revision, owner/device authorization, preferences and relevance are rechecked before send, including after TTS generation. No briefing is sent after its meeting ends.

`notify.done` records outcome and device timestamps. `played` and `silent` are terminal acknowledgements. Only successful spoken playback can activate an offer. `expired`/`dropped` retry after backoff while relevant. No protocol message can retract audio already sent to firmware, and a crash between physical playback and persisted acknowledgement can cause a replay; exactly-once physical playback cannot be guaranteed by this protocol.

Quacktion task lifecycle polling uses durable status markers and deterministic notification IDs. Completion uses `action_done`; failure/rejection uses `failed`; pending approvals use `needs_confirmation`. Pending approvals are refreshed within the two-minute firmware LED timeout, and another pending approval restores the pulse after a terminal notification. Voice never grants protected-action approval.

## Conversation behaviour

The `meeting_briefing` Gemini Live tool lists upcoming occurrences, reads summaries/details, queues refreshes, and dismisses reminders. Historical context, commitments, questions and saved key points are available through these tools and the existing Argon preparation routing.

After audible offer playback, the backend informs the active Gemini conversation. Short affirmative/negative responses resolve the persisted offer only for the same live microphone session and device connection, within 45 seconds. Unrelated speech consumes the implicit offer. Muted, quack-only, expired and disconnected offers cannot implicitly accept. Accepted briefings use the normal interruptible Gemini audio pipeline, not notification audio.

**The notification protocol cannot open the microphone.** A bare “yes” requires an already active audio session. If the duck is waiting for its wake word, the user must wake it and explicitly request the briefing. No new firmware controls or second device socket were added. A fresh connection restores durable notifications and retains explicit access to saved briefings; it deliberately invalidates implicit offers from the previous connection.

## Validation and deployment boundaries

See `VALIDATION.md` for executed tests. Production Atlas, real Calendar/Gmail MCP schemas, Gemini reasoning/TTS and physical duck playback need an authenticated deployment acceptance run. Features remain disabled globally until configured; this change does not deploy the backend or provision production indexes.

Provider references: [Gemini embeddings](https://ai.google.dev/gemini-api/docs/embeddings), [Gemini TTS](https://ai.google.dev/gemini-api/docs/speech-generation), [MongoDB rank fusion](https://www.mongodb.com/docs/v8.3/reference/operator/aggregation/rankfusion/), [Gemini Live protocol](https://ai.google.dev/api/live).
