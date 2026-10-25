# Backend handoff: fix MCP authorization returning to the token page

## Scope and evidence

Inspected QuacktionBE `main` at `d6dd9a5` and QuacktionAI `main` at `ff6489a`.

The mobile connect flow ends on `mcp.adriantd.com` showing “Connected as …” and a copyable bearer token. That page is the gateway's manual-token flow, not completion of the backend's OAuth transaction.

The deployed gateway's public metadata advertises:

```text
https://mcp.adriantd.com/.well-known/oauth-authorization-server

authorization_endpoint = https://mcp.adriantd.com/oauth/authorize
token_endpoint         = https://mcp.adriantd.com/oauth/token
registration_endpoint  = https://mcp.adriantd.com/oauth/register
```

## Root cause

`src/Quacktion.Data/Agents/McpConnectionService.cs`, in `BeginConnectAsync`, discovers the OAuth metadata but overrides its authorization endpoint with:

```csharp
var authorizationEndpoint = new Uri(registry.BaseUri,
    "api/auth/" + Uri.EscapeDataString(provider.ToLowerInvariant()));
```

QuacktionAI's `/api/auth/google` handler starts direct provider sign-in without the MCP client context. It does not preserve the supplied backend `redirect_uri`, `state`, or PKCE parameters. Its callback consequently shows the manual bearer-token page instead of returning an authorization code to QuacktionBE.

This explains why Google sign-in can succeed while no backend connection is created.

## Required minimal change

In `BeginConnectAsync`:

1. Use `metadata.AuthorizationEndpoint`, already returned by discovery, instead of constructing `/api/auth/{provider}`.
2. Add `provider = provider.ToLowerInvariant()` to the existing authorization query dictionary. The gateway supports this parameter; it avoids its provider-selection screen when the resource is the gateway's base URL.
3. Preserve all existing state, PKCE, resource, scope, client registration, and transaction persistence logic.
4. Replace the existing comment that incorrectly describes the direct provider route as the client authorization entry point.

The relevant resulting code should use:

```csharp
query["provider"] = provider.ToLowerInvariant();
var authorizationEndpoint = metadata.AuthorizationEndpoint;
```

Append the existing encoded query parameters to that endpoint using a URI-safe query builder. Preserve any existing endpoint query parameters rather than assuming a second `?` is valid.

Example resulting URL shape, with placeholders only:

```text
https://mcp.adriantd.com/oauth/authorize
  ?provider=google
  &client_id=<registered-client-id>
  &redirect_uri=https%3A%2F%2Fquackback.ofneill.com%2Fapi%2Fmcp%2Foauth%2Fcallback
  &response_type=code
  &state=<backend-state>
  &resource=<discovered-resource>
  &code_challenge_method=S256
  &code_challenge=<backend-pkce-challenge>
```

The line breaks above are illustrative; the actual URL is one encoded URL.

## Keep the rest of the flow unchanged

```text
App requests backend connect URL
  -> gateway /oauth/authorize?provider=google&...
  -> Google sign-in / consent
  -> gateway /api/auth/google/callback
  -> backend /api/mcp/oauth/callback?code=...&state=...
  -> backend exchanges code at gateway /oauth/token using PKCE
  -> backend persists the connection and encrypted credentials
  -> backend redirects to /mobile/mcp-complete
  -> Android opens the app when App Link verification succeeds
  -> app refreshes authenticated /api/mcp/connections
```

Do not redirect Google directly to the app, bypass the backend code exchange, or ask the mobile app to copy/store the bearer token displayed by the manual flow.

No frontend API contract change is required. The frontend already opens the returned `authorizationUrl` as supplied by the backend.

## Tests to update / add

`tests/Quacktion.Web.Tests/McpConnectionTests.cs` currently has a regression assertion in `GatewayOAuthBindsPkceStateAndOneGoogleGrantToAllServices` that expects `/api/auth/google`. Change it to expect the discovered `/oauth/authorize` path and assert `provider=google`.

Verify:

- Authorization URL uses the discovered endpoint, including when the test metadata supplies a different authorization path.
- Existing `client_id`, callback URL, state, resource, and S256 challenge remain present and correctly encoded.
- One Google connection still covers the intended configured Google services.
- The saved verifier matches the authorization challenge during token exchange.
- Invalid, expired, or replayed backend state remains rejected.
- The successful backend callback redirects only after connection persistence.

Run the backend MCP connection and callback tests, then test the complete hosted flow. Unit tests with a mocked code exchange cannot detect choosing the wrong gateway entry route unless the authorization URL is asserted correctly.

## Deployment and retest

1. Deploy the backend authorization URL fix.
2. Cancel the pending connection attempt in the app and start a fresh one. An already opened manual-token flow cannot be repaired by refreshing its final page.
3. Confirm the first gateway URL uses `/oauth/authorize`, not `/api/auth/google`.
4. Complete Google consent and verify the backend callback is reached and a connection appears in the authenticated list.
5. Verify Android return-to-app independently. A successful backend callback and Android domain verification are separate stages.

Do not include real authorization URLs, state, codes, bearer tokens, or provider tokens in logs or test reports. The screenshot contains a real bearer credential; do not reuse or copy it into the app as a workaround.

## Acceptance criteria

The app-initiated connection no longer ends on the gateway's copy-token page. It returns a code to the backend, creates the account connection, and proceeds to the existing mobile completion URL.
