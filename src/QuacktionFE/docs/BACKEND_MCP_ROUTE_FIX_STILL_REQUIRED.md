# MCP connection still ends on the gateway token page

## Latest verification

Checked on 25 October 2026 after another connection attempt:

- Backend remote `main` is still at `d6dd9a5`.
- Its `BeginConnectAsync` implementation still generates `/api/auth/google`.
- The browser activity on the connected phone confirms this attempt started at `https://mcp.adriantd.com/api/auth/google` with the backend OAuth parameter names present.
- The browser consequently ends on the gateway's “Connected as …” bearer-token page.

No OAuth parameter values or tokens are included in this report.

## Required backend fix

File: `src/Quacktion.Data/Agents/McpConnectionService.cs`

Method: `BeginConnectAsync`

The current code overrides the discovered authorization endpoint:

```csharp
var authorizationEndpoint = new Uri(registry.BaseUri,
    "api/auth/" + Uri.EscapeDataString(provider.ToLowerInvariant()));
```

Replace that choice with the discovered endpoint and include the provider in the query:

```csharp
query["provider"] = provider.ToLowerInvariant();
var authorizationEndpoint = metadata.AuthorizationEndpoint;
```

Append the existing encoded OAuth query to this endpoint using a URI-safe query builder. Preserve any query parameters already present on the discovered endpoint.

The resulting authorization URL must start with:

```text
https://mcp.adriantd.com/oauth/authorize
```

It must include `provider=google` alongside the existing `client_id`, `redirect_uri`, `response_type`, `state`, `resource`, `code_challenge_method=S256`, and `code_challenge` parameters. Preserve existing scope handling.

## Why this matters

In QuacktionAI:

- `/api/auth/google` starts manual bearer-token setup. It does not preserve the backend's OAuth client context, even when those parameters appear in its URL.
- `/oauth/authorize` validates the client and callback, preserves state and PKCE through provider sign-in, and returns an authorization code to the backend.

The manual route therefore never reaches the backend callback that saves the connection and redirects to the app.

## Android return status

The hosted certificate association now matches the installed build. Android reports the domain as verified, and opening the completion URL through a normal Android URL intent successfully launches Quacktion.

This does not fix the earlier gateway routing problem: the current authorization attempt never reaches that completion URL.

## Tests and deployment

1. Update `GatewayOAuthBindsPkceStateAndOneGoogleGrantToAllServices` in `tests/Quacktion.Web.Tests/McpConnectionTests.cs`. It currently asserts the incorrect `/api/auth/google` path. Assert the discovered authorization endpoint and `provider=google` instead.
2. Keep the existing state, PKCE, callback validation, and connection persistence tests passing.
3. Deploy the backend fix.
4. Cancel the old pending connection and start a fresh authorization attempt from the app. Refreshing the existing token page will not repair it.
5. Confirm the new attempt starts at `/oauth/authorize`, then follows:

```text
Google consent
  -> gateway /api/auth/google/callback
  -> backend /api/mcp/oauth/callback
  -> backend exchanges code and saves connection
  -> /mobile/mcp-complete
  -> Quacktion app refreshes /api/mcp/connections
```

No frontend or QuacktionAI source change is required for this specific routing fix. Do not copy the displayed bearer token into the app as a workaround.

Please confirm when the backend fix is deployed so the complete flow can be retested.
