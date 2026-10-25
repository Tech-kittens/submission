# Quacktion backend

A .NET 10 LTS, controller-based backend foundation for an autonomous meeting assistant. MongoDB is authoritative for identity, ownership, sessions, approvals, and durable task execution.

Duck integration: [backend handoff status](docs/DUCK-BACKEND.md), [assistant audio](docs/SESSION.md), and [device meeting segments](docs/transcription-protocol.md).

The backend uses Gemini 3.8 Live for real-time meetings, Jev for optional typed task routing, Gemini 4 Argon for complex reasoning, and Gemini 3.8 Flash for concise background work. The device continues to use mono PCM16 at 16 kHz; the backend resamples Gemini’s 24 kHz output. A session becomes active only after Gemini acknowledges setup. See the [voice migration guide](docs/GEMINI.md) and [multi-agent/MCP guide](docs/MULTI-AGENT-MCP.md).

## Implemented

- Three production projects: `Quacktion.Web` → `Quacktion.App` + `Quacktion.Data`; `Quacktion.Data` → `Quacktion.App`.
- Controllers, validation, ProblemDetails, CORS allowlist, per-IP rate limits, structured audit events, OpenAPI/Swagger in Development.
- PBKDF2 through ASP.NET Core PasswordHasher, 10-minute JWTs, database-backed account/security-stamp validation, 30-day refresh families with atomic rotation and replay revocation.
- Owner-scoped device/session/task APIs; one-time pairing challenges; hashed device credentials; authenticated native WebSockets.
- Versioned control/audio boundary, bounded bidirectional streaming, format allowlists, fragmentation limits, cancellation, reconnect replacement, credential revalidation.
- Session transitions and cleanup, startup recovery, exclusive device reservations, incremental transcript persistence.
- Gemini Live server-to-server WebSockets, typed setup, bounded audio streaming, transcription, interruptions, automatic reconnection with persisted resumption handles, and context recovery.
- Authenticated `session.start` wake-word protocol with request idempotency. The wake phrase is detected locally and excluded from the initial prompt.
- Deterministic action allowlist, immutable action parameters, digest-bound approval, leased durable work, attempts/history, retries/backoff, cancellation and idempotency contracts.
- Account-authorized, session-scoped SignalR groups, including events after meeting completion.
- Real MongoDB repositories/indexes, optimistic concurrency and repairable approval/execution audit projections.
- Unit, HTTP, SignalR, WebSocket, and optional real-MongoDB integration tests.

## Run locally

Install the [.NET 10 LTS SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) and Docker Compose. The application compiles without MongoDB. Database-dependent requests return 503 until initialization succeeds.

```bash
docker compose up -d mongodb
export Jwt__SigningKey="$(openssl rand -hex 32)"
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://localhost:5188
export Cors__Origins__0=http://localhost:3000
dotnet restore Quacktion.sln
dotnet build Quacktion.sln --no-restore
dotnet run --project src/Quacktion.Web --no-build
```

Open `http://localhost:5188/swagger`. `GET /health/live` reports process liveness; `GET /health/ready` reports whether indexes and startup recovery completed. Readiness is an initialization check, not a continuous database ping. MongoDB outages after startup return errors from the affected operation.

Use HTTPS/WSS for any non-local deployment. The application requires a signing key of at least 32 bytes and fails startup without it. Generate a cryptographically random key; do not use a human password. Persist it in your deployment's secret store. Development MongoDB is unauthenticated and bound to loopback only. Production MongoDB must use authentication, TLS, backups, and appropriate network controls.

See [AUTH.md](AUTH.md) for authentication endpoints, token rotation, revocation behavior, device credentials, and SignalR authentication.

Open `/session-test.html` on the running site for a minimal browser session tester. It can create an account or log in, start an account-owned session, connect the audio WebSocket, negotiate formats, capture microphone PCM, and play returned PCM audio. After `audio.ready`, choose **Start microphone** and speak; browser microphone permission is required. Set the input and output formats to values present in the server's `Audio__AllowedInputFormats__*` and `Audio__AllowedOutputFormats__*` settings. The browser tester currently handles PCM formats only. The browser WebSocket API cannot set an Authorization header, so this endpoint accepts the account JWT as the `access_token` query parameter specifically on `/ws/sessions`; use HTTPS in deployment and avoid sharing copied WebSocket URLs.

## Configuration

Use environment variables, `dotnet user-secrets` after initializing the Web project, or a deployment secret provider. No real credentials are included.

