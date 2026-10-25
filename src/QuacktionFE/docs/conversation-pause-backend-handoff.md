# Conversation pause/resume integration

Backend main was pulled to `9d736e8` on 25 October 2026. The fetched
`origin/feature/saint` contains `f034be7` (fix: resume convos), which adds the
required persistent session API. Main does not yet contain that commit.
No backend source was edited or deployed.

## Confirmed contract on feature/saint

- `POST /api/sessions/{id}/resume` returns the same Session with status
  `Suspended`; it does not return a WebSocket URL.
- Reconnect with the account bearer token to `/ws/sessions/{id}/audio`, then
  negotiate `audio.start` after `session.started` and wait for `audio.ready`.
- Closing the WebSocket without `session.end` suspends the conversation.
- Suspended and explicitly selected completed conversations retain their ID,
  transcript, actions and approval references.
- A live provider lease rejects resume with `409 conversation_in_use`. The user
  can retry after cleanup; the frontend never falls back to creating a new chat.

## Frontend behavior

Pause disconnects the conversation socket without ending the session, clears
assistant playback and retains microphone capture for meeting upload. Starting a
meeting pauses before the start request. Finalizing the meeting resumes the same
conversation unless the user had manually paused it. Failed finalization keeps
it paused. Saved conversations expose Resume and use the same API and socket path.

## Deployment prerequisite and verification

Deploy the persistent resumption implementation from `f034be7` (or its successor)
before using this frontend flow. Main's earlier socket handler ends a disconnected
session and lacks `/resume`, so it cannot provide persistent resumption. The UI
reports an unavailable resume API instead of silently starting a different chat.

The frontend uses local WebSocket regression tests for pause/disconnect, meeting
capture continuity, resume negotiation and saved-session identity preservation.
Verify against the deployed provider: immediate resume while a lease is releasing,
long meetings, network loss, provider context restoration, and end while paused.
Provider context restoration is backend-owned; a live provider was not exercised
by the frontend tests.
