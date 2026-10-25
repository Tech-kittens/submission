# Backend handoff: connected Calendar refused by voice

> Implementation status: explicit connected-account routing was subsequently added to the Live tool declaration and setup instruction, along with automatic binding for a single eligible account. The original incident's deployed-session evidence remains unavailable, so its cause is still unconfirmed.

## Status and scope

The incident report was prepared against backend `b621f98`. This handoff was checked against the clean local checkout at `6b9d3ed` on 25 October 2026. No backend source, test, or configuration change is made here. The affected session's database records and deployed logs were unavailable, so the cause of the spoken refusal is unconfirmed.

The reported screen shows a connected Google grant with eight enabled tools. That establishes neither that Gemini Live called a backend function nor that the owner selected and approved the account and service for this particular spoken request. Moving approval reviews into chat could hide a pending request if loading fails, but it does not by itself explain a refusal before a request is created. Do not classify the inline approval change as the cause without session evidence.

## Current path

1. `GeminiTools.Declarations` gives Live `execute_quacktion`, with a general private-account description. Live does not receive Calendar or Gmail MCP tool schemas.
2. `GeminiLiveService.DispatchLoopAsync` receives a model function call and records it through `MeetingToolService`.
3. `MeetingToolService.ResolveAsync` turns `execute_quacktion` into a session-scoped `mcp.request` in `PendingApproval`, preserving the request in `query`.
4. The authenticated owner selects a connected account and allowed service, then approves the updated task digest.
5. `GeminiTaskGateway` discovers that account's enabled tools and runs the approved background task. Protected operations may create a further `mcp.invoke` approval. Private results remain behind announcement controls.

The default Live persona says to use an appropriate tool for tasks, but does not explicitly say that Calendar, Gmail, and Meet requests should use `execute_quacktion`. `GeminiProtocol.Setup` currently sends only the configurable `GeminiSettings.SystemInstruction` as its system instruction, so an override can omit any persona-level routing guidance. These are plausible routing weaknesses, not a proven regression.

## Evidence needed for the affected session

Use the owner and session associated with the screenshot timestamp. Record only IDs, timestamps, statuses, safe error codes, and service/tool identifiers. Do not export credentials, tokens, private MCP results, email/calendar content, or other owners' records.

1. Check `meeting_tool_calls` for a received function call and correlate its `CallId` with the `Gemini tool received` log event. The existing log event does not include the tool name; the persisted call does.
2. Check session-scoped `quacktions` for an `mcp.request`, its status, approval state, action digest version, and failure code. If absent, investigate Live tool choice/dispatch before the inline approval UI.
3. If `PendingApproval`, verify the authenticated `GET /api/sessions/{sessionId}/quacktions` response contains the task, the inline card uses that same session ID, and polling or SignalR refresh reaches it. Check frontend request-loading errors separately.
4. If approved, inspect the selected connection ID, allowed services, enabled tool keys, and non-secret credential metadata. Check `mcp_execution_audit` for discovery or execution failure; correlate any child `mcp.invoke` task and its approval. Use health/tool discovery endpoints as the affected owner if needed.
5. Match the spoken response to the stage established above. A missing function call is a model-routing issue; a pending task missing from chat is a loading/session/rendering issue; an approved failed task points to discovery, permissions, credentials, or execution.

## Proposed backend change, conditional on routing evidence

- In `src/Quacktion.Data/Agents/GeminiTools.cs`, describe `execute_quacktion` explicitly as the route for checking Calendar availability, creating or changing events, Gmail requests, and Meet requests. Say that Live delegates through this function even though service-specific tool declarations are absent. Preserve the rule that a connection and an operation are not confirmed until the backend reports them.
- In `src/Quacktion.Data/Agents/GeminiProtocol.cs`, include a separate, non-configurable connected-account routing instruction in `GeminiProtocol.Setup` alongside the configurable persona. This keeps routing guidance present when `Gemini__SystemInstruction` overrides the default. Preserve the user's requested date, time, time zone, participants, and intent in the function request; clarify essential ambiguities before a write.
- Keep authenticated account selection, updated-digest approval, separate protected-operation approval, and private-result disclosure checks unchanged. Do not make voice recognition or the presence of a connected grant authorize access.

## Verification before rollout

- Scripted Live setup tests should confirm the routing instruction and `execute_quacktion` declaration with both default and custom personas.
- A supplied Calendar function call should create a `PendingApproval` `mcp.request` with the request text intact. It must cause no private MCP access before approval.
- Private results must remain absent from unapproved voice responses. Existing account binding, digest validation, protected-operation approval, and disclosure tests must continue to pass.
- After deployment, make a fresh voice request with the affected connected account. Verify model tool choice, task creation, inline approval, selected account/services, background discovery/execution, and final response. Scripted tests cannot prove Live model choice or a live Google operation.

The earlier temporary backend proposal reportedly passed 130 tests with five optional integrations skipped before it was removed. Those tests do not validate this checkout or the deployed incident. The reported frontend approval/error-recovery verification passed 22 tests; that likewise does not establish the backend failure stage.
