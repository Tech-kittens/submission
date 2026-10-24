# Quacktion backend

A .NET 10 LTS, controller-based backend foundation for an autonomous meeting assistant. MongoDB is authoritative for identity, ownership, sessions, approvals, and durable task execution.

The backend supports ElevenLabs conversational sessions and a versioned wake-word device start message. Live use requires valid ElevenLabs settings and a firmware audio format that matches the provider's negotiated format. No active session is reported until the provider WebSocket has initialized.

## Implemented

- Three production projects: `Quacktion.Web` → `Quacktion.App` + `Quacktion.Data`; `Quacktion.Data` → `Quacktion.App`.
- Controllers, validation, ProblemDetails, CORS allowlist, per-IP rate limits, structured audit events, OpenAPI/Swagger in Development.
- PBKDF2 through ASP.NET Core PasswordHasher, 10-minute JWTs, database-backed account/security-stamp validation, 30-day refresh families with atomic rotation and replay revocation.
- Owner-scoped device/session/task APIs; one-time pairing challenges; hashed device credentials; authenticated native WebSockets.
- Versioned control/audio boundary, bounded bidirectional streaming, format allowlists, fragmentation limits, cancellation, reconnect replacement, credential revalidation.
- Session transitions and cleanup, startup recovery, exclusive device reservations, incremental transcript persistence.
- ElevenLabs signed-URL/private and direct/public WebSocket connections, session-bound conversation IDs, initial text prompts, audio transport, transcript events, provider ping/pong, and disconnect failure handling.
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
| `ElevenLabs__AgentId` | Required configured agent ID |
| `ElevenLabs__ApiKey` | Required for private agents when `ElevenLabs__UseSignedUrl=true` (default); keep it server-side |
| `ElevenLabs__UseSignedUrl` | `true` for private agents; set `false` for public agents |
| `Sessions__InactiveTimeoutSeconds` | End idle sessions after this many seconds; `0` disables the timeout; default `1800` |
| `SessionAudio__ConnectionWindowSeconds` | Time allowed to open the session audio WebSocket after session creation; default `60`, capped at 300 seconds |
| `Audio__AllowedInputFormats__0__Codec`, `SampleRate`, `Channels` | Explicit device input allowlist |
| `Audio__AllowedOutputFormats__0__Codec`, `SampleRate`, `Channels` | Explicit output allowlist |

Both audio format lists default to empty. Configure exact device formats only after confirming the codec/rate/channels match ElevenLabs metadata; no transcoding is performed. The API key and signed URL are never returned to clients.

This version supports **one Web/transport process per deployment**. Its connection registry and live voice resources are process-local. Durable task leases support competing workers, but do not run multiple Web instances: startup recovery assumes it owns the transport. Multi-host WebSocket ownership, routing, SignalR backplanes, and coordinated session recovery are future work. Behind a proxy, configure trusted forwarded headers explicitly before enabling redirects/rate limits; the application does not trust arbitrary forwarding headers.

## API

Except account creation and `/api/auth/*`, APIs require `Authorization: Bearer <access-token>`. Device sockets use a distinct credential. All resource operations verify ownership; inaccessible resources return 404. Lists accept `offset` (>= 0) and `limit` (1–100, default 50) and return `{ items, offset, limit }`.

| Resource | Routes |
| --- | --- |
| Account | `POST /api/accounts`; `GET`, `PATCH`, `DELETE /api/accounts/me`; `PUT /api/accounts/me/password` |
| Authentication | `POST /api/auth/login`, `/refresh`, `/logout` |
| Devices | `POST`, `GET /api/devices`; `GET`, `PATCH`, `DELETE /api/devices/{id}` |
| Pairing/status | `POST /api/devices/{id}/pairing-challenges`, `/pair`, `/unpair`; `GET /api/devices/{id}/status` |
| Sessions | `POST`, `GET /api/sessions`; `GET`, `PATCH`, `DELETE /api/sessions/{id}`; `POST .../start`, `.../end` |
| Session data | `GET /api/sessions/{id}/transcript`, `.../quacktions`; `POST .../quacktions` to propose a task |
| Quacktions | `GET /api/quacktions`, `/{id}`; `POST /{id}/approve`, `/reject`, `/cancel`, `/retry` |
| Agent catalog | `GET /api/agents` (enabled server-configured providers) |
| Notifications | SignalR `/hubs/sessions`; `JoinSession(sessionId)`, `LeaveSession(sessionId)` |
| Hardware | Native WebSocket `/ws/devices/{deviceId}` |

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

