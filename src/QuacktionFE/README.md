# Quacktion Frontend

Flutter Android app with account authentication, backend conversation sessions, and mock speaker/tool setup.

## Run on an Android phone

Enable Developer options and USB debugging, connect the phone via USB, and
approve the debugging prompt on the phone. Then run:

```sh
flutter devices
flutter run -d <device-id>
```

On Arch Linux, install USB device rules if needed:

```sh
sudo pacman -S --needed android-udev
```

## Development

```sh
flutter pub get
flutter analyze
flutter build apk --debug
```

The entry point is `lib/main.dart`. The Dart package name is
`quacktion_frontend`; the Android application ID uses Flutter's default
`com.example.quacktion_frontend` and should be changed before publishing.

## Authentication and local API

The app uses the backend's email/password authentication. Create an account with
a name, email, a 12–128 character password, and password confirmation. The app
signs you in automatically after registration.
Tokens are stored as one pair in platform secure storage. The app restores the
session on launch, serializes refresh requests, and discards an uncertain refresh
instead of replaying its single-use token. Password changes require the current
password and return you to sign-in. Forgotten-password recovery is not supported
by the backend yet; the login screen explains this limitation.

Start the backend using the instructions in the sibling `QuacktionBE/README.md`.
For a USB-connected Android phone (or emulator), forward its localhost API port:

```sh
adb reverse tcp:5188 tcp:5188
flutter run
```

Repeat `adb reverse` after reconnecting the device. The default API address is
`http://localhost:5188`. Override it for a deployed API with:

```sh
flutter run --dart-define=API_BASE_URL=https://your-api.example.com
```

Use an origin without a trailing slash. Local HTTP is enabled only in Android
debug builds; release builds should connect to HTTPS. After adding the secure
storage plugin, stop and rerun Flutter once (hot reload alone is insufficient).

Run `flutter test` for local auth tests. With the backend running, use
`flutter test --dart-define=RUN_AUTH_INTEGRATION=true` to also exercise the real
registration/login/password/logout flow. That test creates a uniquely named
account and deactivates it afterward (the API retains deactivated account records).

## Speakers

The Devices page temporarily uses demo speaker setup: simulated Bluetooth scanning,
pairing, and Wi-Fi configuration with sample networks. Paired speakers show
**Not configured** until **Configure now** completes the demo Wi-Fi flow.
State survives navigation within the app, but resets when the app restarts.
No Bluetooth, Wi-Fi credentials, or backend device records are changed.

The backend device client remains available for future integration. Physical
firmware provisioning still requires configuration during flashing.

## Mock tool configuration

The sidebar’s **Configuration** page contains simulated MCP connections for Calendar, Notion,
Slack, and GitHub, plus custom servers. Connect, rename, edit the sample URL,
toggle the mock tool-action preference, or disconnect from the management sheet.
These settings stay in memory across page changes and reset on sign-out or app
restart. No authentication, MCP discovery, or network requests occur.

## Project structure

```text
lib/
  main.dart
  app/                  # Composition root, navigation, theme
    dependencies.dart
    navigation/
    theme/
  core/                 # Shared configuration, HTTP/authenticated API, storage
    config/
    networking/
    storage/
  shared/widgets/       # Reusable visual components
  features/
    auth/               # Account session lifecycle and auth forms
    account/            # Account settings UI
    conversations/      # History, transcripts, selection and live updates
    voice/              # Audio session lifecycle, transport and microphone UI
    devices/            # Demo provisioning plus retained backend device client
    integrations/       # Mock MCP connection settings
```

Features use `models/`, `data/`, `application/` and `presentation/` as needed.
`data` owns external I/O and data conversion; `application` owns ChangeNotifier
controllers and feature state; `presentation/screens` and `presentation/widgets`
render state and coordinate navigation. Avoid creating empty layers.

`app/dependencies.dart` selects implementations and owns the signed-in feature
controllers. The shell handles navigation; leaving a page preserves feature state.
Signing out disposes it. Demo device setup is selected here; `ApiDeviceRepository`
remains available for backend device management. The current backend has no BLE
provisioning implementation, so changing repositories alone does not enable real pairing.

`ApiClient` is the feature-facing authenticated HTTP contract. `AuthController`
owns tokens and refresh lifecycle; `AuthenticatedApiClient` owns request retries;
`HttpTransport` owns JSON requests and HTTP errors. Features do not depend on the
auth controller.

