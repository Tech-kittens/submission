# Authentication and authorization

This document describes the authentication implemented in this repository. Quacktion owns account identity, device ownership and action permissions; external AI providers cannot authenticate users or grant approval for actions.

## Credential types

| Credential | Used by | Transport | Lifetime |
| --- | --- | --- | --- |
| Account access token | Frontend/API clients | `Authorization: Bearer <accessToken>` | 10 minutes, with 15 seconds of validation clock skew |
| Account refresh token | Frontend/API clients | JSON body of `/api/auth/refresh` or `/api/auth/logout` | Family expires 30 days after login; each token is single-use for refresh |
| Pairing challenge | Authenticated device owner | JSON body of `/api/devices/{id}/pair` | Five minutes; consumed once |
| Device credential | ESP32-S3 | `Authorization: Device <credential>` during WebSocket upgrade | Until revoked; no automatic expiry |
| External provider API key | Backend only | Provider-specific server-side authentication | Managed outside this account/device system |

An account JWT cannot replace a device credential. A device credential cannot authorize account APIs. All non-local traffic must use HTTPS/WSS.

## Configuration

The Web application requires a random signing secret of at least 32 UTF-8 bytes. It validates this requirement at startup. Configure secrets through environment variables or your deployment's secret provider, rather than committing them to appsettings.

```bash
# Generate once for a local environment; retain the value across application restarts.
export Jwt__SigningKey="$(openssl rand -hex 32)"
export Jwt__Issuer=Quacktion
export Jwt__Audience=Quacktion.Frontend
export MongoDB__ConnectionString=mongodb://localhost:27017
export MongoDB__Database=quacktion
export Cors__Origins__0=http://localhost:3000
```

Issuer and audience above are the defaults. The signing key has no default. CORS permits only configured origins; an empty list does not enable cross-origin browser access. CORS is not an authorization mechanism.

MongoDB stores credential state and is required for authentication. Requests under `/api`, `/ws`, and `/hubs` receive `503 database_initializing` until indexes and startup recovery complete. JWT authentication also reads the account from MongoDB; this is not an offline, signature-only authentication scheme.

There is one active JWT signing key and no overlapping key ring. Replacing it invalidates previously issued access tokens on subsequent validation. Refresh tokens do not use that signing key and can obtain tokens signed with the replacement key if their account and family remain valid.

## Account endpoints

Requests and responses use JSON with camelCase properties.

| Endpoint | Credentials required | Success |
| --- | --- | --- |
| `POST /api/accounts` | None | `201`, account profile |
| `POST /api/auth/login` | Email/password in body | `200`, token pair |
| `POST /api/auth/refresh` | Refresh token in body | `200`, replacement token pair |
| `POST /api/auth/logout` | Refresh token in body | `204` |
| `GET /api/accounts/me` | Account JWT | `200`, account profile |
| `PATCH /api/accounts/me` | Account JWT | `200`, updated profile |
| `PUT /api/accounts/me/password` | Account JWT and current password | `204` |
| `DELETE /api/accounts/me` | Account JWT | `204` |

### Register and log in

```http
POST /api/accounts
Content-Type: application/json

{
  "email": "alex@example.com",
  "password": "example-only-password-123",
  "name": "Alex"
}
```

Passwords must be 12–128 characters. Email is required, validated as an email address, limited to 254 characters, trimmed and normalized to uppercase for storage and lookup. MongoDB enforces email uniqueness. Email is the only login identifier. The profile name is required and limited to 128 characters.

Registration returns the profile, not tokens. Password hashes and account security stamps are excluded from public account serialization. Production password hashing uses ASP.NET Core `PasswordHasher<Account>` with a configured iteration count of 210,000; passwords are never stored in plaintext. Login rehashes a successfully verified password if the hasher requests an upgrade.

```http
POST /api/auth/login
Content-Type: application/json

{"email":"alex@example.com","password":"example-only-password-123"}
```

A successful login returns:

```json
{
  "accessToken": "<signed-jwt>",
  "refreshToken": "<family-id>.<random-secret>",
  "expiresAt": "2026-10-23T16:10:00Z"
}
```

The timestamp is illustrative and describes the access token's expiry, not the refresh family's expiry. Invalid passwords, unknown emails and inactive accounts return `401 invalid_credentials`.

Use the access token for protected API calls:

```http
GET /api/accounts/me
Authorization: Bearer <accessToken>
```

`PATCH /api/accounts/me` accepts `{ "name": "Alex Updated" }`. It does not change email, password, account ID or ownership.

### Access-token validation

Tokens are signed with HS256. Claims include `sub` (account ID), `stamp` (account security stamp), `jti` (token ID), issuer, audience, not-before time and expiry. ASP.NET Core maps the subject into the name-identifier claim used by controllers.

