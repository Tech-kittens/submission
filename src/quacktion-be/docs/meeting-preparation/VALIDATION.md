# Native preparation validation

Validation date: 2026-10-25.

- `dotnet build Quacktion.sln --no-restore -m:1 -p:UseSharedCompilation=false -v:minimal`: succeeded, zero warnings/errors.
- `QUACKTION_TEST_MONGO=mongodb://127.0.0.1:27029 dotnet test Quacktion.sln --no-restore -m:1 -p:UseSharedCompilation=false -v:minimal`: **190 passed, 0 failed, 3 skipped** (App: 33 passed; Web: 157 passed, 3 skipped).
- `git diff --check`: passed.

The local database is MongoDB 8.0.16, isolated on port 27029 with disposable test databases. An initial run exhausted MongoDB's inherited file descriptor limit; restarting with a 64,000 descriptor limit resolved that environment failure. An added preference regression initially had an invalid seeded preference ID; the fixture now uses the production owner-key convention.

Coverage includes existing calendar creation/update/cancellation/deduplication, owner filtering, rank fusion construction, memory indexing, research persistence and revision conflicts. New coverage exercises configured Argon research with mocked provider output, source validation, preservation of clarification answers, all notification kinds, PCM chunking and limits, durable enqueue and deduplication, connection replay, one-in-flight queue limits, all acknowledgement outcomes, silent delivery, preference changes, offer acceptance/decline/postponement/expiry, unrelated speech, assistant speech, disconnects, stale meeting suppression, and task/approval lifecycle notifications. Existing voice, session restoration, approval and MCP regression tests run in the same suite.

Live integration tests require credentials and are intentionally skipped when unavailable: Atlas Search/Vector Search/native fusion, authenticated real MCP tool discovery, and a real Gemini Live spoken response. Production Calendar sync, Gemini reasoning/TTS, physical duck LEDs/audio and end-to-end booking-to-briefing behaviour were not exercised. No production deployment or index provisioning was performed.

The checked-in transcription protocol previously deferred notifications and lacked sections 9–10. Its new sections document the firmware contract supplied with this task; no firmware files were changed.