`ConversationRepository` owns REST/history conversion and `ConversationUpdates`
owns SignalR and polling. `ConversationController` owns selected-chat state and
retry keys. `VoiceTransport` opens the credential-bound WebSocket;
`VoiceController` manages negotiation, capture/playback, meters and cleanup.
`ConversationVoiceControls` connects those features; the reusable `VoiceControls`
widget only renders a supplied voice controller. Voice stops when that composition
widget is disposed, the app backgrounds, or the selected conversation changes.

Tests mirror feature folders under `test/features/`, with app navigation coverage
under `test/app/`. API and device mocks never require live credentials.

After moving widgets between files or changing the app structure, use a hot
restart (`R` in `flutter run`) or stop and rerun the app. Hot reload (`r`) can
retain objects from old library paths and cause lookup errors.

## Backend conversations

The app loads saved sessions and opens the most recently updated conversation.
The history sidebar searches loaded titles/transcripts; Devices, Configuration,
and Account settings stay pinned at the bottom. Speaker setup and tool connections are currently demos.

New and saved conversations share the same audio-only controls. Tap **Start
conversation** to begin a live session; there is no text composer. Existing
transcripts remain readable in history. No fake replies or recordings are generated.

SignalR connects to `/hubs/sessions` with the current account access token and
joins the viewed session. Events trigger REST transcript/status recovery; reconnects
rejoin the session. A 15-second refresh also recovers missed events and discovers
speaker sessions. All history/transcript pages are loaded and duplicate transcript
events are suppressed. Navigation drafts stay in memory until sign-out/restart.

Voice setup retries retain the idempotency key after an ambiguous network failure.
End retries also retain their request key. Provider failures are displayed without
substituting simulated responses.

Requires the backend session APIs documented in `QuacktionBE/docs/FE-SESSION.md`.
Live responses require its ElevenLabs configuration and matching audio allowlists.
Speaker firmware is independent of the Android voice integration.

Run `flutter test` for API-store and UI tests covering history pagination,
transcript deduplication, retry keys, ending sessions, navigation, and auth.

## Android voice conversations

Tap **Start conversation** from either a new chat or a saved conversation.
Allow microphone access, wait for **Listening**, and speak. The animated waveform
uses measured PCM microphone levels (not a simulated animation) and goes flat when
muted or silent. Replies play through
Android audio output while transcripts are stored in the selected chat. The start button becomes an inline microphone waveform, with mute and stop
controls; there is no separate capture sheet. Mute stops microphone transmission.
Ending voice, switching conversations, leaving chat, backgrounding the app,
or losing audio focus stops recording/playback and closes the conversation.
A disconnected session cannot be resumed; reconnect starts a new one.

`VoiceController` obtains a one-use credential from `POST /api/sessions/start` using
the account bearer token, opens `quacktion.audio.v1`, negotiates `audio.start`, and
starts capture only after `audio.ready`. `audio.clear` immediately flushes queued
playback on interruption. Setup failures retain the request key until the session
is consumed; secrets and PCM are not persisted or logged. SignalR/REST remain the
source of transcript/history data. Existing text or speaker sessions aren't taken
over by the phone: the voice button explicitly creates a new app conversation.

`PhoneAudio.kt` uses Android AudioRecord/AudioTrack with PCM16 mono, 20 ms input
frames, bounded playback queues, communication audio routing, and echo/noise
suppression where supported. This repository currently targets Android only.

Default formats are `pcm_16000` in both directions. They must match ElevenLabs
conversation metadata and backend `Audio__AllowedInputFormats__0__*` and
`Audio__AllowedOutputFormats__0__*` settings (Codec=pcm_16000, SampleRate=16000,
Channels=1). For a different PCM provider configuration, build with
`--dart-define=VOICE_INPUT_RATE=<rate> --dart-define=VOICE_OUTPUT_RATE=<rate>` and
update the server allowlists to match. There is no compressed-codec or resampling
fallback. Use HTTPS/WSS outside local development. When using `localhost:5188`
on a USB/debug-connected phone, run `adb reverse tcp:5188 tcp:5188`.

Voice tests exercise negotiation gating, binary microphone/reply transport,
mute, interruption flushing, clean end, permission denial, cancellation during
setup, request-key reuse, and rejection of untrusted audio URLs. Test on a phone
for microphone quality, echo cancellation, permission prompts, and chosen output
route; those hardware behaviors cannot be validated by Dart tests.