Validation checks signature, algorithm, issuer, audience and lifetime. It then loads the account and rejects the token if the account is missing/inactive or its security stamp differs. A `jti` is issued but there is no individual access-token denylist.

### Refresh rotation and replay handling

```http
POST /api/auth/refresh
Content-Type: application/json

{"refreshToken":"<current-refresh-token>"}
```

An account access token is not required. Each successful login creates a separate refresh family in `refresh_tokens`. The secret is 32 cryptographically random bytes encoded as hexadecimal; only its SHA-256 hash is persisted. The family stores account ID, security stamp, current hash, previously used hashes, fixed expiry and revocation state.

Refresh checks the family, expiry, current secret and account/security stamp. A version-checked MongoDB replacement atomically moves the current hash into the used list and installs a new hash. The response contains a new access token and refresh token. Refreshing does not extend the family's original 30-day lifetime. After 500 successful rotations, another refresh revokes the family and requires login.

Reusing a previously consumed token revokes the entire family, including its newest refresh token. A competing rotation that loses the database concurrency check also revokes the family. An arbitrary incorrect secret is rejected without revoking a family just because its ID was supplied. Revoking a refresh family does not itself invalidate access JWTs already issued from it.

Client behavior:

1. Serialize refresh operations, including across browser tabs sharing credentials.
2. Replace the stored token pair together after a successful refresh; discard the old refresh token.
3. On a refresh `401`, clear the local token pair and require login.
4. Do not blindly retry the old token after an ambiguous network failure: the server may already have consumed it. Login again if the replacement pair was lost.
5. On `429` or a temporary service failure, avoid a tight retry loop.

The API returns tokens in JSON and does not create authentication cookies or provide a cookie-based browser session. Choose client storage deliberately; persistent JavaScript-readable storage exposes tokens to successful XSS. A server-managed browser session/BFF would be a separate integration, not an existing feature.

### Logout, password change and deletion

Logout takes `{ "refreshToken": "<token>" }`. A matching current or previously used secret revokes that family. A well-formed unknown token is a no-op with `204`; malformed token syntax returns `401`. Logout does not revoke other login families or already issued access tokens. Clients should discard their local pair and disconnect their frontend sockets.

Change password with:

```http
PUT /api/accounts/me/password
Authorization: Bearer <accessToken>
Content-Type: application/json

{"currentPassword":"example-only-password-123","newPassword":"replacement-example-password-456"}
```

The current password must verify. Success changes the password hash and security stamp, invalidating all previous account access tokens on subsequent validation and preventing all existing refresh families from refreshing. No replacement tokens are returned; log in again. Device credentials are separate and are not revoked by password change.

Account deletion is soft deactivation. It clears the password hash, changes the security stamp and marks the account inactive. Subsequent account/device authentication fails. Existing device connections and workers periodically recheck account activity; maintenance revokes stored device credentials, closes sessions and cancels eligible work. Deletion retains sessions, transcripts, results and audit records, and keeps the email reserved. It is not a data-erasure endpoint. See the retention policy in [README.md](README.md).

## Resource authorization

Controllers derive the account ID from the validated JWT, not a request-supplied owner ID. Device, session and Quacktion services enforce ownership; missing or inaccessible owned resources generally return `404 not_found`.

Approvals additionally bind the owner to the exact action and parameters through `actionDigest`. Approval is not a credential and cannot grant access to another account's resources. Execution rechecks the approval and account state. See [QuacktionService.cs](src/Quacktion.App/Services/QuacktionService.cs) for the implemented policy.

## Device pairing and WebSocket authentication

Pairing requires an authenticated owner:

1. `POST /api/devices` with `{ "deviceIdentifier", "name", "firmwareVersion" }` creates an owned, unpaired device. The response includes `device` and `pairing`, containing `challengeId`, `challenge` and `expiresAt`.
2. Confirm with `POST /api/devices/{id}/pair`, the account JWT, and `{ "challengeId": "<id>", "challenge": "<secret>" }`. The five-minute challenge is bound to that account and device, and consumed once using optimistic concurrency.
3. Success returns `{ "deviceId": "<id>", "credential": "<secret>" }`. The credential is a new random 256-bit secret encoded as 64 hexadecimal characters. Only its hash is stored; the plaintext is returned once.
4. Install the credential on the physical device using a trusted provisioning channel. The backend does not implement that physical channel, factory attestation or firmware secret storage. A serial number alone is not proof of physical ownership.

An unpaired device can obtain a replacement challenge through authenticated `POST /api/devices/{id}/pairing-challenges`. If a committed credential response is lost, unpair and pair again; the old secret cannot be retrieved. Challenge consumption and credential installation are separate writes, so failed pairing can require a new challenge.

The hardware handshake is:

