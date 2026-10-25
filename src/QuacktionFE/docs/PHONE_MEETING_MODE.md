# Standalone app meeting mode

## Usage

Start a phone voice conversation, open Meeting mode, choose a name/type, and
explicitly confirm recording and transcription consent. A speaker is not needed.
The existing phone microphone stream feeds both voice conversation and meeting
recording; the app does not start a second native recorder.

The banner shows recording only when the backend meeting is active and the app
is accepting microphone frames. Upload failures pause meeting capture and remain
visible. Muting/stopping the microphone stops meeting capture and attempts to
flush/end the meeting; unmuting does not silently resume a finalized recording.
Ending meeting recording leaves ordinary voice conversation operational.

## Audio and finalization

The shared `/api/meetings/start` endpoint is called without a device ID. PCM16
mono 16 kHz frames are buffered into consecutive chunks up to 30 seconds, uploaded
as `application/octet-stream` with the account bearer token. Binary requests use
the same refresh flow as other authenticated requests. Upload timeout is 90
seconds because the backend may spend up to 75 seconds on provisional
transcription before returning.

Chunks upload in order. A failed request retains its original bytes and offset.
The final partial 40 ms frame is padded with at most 39.97 ms of silence. The app
never calls `/end` until all queued chunks succeed. Retry upload and end meeting
retries retained audio, then finalizes. The queue is bounded to approximately two
minutes of audio; when full, capture pauses with an error rather than growing
without limit or silently claiming an intact recording.

The selection supports only 16 kHz microphone input; other configured rates show
an error instead of uploading audio with the wrong format.

## Recovery and boundaries

Meeting IDs are associated with conversation IDs using account- and API-scoped
secure storage. Status is polled every five seconds while that conversation is
mounted and the app is resumed. Other users' associations are never reused.

The backend can return an existing active phone meeting on start. Existing
segments are checked before attaching capture at offset zero. A recovered meeting
is displayed as paused and can be finalized; recording is not silently resumed
with an incorrect offset or inferred consent. One account-wide phone meeting is
supported at a time.

Audio awaiting upload is held in memory. Keep the app open if an upload fails.
Process termination can lose unuploaded audio. Signing out disposes the queue;
an already-sent request can finish, but subsequent chunks are not sent. The
backend meeting may remain active after an abrupt process exit; reopening its
associated conversation allows status recovery and explicit finalization.
Background recording and automatic continuation after process death are not
implemented. Incomplete recordings/reports must not be represented as intact.

Report browsing, speaker correction, downloads and a separate meeting-history
screen are outside this conversation entry/indicator change. Existing MCP
approvals continue independently.

## Verification

Client tests cover consent, duplicate taps, no-device starts, binary token
refresh, ordered frame-aligned chunks, exact retry bytes/offsets, final padding,
flush-before-end, failed finalization recovery, microphone stop handling,
recording indicators, and cancellation of queued work at disposal. All tests use
scripted API responses/audio; verify microphone capture and final reports on the
actual deployed backend separately.