| Setting | Purpose |
| --- | --- |
| `Jwt__SigningKey` | Required random signing secret, at least 32 bytes |
| `Jwt__Issuer`, `Jwt__Audience` | Token issuer/audience; defaults in appsettings |
| `MongoDB__ConnectionString` | Defaults to `mongodb://localhost:27017` |
| `MongoDB__Database` | Defaults to `quacktion` |
| `Cors__Origins__0`, etc. | Exact frontend origins in Development; deployed environments allow all origins |
| `AllowedHosts` | Set to deployed hostnames; defaults to local hosts |
| `Gemini__LiveModel` | Default `gemini-3.8-live` |
| `Gemini__ApiKey` | Google AI Studio API key; server-side only; required in Production |
| `Gemini__ReasoningModel`, `Gemini__FastReasoningModel`, `Gemini__Voice` | Defaults `gemini-4-argon`, `gemini-3.8-flash`, `Puck` |
| `Gemini__SystemInstruction` | Optional override of the meeting persona in `GeminiSettings` |
| `Gemini__EnableInputTranscription`, `Gemini__EnableOutputTranscription`, `Gemini__EnableSessionResumption`, `Gemini__EnableContextCompression` | All default `true` |
| `Sessions__InactiveTimeoutSeconds` | End idle sessions after this many seconds; `0` disables the timeout; default `1800` |
| `SessionAudio__ConnectionWindowSeconds` | Time allowed to open the session audio WebSocket after session creation; default `60`, capped at 300 seconds |
| `Audio__AllowedInputFormats__0__Codec`, `SampleRate`, `Channels` | Explicit device input allowlist |
| `Audio__AllowedOutputFormats__0__Codec`, `SampleRate`, `Channels` | Explicit output allowlist |

Input defaults to mono PCM16 little-endian at 16 kHz. Output allows mono PCM16 at 16 kHz (backend resampled) or 24 kHz (native Gemini). No API key or resumption handle is returned to clients.

This version supports **one Web/transport process per deployment**. Its connection registry and live voice resources are process-local. Durable task leases support competing workers, but do not run multiple Web instances: startup recovery assumes it owns the transport. Multi-host WebSocket ownership, routing, SignalR backplanes, and coordinated session recovery are future work. The app trusts one `X-Forwarded-Proto` hop to detect Railway's public HTTPS scheme; Railway overwrites that header on public ingress. Keep direct/private access to the app within the same trust boundary. Forwarded client-IP and host headers are not consumed.

## API

Except account creation and `/api/auth/*`, APIs require `Authorization: Bearer <access-token>`. Device sockets use a distinct credential. All resource operations verify ownership; inaccessible resources return 404. Lists accept `offset` (>= 0) and `limit` (1–100, default 50) and return `{ items, offset, limit }`.

| Resource | Routes |
| --- | --- |
| Account | `POST /api/accounts`; `GET`, `PATCH`, `DELETE /api/accounts/me`; `PUT /api/accounts/me/password` |
| Authentication | `POST /api/auth/login`, `/refresh`, `/logout` |
| Devices | `POST`, `GET /api/devices`; `GET`, `PATCH`, `DELETE /api/devices/{id}` |
| Pairing/status | `POST /api/devices/{id}/pairing-challenges`, `/pair`, `/unpair`; `GET /api/devices/{id}/status` |
| Meetings | `POST`, `GET /api/meetings`; `GET`, `DELETE /api/meetings/{id}`; `POST /api/meetings/{id}/end` (Bearer token; device ID optional on creation) |
| Sessions | `POST`, `GET /api/sessions`; `GET`, `PATCH`, `DELETE /api/sessions/{id}`; `POST .../start`, `.../end` |
| Session data | `GET /api/sessions/{id}/transcript`, `.../quacktions`; `POST .../quacktions` to propose a task |
| Quacktions | `GET /api/quacktions`, `/{id}`; `POST /{id}/approve`, `/reject`, `/cancel`, `/retry` |
| Agent catalog | `GET /api/agents` (enabled server-configured providers) |
| MCP connections | `GET /api/mcp/providers`, `/api/mcp/connections`; connect, OAuth callback, disconnect, and tool-selection routes in [the MCP guide](docs/MULTI-AGENT-MCP.md) |
| Notifications | SignalR `/hubs/sessions`; `JoinSession(sessionId)`, `LeaveSession(sessionId)` |
| Hardware | Native WebSocket `/ws/devices/{deviceId}` |

Create a meeting from an app without a duck:

```http
POST /api/meetings
Authorization: Bearer <access-token>
Content-Type: application/json

{"name":"Planning","recordingConsent":true}
```

