# Backend handoff: approvals only for destructive MCP actions

> Implementation status: the backend policy described here has since been implemented locally. See `docs/MCP-SKILLS.md` for current behaviour. Deployment and a live voice/Google check remain outstanding.

## Requested policy

The user wants routine connected-account work to proceed without a separate review prompt. Treat this as a change to approval policy, **not** as permission to bypass authentication, account ownership, OAuth scope, enabled-service/tool checks, or exact tool-schema validation. This document proposes backend changes only; no backend source or tests were edited, following the handoff-only instruction.

| Operation | User review under proposed policy |
|---|---|
| Read a connected Calendar, Gmail, or Meet resource | No per-request approval |
| Create or update a calendar event, draft an email, or change a non-destructive setting | No per-operation approval |
| Send, reply to, or forward an email | No separate approval when recipients and content are unambiguous; this deliberately changes the previous send-approval policy |
| Delete or trash data, remove an attendee/participant, cancel or end a meeting, restrict access, or perform an equivalent destructive action | Explicit approval bound to the exact operation and account |
| Tool or skill whose effect cannot be reliably classified | Treat as protected until classified; never infer safety from missing MCP metadata |

This proposal retains the meeting recording/transcription consent flow. It also retains private-result handling: return private account data to the authenticated owner in the app; do not announce it to a room merely because a tool call succeeded. A direct owner request to read a specific result aloud should be handled under the product's disclosure policy without a redundant approval card. Speaker recognition alone is not authentication.

## Why the current backend prompts twice

- `MeetingToolService` creates a `mcp.request` from Live's `execute_quacktion`. `ActionRiskEvaluator` does not mark it safe, so `QuacktionService.ProposeAsync` puts it in `PendingApproval`.
- `McpConnectionService.BindTaskAsync`, `GeminiTaskGateway`, and `McpConnectionService.AuthorizeParent` require the task to be bound to a selected connection/service and to have `ValidApproval`.
- `McpClientGateway.DiscoverAsync` marks a tool read-only only when both the operator allowlist and provider `readOnlyHint` agree. Missing annotations cause even ordinary reads to produce an `mcp.invoke` child approval. `McpConnectionService.ExecuteOrProposeAsync`, `McpClientGateway.ExecuteAsync`, the skill registry/executor, and the worker all currently rely on that classification.
- Protected operations use a durable child task, exact action digest, and audit marker. These controls must remain for destructive calls.

## Proposed implementation

1. Add a server-side account-use preference or grant selected through an authenticated frontend action. It names the owner's default connected account and enabled services. A connected grant alone must not let the model choose between multiple accounts. If no eligible account is selected, ask the owner to choose one once; changing or disconnecting it invalidates the preference. This is account selection, not an approval for each read/write.
2. Have `execute_quacktion` create a bound, queued `mcp.request` when that preference identifies exactly one eligible, connected account. Preserve the user's complete request and service scope. With no valid selection, create a task that waits for authenticated account selection; after selection, queue it without a second general approval. Keep task ownership, session status, cancellation, rate limits, and audit checks.
3. Replace `ValidApproval(parent)` as the sole MCP parent authorization test with an explicit policy predicate: authenticated owner + active session + bound eligible connection/services + unmodified request + either a valid authenticated account-use grant or the existing explicit approval. Use that same predicate in the worker, `GeminiTaskGateway`, and `McpConnectionService`. Do not manufacture an `Approved` record for an auto-authorized task.
4. Classify every skill/tool from a reviewed application policy table. For known non-destructive skills, allow execution through the existing MCP gateway without an approval child, even when the provider omits `readOnlyHint`. For destructive skills, always create the existing `mcp.invoke` approval child, including its connection, target, arguments, and recurrence scope in the digest. Unknown or dynamically discovered tools remain protected until reviewed. Provider hints can add restrictions but cannot downgrade an application-classified destructive operation.
5. Update `McpSkillRegistry.RequiresApproval`, `McpSkillExecutor`, `McpConnectionService.ExecuteOrProposeAsync`, and `McpClientGateway.ExecuteAsync` together. The low-level `approved` boolean should become a policy-authorization decision tied to the selected skill/tool and owner; do not let an agent set it. Preserve re-discovery, schema-digest checks, tool allowlisting, and OAuth scope checks immediately before execution.
6. Preserve durable audit/idempotency fencing for every external write that proceeds without review. Never automatically retry a write whose outcome is uncertain after a timeout or disconnect. Surface `outcome_unknown` for reconciliation. Composite workflows should continue to pause only at destructive steps and resume from the next step after exact approval.
7. For email sending, require a resolved sending account, exact recipients, subject, body, and attachment references before calling the gateway. If any are ambiguous, ask a clarifying question or create a draft. The user has requested no extra approval for a complete, non-destructive send; record the exact submitted content digest and result in the audit without exposing private content in logs.
8. Update the frontend contract and voice messaging: routine work should acknowledge that it is underway, not ask the user to review an approval. Show an approval card only for destructive actions or unresolved account choice. Retain a way to cancel a queued task and inspect its status.

## Tests and rollout checks

- A connected, selected owner's Calendar read queues and executes without an approval task even if the MCP tool has no `readOnlyHint`.
- Calendar create/update and a fully specified Gmail send execute without approval, with owner, scope, schema, enabled-tool, and audit checks intact. Ambiguous recipients or dates do not trigger a write.
- Calendar deletion, Gmail trash/deletion, Meet end/restrict operations, and a composite workflow's destructive step still create exact, owner-scoped approval tasks. A changed digest, rejection, expired approval, or another owner's approval cannot execute them.
- An unclassified MCP tool cannot bypass approval by advertising `readOnlyHint`; a known destructive tool cannot bypass approval through a misleading hint.
- Multiple connections require an authenticated account selection. Disconnection, disabled service/tool, insufficient scope, inactive owner, expired session, and revoked credentials block execution.
- Duplicate requests and uncertain write outcomes do not repeat external writes. Live remains responsive while the background worker runs.
- Verify the complete voice path after deployment with a fresh Calendar request. Correlate the Live function call, bound task, skill/tool execution audit, and response. The earlier spoken refusal remains undiagnosed until the affected session evidence in `HANDOFF-VOICE-CALENDAR-ROUTING.md` is checked.

## Existing policy deliberately changed

Earlier Quacktion requirements called for approval before external email sending and some non-destructive writes. This handoff follows the user's newer instruction to reserve review prompts for destructive actions. The policy change should be reflected in API documentation, frontend copy, and tests when implemented. No deployed behaviour changes from this Markdown handoff alone.
