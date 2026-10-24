# Flutter session integration handoff

This document describes the authenticated REST and SignalR flow for a Flutter client to create, view and end Quacktion sessions and display their transcripts. The Flutter app authenticates as an account using a bearer access token. Do not use hardware device credentials in the app.

## Authentication

Use the account authentication flow in [AUTH.md](../AUTH.md). Send:

```http
Authorization: Bearer <accessToken>
Content-Type: application/json
```

Access tokens last 10 minutes. Refresh tokens are single-use and rotate; serialize refresh attempts and replace the access/refresh token pair together. On refresh failure, clear local credentials and require login. All session resources are owner-scoped by the backend; do not send an account ID as an ownership claim.

## Start a voice session

There are two web-client flows.

### Create and start in one request

Use this when the user has supplied the name and opening prompt:

```http
POST /api/sessions
Authorization: Bearer <accessToken>
Idempotency-Key: <unique-client-request-id>
Content-Type: application/json
```

```json
{
  "name": "Sprint planning",
  "deviceId": null,
  "initialPrompt": "Let's review the sprint goals and open issues.",
  "promptFormat": "text"
}
```

The backend creates the MongoDB session, connects to the configured ElevenLabs conversation, waits for provider initialization, submits the prompt once, and returns the session object with status `Active`. Use `deviceId: null` for a Flutter-only session. If an idempotency key is supplied, reuse it only when retrying the same request. Prefer a stable unique key per user action (for example, a UUID).

`promptFormat` is `text` by default. `audio` is reserved for an audio-only prompt path; do not send both a transcription and audio for the same utterance. `/api/sessions/start` and `/ws/sessions/{id}/audio` both use the account access token as a Bearer token. The backend selects the account's only paired device; with multiple paired devices, it returns `device_selection_required`.

### Create first, then start

If the UI needs to save a draft session before the user starts it, create without an initial prompt:

```http
POST /api/sessions
Authorization: Bearer <accessToken>
Idempotency-Key: <unique-create-request-id>
Content-Type: application/json
```

```json
{ "name": "Sprint planning", "deviceId": null }
```

This returns a `Created` session. Start it when the user is ready:

```http
POST /api/sessions/<sessionId>/start
Authorization: Bearer <accessToken>
Idempotency-Key: <unique-start-request-id>
Content-Type: application/json
```

```json
{
  "initialPrompt": "Let's review the sprint goals and open issues.",
  "promptFormat": "text"
}
```

The start response is returned only after provider initialization and prompt submission. Starting a session that is already Active with the same start idempotency key returns its current session; an incompatible key/state returns a conflict. Failed initialization is terminal for that session; create a new one after resolving the reported configuration/provider failure.

## Session endpoints

All routes below require the account bearer token and verify session ownership.

| Purpose | Request | Result |
| --- | --- | --- |
| Create (and optionally start) | `POST /api/sessions` | Session JSON (`201`) |
| List | `GET /api/sessions?offset=0&limit=50` | `{ items, offset, limit }` |
| Read | `GET /api/sessions/{id}` | Session JSON |
| Start an existing draft | `POST /api/sessions/{id}/start` | Session JSON |
| End | `POST /api/sessions/{id}/end` | Final session JSON |
| Transcript | `GET /api/sessions/{id}/transcript?offset=0&limit=50` | `{ items, offset, limit }` |

Use `Idempotency-Key` for create/start/end retries where the request represents a user action. Only Created sessions can be deleted. Ending a session stops voice resources but does not cancel independent Quacktions. A completed or failed session and its transcript remain available.

Status values currently returned as strings are `Created`, `Starting`, `Ready`, `Connecting`, `Active`, `Ending`, `Completed`, and `Failed`. Web-created sessions normally move through `Created → Starting → Active → Ending → Completed`; a hardware session uses `Ready → Connecting → Active` after its device WebSocket connects.

Session JSON includes fields such as `id`, `ownerAccountId`, `deviceId`, `title`, `status`, `agentSessionId`, `startRequestId`, `initialPrompt`, `promptFormat`, `createdAt`, `updatedAt`, and (when applicable) `endedAt` / `failureCode`. Treat provider-specific identifiers as opaque and do not display or use them for authorization. Device connection credential hashes/expiry/consumption state are never serialized.

## Live updates with SignalR

Connect a SignalR client to:

```text
https://<api-host>/hubs/sessions
```

Configure the SignalR access-token provider to return the current account JWT. After connecting, invoke `JoinSession(sessionId)` for each session the signed-in user is viewing. The hub checks ownership before joining; an inaccessible session returns `not_found`. Invoke `LeaveSession(sessionId)` when leaving the view and reconnect/join again after a SignalR reconnect.

Handle these server event names:

| Event | Payload | Client use |
| --- | --- | --- |
| `SessionStatusChanged` | `{ id, status }` | Update status; refetch `GET /api/sessions/{id}` for full details. |
| `AgentStatusChanged` | `{ status }` with `Connecting`, `Connected`, or `Disconnected` | Show conversational-agent connection state. |
| `TranscriptReceived` | Transcript entry (`sessionId`, `speakerIdentifier`, `text`, `startTime`, `endTime`, `source`, `sourceEventId`, timestamps/ID) | Append/deduplicate a transcript row. |
| `AgentResponseReceived` | Transcript entry for the agent speaker | Optional agent-response-specific UI; it accompanies `TranscriptReceived`. |

SignalR notifications are best effort, not a durable event log. Use events to update the UI, then refetch REST state after reconnect or if an event is missed. Transcript ordering should use event arrival with a stable deduplication key such as `sourceEventId`; the REST transcript endpoint is the recovery source. Do not assume every entry has timing data suitable for audio synchronization.

The app supports the SignalR JSON protocol. Use a Flutter SignalR client library compatible with ASP.NET Core SignalR; a raw WebSocket to `/hubs/sessions` is not equivalent to the SignalR protocol. Flutter/native clients should pass bearer tokens using the library's access-token provider. The server also accepts `access_token` in the hub query for browser transports, but query tokens can leak into access logs and are not preferred for Flutter.

## Error and retry behavior

REST validation and business errors use Problem Details (`application/problem+json`) with a stable `code`. Typical cases include:

- `401`: access token is missing, expired or revoked; refresh or require login.
- `404 not_found`: session does not exist or belongs to another account.
- `409`: session/device state conflict, duplicate active device session or incompatible idempotency use.
- `502` / `503`: provider initialization/configuration failure. The session may be Failed; refetch it before deciding whether to create a replacement.
- `429`: rate limit. Back off before retrying.

Do not blindly repeat a create/start operation with a new idempotency key after a timeout: the original request may have succeeded. Retry with the same key, then fetch the resulting session. Do not retry a terminal Failed session as if it were Created.

## Hardware-created sessions

The ESP32-S3 session audio flow also uses an account access token with `Authorization: Bearer <accessToken>` for both `POST /api/sessions/start` and the returned `/ws/sessions/{id}/audio` URL. The separate legacy `/ws/devices/{deviceId}` control socket continues to use a device credential. The ElevenLabs API key must never be sent to or stored by Flutter or firmware.

The hardware request and binary audio contract are documented in [SESSION.md](SESSION.md). Session persistence, device ownership, provider initialization, transcript deduplication and SignalR authorization are backend responsibilities.
