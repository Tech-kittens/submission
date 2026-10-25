# Native meeting preparation implementation report

## Application services

- `MeetingPreparationAgent` implements `IMeetingPreparationAgent` using the existing configured Argon reasoning HTTP client, owner-isolated hybrid memory and optional authorised Gmail reads.
- `MeetingPreparationResearchWorker` claims durable research leases and persists bounded retry state.
- `NativePreparationDelivery` replaces external webhook publishing with durable notification scheduling.
- `DeviceNotificationService` implements `IDeviceNotificationService`, validates ownership and relevance, serializes protocol batches and records playback acknowledgements.
- `NotificationSpeech` generates generic provider-backed speech and uses the existing 24-to-16-kHz PCM resampler. `NotificationProtocol` validates kinds, IDs, PCM bounds and framing.
- `BriefingConversation` resolves recent audible offers and implements explicit read/details/upcoming/refresh/dismiss commands.
- `DeviceNotificationWorker` dispatches durable notifications and integrates task completion, failure, rejection and authenticated approval lifecycle states.

## Calendar, memory and persistence

Existing Google Calendar MCP polling is preserved. Expanded occurrence identity, fingerprints, cancellation and rescheduling remain durable; organiser context and research retry resets are included. No direct Google Calendar API client is introduced.

Atlas keyword and vector retrieval both filter by owner and access metadata before ranking. The configured supported embedding model and dimensions remain compatible with the supplied index definitions. Search checks server version and attempts native fusion when available, with application reciprocal rank fusion for unsupported native stages/features.

Collections: existing `meeting_memory`, `meeting_preparations`, `meeting_preparation_preferences`, `calendar_sync_states`; new `device_notifications`. Added indexes cover research status/retry/lease, notification device/status/retry, status/expiry, owner/date and retention TTL. User clarifications survive refreshed research; decisions and commitments include typed evidence references.

## Transport, voice and scheduling

The existing authenticated device socket carries version-1 payload controls. `notify.play` and optional PCM start/binary/end frames are an indivisible batch in the existing socket writer. Acknowledgements persist outcomes and device timestamps. Silent playback is terminal; dropped/expired deliveries have bounded retries. Disconnected notifications survive restarts and are retried after reconnect while relevant.

A recent spoken offer can resolve an affirmative/negative only in the same active audio session and connection. Saved key points are sent through Gemini Live's normal conversational pipeline. Explicit briefing tools remain available later. Protected action approval remains app-controlled.

Calendar, ingestion, research, scheduling and delivery use separate background loops backed by MongoDB job state, leases, revision checks and deterministic IDs. Polling timers wake workers; they do not hold the actual schedule in memory.

## Removed integration and validation boundaries

Removed the external research/delivery adapter, callback controller, credentials, callback replay collection mapping and obsolete workflow instructions. No messaging channel or firmware integration was added. Historical changelog entries remain historical records.

See [validation](VALIDATION.md) for test results and [setup](README.md) for deployment configuration. Bare affirmative replies require an already active microphone session. Hardware delivery, real provider integration and production Atlas capability still require deployment validation. Crash-boundary physical playback cannot be exactly once because the firmware protocol has no durable playback receipt across reboots.

## Files created, modified or removed

- `README.md`
- `docs/meeting-preparation/CHANGES.md`
- `docs/meeting-preparation/README.md`
- `docs/meeting-preparation/SAINT-WORKFLOW.md` (removed)
- `docs/meeting-preparation/VALIDATION.md`
- `docs/transcription-protocol.md`
- `src/Quacktion.App/Interfaces/DeviceNotifications.cs`
- `src/Quacktion.App/Interfaces/MeetingPreparation.cs`
- `src/Quacktion.App/Models/DeviceNotification.cs`
- `src/Quacktion.App/Models/MeetingPreparation.cs`
- `src/Quacktion.App/Models/Quacktion.cs`
- `src/Quacktion.App/Services/MeetingToolService.cs`
- `src/Quacktion.Data/Agents/GeminiLiveService.cs`
- `src/Quacktion.Data/Agents/GeminiMeetingIntelligence.cs`
- `src/Quacktion.Data/Agents/GeminiTools.cs`
- `src/Quacktion.Data/Audio/DeviceConnectionManager.cs`
- `src/Quacktion.Data/MongoDB/MongoDbContext.cs`
- `src/Quacktion.Data/Preparation/BriefingConversation.cs`
- `src/Quacktion.Data/Preparation/CalendarPreparationSync.cs`
- `src/Quacktion.Data/Preparation/DeviceNotificationService.cs`
- `src/Quacktion.Data/Preparation/MeetingMemorySearch.cs`
- `src/Quacktion.Data/Preparation/MeetingPreparationAgent.cs`
- `src/Quacktion.Data/Preparation/NotificationSpeech.cs`
- `src/Quacktion.Data/Preparation/PreparationDeliveryWorker.cs`
- `src/Quacktion.Data/Preparation/PreparationSettings.cs`
- `src/Quacktion.Data/Preparation/PreparationStore.cs`
- `src/Quacktion.Data/Preparation/SaintIntegration.cs` (removed)
- `src/Quacktion.Web/Controllers/MeetingPreparationController.cs`
- `src/Quacktion.Web/Controllers/NotificationsController.cs`
- `src/Quacktion.Web/Controllers/SaintPreparationController.cs` (removed)
- `src/Quacktion.Web/Infrastructure/DeviceNotificationWorker.cs`
- `src/Quacktion.Web/Infrastructure/MeetingPreparationWorker.cs`
- `src/Quacktion.Web/Program.cs`
- `src/Quacktion.Web/WebSockets/DeviceWebSocketHandler.cs`
- `tests/Quacktion.Web.Tests/MeetingPreparationTests.cs`
- `tests/Quacktion.Web.Tests/NativeNotificationTests.cs`
