# Changelog

This changelog covers the complete Git history from the repository's initial commit (`0d13561`) through `c19bf08` (`feat: saint`), on branch `feature/saint`. Entries are ordered by commit date. Commit subjects have been normalised for clarity; merge commits are listed separately because their functional changes are described under the commits they merged.

## 2026-10-25

- **`2ef5fbd` — Argon reasoning and Jev routing:** Added the advanced reasoning path and Jev agent selection, and hardened MCP authentication, account connections, tool execution, durable task handling, and approval enforcement. Updated integration docs and tests.
- **`ea4f551` — MCP documentation and configuration:** Expanded the mobile MCP integration guide and adjusted service configuration.
- **`30cfe0d` — Meeting transcription and device protocol:** Added meeting lifecycle and transcript domain models, Gemini transcription and meeting intelligence, meeting APIs, recording/audio persistence, device meeting streaming, meeting analysis and retention workers, and protocol documentation and tests.
- **`5bb1782` — MCP test page:** Added a browser test page for MCP and adjusted Gemini Live transport handling.
- **`40eec10` — MCP callback authentication:** Added mobile OAuth callback handling, authentication routes/configuration, and callback tests.
- **`6716584` — MCP OAuth configuration fixes:** Corrected authorization configuration and connection handling; updated integration documentation and tests.
- **`b8a2057` — Voice protocol hardening:** Tightened Gemini Live protocol validation and documented the supported behavior.
- **`0faef3a` — Voice latency and routing:** Reduced latency across Gemini Live, audio/WebSocket handling, agent routing, and MCP transport. Added latency documentation and tests.
- **`d6dd9a5` — Android App Links test certificate:** Added the local test certificate configuration and callback coverage.
- **`eefb030` — Meeting Mode and speech recognition:** Added meeting recording, transcription, analysis, scoring, meeting APIs and audio storage, plus mobile-client integration documentation and tests.
- **`82ce873` — OAuth authorization routing:** Fixed MCP OAuth authorization routing and expanded its tests.
- **`6b9d3ed` — MCP skills:** Added a skill registry and executor, durable skill executions, skill APIs and documentation, and approval-aware orchestration and tests.
- **`3107bed` — MCP approval flow:** Refined approval requirements and MCP execution rules; expanded voice/client integration handoff documentation and tests.
- **`1ee1b56` — Standalone meeting clients:** Added mobile audio upload and device authentication support for clients without dedicated firmware devices, with integration tests.
- **`c19bf08` — Saint and meeting preparation:** Added calendar sync through the existing MCP gateway; meeting preparation storage and APIs; signed, scoped Saint callbacks; durable delivery and briefing scheduling; Gemini embeddings and Atlas Search/Vector Search hybrid retrieval; memory ingestion, backfill and relationship summaries; Argon preparation retrieval; Atlas index definitions, setup guidance and tests. Saint's provider-specific trigger and WhatsApp messaging capabilities were not verified; the feature is configurable and disabled by default pending deployment and external setup.

## 2026-10-24

- **`e94f5a7` — Session start contract:** Removed the device ID from the session-start request and aligned the service, API and protocol documentation.
- **`59e2960` — Docker support:** Added the container build and Docker ignore configuration.
- **`4fe7267` — Swagger setup:** Adjusted API documentation configuration.
- **`4b9711d` — API formatting and response fixes:** Improved account/session response formatting and WebSocket handling.
- **`d62d507` — Session bearer authentication:** Switched session WebSocket authentication to bearer tokens and updated client examples/tests.
- **`b350b6d` — CORS:** Added and documented cross-origin request configuration.
- **`8fc6cf8` — Session authentication redesign:** Reworked device/session authentication across the API, audio stream coordination, WebSockets, simulator, docs and tests.
- **`c93db10` — Device session authorization:** Tightened ownership checks and authorization for device-owned sessions and WebSocket connections.
- **`5ff2d52` — Session lifecycle fixes:** Corrected session state handling and audio flow; added an in-browser session test page.
- **`0e60433` — Session reliability:** Fixed session startup/error handling and background-worker behavior; expanded service and API tests.
- **`8550c34` — Session server-error fixes:** Addressed session failures that produced HTTP 500 responses and added Mongo/WebSocket regression coverage.
- **`bd5c817` — Gemini Live voice:** Replaced the initial conversational voice integration with Gemini Live; added persistent realtime state, audio resampling, meeting tools, background reasoning, integration tests and Gemini documentation.
- **`d1fbcdd` — Session startup reliability:** Fixed an issue in session startup and added a regression test.
- **`e5d530e` — Gemini Live protocol and system instructions:** Refined the Gemini Live system prompt and protocol handling.
- **`6ae2837` — Voice selection:** Set the custom voice configuration.
- **`da0897b` — MCP integrations:** Added Google/GitHub MCP server connections, OAuth and credential persistence, tool discovery/execution, Jev routing, agent orchestration, MCP APIs and documentation, and approval/security tests.

## 2026-10-23

- **`0d13561` — Initial repository:** Created the ASP.NET Core solution with App, Data and Web projects; account/device authentication and pairing; MongoDB repositories; sessions and realtime audio/WebSockets; agent and durable task orchestration; health and API controllers; initial device protocol, Docker Compose MongoDB setup and application/API/audio tests.
- **`a2e31a7` — Postman collection:** Added an importable Postman collection for the API.
- **`ec67c24` — Account identity fields:** Standardised account identity around email and name, updated validation/storage and API configuration, and adjusted documentation and tests.
- **`4ef031d` — Formatting:** Applied formatting fixes to domain models.
- **`4a63898` — ElevenLabs conversation integration:** Added the initial ElevenLabs conversational audio provider, event handling and session integration, with updated protocol support and tests. This provider was later replaced by Gemini Live (`bd5c817`).
- **`62cebc4` — Session audio WebSocket:** Added the session-level audio streaming protocol, service/repository support, audio simulator, and protocol tests/documentation.
- **`59f040b` — Session documentation:** Added frontend and backend session integration guides.

## Merge commits

These merge commits are included in the repository history; their feature or fix details appear above under the associated changes.

- **`9544928`** — Merged the Postman collection work (PR #1).
- **`867f7d2`** — Merged account/authentication work (PR #2).
- **`be0dcb7`** — Merged ElevenLabs integration work (PR #3).
- **`7c07834`** — Merged session authentication work (PR #4).
- **`e8ddd7c`** — Merged session lifecycle fixes (PR #5).
- **`0bcab70`** — Merged additional session fixes (PR #6).
- **`04f2506`** — Merged session server-error fixes (PR #7).
- **`0cfdbd0`** — Merged Gemini Live voice work (PR #8).
- **`d990b24`** — Merged agent refinement work (PR #9).
- **`e4f13ed`** — Merged meeting/device protocol work (PR #10).
- **`b621f98`** — Merged Meeting Mode and voice recognition work (PR #11).
