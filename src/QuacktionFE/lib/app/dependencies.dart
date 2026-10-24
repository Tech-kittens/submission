import 'package:quacktion_frontend/core/networking/api_client.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/devices/application/device_setup_controller.dart';
import 'package:quacktion_frontend/features/devices/data/speaker_provisioning.dart';
import 'package:quacktion_frontend/features/integrations/application/integrations_controller.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';

/// Composition root. Create fresh feature state for each signed-in app shell.
class AppDependencies {
  static AuthController createAuth() => AuthController();
  AppDependencies(this.api, {ConversationController? conversations})
    : conversations = conversations ?? ConversationController() {
    deviceSetup = DeviceSetupController(FirmwareDeviceProvisioning());
  }
  final ApiClient api;
  final ConversationController conversations;
  late final DeviceSetupController deviceSetup;
  final integrations = IntegrationsController();
  VoiceController createVoice() => VoiceController();
  void dispose() {
    conversations.dispose();
    deviceSetup.dispose();
    integrations.dispose();
  }
}
