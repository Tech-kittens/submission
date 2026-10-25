import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/conversations/data/session_repository.dart';
import 'package:quacktion_frontend/features/conversations/data/session_event_client.dart';
import 'package:quacktion_frontend/features/conversations/models/conversation.dart';

void main() {
  test('loads local demo history and supports selecting a new chat', () async {
    final store = ConversationController();
    addTearDown(store.dispose);

    await store.initialize();
    expect(store.conversations, hasLength(2));
    expect(store.active?.title, 'Planning the weekend');

    await store.select(null);
    expect(store.active, isNull);
  });

  test('voice session lifecycle and replies stay local', () async {
    final store = ConversationController();
    addTearDown(store.dispose);

    await store.initialize();
    await store.select(null);
    store.beginMockVoiceConversation();
    expect(store.active?.status, 'Active');
    store.addMockReply();
    expect(store.active?.messages, hasLength(1));
    await store.end();
    expect(store.active?.status, 'Completed');
  });

  test(
    'backend history loads transcripts and persists title and deletion',
    () async {
      final repo = _FakeSessionRepository();
      final store = ConversationController(repository: repo);
      addTearDown(store.dispose);

      await store.initialize();
      expect(store.active?.id, 'server-session');
      expect(store.active?.messages.single.text, 'Hello from the speaker');

      final session = store.active!;
      expect(await store.renameConversation(session, 'Updated title'), isTrue);
      expect(repo.renamedTitle, 'Updated title');
      expect(await store.deleteConversation(session), isTrue);
      expect(repo.deletedId, 'server-session');
      expect(store.conversations, isEmpty);
    },
  );

  test(
    'joins the active backend chat for live speaker transcript events',
    () async {
      final repo = _FakeSessionRepository();
      final events = _FakeSessionEventClient();
      final store = ConversationController(repository: repo, events: events);
      addTearDown(store.dispose);

      await store.initialize();
      expect(events.joined, ['server-session']);

      await store.select(null);
      await store.select(repo.session);
      expect(events.joined, ['server-session', 'server-session']);
    },
  );
}

class _FakeSessionEventClient implements SessionEventClient {
  final joined = <String>[];
  @override
  Future<void> connect() async {}
  @override
  Future<void> join(String sessionId) async => joined.add(sessionId);
  @override
  Future<void> leave(String sessionId) async {}
  @override
  Future<void> dispose() async {}
}

class _FakeSessionRepository implements SessionRepository {
  final session = SessionConversation(
    id: 'server-session',
    title: 'Speaker chat',
    updatedAt: DateTime.utc(2026),
  );
  String? renamedTitle;
  String? deletedId;

  @override
  Future<List<SessionConversation>> load() async => [session];
  @override
  Future<SessionConversation> loadOne(String id) async => session;
  @override
  Future<List<ChatMessage>> transcript(String id) async => [
    ChatMessage(
      text: 'Hello from the speaker',
      source: MessageSource.assistant,
      sentAt: DateTime.utc(2026),
    ),
  ];
  @override
  Future<SessionConversation> create(String title) async => session;
  @override
  Future<void> rename(String id, String title) async {
    renamedTitle = title;
  }

  @override
  Future<void> start(String id, {String promptFormat = 'text'}) async {}
  @override
  Future<void> end(String id) async {}
  @override
  Future<void> delete(String id) async {
    deletedId = id;
  }
}
