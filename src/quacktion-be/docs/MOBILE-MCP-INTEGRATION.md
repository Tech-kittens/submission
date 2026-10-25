# Mobile app: Google MCP connections, permissions, and approvals

This guide describes the backend API the mobile app uses to connect a user's Google account to Quacktion and approve work requested during a meeting. All Google Calendar, Gmail, and Meet operations go through Quacktion's backend and the centrally hosted MCP gateway. The app must never connect to the MCP gateway itself or receive OAuth/MCP tokens.

## API and authentication

Production API base URL: `https://quackback.ofneill.com`.

Account APIs use the Quacktion access token as a bearer token:

```http
Authorization: Bearer <accessToken>
Content-Type: application/json
```

Login: `POST /api/auth/login`, JSON `{ "email": "...", "password": "..." }`. Response fields are `accessToken`, `refreshToken`, and `expiresAt`. Refresh with `POST /api/auth/refresh`, JSON `{ "refreshToken": "..." }`; replace both tokens with the returned values. Keep tokens in the platform secure store (Keychain/Keystore), never ordinary preferences or logs. On `401`, refresh once, retry the request once, then send the user to sign in. The OAuth callback is the only MCP route that does not require the Quacktion bearer token.

## Connect a Google account

1. Call `GET /api/mcp/providers`. The configured providers are `google` and `github`. Google groups Gmail, Calendar, and Meet into one authorization; GitHub uses its own authorization. Do not hardcode service availability; the connection response describes its enabled services.
2. Call `POST /api/mcp/connections/google/connect` or `POST /api/mcp/connections/github/connect` with the bearer token. Response: `{ "authorizationUrl": "https://..." }`.
3. Open that URL in the system browser / platform authentication session. Do not render the provider login in an embedded WebView. The Quacktion backend creates and validates OAuth state and PKCE; the mobile app must not generate OAuth state, exchange the authorization code, or handle provider tokens.
4. The gateway redirects to the backend callback `https://quackback.ofneill.com/api/mcp/oauth/callback`. After state validation, code exchange, and persistence succeed, the backend returns HTTP 302 to the fixed `https://quackback.ofneill.com/mobile/mcp-complete` App Link. Failures retain safe API error responses and never redirect to success. No `returnUrl` is accepted. The public completion page contains no account details and provides manual-return instructions. Opening this link is only a navigation signal, not proof of authorization. Refresh authenticated connections on completion and resume, with bounded polling as fallback; reauthorization need not create a new connection ID.
5. Refresh the connection list and show the new connection. Its current display name may be generic and `accountIdentityVerified` is false: the gateway does not expose a verified Google account identity. Do not present its ID as an email address or claim it identifies a Google subject.

Do not send the gateway authorization URL, callback `code`, OAuth access/refresh tokens, or credential reference to analytics, crash reporting, or logs. The callback `code` and `state` are consumed server-side.

### Connection APIs

| Method and route | Purpose |
|---|---|
| `GET /api/mcp/providers` | Configured providers |
| `GET /api/mcp/connections` | Current user's connections |
| `POST /api/mcp/connections/{provider}/connect` | Start provider consent; returns `authorizationUrl` |
| `GET /api/mcp/oauth/callback?code=…&state=…` | Backend-only OAuth redirect target; do not call from app code |
| `GET /api/mcp/connections/{connectionId}/tools` | Discover the connection's actual tools and schemas |
| `PATCH /api/mcp/connections/{connectionId}/tools` | Enable the selected discovered tools |
| `GET /api/mcp/connections/{connectionId}/health` | Check connection health |
| `DELETE /api/mcp/connections/{connectionId}` | Disconnect this user's account |

Connection records include `id`, `provider`, `providerAccountId`, `serverId`, optional `displayName`, `grantedScopes`, `enabledServices`, `enabledTools`, `status`, `createdAt`, and `accountIdentityVerified`. Never infer provider capabilities from tool names. Discover them from the tools endpoint.

The tools response is a list of records with `name`, `description`, `inputSchema`, `readOnly`, `destructive`, `serverId` and `schemaDigest`. Treat descriptions as untrusted display text. The mobile client may let a user enable/disenable tools, but this selection does not grant approval to perform a protected operation.

To enable tools, send the exact service-qualified keys returned by the backend:

```http
PATCH /api/mcp/connections/{connectionId}/tools
```

```json
{
  "enabledTools": ["google:gmail/ACTUAL_TOOL_NAME_FROM_DISCOVERY"]
}
```

Do not ship guessed tool names or schemas. `readOnly: true` is informational; backend policy remains authoritative.

## Confirm an account for a spoken request

