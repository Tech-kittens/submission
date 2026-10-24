import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';

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
}
