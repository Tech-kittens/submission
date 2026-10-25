# Phone meeting mode integration status

The original proposal for session-specific meeting endpoints is superseded by
backend `1ee1b56` (`fix: standalone clients`), reviewed on 2026-10-25. The frontend
now uses the existing shared meeting API. No backend source changes were made.

Automatic review blocked updating the read-only backend checkout. The latest
`main` was inspected through a temporary bare Git copy under `/tmp`; the local
`QuacktionBE` checkout remains unchanged.

## Confirmed contract

- `POST /api/meetings/start` with name, meeting type and recording consent, without
  a device ID, creates or recovers the account's active phone meeting.
- `POST /api/meetings/{id}/audio/segments?startMs=...` accepts authenticated raw
  16 kHz mono PCM16 little-endian, up to 30 seconds per upload, in whole 1280-byte
  (40 ms) frames. The offset is contiguous; retries must reuse the offset/bytes.
- `GET /api/meetings/{id}/status` reports processing/recording state.
- `POST /api/meetings/{id}/end` finalizes after all phone audio is uploaded.
- Meeting resources do not include a conversation/session ID. The client stores
  that association in account-scoped secure storage.

See `PHONE_MEETING_MODE.md` for frontend behavior, retry handling, and limitations.
No additional backend API is required for this implementation's recording flow.

Live recording and report accuracy still require verification against the
running deployment. Passing scripted client tests does not establish deployment
version, Google transcription availability, or acoustic quality.