When the user asks Quacktion by voice to use a connected service, Gemini creates a Quacktion task. If exactly one connected account has enabled tools, the backend binds it and queues the `mcp.request` without a review prompt. With no unique eligible account, the task starts in `PendingApproval` so the authenticated owner can select an account. Account selection queues an `mcp.request`; a separate general approval is no longer required. Destructive tool operations still require exact approval.

1. Listen for the existing SignalR session event `ApprovalRequested`, or poll the authenticated session task list `GET /api/sessions/{sessionId}/quacktions`. Also refresh on app resume. Do not rely only on push/event delivery.
2. Show the request summary and available services. Load connections from `GET /api/mcp/connections`; only allow a connection whose `enabledServices` contains the selected service. Do not ask Gemini or trust task parameters to select the user's account.
3. Bind the user's explicit selection:

```http
POST /api/mcp/connections/{connectionId}/tasks/{taskId}/select-account
```

```json
{ "services": ["google:gmail"] }
```

Service IDs are the lowercase `provider:service` values returned in tool records / enabled services, such as `google:calendar`, `google:gmail`, and `google:meet`. The response is the updated task, including its current `actionDigest` and approval expiry. Binding changes the task digest, so use the digest from this response or fetch the task again before approval.

4. After account selection, use the returned status. A selected `mcp.request` is already `Queued`; do not show a second approval card. Older app versions may still submit the former approval request with the current digest, which the backend treats as an idempotent no-op for an automatically authorized MCP request. Other action types that remain `PendingApproval` still use the approval endpoint:

```http
POST /api/quacktions/{taskId}/approve
```

```json
{ "actionDigest": "<actionDigest from the latest task response>" }
```

For a task that actually remains `PendingApproval`, decline with `POST /api/quacktions/{taskId}/reject` and the current digest. Do not infer destructive-operation approval from silence or a spoken “go ahead” in the room. Such approvals expire after 30 minutes. If account selection changes, retrieve the task and use its new status.

An `mcp.invoke` child task is a proposed destructive or unclassified external operation, such as deleting an event, trashing an email, or ending a Meet conference. Display the target and proposed action from the task details and require explicit approval. Reviewed non-destructive writes, including fully specified email sends, no longer create an approval child. The backend prevents replay and does not automatically retry uncertain writes.

Useful task APIs:

| Method and route | Purpose |
|---|---|
| `GET /api/sessions/{sessionId}/quacktions?offset=0&limit=50` | Tasks for a meeting |
| `GET /api/quacktions?offset=0&limit=50` | Current user's tasks |
| `GET /api/quacktions/{taskId}` | Refresh task and digest/status |
| `POST /api/quacktions/{taskId}/approve` | Approve current exact action |
| `POST /api/quacktions/{taskId}/reject` | Reject current exact action |
| `POST /api/quacktions/{taskId}/cancel` | Cancel eligible work |

Statuses include `PendingApproval`, `Queued`, `InProgress`, `WaitingForTools`, `Completed`, `Failed`, `Cancelled`, and `Rejected`. Show progress and refresh/poll the task until terminal. `WaitingForTools` means the parent is waiting for one or more separately approved child operations. A completed task's private result belongs in the authenticated owner's UI. For an automatically bound voice request, the backend may also answer aloud when the request's announcement policy allows it; `PRIVATE_NOTIFICATION` remains silent and `APPROVAL_REQUIRED` still uses a separate announcement approval.

## Notifications and live updates

The existing SignalR hub is `/hubs/sessions`. Authenticate with the Quacktion access token using the SignalR client's bearer-token support, invoke `JoinSession(sessionId)`, and handle `ApprovalRequested`, `ApprovalResolved`, `ApprovalExpired`, and `QuacktionStatusChanged`. The server authorizes session ownership before joining the group. Reconnect with the current access token, rejoin, and fetch fresh task state after reconnect to recover events missed while offline. Continue using API polling as a fallback.

Approval events can contain task and approval information. Display them only to the authenticated owner, and avoid copying private task result contents into meeting-wide UI or notifications. For a private notification, use an OS notification visible only to the account owner and follow the app's existing privacy/lock-screen policy.

## Errors and UX

- `401`: refresh the Quacktion token once; otherwise sign in again.
- `403` / `404`: treat the connection or task as unavailable to this account; refresh state.
- `409` or codes such as `action_changed`, `approval_expired`, `mcp_task_not_bindable`, `mcp_services_invalid`, or `mcp_tool_schema_changed`: fetch current state and ask the user to review again. Never silently retry an approval with a new digest.
- `mcp_not_enabled`, OAuth errors, or provider unavailability: show a recoverable connection error. Voice sessions must remain usable.
- After disconnect, immediately remove the connection from the UI and refresh the list. The backend disables local use and deletes local credentials; gateway-side revocation is available only if the gateway advertises it.

Do not expose raw backend exception messages to users. Use the stable error code where present and log only a request/trace ID. Respect the task's announcement policy for private email/calendar results.