The Bearer token identifies the owner. No `deviceId` is required for phone recording. To record through a paired duck, optionally add its `deviceId` to this body; ownership is checked. `/api/meetings/start` remains an alias. Swagger's **Authorize** button accepts an access token and sends the Bearer header for protected endpoints. A device ID alone never authenticates a request.

Registration: `{ "email", "password", "name" }`; email is the login identifier and passwords are 12–128 characters. Login: `{ "email", "password" }`. The profile name is editable independently. Refresh/logout: `{ "refreshToken" }`. Store refresh tokens securely in the frontend, never in URLs. Refresh rotation is intentionally strict: serialize refresh requests; concurrent use revokes the family. Logout revokes that refresh family; existing access tokens expire within 10 minutes. Password change and account deletion invalidate access tokens through the database security stamp check.

Session creation: `{ "name", "deviceId": null, "initialPrompt": "Let's discuss sprint progress." }` creates and starts a voice session. A session without a prompt can also be created first and started later. `POST /api/sessions/{id}/start` accepts `{ "initialPrompt", "promptFormat" }`; `Idempotency-Key` can be used for retries. Device registration: `{ "deviceIdentifier", "name", "firmwareVersion" }`. Task proposal: `{ "actionType": "internal.summary", "parametersJson": "{}" }`. Approval/rejection requires `{ "actionDigest": "<digest from task>" }`. Approval returns **202 Queued**, not execution completion. Task parameters are immutable through public APIs; propose a new task for changed intent.

Only never-started sessions can be deleted. Completed/failed sessions remain available for evidence and outstanding tasks. Session ending never cancels tasks. A failed initialization is terminal for that session; create a new session after resolving configuration.

## Security and retention policies

- Registration normalizes email; MongoDB enforces uniqueness. Hashes, credential material, and security stamps are excluded from HTTP serialization. Device credentials are random 256-bit secrets, stored only as SHA-256 hashes; password hashing uses the ASP.NET facility.
- Account deletion is **soft deactivation**, not immediate data erasure. It immediately revokes user/device authorization. Running sockets and workers recheck account state at most every five seconds; background maintenance revokes device material, stops sessions, and cancels unstarted work. In-flight external side effects cannot necessarily be undone.
- Sessions, transcripts, task results, and audit history are retained after deactivation; the email remains reserved. There is no automatic retention purge yet. Define and implement an organization-specific erasure/export policy before storing real meeting data. Retention is intentionally not conflated with authentication revocation.
- No raw audio is persisted. Audio/transcript text, passwords, tokens, signed provider URLs, and API keys are not included in application logs. Keep proxy/APM request-body and query-string capture disabled for these routes, especially SignalR's browser `access_token` query.
- Pairing is an authenticated owner provisioning flow, not unauthenticated claiming by serial number. See [device protocol](docs/device-protocol.md) for out-of-band credential installation and its firmware limitations.
- Only exact trusted capabilities `internal.research`, `internal.summary`, `internal.suggestions`, `internal.analysis`, and `claims.verify` are allowlisted. All unknown/external operations need approval. Provider metadata and LLM risk claims cannot change this policy. A registered gateway must constrain these capabilities to authorized read-only/internal actions.

## Execution and notifications

MongoDB work documents are the queue. Claims use compare-and-swap with renewable 30-second leases, renewed every five seconds. Execution has a two-minute attempt timeout. Transient failures retry up to three attempts with exponential backoff (10s, 20s); manual retries are bounded by five total attempts. Expired leases recover using the same idempotency key; five total claims cap repeated crash recovery. Permanent errors fail immediately. Cancellation is cooperative and wins over a concurrent completion write.

An expired worker cannot commit a result over a newer lease. **External exactly-once execution is not promised**: future gateways must pass the stable idempotency key to providers or implement durable deduplication/reconciliation. An external operation may succeed before the worker loses its lease.

Approval and history snapshots embedded in the task document are authoritative, so approval and queueing commit atomically without requiring a MongoDB replica-set transaction. `approval_requests` and `quacktion_executions` are repairable read projections, periodically synchronized by maintenance. Current maintenance scans are appropriate for a foundation deployment; indexed incremental projection/retention work is needed at scale.

SignalR events: `SessionStatusChanged`, `TranscriptReceived`, `QuacktionProposed`, `QuacktionStatusChanged`, `ApprovalRequested`, `ApprovalResolved`, `AgentStatusChanged`, `ResearchCompleted`. Reconnect and rejoin your authorized sessions, then refetch REST state. Notifications are best-effort, not a durable event log; delivery failure does not roll back a committed action. Browser connections close on access-token expiry. Deactivated accounts receive no subsequent broadcasts.

