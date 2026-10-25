# QuacktionAI handoff: MCP OAuth return-flow findings

## Required change status

**No QuacktionAI source change is required to fix the reported token-page problem**, based on `main` at `ff6489a` and the deployed gateway's OAuth metadata.

The backend currently chooses the wrong gateway entry point. The backend handoff is `BACKEND_MCP_AUTHORIZATION_ROUTE_FIX.md` alongside this document.

## Gateway behavior verified in the code

| Entry point | Intended use | Result after provider sign-in |
| --- | --- | --- |
| `/api/auth/google` | Direct/manual bearer-token setup | Displays the “Connected as …” token page |
| `/oauth/authorize` | Registered OAuth client, such as QuacktionBE | Sends a short-lived code and client state to the registered callback |

Relevant files, relative to the QuacktionAI repository root:

- `mcps/app/api/auth/google/route.ts`: calls `beginSignIn(req, "google", services)` without an MCP client context. It does not interpret client callback / PKCE parameters.
- `mcps/app/oauth/authorize/route.ts`: validates the registered client, callback URL, response type, and S256 challenge; accepts `provider=google`; passes the client context into `beginSignIn`.
- `mcps/lib/signin.ts`: stores that context in the sealed sign-in cookie. On callback, `saved.mcp` determines whether to redirect with a sealed authorization code or show the manual token page.
- `mcps/app/oauth/token/route.ts`: checks the code and PKCE verifier, then returns the gateway bearer to the backend.
- `mcps/lib/oauth.ts`: permits the Quacktion backend callback host `quackback.ofneill.com` during client registration.
- `mcps/app/.well-known/oauth-authorization-server/route.ts`: advertises `/oauth/authorize` as the authorization endpoint, matching the deployed public metadata.

The screenshot's token page is therefore consistent with the backend starting `/api/auth/google`. It does not demonstrate a failure of the gateway's OAuth-client callback branch.

## Contract to preserve

QuacktionBE should open the discovered `/oauth/authorize` endpoint with:

```text
provider=google
client_id=<registered-client-id>
redirect_uri=https://quackback.ofneill.com/api/mcp/oauth/callback
response_type=code
state=<backend-state>
resource=<discovered-resource>
code_challenge_method=S256
code_challenge=<backend-challenge>
```

Google should still return to the gateway's own `/api/auth/google/callback`. The gateway then returns an authorization code to QuacktionBE, which exchanges it server-side and later redirects to the mobile completion link.

Do not add an unconditional redirect to the manual token page, blindly honour arbitrary callback query parameters on `/api/auth/google`, or send the final gateway bearer to the mobile app. Keep registered-client redirect validation and PKCE in `/oauth/authorize`.

## Verification after the backend fix

1. Start a fresh authorization attempt from the mobile app through the corrected backend URL.
2. Confirm `/oauth/authorize` selects Google and preserves the client context through sign-in.
3. Confirm the gateway callback takes the `saved.mcp` branch and returns a code plus the original client state to the registered QuacktionBE callback.
4. Confirm backend token exchange succeeds and the backend creates the connection.
5. Confirm the direct/manual flow still displays its token page when deliberately started from the gateway homepage.

Do not collect actual codes, state, cookies, provider credentials, or bearer tokens in debug screenshots or reports.

## Optional follow-up, not required for this fix

- Clarify the README and code comments that `/oauth/authorize` supports any permitted registered client, including QuacktionBE; some comments currently refer only to Claude clients.
- If adding gateway regression tests, cover the distinction between a manual sign-in and a sign-in carrying MCP client context, plus rejection of unregistered redirects and invalid PKCE.

No changes to tool implementations, provider scopes, or firmware are needed for this routing issue.
