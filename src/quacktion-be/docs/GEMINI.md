# Gemini meeting infrastructure

## What changed

`GeminiLiveService` implements the existing `IConversationalAgentGateway` and `IAudioProviderAdapter`. Session orchestration, device authentication, pairing, REST routes, SignalR, task leases, and approvals remain in their existing layers. The obsolete conversational provider client and its event sink/configuration have been removed.

The wire message is `setup.generationConfig.responseModalities: ["AUDIO"]`; voice configuration is also inside `generationConfig`. SDK configuration examples are not interchangeable with raw WebSocket payloads. Setup includes transcription, allowlisted function declarations, session resumption and sliding-window compression. Standard Live has no thinking configuration and proactive audio is not disabled. The API key is sent only by the backend to Google's documented TLS WebSocket endpoint.

The background gateway uses Gemini 4 Argon for complex analysis, MCP tool orchestration, and research, with Gemini 3.8 Flash for summaries and suggestions. Jev can route only among backend-eligible providers and receives the requested query and eligible capability metadata. Explicit enabled agent catalog entries retain precedence. User-connected Google Calendar, Gmail, and Meet workflows use the hosted MCP gateway described in [the multi-agent/MCP guide](MULTI-AGENT-MCP.md). Account selection and request approval are required before private-account access; writes require exact-operation approval. No CRM, ticket, arbitrary code execution, or internal business-system integration is claimed.

During meeting mode, a reported software bug or technical failure triggers a background investigation through an unambiguous connected GitHub MCP account. The investigation is limited to enabled read-only repository tools and returns a diagnosis grounded in the retrieved source. It does not edit code or write to GitHub. Code and repository writes remain behind the exact-operation approval flow.

## Files

| Area | Files |
| --- | --- |
| Live transport and wire contract | `src/Quacktion.Data/Agents/GeminiLiveService.cs`, `GeminiTransport.cs`, `GeminiProtocol.cs`, `GeminiTools.cs` |
| Background reasoning | `src/Quacktion.Data/Agents/GeminiTaskGateway.cs`, `src/Quacktion.App/Services/AgentOrchestrator.cs` |
| Tool validation, idempotency and approval | `src/Quacktion.App/Services/MeetingToolService.cs`, `QuacktionService.cs`, `src/Quacktion.App/DTOs/Contracts.cs` |
| Persistence | `src/Quacktion.App/Models/RealtimeState.cs`, repository interfaces/implementations, `MongoDbContext.cs` |
| Audio and interruption propagation | `src/Quacktion.Data/Audio/PcmResampler.cs`, `DeviceConnectionManager.cs`, both WebSocket handlers, `AgentsAndAudio.cs` |
| Host/configuration | `src/Quacktion.Web/Program.cs`, `appsettings.json` |
| Test clients | `src/Quacktion.Web/wwwroot/session-test.html`, `tools/Quacktion.AudioSimulator/Program.cs` |
| Tests | `tests/Quacktion.Web.Tests/GeminiTests.cs`, `GeminiIntegrationTests.cs`, existing app/host tests and fakes |
| Documentation | `README.md`, this guide, `SESSION.md`, `FE-SESSION.md`, `device-protocol.md` |

## Device audio

No firmware format change is required. Inspection of the adjacent `QuacktionHW` repository found:

- `firmware/main/board.h`: `BOARD_SAMPLE_RATE` is 16000.
- `firmware/main/session_audio.c`: both directions negotiate `pcm_16000`, mono; `audio.clear` clears pending playback.
- `firmware/main/audio_out.c`: playback uses that rate and a PCM ring buffer.

Keep `/ws/sessions/{sessionId}/audio`, subprotocol `quacktion.audio.v1`, and the existing authenticated `POST /api/sessions/start` flow. Send signed PCM16 little-endian mono audio in binary WebSocket messages. The backend splits input into chunks of at most 30 ms and sends `realtimeInput.audio`, with `audio/pcm;rate=16000`. It never buffers entire utterances.

Gemini output is PCM16 at 24 kHz. A stateful 63-tap windowed-sinc low-pass filter at 7.2 kHz precedes rational 24→16 kHz resampling. Chunk boundaries preserve filter history and fractional phase; interruption resets both. A client may explicitly negotiate `pcm_24000` output to bypass resampling, but must actually play it at 24 kHz.

All audio queues are bounded. Slow output consumers cause obsolete audio to be discarded and an `audio.clear` notification; private audio is neither persisted nor logged. Playback cancellation travels with queued frames so a barge-in invalidates audio already moved into the device queue. The frame currently on the wire cannot be recalled; the device must clear its playback ring immediately on `audio.clear`.

## Tasks and approval

All declared functions are `NON_BLOCKING`:

| Function | Backend behavior |
| --- | --- |
| `create_quacktion` | Validates the capability and query; creates one durable task per provider call ID |
| `get_quacktion_status` | Reads status of an owned task in the current meeting; does not disclose result content |
| `research_external_information` | Creates a grounded public research task through Gemini Flash |
| `announce_quacktion_result` | Proposes sharing an exact snapshot of a completed result; requires owner approval |
| `request_quacktion_approval` | Same approval request for sharing a completed result; cannot approve anything |

