# Asking the assistant during phone meetings

While a phone meeting records, say **Hey Quacktion**. Once the app shows the
assistant is listening, ask the question. The same conversation resumes through
the existing session resume API and audio WebSocket; meeting audio upload
continues. **Ask assistant** is also available as a fallback.

**Back to meeting** disconnects/suspends the assistant without stopping recording.
Returning is explicit; there is no automatic end-of-answer detection. Any earlier
manual pause is preserved when the meeting ends.

## Wake phrase implementation

`OfflineWakePhraseDetector` uses sherpa_onnx 2.0.0 and a bundled approximately
5.2 MB keyword model. See `assets/wake_word/README.md` for upstream attribution,
license, model identifiers and pronunciation tokens. No runtime model download,
cloud speech recognition, account key or second microphone recorder is needed.

The app feeds its existing 16 kHz mono PCM16 frames to both meeting upload and,
only when armed, the detector. Native inference runs in a Dart isolate on one CPU
thread. One batch is in flight; pending audio is bounded to half a second.
Generation checks discard delayed detections after a reset/disarm.

Detection is armed only while recording is active, the assistant is paused, the
mic is unmuted and meeting status is confirmed. It is disarmed during assistant
responses, transitions, finalization, errors and disposal. The existing app
lifecycle stops capture when backgrounded; this is foreground meeting detection,
not system-wide always-on hotword support. A detector failure leaves recording
and the fallback button available.

Resume requires a network round trip, so wait for Listening after the wake
phrase. Audio spoken while reconnecting is not buffered for the assistant.
Meeting recording still receives that audio. Assistant playback may be picked up
by the phone microphone and appear in the meeting recording.

## Backend contract

Backend main at `13feeb1` includes persistent session resumption; the hosted
server must deploy that contract. No backend or firmware changes were made.
This does not add phone support to the agent's `start_meeting`/`end_meeting` tools.

## Validation

Controller/widget tests cover same-session resume, uninterrupted meeting upload,
status polls, detector failure, retry, disarming during assistant playback,
rearming on return, and prior manual pause. A real native-worker test detects
synthesized "Hey Quacktion", rejects the tested ordinary speech and "Hey question",
and detects again after resetting. This is a small regression test, not a
false-accept/reject benchmark or a replacement for testing real voices/noise.

To run the native test on Linux, set QUACKTION_SHERPA_LIB_DIR to the downloaded
sherpa_onnx_linux package's `linux/x64` directory and run:

```
flutter test test/features/voice/wake_phrase_native_test.dart
```

The three test WAV fixtures are mono PCM16 at 16 kHz, generated locally with
Piper en_US-lessac-low for this regression test. They contain only the wake
phrase, "Hey question", and "Please start the meeting". No user audio was used.
