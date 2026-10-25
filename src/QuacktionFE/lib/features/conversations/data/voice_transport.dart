import 'package:quacktion_frontend/features/conversations/models/session_conversation.dart';

abstract interface class VoiceTransport {
  SessionConversation? get session;

  Future<SessionConversation> start({
    required String name,
    SessionConversation? resumeSession,
    required void Function(SessionConversation) onSessionCreated,
    required void Function(String) onStatus,
    required void Function(double) onInputLevel,
    required void Function(String) onClosed,
  });

  /// Suspends conversation audio while keeping the microphone available to meetings.
  Future<void> setPaused(bool paused);
  void setMuted(bool muted);
  Future<void> end();
  Future<void> stop({bool sendEnd = true});
}
