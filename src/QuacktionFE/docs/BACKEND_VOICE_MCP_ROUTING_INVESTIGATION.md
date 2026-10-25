# Backend handoff: connected calendar refused by voice

## Status

Backend reviewed at `b621f98`. No backend implementation changes are included. The investigator's temporary backend source, test, and documentation edits were removed at the user's request. Backend changes must be proposed through Markdown handoffs only.

The user reports that approval previously worked and asks whether moving approval reviews inline into chat introduced the failure. The screenshot shows a connected Google grant with eight enabled tools, followed by a voice response denying calendar access. Neither the affected session's database records nor deployed logs were available to establish the failure stage.

## Findings

- Gemini Live receives the generic `execute_quacktion` declaration, rather than Calendar/Gmail tool schemas.
- `MeetingToolService` turns this function call into an `mcp.request` task requiring authenticated account/service selection and approval.
- The background `GeminiTaskGateway` discovers the selected account's enabled MCP tools after approval.
- The existing Live persona does not explicitly map Calendar/Gmail requests to `execute_quacktion`. Its function description refers to private accounts and internal agent names. This is a possible routing weakness, not a confirmed cause or a demonstrated new regression.
- Frontend inline approval tests pass for account binding, updated digests, and operation approval. The inline screen did omit request-loading error feedback that the separate Requests page provides. A frontend-only change restores that feedback and a retry button. This omission could hide requests; it does not explain the model's spoken denial by itself.

## Evidence needed before attributing the regression

For the affected owner's session around the screenshot timestamp:

1. Check `meeting_tool_calls` and the `Gemini tool received` log for a received function call.
2. Check `quacktions` for a session-scoped `mcp.request`, status, approval state, and failure code.
3. If PendingApproval, verify the authenticated task-list response includes it, its session ID matches the displayed conversation, and polling/SignalR updates reach the inline card.
4. If approved, check the selected connection/services, enabled tool keys, credential metadata, and `mcp_execution_audit` for discovery/execution failure. Do not dump credentials, tokens, private results, or unrelated account data.

A missing request places the failure before approval rendering. A persisted pending request missing from chat points to loading, session matching, or rendering. A failed approved request points to background discovery/execution.

## Proposed backend change, if routing is confirmed

In `GeminiTools.cs`, explicitly describe `execute_quacktion` as the route for scheduling/updating Calendar events, checking availability, Gmail, and Meet. Explain that absent service-specific declarations do not prevent delegation. Never claim a connection or successful operation without backend evidence.

In `GeminiProtocol.Setup`, include a separate connected-account routing instruction alongside the configurable persona so a `Gemini__SystemInstruction` override cannot omit the routing contract. Retain all account-selection, authenticated approval, write approval, and private-result disclosure controls. Preserve requested date/time information and clarify essential ambiguities.

Suggested checks:

- Scripted Live setup includes the routing contract with both default and custom personas and still declares `execute_quacktion`.
- A supplied calendar function call produces a PendingApproval `mcp.request`, preserving the request text; no private access occurs before approval.
- Private results remain absent from unapproved voice responses.
- Verify actual model tool choice in a new deployed voice conversation; scripted tests cannot prove model behaviour or live Google execution.

The temporary proposed backend implementation passed 130 tests with five optional integration tests skipped before removal. That result does not validate the deployed incident or apply to a retained backend patch. Frontend approval/error-recovery verification passed 22 tests.
