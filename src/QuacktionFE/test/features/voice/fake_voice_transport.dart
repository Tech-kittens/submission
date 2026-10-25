import 'package:quacktion_frontend/features/conversations/data/voice_transport.dart';
import 'package:quacktion_frontend/features/conversations/models/session_conversation.dart';

class FakeVoiceTransport implements VoiceTransport {
  @override
  SessionConversation? session;

  @override
  Future<SessionConversation> start({
    required String name,
    SessionConversation? resumeSession,
    required void Function(SessionConversation) onSessionCreated,
    required void Function(String) onStatus,
    required void Function(double) onInputLevel,
    required void Function(String) onClosed,
  }) async {
    await Future<void>.delayed(const Duration(milliseconds: 50));
    onStatus('Connecting to voice service…');
    final created =
        resumeSession ??
              SessionConversation(
                id: 'test-session',
                title: name,
                updatedAt: DateTime.utc(2026),
              )
          ..status = 'Active';
    session = created;
    onSessionCreated(created);
    onStatus('Listening');
    onInputLevel(0.5);
    return created;
  }

  bool paused = false;
  @override
  Future<void> setPaused(bool value) async {
    paused = value;
  }

  @override
  void setMuted(bool muted) {}

  @override
  Future<void> end() async {
    if (session?.status == 'Active') session!.status = 'Completed';
  }

  @override
  Future<void> stop({bool sendEnd = true}) async {}
}
