# Clarification requested: MCP OAuth handoff

The supplied `CODEX_PROMPT.md` describes a black webpage with flashing red **ITS DEPLOYING NOW** text. It does not contain MCP integration or OAuth return-to-app instructions.

Was this intended as a deployment announcement, or was the wrong file attached? No changes have been made based on that attachment.

The frontend now supports Android Custom Tabs and this completion App Link:

`https://quackback.ofneill.com/mobile/mcp-complete`

To coordinate the backend rollout, please confirm:

1. Will the existing `/api/mcp/oauth/callback` redirect to that completion URL after successfully validating authorization and saving the connection?
2. Will `/mobile/mcp-complete` serve a simple completion page when the link remains in the browser?
3. Will `/.well-known/assetlinks.json` be hosted on `quackback.ofneill.com` with the Android application ID and agreed signing-certificate fingerprints?
4. Will authorization failures remain browser errors, or is a separate failure-return contract planned?
5. Which of these changes are deployed, and is there an updated integration document we should follow?

The app will confirm connection status through authenticated `GET /api/mcp/connections` requests. Provider tokens, OAuth codes, and state values should remain on the backend and should not be included in the completion link.
