# Duck-server protocol

Source of truth for both the firmware (`firmware/`) and the server (`server/`).

## Transport

- One persistent WebSocket per duck. The duck connects to the server at `/ws`.
- Control messages are JSON text frames.
- Audio is binary frames in both directions.

## Audio format

| Property | Value |
| --- | --- |
| Encoding | Raw PCM, signed 16-bit little-endian |
| Sample rate | 16 kHz |
| Channels | 1 (mono) |
| Frame length | 20 ms = 320 samples = 640 bytes |

## Duck to server

| Frame | Meaning |
| --- | --- |
| `{"type":"hello","device":"duck-1"}` | Sent on connect |
| binary PCM | Voice-detected speech, continuous |
| `{"type":"speech_start"}` / `{"type":"speech_end"}` | Voice detection edges, so the server can cut segments |
| `{"type":"wake"}` | Wake word heard; the next segment is a request |
| `{"type":"mute","on":true}` | Mute toggled; no audio follows while on |

## Server to duck

| Frame | Meaning |
| --- | --- |
| `{"type":"reply_start"}` | Reply audio follows; duck switches LEDs to speaking |
| binary PCM | Reply audio |
| `{"type":"reply_end"}` | Reply done; duck returns to listening |

The server is the only side that knows who spoke, so speaker names never travel to the duck.