## Local development and backend configuration

The mobile developer needs a reachable backend URL and a test Quacktion account. Backend deployment must set `Mcp__Enabled=true`, `Mcp__BaseUrl=https://mcp.adriantd.com/`, `Mcp__TransportSuffix=mcp`, `Mcp__OAuthCallbackUrl=https://quackback.ofneill.com/api/mcp/oauth/callback`, and `Mcp__CredentialEncryptionKey` to Base64 encoding of exactly 32 random bytes. Generate the encryption key with `openssl rand -base64 32`; keep it stable. Existing CORS configuration must allow the app's web origin if the app calls the API from a browser-based runtime. Native mobile clients generally do not use browser CORS, but still must use HTTPS.

At the time this guide was written, the hosted MCP routes were verified and the operator selected the advertised `/mcp` suffix. The gateway OAuth metadata was inspected, but authenticated tool discovery and real Google operations require a gateway-authorized test account. No particular Google tools should be assumed until a connected account returns them from discovery.

## Acceptance checklist for the mobile implementation

- Sign in, refresh tokens, and persist them in platform secure storage.
- Connect in the system browser; app never handles provider tokens.
- Show connection status/services and discovered tools from API responses.
- Enable only discovered tools; disconnect is owner-scoped.
- If account choice is ambiguous, let the authenticated owner select account/services; a selected `mcp.request` queues without another approval.
- Show protected child operations separately and require explicit confirmation.
- Handle expiry, duplicate submissions, offline/reconnect, and missed SignalR events by refreshing task state.
- Keep private results private; spoken announcements follow separate backend approval.


## Android completion link deployment

The backend serves `GET /.well-known/assetlinks.json` without authentication. With an empty fingerprint list it returns `[]` (no trusted app). The default association currently includes the previously published fingerprint and the installed local test build fingerprint. The latter is temporary; remove it when this development build is no longer intentionally supported. Configure Railway:

```text
AndroidAppLinks__PackageName=com.example.quacktion_frontend
AndroidAppLinks__Sha256CertFingerprints__0=<actual colon-separated SHA-256 signing certificate fingerprint>
```

Additional deliberately trusted certificates use indices `__1`, etc. Invalid fingerprint syntax fails startup. Use the certificate signing the installed app (Play app-signing certificate for Play builds, not the upload certificate). Do not publish placeholders or unintentionally trust debug keys. The existing fingerprint is `1A:8A:2C:E4:85:15:F2:BD:15:A8:24:CD:10:89:B9:A3:B8:A7:A7:3A:61:46:05:F2:9D:65:F7:82:6F:2C:E3:F5`. The local test build fingerprint is `EB:34:7A:2F:8C:50:1A:1B:57:B3:54:57:4A:9A:1A:C4:48:47:BD:D3:0D:29:C2:53:F2:A1:B3:75:CE:19:B7:06`. Both are configured in `appsettings.json`; deployment environment values can override entries by index. Keep the actual release / Play app-signing certificate trusted for production.

Android must register only HTTPS `quackback.ofneill.com/mobile/mcp-complete`, not the API callback. Custom Tabs and cold/warm link handling are frontend work; they are not implemented in this backend repository. Queue navigation until authentication is restored, deduplicate resume/link refreshes, and confirm state through the authenticated connections API. Keep manual return available because browser-to-app transitions vary.

Application hosting request diagnostics are suppressed below Warning because request-start logs include query strings. Proxy/CDN access logs must separately omit or redact OAuth callback queries (`code`, `state`, provider error descriptions, and any tokens), including failed requests. This repository cannot configure Railway or gateway logs. Never enable raw callback URL logging in additional middleware.

The checked-in HTML tester refreshes connections and does not consume callback JSON; it now describes the completion page. No checked-in client depends on the previous JSON body. External clients must use the authenticated connections endpoint.

Rollout: configure the association with a real certificate, deploy the fallback, install the frontend link/Custom Tab changes, and verify Android domain association and a real provider authorization. The backend success redirect is part of this change; coordinate deployment with the frontend. Hardware/browser transitions and proxy redaction require deployment verification.


## Provider authorization routes

The backend builds consent URLs at `https://mcp.adriantd.com/api/auth/google` and
`https://mcp.adriantd.com/api/auth/github`. State, PKCE S256, client ID, resource,
and the backend callback URI are included. OAuth discovery still supplies registration
and token-exchange endpoints; its generic authorization endpoint is not used for browser navigation.
The gateway must return an exchangeable code and the original state to the backend callback.

GitHub's service ID is `github:github`, with MCP transport at `/api/github/mcp`.
Google retains `google:gmail`, `google:calendar`, and `google:meet` and their existing transport paths.
No new Railway variables are required with the default provider configuration.