- **ElevenLabs:** the live server-to-server WebSocket and event/audio bridge are implemented against the documented [WebSocket contract](https://elevenlabs.io/docs/eleven-agents/libraries/web-sockets). Configure an agent and credentials before starting. Firmware codec selection and physical playback still need validation. The backend intentionally does not transcode.
- **quack.ofneill.com:** protocol and responsibilities remain undecided. `QuacktionAgentGateway` reports `quack_protocol_not_configured`; no remote endpoints are invented or called.
- **Task agents:** register an `IAgentGateway` with explicit capabilities and provision an enabled `agents` document with a matching `Provider` and capabilities. Metadata alone cannot make a provider executable. Gateway requests include owner, session, exact action parameters, and stable idempotency key. There is deliberately no public endpoint to register arbitrary URLs or change capabilities.
- **Research/verification:** contracts model sources, retrieval times, evidence, and assessment. Unconfigured research fails explicitly; claim verification returns `NotVerified`, not invented evidence. No pre-meeting research or proactive suggestion provider has been installed.
- **Hardware:** binary/control framing is tested against a fake adapter, not physical firmware. No production audio format is enabled and no transcoding is implemented. Add `Audio__AllowedInputFormats__0__...` and output settings only after matching them to the provider’s conversation-init metadata and firmware output.

For local private-agent development, keep the API key out of source control with `dotnet user-secrets` from `src/Quacktion.Web` (`dotnet user-secrets set "ElevenLabs:ApiKey" "..."`; set `ElevenLabs:AgentId` the same way). Deployment environments can use `ElevenLabs__ApiKey`, `ElevenLabs__AgentId`, and `ElevenLabs__UseSignedUrl`. Public agents can use `UseSignedUrl=false` and do not require an API key.

## Tests

```bash
dotnet test Quacktion.sln
# Optional real MongoDB checks; each test creates and removes its own uniquely named database:
QUACKTION_TEST_MONGO=mongodb://localhost:27017 dotnet test Quacktion.sln
```

No live ElevenLabs credentials are needed for tests. The WebSocket wake-word test logs in, pairs a fake device, sends `session.start` with the prompt text (without “Quacktion”), retries the same request ID, and checks the created Active session and one prompt submission. It uses in-memory repositories and a fake ElevenLabs gateway; it does not validate a live MongoDB or provider response. The application tests cover account/authentication, token replay, pairing, owner checks, session transitions/device association, startup idempotency, deterministic risk, approvals/rejections, safe execution, retries, cancellation, duplicate worker claims, lease recovery, and tasks surviving session end. Web tests exercise controller discovery/DI, JWT revocation, ProblemDetails, SignalR authorization, device authentication, bidirectional audio using an explicit test-only format, reconnect and message limits. A full-host test runs actual startup/index initialization and the MongoDB-backed BackgroundService with a test-only task gateway, and checks fail-closed voice startup. Mongo tests cover actual driver serialization, unique indexes, optimistic concurrency, concurrent leases, fencing, audit projection and refresh-token reuse.

To exercise the wake-word control protocol without hardware, run `dotnet test tests/Quacktion.Web.Tests --filter AuthenticatedWakeWordMessageCreatesOneSessionAndSendsInitialPromptOnce`. For session-based audio proxying, provision and pair a device as described in [the device protocol](docs/device-protocol.md), configure the server-side ElevenLabs settings and exact audio allowlists, then run the WAV simulator:

```bash
dotnet run --project tools/Quacktion.AudioSimulator -- http://localhost:5188 <access-token> input.wav output.wav [input-codec] [output-codec] [output-rate] [output-channels]
```

The simulator calls `POST /api/sessions/start` with `Authorization: Bearer <accessToken>`, opens the returned URL with the same bearer token, negotiates `audio.start`, streams PCM 16-bit WAV data as binary frames and writes received PCM frames to `output.wav`. The backend selects the account's only paired device; if there are multiple paired devices, the request returns `device_selection_required`. The input WAV sample rate, channel count and codec must match the configured device input allowlist and ElevenLabs conversation metadata; pass the optional output codec/rate/channel arguments when provider output differs. No resampling or transcoding is performed. The WAV tool supports PCM 16-bit input and output only. Configure `Audio__AllowedInputFormats__0__Codec`, `SampleRate`, `Channels` and the corresponding output settings to match the actual hardware/provider formats. Never put account access tokens or ElevenLabs API keys in firmware logs.

For live ElevenLabs verification, set `ElevenLabs__AgentId` and (for a private agent) `ElevenLabs__ApiKey` server-side, then run the simulator with a short PCM16 WAV whose exact format is allowlisted. Confirm the session reaches `Active`, provider audio appears in `output.wav`, transcripts show up via `GET /api/sessions/{id}/transcript` and SignalR, and the simulator receives `session.ended`. Do not use the ElevenLabs text-to-speech-only WebSocket endpoint; this integration uses the Conversational AI conversation WebSocket and its `user_audio_chunk`/`audio` events.

## Firmware next steps

1. Agree codec, channel count, sample rates, byte ordering, framing/duration and buffering limits with the ESP32-S3 firmware team, then set the audio allowlists.
2. Implement physical provisioning of the one-time returned device credential, secure device storage, certificate validation, heartbeat and reconnect behavior.
3. Validate the ElevenLabs adapter against the configured provider audio formats and events; add any required resampling/transcoding explicitly.
4. Exercise mic → backend → provider → backend → speaker with physical hardware. Measure latency, disconnect recovery, backpressure, echo handling, interruptions, and session teardown before enabling real meetings.
5. Add production retention, secret rotation, deployment monitoring, and multi-host coordination as deployment requirements become known.