## Incomplete integrations

- **Gemini:** automated tests use scripted provider sockets. Live verification requires a valid Google AI Studio key and model access. Physical microphone/speaker validation remains a deployment check.
- **quack.ofneill.com:** protocol and responsibilities remain undecided. `QuacktionAgentGateway` reports `quack_protocol_not_configured`; no remote endpoints are invented or called.
- **Task agents:** Gemini is the built-in fallback for `internal.summary`, `internal.analysis`, `internal.suggestions`, `internal.research`, and approval-gated `meeting.announce`. Explicit enabled agent catalog entries take precedence. Other capabilities require a real `IAgentGateway`; metadata cannot make them executable.
- **Research/verification:** Gemini task research uses Google Search grounding and persists the evidence metadata. The standalone research/claim-verification service remains unconfigured and does not fabricate results. There is no CRM, ticket, email or internal-search connector.
- **Hardware:** existing `QuacktionHW/firmware/main/session_audio.c` negotiates 16 kHz input/output and handles `audio.clear`. No firmware audio-format change is required. Validate physical playback, barge-in and echo cancellation on the device before rollout.

Store the local API key with `dotnet user-secrets set --project src/Quacktion.Web "Gemini:ApiKey" "<key>"`. In Railway use `Gemini__ApiKey`. Development without a key starts the HTTP API but rejects live sessions; Production validates the key is present at startup.

## Tests

```bash
dotnet test Quacktion.sln
# Optional real MongoDB checks; each test creates and removes its own uniquely named database:
QUACKTION_TEST_MONGO=mongodb://localhost:27017 dotnet test Quacktion.sln
```

Ordinary tests require no Google credentials. The suite covers authentication, ownership, approvals, leases, session APIs, the device protocol, Gemini setup, PCM encoding/decoding, resampling, interruption, tool correlation/idempotency, transcripts, reconnection and cancellation. MongoDB tests use isolated databases. The real provider smoke test is opt-in: set `QUACKTION_TEST_GEMINI_KEY` and run `dotnet test --filter Category=LiveGemini`.

To exercise the wake-word control protocol without hardware, run `dotnet test tests/Quacktion.Web.Tests --filter AuthenticatedWakeWordMessageCreatesOneSessionAndSendsInitialPromptOnce`. For session audio, configure the Gemini key and run the WAV simulator:

```bash
dotnet run --project tools/Quacktion.AudioSimulator -- http://localhost:5188 <access-token> input.wav output.wav [input-codec] [output-codec] [output-rate] [output-channels]
```

The simulator calls `POST /api/sessions/start` with the owning account bearer token, connects to `/ws/sessions/{id}/audio`, negotiates audio, sends 30 ms binary PCM frames and saves returned PCM to WAV. Use mono PCM16 input at 16 kHz. Output defaults to 16 kHz; optionally request `pcm_24000 24000 1` for native output. Hardware uses its paired device credential. The backend performs 24→16 kHz resampling when needed.

For live verification, run the simulator with a short spoken request addressing Quacktion. Confirm the session reaches `Active`, `output.wav` contains a correctly paced reply, transcripts appear in REST and SignalR, interruption clears queued playback, and session ending releases the device. See [deployment and smoke-test instructions](docs/GEMINI.md).

## Firmware next steps

1. Keep the existing 16 kHz PCM16 mono input/output negotiation. Native 24 kHz output is optional for clients that explicitly support it.
2. Implement physical provisioning of the one-time returned device credential, secure device storage, certificate validation, heartbeat and reconnect behavior.
3. Verify `audio.clear` immediately discards unplayed audio and that acoustic echo cancellation works on the target hardware.
4. Exercise mic → backend → provider → backend → speaker with physical hardware. Measure latency, disconnect recovery, backpressure, echo handling, interruptions, and session teardown before enabling real meetings.
5. Add production retention, secret rotation, deployment monitoring, and multi-host coordination as deployment requirements become known.

### Autonomous meeting preparation

The optional [meeting preparation backend](docs/meeting-preparation/README.md) adds calendar synchronisation through the existing MCP gateway, native Argon research, Atlas hybrid meeting memory, durable duck notifications and Gemini Live briefings. It is disabled by default and requires Atlas/MCP/Gemini configuration. See the guide for verified boundaries, API examples, environment variables and tests.
