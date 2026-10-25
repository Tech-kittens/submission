# Investigate voice agent declining MCP requests

## Reported behavior

The user has connected Google and GitHub accounts, but the voice agent sometimes declines account tasks in ordinary speech instead of creating an approval request. A previous transcript said it could not access personal account credentials to schedule meetings.

## Confirmed in the inspected backend checkout

- `GeminiProtocol.Setup` supplies `GeminiTools.Declarations` to the voice model. It does not inject per-user connected-account or enabled-MCP-tool information into that setup.
- `GeminiTools` exposes a generic `execute_quacktion` function for account tasks. Its description explains approval requirements; it does not expose individual Calendar or GitHub operations directly to the voice model.
- `MeetingToolService` routes `execute_quacktion` to an `mcp.request` proposal. Initiating this proposal is distinct from granting account access or executing an external operation.
- The default system instruction in `GeminiProtocol.cs` repeatedly describes confidentiality, approvals, and avoiding invented access. It also instructs the model to use appropriate tools, but lacks an explicit private-account routing rule that distinguishes requesting approval from using credentials.
- `GeminiTaskGateway.ExecuteMcpReadAsync` exposes discovered tools to the background model only after account/service approval, filtering by enabled services and exact enabled-tool keys. It returns `mcp_no_enabled_tools` when nothing remains.
- `UserMcpConnection.EnabledTools` defaults to an empty list. Connecting an account alone does not imply all its tools are enabled.

## Hypothesis, not a confirmed runtime cause

The voice model may interpret the lack of direct account tools, combined with the caution-heavy instruction, as a reason to refuse. It should instead route a clearly requested account task through `execute_quacktion` and let backend approval controls decide what can execute.

Source inspection alone cannot establish which prompt was deployed, whether configuration overrides the default system instruction, or whether a specific refusal involved a tool call. Connected-account, enabled-tool, approval, and gateway failures can also prevent execution and must be distinguished from a model declining before invoking any tool.

## Proposed minimal backend work

1. Add explicit routing guidance to the voice system instruction and clarify the `execute_quacktion` description. Suggested intent:

   > For a clear request involving an external account, such as checking Calendar, scheduling a meeting, reading Gmail, or using GitHub, invoke execute_quacktion to request the work through the backend. Creating this request does not grant access or execute an operation. Account selection, service access, and protected operations remain subject to backend approval. Do not claim account access is unavailable without an actual backend result establishing that. Do not ask for passwords or tokens. Clarify essential missing details when necessary; do not substitute a generic refusal for the tool request.

2. If supplying account capability context, derive it from authenticated backend state. Distinguish connected providers, enabled tools, and task-specific approvals. Supply only the minimum non-secret capability information needed; do not grant new permissions through prompt text.
3. Ensure status/error responses distinguish disconnected accounts, missing account selection, disabled tools, pending approvals, and actual gateway failures. The voice model should accurately explain the required next step.
4. Retain all backend enforcement, state/digest checks, exact-operation approvals, and announcement consent. Improving tool initiation must not bypass any of them.

## Verification needed

- Trace a new explicit Calendar/GitHub request: does the voice model call `execute_quacktion`, and does an `mcp.request` reach the frontend?
- If it calls the tool, inspect sanitized task status/failure codes before changing the prompt.
- Confirm the connected account has the required service and enabled tool keys. Do not silently enable tools to make tests pass.
- Test prompting with representative user requests and assert proposal initiation rather than unsupported refusal. A declaration/unit test alone does not establish model tool-selection behavior.
- Test unconnected accounts, no enabled tools, rejected/expired approval, changed action digests, and a real gateway failure. None should be described as successful execution.
- Confirm ordinary questions do not create external actions, and missing essential meeting details are clarified.

No backend files were edited. This document records inspection findings and proposed investigation/change work only.