```http
GET /ws/devices/<deviceId>
Authorization: Device <credential>
Connection: Upgrade
Upgrade: websocket
Sec-WebSocket-Protocol: quacktion.v1
```

A WebSocket client supplies the remaining standard handshake headers. The `Device ` scheme is case-sensitive in the current middleware. Credentials are accepted from this header only, not query strings. The backend validates the credential and active owner before upgrading, rechecks on acceptance and every five seconds during the connection, and requires the versioned subprotocol. Session association separately verifies that the session belongs to the same account and is assigned to that device.

`POST /api/devices/{id}/unpair` revokes the credential and disconnects the device. End an associated active session first; otherwise unpair returns `409 device_has_active_session`. Device credentials have no automatic expiry; rotate through unpair/pair. See [device-protocol.md](docs/device-protocol.md) for framing, connection replacement and audio negotiation.

## SignalR authentication

The frontend connects to `/hubs/sessions` using an account JWT. The bearer header is accepted; `access_token` in the query string is also accepted for this hub path to support browser transports. Do not use this query mechanism for ordinary REST or device requests, and exclude token-bearing query strings from proxy/APM logs.

`JoinSession(sessionId)` checks account activity and ownership before adding a connection to an account/session group. A client cannot choose an arbitrary group name. Completed meetings remain subscribable by their owner so background Quacktion progress can continue.

Connections close on authentication expiry. After reconnecting with a fresh access token, rejoin authorized sessions and refetch REST state. Existing hub connections do **not** revalidate the security stamp on every invocation or broadcast: password changes, signing-key replacement and logout do not immediately disconnect them. Broadcasts check account activity, so deactivated accounts receive no new broadcasts, and `JoinSession` rejects inactive accounts. Immediate stamp-based hub disconnection is not implemented.

## Errors and throttling

Errors use ASP.NET Core ProblemDetails. Service-generated errors use stable codes in `title`; framework bearer challenges and validation responses may use standard framework titles instead.

| Status | Examples |
| --- | --- |
| `400` | Invalid input, `invalid_pairing_challenge`, missing WebSocket upgrade/subprotocol |
| `401` | Invalid/expired bearer token; `invalid_credentials`, `invalid_refresh_token`, `refresh_reuse_detected`, `login_required`, `invalid_device_credentials` |
| `404` | Missing/inaccessible owned resource or missing pairing challenge |
| `409` | `duplicate_key`, `concurrency_conflict`, `already_paired`, active device association |
| `429` | `rate_limit_exceeded` |
| `503` | Database initialization or availability failure |

The global limiter allows 300 requests per minute per remote IP. The sensitive policy allows 10 requests per minute per IP for registration, login, refresh, logout, password changes, challenge renewal and pairing confirmation. Limits use fixed windows and no queue. They are process-local, not a distributed abuse-control system. Trusted proxy forwarding must be configured explicitly before using forwarded addresses for limits.

## Persistence, implementation and tests

`accounts` stores password hashes and account security stamps. `refresh_tokens` stores refresh families; `devices` stores credential hashes; `device_pairing_challenges` stores challenge hashes. Refresh families and challenges have expiry indexes, and services independently enforce expiry rather than waiting for MongoDB's TTL cleanup. Hash comparison for random secrets uses a constant-time comparison.

Implementation references:

- [AccountService.cs](src/Quacktion.App/Services/AccountService.cs): registration, login, rotation, revocation and account changes.
- [JwtTokenIssuer.cs](src/Quacktion.Data/Security/JwtTokenIssuer.cs): token claims and signing.
- [Program.cs](src/Quacktion.Web/Program.cs): validation, password hashing options, CORS and rate limits.
- [DeviceService.cs](src/Quacktion.App/Services/DeviceService.cs): pairing and credential authentication.
- [DeviceWebSocketMiddleware.cs](src/Quacktion.Web/WebSockets/DeviceWebSocketMiddleware.cs): pre-upgrade device authentication.
- [SessionHub.cs](src/Quacktion.Web/Hubs/SessionHub.cs): group authorization and broadcast activity checks.

```bash
dotnet test Quacktion.sln
# Include isolated, real-MongoDB tests:
QUACKTION_TEST_MONGO=mongodb://localhost:27017 dotnet test Quacktion.sln
```

Authentication coverage includes password hashing/login, email uniqueness, token rotation/replay/concurrency, logout/password/deletion revocation, device pairing and unpairing, JWT rejection after account deletion, resource ownership, WebSocket authentication and SignalR group authorization. Tests use fakes or isolated databases; live AI-provider credentials are unnecessary.

Email verification, password reset/recovery, MFA, external identity providers, role-based administration, session inventory, an explicit logout-all endpoint, automatic device credential expiry and a multi-key JWT rotation mechanism are not implemented.
