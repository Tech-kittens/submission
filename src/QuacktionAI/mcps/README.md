# Google + GitHub MCP

One Next.js app exposing five MCP servers: `/api/google/gmail/mcp`, `/api/google/calendar/mcp`, `/api/google/meet/mcp`, `/api/github/mcp`, `/api/jira/mcp`.
Stateless (no database). Works as Claude.ai custom connectors (OAuth) and with Claude Code (bearer token from the home page).
You can configure just one provider: the other provider's env vars are only read when someone signs in with it.

## 1. Google (Gmail, Calendar, Meet)
1. Google Cloud: enable the Gmail API, Google Calendar API and Google Meet REST API.
2. OAuth consent screen: add the scopes in `lib/scopes.ts`; add yourself as a test user. (In "Testing" mode Google
   expires refresh tokens after 7 days; publish the app to avoid weekly reconnects.)
3. Credentials -> OAuth client ID -> Web application. Redirect URI: `https://<your-domain>/api/auth/google/callback`

## 2. GitHub
GitHub -> Settings -> Developer settings -> OAuth Apps -> New.
- Homepage URL: `https://<your-domain>`
- Callback URL: `https://<your-domain>/api/auth/github/callback`
- Generate a client secret.
OAuth Apps allow one callback URL, so use a separate OAuth App for local dev. Org-owned repos may need org approval of the app.

## 3. Jira (Atlassian Cloud)
developer.atlassian.com/console/myapps -> Create -> OAuth 2.0 integration.
- Permissions -> Jira API -> Configure: add `read:jira-work`, `write:jira-work`, `read:jira-user`.
- Authorization -> OAuth 2.0 (3LO) -> Callback URL: `https://<your-domain>/api/auth/jira/callback`
- Settings: copy the Client ID / Secret into `ATLASSIAN_CLIENT_ID` / `ATLASSIAN_CLIENT_SECRET`.
Notes: Atlassian rotates refresh tokens, so Jira uses short-lived bearers (~1h) and the MCP client refreshes them via
`/oauth/token` (Claude.ai and Claude Code do this automatically). That means there is no copy-and-paste bearer token for
Jira. If your account has several Jira sites, the first one Atlassian returns is used.

## 4. Deploy (Vercel)
Set the env vars from `.env.example` and deploy.

## 5. Connect
- **Claude.ai**: Settings -> Connectors -> Add custom connector -> one of the MCP URLs above, then sign in.
  Each service is its own connector; Google services share one Google sign-in per connector.
- **Claude Code**: open the home page, connect a provider, run the `claude mcp add` commands shown.

## Notes
- Issued tokens (`mcp_...`) are sealed with AES-256-GCM using `TOKEN_ENCRYPTION_KEY` and embed your Google refresh token
  or GitHub access token. Treat them like passwords. Revoke at myaccount.google.com/permissions or
  github.com/settings/applications. Old `gmcp_` tokens from the Google-only app still work.
- Write tools (send email, create events, commit files, merge PRs...) act immediately as you.
