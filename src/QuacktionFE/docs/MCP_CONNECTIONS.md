# Mobile MCP connections

The frontend uses the account-authenticated API described in
`QuacktionBE/docs/MOBILE-MCP-INTEGRATION.md`. Backend `main` was pulled to
`ea4f551` when this implementation was added. No backend or firmware source changes
are part of this frontend integration.

## Account connection

Connections loads providers from `/api/mcp/providers` and accounts from
`/api/mcp/connections`. Providers absent from this response are not advertised as
working connections. Enabled service chips come from each account's response.
Unverified connection identities are labelled; provider grant IDs are never
presented as email addresses.

Connect starts consent through `/api/mcp/connections/{provider}/connect` and opens
the returned HTTPS authorization URL in an Android Custom Tab (with normal browser
fallback). The app never
calls the OAuth callback, stores provider tokens, or connects to the MCP gateway.
The existing Quacktion bearer client handles token refresh and secure storage.
The backend callback currently stays in the browser. Return to Quacktion once it
finishes; Connections refreshes on resume. A connection wait is bounded to three
minutes; an active foreground wait checks every three seconds and can be cancelled
or explicitly retried. Cancelling does not revoke a consent already completed on
the provider; refresh Connections to see any newly linked account.

The frontend also handles the future completion App Link at
`https://quackback.ofneill.com/mobile/mcp-complete`. Initial and subsequent links
are validated natively, queued until sign-in, and used only to open Connections
and fetch the authenticated account list. They never grant access or carry
provider tokens. A completion can confirm a reauthorized account with the same ID.
Connections opens over the current screen so link navigation does not dispose a
live voice controller. Duplicate pending links and concurrent refreshes are
coalesced. The backend still needs to redirect to this URL and deploy its domain
association file before automatic browser-to-app return works. See
`MCP_OAUTH_RETURN_TO_APP.md` for the deployment contract and verification commands.

Manage tools independently loads discovery and health, displays the returned
schemas as plain text, and saves only exact discovered `serverId/name` keys. A
selection is not approval to perform an action. Disconnect removes the account
from the UI after the API acknowledges revocation and refreshes server state.

Successful authorization queues the connected account's Allowed tools sheet and
opens it automatically over the current screen after returning to the app. The
authenticated connection refresh confirms success before the sheet is queued;
duplicate completion links do not reopen a consumed review. A completion after
app recreation can automatically select an account only when the connected
account is unambiguous. Multiple existing accounts remain available in Connections
for explicit selection when the completion link carries no account identity.

Service groups start collapsed and show their selection count. A checkbox on the
right selects or clears that group's tools without expanding it. Partial selection
shows a mixed checkbox; tapping it selects the whole group. Expansion arrows are
on the left for both groups and tools, and tool checkboxes are on the right.
Group selection affects only that service; global controls still apply to all
tools. Collapsing a group preserves unsaved selections.

Collapsed tool rows show only a readable tool name, checkbox, and expansion arrow.
Expand a row for its service, description, approval information, and original tool
name. JSON schemas and separate Tool details sections are not displayed. Select all and Deselect all edit the
local selection, with a selected count. Only Save tool permissions persists it;
closing the sheet does not save or approve an operation.

## Requests and explicit approval

Requests appear as inline review cards in their matching conversation, ordered
alongside transcript messages. Account selection, service selection, and the
separate approval action happen directly in the card. Resolved requests become
compact status cards. The account-wide Requests list remains accessible from the
history sidebar; the separate chat request button has been removed. Reviews keep
the chat and phone audio controls mounted. No private
results are included in app notifications or announced by frontend code.

For an `mcp.request`, the owner selects a connected account and a nonempty subset
of that account's enabled services. Review access binds those choices through the
select-account endpoint. Its response replaces the reviewed task and digest.
Approve access is a separate, explicit user action. Protected `mcp.invoke` child
operations have their own review of the exact arguments and their own approval.
Closing a review approves nothing. Completed-task results are not broadcast or
rendered in notifications.

Every approve/reject fetches the current task first. If the digest or status has
changed, or approval has expired, no decision is sent; the refreshed request must
be reviewed again. A 409 refreshes state without automatically resubmitting.
Duplicate submissions for the same task are blocked while a decision is pending.

SignalR handles ApprovalRequested, ApprovalResolved, ApprovalExpired and
QuacktionStatusChanged. After reconnect it rejoins the current session and fetches
fresh tasks. Foreground task polling every six seconds recovers events for other
sessions or while the hub is unavailable. App resume also refreshes state.
Connection failures do not disable voice sessions.

## Verification

Run `flutter test --no-pub` and `dart analyze lib test`. Integration tests cover
plain-array responses, browser-only consent, timeout/cancellation, exact discovered
tool keys, disconnect, explicit service selection, changed digests, expiration,
duplicate submissions and a protected operation dismissed without approval.

Manual verification requires a hosted backend with MCP enabled and an account
that can complete gateway consent. In Connections, connect an account, finish
consent in the browser, return, discover and enable actual tools, then ask the
assistant to use a service. Review the initial account-access request and any
separate protected child operation. Do not share OAuth URLs or codes in logs.
