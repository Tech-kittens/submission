import 'package:quacktion_frontend/features/voice/data/wake_phrase_detector.dart';

import 'dart:async';

import 'package:quacktion_frontend/core/config/app_config.dart';
import 'package:quacktion_frontend/features/meetings/data/meeting_links.dart';

import 'package:quacktion_frontend/features/meetings/application/meeting_mode_controller.dart';

import 'package:quacktion_frontend/core/networking/api_client.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/devices/application/device_setup_controller.dart';
import 'package:quacktion_frontend/features/devices/application/device_controller.dart';
import 'package:quacktion_frontend/features/devices/data/api_device_repository.dart';
import 'package:quacktion_frontend/features/devices/data/backend_device_linker.dart';
import 'package:quacktion_frontend/features/devices/data/speaker_provisioning.dart';
import 'package:quacktion_frontend/features/devices/data/saved_speaker_store.dart';
import 'package:quacktion_frontend/features/integrations/application/integrations_controller.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';
import 'package:quacktion_frontend/features/conversations/data/session_repository.dart';
import 'package:quacktion_frontend/features/conversations/data/session_event_client.dart';
import 'package:quacktion_frontend/features/conversations/data/session_audio_transport.dart';

/// Composition root. Create fresh feature state for each signed-in app shell.
class AppDependencies {
  static AuthController createAuth() => AuthController();
  AppDependencies(
    this.api, {
    ConversationController? conversations,
    String? accountId,
  }) : backend = BackendApi(api) {
    integrations = IntegrationsController(api: backend);
    meetings = MeetingModeController(
      backend,
      sourceSampleRate: AppConfig.voiceInputRate,
      links: accountId == null
          ? null
          : SecureMeetingLinks('${api.baseUrl}:$accountId'),
    );
    this.conversations =
        conversations ??
        _createConversations(
          api,
          backend,
          () => unawaited(integrations.refreshTasks(quiet: true)),
        );
    devices = DeviceController(ApiDeviceRepository(api));
    deviceSetup = DeviceSetupController(
      FirmwareDeviceProvisioning(),
      speakerStore: SecureSavedSpeakerStore(),
      backendLinker: BackendDeviceLinker(backend),
      onBackendLinked: devices.refresh,
    );
  }
  final ApiClient api;
  final BackendApi backend;
  late final ConversationController conversations;
  late final DeviceController devices;
  late final DeviceSetupController deviceSetup;
  late final IntegrationsController integrations;
  late final MeetingModeController meetings;

  static ConversationController _createConversations(
    ApiClient api,
    BackendApi backend,
    void Function() onTaskEvent,
  ) {
    late final ConversationController controller;
    final events = SignalRSessionEventClient(
      baseUrl: api.baseUrl,
      accessToken: api.accessToken,
      onSessionEvent: (sessionId) => controller.onSessionEvent(sessionId),
      onTaskEvent: onTaskEvent,
    );
    controller = ConversationController(
      repository: ApiSessionRepository(backend),
      events: events,
    );
    return controller;
  }

  VoiceController createVoice() {
    late final VoiceController voice;
    voice = VoiceController(
      meetings: meetings,
      wakeDetector: OfflineWakePhraseDetector(),
      transport: SessionAudioTransport(
        api: backend,
        client: api,
        onMicrophoneFrame: (sessionId, pcm) {
          meetings.acceptAudio(sessionId, pcm);
          voice.acceptMicrophoneFrame(pcm);
        },
        onCaptureStarted: meetings.captureStarted,
        onCaptureStopped: meetings.captureStopped,
      ),
    );
    return voice;
  }

  void dispose() {
    conversations.dispose();
    devices.dispose();
    deviceSetup.dispose();
    integrations.dispose();
    meetings.dispose();
  }
}
