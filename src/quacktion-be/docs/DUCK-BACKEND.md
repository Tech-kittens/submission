# Duck backend handoff

Tracks the firmware report dated 2026-10-24. Repository changes require deployment before affecting `quackback.ofneill.com`.

| Item | Status |
| --- | --- |
| A1 | Persistent device socket supports Gemini assistant audio, `audio.ready`, terminal notifications, and another wake on the same connection. Tests exercise the actual Gemini adapter with a simulated provider. |
| A2 | RSA certificate change remains an infrastructure task. TLS terminates at Railway's edge; no edge configuration or Railway credentials are available in this checkout. Certbot inside the app would not replace the served certificate. |
| A3 | Assistant input queues wait for capacity (16 messages). Buffered upload is accepted without pacing. Gemini turn detection at 3x still needs a live-provider/hardware trial; failed writes are now surfaced rather than silently discarded. |
| A4 | Configurable 500 ms end silence, low speech-start sensitivity and 40 ms prefix padding. |
| B1 | Device credential is supported on both HTTP start and session socket. No `connectionCredential` or `Authorization: Session`. Apps use Bearer. |
| B2 | URL generation honors HTTPS/forwarded HTTPS and forces WSS outside Development. Existing forwarded headers run before routing. |
| B3 | Ten-second sweep expires Ready sessions; new starts also recover expired, terminal or deleted reservations. Retries preserve the original deadline. Version comparison protects concurrent connection claims. |
| B4 | Baseline already ignores obsolete Mongo session fields such as `ConnectionCredentialHash`, with a regression test. Production trace IDs have not been checked against Railway logs here; replay after deployment before claiming the live 500s resolved. |
| B5 | Client/HTTP ends send `session.ended` before closing session audio, for both auth schemes. Real provider/input/output failures send `session.failed` with `payload.code`. Broken network connections cannot deliver terminal controls. The device socket remains open after voice sessions. |
| B6 | Gemini's native 24 kHz output is filtered/resampled to negotiated mono PCM16/16 kHz. Tests check duration/sample count. |
| B7 | [SESSION.md](SESSION.md) documents `{"name":"..."}`, auth, expiry and persistent-socket reuse. |
| C | [Meeting protocol](transcription-protocol.md) covers app mode endpoints, segments, transcription/storage, ordered continuations, reconnect/backlog, mute and wake. Notification/confirmation execution remains deferred. |

## TLS deployment

Railway documents automatic Let's Encrypt issuance in [its SSL guide](https://docs.railway.com/networking/troubleshooting/ssl). RSA key selection needs edge-provider support or a TLS terminator under your control. Request RSA-2048 and inspect the served intermediate chain: an RSA leaf alone does not establish that every chain signature uses RSA. Verify SNI `quackback.ofneill.com` on the duck before claiming the measured 1.5-second target. No certificate or DNS changes were made here.

## Remaining acceptance checks

Run the firmware scenarios against the deployed build: wake-to-answer timing, normal end/next wake, natural playback, and forced Wi-Fi interruption/backlog recovery. Automated tests use simulated provider responses and synthetic PCM; they do not establish Gemini's behavior at 3x, physical LED behavior, or production latency.

VAD fields follow Google's [Live capabilities reference](https://ai.google.dev/gemini-api/docs/live-api/capabilities); segment transcription follows [audio understanding](https://ai.google.dev/gemini-api/docs/audio). The original firmware proposal was unavailable, so compare the new wire examples with firmware before enabling its feature flag.