Task creation is idempotent across reconnection and worker restarts. Function-call IDs, names, arguments, task IDs, results and delivery state are persisted in `meeting_tool_calls`. Invalid tools/arguments return structured failures. Arguments and results are stored as private meeting data and excluded from logs.

The existing worker claims and executes Quacktions. The Live dispatcher polls pending calls without blocking audio. It first acknowledges durable task creation using `willContinue: true` and `scheduling: "SILENT"`, then sends final results with the original function-call ID/name, `willContinue: false` and `scheduling: "WHEN_IDLE"`. These fields are siblings of `response` in each function response, matching the official SDK wire types. No caller-controlled urgency can bypass approval. `GeminiProtocol.ToolResponse` also supports `SILENT` and `INTERRUPT` for future trusted scheduling policies.

All background result announcements conservatively require approval, including summaries. Completion responses to Live contain status and task IDs, not confidential results. The frontend receives `ApprovalRequested`; the owner uses the existing `/api/quacktions/{id}/approve` or `/reject` route with the action digest. Approval binds the exact content snapshot. `meeting.announce` is never safe-listed; the worker and gateway enforce approval. Only a completed, validly approved announcement can be returned to Live for speech. Rejection is recorded and reported without exposing content.

Cancellation of a Gemini function call suppresses its response; it does not cancel a durable user task or an already approved action. Users cancel tasks through the existing REST API. Ending a meeting leaves durable tasks available in the frontend.

## Connection lifecycle and persistence

The backend meeting ID remains authoritative and separate from the generated provider-connection ID. `realtime_states` stores the latest valid resumption handle, owner and timestamp without racing updates to the session document. Handles are never exposed through the API.

GoAway and transient provider failures trigger bounded reconnect attempts while the device socket and meeting remain active. Provider writes wait for setup acknowledgement during reconnection. A microphone write that fails is reported as a terminal session failure, avoiding silent audio loss or replay of a partially delivered frame. Reconnection has a 35-second overall budget and a configurable per-attempt setup timeout. On exhaustion the client receives a terminal failure and normal session cleanup releases the device.

A valid handle resumes provider context. If resumption is unavailable/rejected, a fresh connection restores recent persisted transcript context (up to 500 entries/approximately 24k characters) and unfinished function calls. Older meeting history may no longer be in model context after fallback; MongoDB retains it. Ordinary reconnect does not silently cancel pending Quacktions.

Resumption restores Google context, not the physical device TCP socket. Existing deployment behavior remains: one Web/transport instance; host shutdown ends active meetings, and startup recovery marks abandoned transports failed. Tasks survive independently. Deployment rollover is not seamless, and multiple Web replicas are unsupported. Schedule deployments outside live meetings.

Tool-result delivery is at least once around a send/commit crash window. Durable tasks are deduplicated, but exactly-once spoken announcements cannot be guaranteed without a provider/device acknowledgement protocol.

## Configuration

| Variable | Default / meaning |
| --- | --- |
| `Gemini__ApiKey` | Required in Production; Google AI Studio API key, server only |
| `Gemini__LiveModel` | `gemini-3.8-live` |
| `Gemini__ReasoningModel` | `gemini-4-argon` (Google API identifier for Gemini 4 Argon) |
| `Gemini__FastReasoningModel` | `gemini-3.8-flash` |
| `Gemini__Voice` | `Puck` |
| `Gemini__LanguageCode` | `en-GB`; sent as `setup.generationConfig.speechConfig.languageCode` |
| `Gemini__SilenceDurationMs` | `500`; valid range 200–2000 |
| `Gemini__PrefixPaddingMs` | `40`; valid range 0–1000 |
| `Gemini__StartOfSpeechSensitivity` | `START_SENSITIVITY_LOW`; also accepts `START_SENSITIVITY_HIGH` |
| `Gemini__EndOfSpeechSensitivity` | `END_SENSITIVITY_LOW`; also accepts `END_SENSITIVITY_HIGH` |
| `Gemini__SystemInstruction` | Meeting-specific default in `GeminiSettings`; override if needed |
| `Gemini__EnableInputTranscription` | `true` |
| `Gemini__EnableOutputTranscription` | `true` |
| `Gemini__EnableSessionResumption` | `true` |
| `Gemini__EnableContextCompression` | `true` |
| `Gemini__ConnectionTimeoutSeconds` | `25`, valid range 1–60 |
| `Gemini__ReconnectAttempts` | `5`, valid range 1–10 within the overall reconnect budget |

Production startup validates key presence, model names and settings. It does not make a billable provider request; key validity/model access are checked when establishing a session. Development can run without a key, but voice starts fail explicitly.

Set the local secret from the repo root:

```bash
read -rsp "Google AI Studio API key: " quacktion_gemini_key
printf '\n'
dotnet user-secrets set --project src/Quacktion.Web "Gemini:ApiKey" "$quacktion_gemini_key"
unset quacktion_gemini_key
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Quacktion.Web
```

