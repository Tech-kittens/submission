import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/conversations/models/conversation.dart';
import 'package:quacktion_frontend/features/conversations/models/session_conversation.dart';

abstract interface class SessionRepository {
  Future<List<SessionConversation>> load();
  Future<SessionConversation> loadOne(String id);
  Future<List<ChatMessage>> transcript(String id);
  Future<SessionConversation> create(String title);
  Future<void> rename(String id, String title);
  Future<void> start(String id, {String promptFormat = 'text'});
  Future<void> end(String id);
  Future<void> delete(String id);
}

class ApiSessionRepository implements SessionRepository {
  ApiSessionRepository(this.api);
  final BackendApi api;

  @override
  Future<List<SessionConversation>> load() async =>
      (await api.sessions()).map(_session).toList();

  @override
  Future<SessionConversation> loadOne(String id) async =>
      _session(await api.session(id));

  @override
  Future<List<ChatMessage>> transcript(String id) async {
    final messages = <ChatMessage>[];
    for (final entry in await api.transcript(id)) {
      final text = entry['text']?.toString() ?? '';
      if (text.isEmpty) continue;
      final source = switch ((entry['speakerIdentifier']?.toString() ??
              entry['source']?.toString() ??
              '')
          .toLowerCase()) {
        'user' || 'app' || 'phone' => MessageSource.app,
        'speaker' || 'device' => MessageSource.speaker,
        _ => MessageSource.assistant,
      };
      final time =
          DateTime.tryParse(entry['createdAt']?.toString() ?? '') ??
          DateTime.now();
      final previous = messages.isNotEmpty && messages.last.source == source
          ? messages.removeLast()
          : null;
      messages.add(
        ChatMessage(
          // Transcription fragments include their own spaces and may split a
          // word. Concatenate them unchanged until the speaker changes.
          text: previous == null ? text : '${previous.text}$text',
          source: source,
          sentAt: previous?.sentAt ?? time,
        ),
      );
    }
    return messages;
  }

  @override
  Future<SessionConversation> create(String title) async =>
      _session(await api.createSession(title: title));

  @override
  Future<void> rename(String id, String title) async {
    await api.updateSession(id, title);
  }

  @override
  Future<void> start(String id, {String promptFormat = 'text'}) async {
    await api.startSession(id, promptFormat: promptFormat);
  }

  @override
  Future<void> end(String id) async {
    await api.endSession(id);
  }

  @override
  Future<void> delete(String id) async {
    await api.deleteSession(id);
  }

  SessionConversation _session(Map<String, dynamic> row) {
    final chat = SessionConversation(
      id: row['id']?.toString() ?? '',
      title:
          row['title']?.toString() ?? row['name']?.toString() ?? 'Conversation',
      updatedAt:
          DateTime.tryParse(
            row['updatedAt']?.toString() ?? row['createdAt']?.toString() ?? '',
          ) ??
          DateTime.now(),
    );
    chat.status = row['status']?.toString() ?? 'Created';
    return chat;
  }
}