Keep the existing JWT and MongoDB configuration. Default audio allowlists now permit PCM16 mono 16 kHz input and 16/24 kHz output. Remove obsolete provider environment variables; none are used by the application.

## Railway deployment

1. In the existing Railway backend service, select this repository and the migration commit/branch. Use the repository root as the root directory. Railway detects the existing root `Dockerfile`; leave custom build/start commands unset. The image publishes the Web project and starts `dotnet Quacktion.Web.dll`.
2. Set these service variables using Railway's Variables editor (substitute real values):

   ```env
   ASPNETCORE_ENVIRONMENT=Production
   ASPNETCORE_URLS=http://0.0.0.0:8080
   PORT=8080
   Gemini__ApiKey=<valid Google AI Studio key>
   Gemini__LiveModel=gemini-3.8-live
   Gemini__ReasoningModel=gemini-4-argon
   Gemini__Voice=Puck
   Gemini__EnableInputTranscription=true
   Gemini__EnableOutputTranscription=true
   Gemini__EnableSessionResumption=true
   Gemini__EnableContextCompression=true
   MongoDB__ConnectionString=<existing production MongoDB connection string>
   MongoDB__Database=quacktion
   Jwt__SigningKey=<existing signing secret, at least 32 bytes>
   AllowedHosts=quackback.ofneill.com;*.up.railway.app;healthcheck.railway.app
   ```

3. Keep **one replica**. Use public target port **8080**. Set the deployment healthcheck path to `/health/ready`, with a 300-second timeout. Health routes support Railway's internal HTTP probe; the public API/device flow uses HTTPS/WSS in Production. TLS terminates at Railway, and forwarded scheme processing runs before redirects.
4. Deploy in a maintenance window. Startup automatically creates the two additive collections/indexes. Existing session documents remain readable; no destructive migration or manual data rewrite is needed.
5. Confirm `/health/ready` returns 200 and logs contain no startup configuration/database errors. A healthy HTTP process does not prove Google credentials are valid.
6. Open `https://quackback.ofneill.com/session-test.html`, authenticate, create a session, connect, negotiate 16 kHz PCM, and start the microphone. Ask “Quacktion, please say hello.” Check `Gemini setup acknowledged`, a correctly paced response, both transcript directions, and interruption behavior.
7. Ask for a summary. Confirm a persisted Quacktion completes while microphone audio continues. Request that it be announced. Confirm the frontend receives an approval request and no confidential result is spoken before approval. Test both approve and reject paths. End the meeting and confirm a new device session can start.
8. If rollout fails, roll back the deployment image/configuration together during a maintenance window. Keep the MongoDB records; the changes are additive. Do not remove task/approval history to recover a connection.

Railway references: [Dockerfiles](https://docs.railway.com/builds/dockerfiles), [variables](https://docs.railway.com/variables), [healthchecks and host/port requirements](https://docs.railway.com/deployments/healthchecks).

## Tests and remaining verification

```bash
dotnet build Quacktion.sln --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test Quacktion.sln --no-restore -m:1 -p:UseSharedCompilation=false
# Isolated test databases on local MongoDB:
QUACKTION_TEST_MONGO=mongodb://127.0.0.1:27017 dotnet test Quacktion.sln --no-restore -m:1
# Opt-in real Google setup and spoken response (read the key interactively):
read -rsp "Google AI Studio API key: " QUACKTION_TEST_GEMINI_KEY
printf '\n'
export QUACKTION_TEST_GEMINI_KEY
dotnet test tests/Quacktion.Web.Tests --filter Category=LiveGemini
unset QUACKTION_TEST_GEMINI_KEY
```

Ordinary tests use scripted WebSockets and mock HTTP responses, with no Google key/network dependency. The actual device-path test passes microphone bytes through ASP.NET to the Gemini adapter, injects provider audio, verifies resampling/playback clearing, and reads persisted transcripts through REST. DSP tests check chunk continuity, sample count, passband amplitude and anti-alias rejection. Existing authorization, approval, task leasing and MongoDB tests remain relevant.

A live test requires a valid Google API key and model access. A passing mock suite does not establish Gemini availability, physical speaker fidelity, acoustic echo cancellation, or production latency. Those must be checked with the actual key and ESP32 hardware before claiming end-to-end production acceptance.

Official schemas consulted: [Live wire API](https://ai.google.dev/api/live), [Gemini 3.8 Live](https://ai.google.dev/gemini-api/docs/models/gemini-3.8-live), [capabilities](https://ai.google.dev/gemini-api/docs/live-api/capabilities), [tools](https://ai.google.dev/gemini-api/docs/live-api/tools), [official SDK function-response wire types](https://github.com/googleapis/js-genai/blob/main/src/types.ts), [best practices](https://ai.google.dev/gemini-api/docs/live-api/best-practices), [session management](https://ai.google.dev/gemini-api/docs/live-api/session-management), and [Gemini 3.8 Flash](https://ai.google.dev/gemini-api/docs/whats-new-gemini-3.8).
