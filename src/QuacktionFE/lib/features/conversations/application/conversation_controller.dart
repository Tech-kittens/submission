import 'package:quacktion_frontend/features/conversations/application/conversation_history.dart';
import 'package:quacktion_frontend/features/conversations/models/conversation.dart';
import 'package:quacktion_frontend/features/conversations/models/session_conversation.dart';

export 'package:quacktion_frontend/features/conversations/models/session_conversation.dart';

/// Local-only conversation state for the current UI prototype.
class ConversationController extends ConversationHistory {
  final List<SessionConversation> _items = [];
  @override
  List<SessionConversation> get conversations =>
      List.of(_items)..sort((a, b) => b.updatedAt.compareTo(a.updatedAt));

  SessionConversation? active;
  bool loading = false, busy = false;
  String? error;

  Future<void> initialize() async {
    if (_items.isEmpty) {
      final now = DateTime.now();
      _items.addAll([
        SessionConversation(
            id: 'demo-1',
            title: 'Planning the weekend',
            updatedAt: now,
          )
          ..status = 'Completed'
          ..messages.addAll([
            ChatMessage(
              text: 'Help me plan a relaxed weekend.',
              source: MessageSource.app,
              sentAt: now.subtract(const Duration(days: 1)),
            ),
            ChatMessage(
              text: 'A walk, a good lunch, and some free time sounds like a nice balance.',
              source: MessageSource.assistant,
              sentAt: now.subtract(const Duration(days: 1, minutes: -1)),
            ),
          ]),
        SessionConversation(
            id: 'demo-2',
            title: 'Ideas for dinner',
            updatedAt: now.subtract(const Duration(hours: 3)),
          )
          ..status = 'Completed'
          ..messages.addAll([
            ChatMessage(
              text: 'Give me a few easy dinner ideas.',
              source: MessageSource.app,
              sentAt: now.subtract(const Duration(hours: 3)),
            ),
            ChatMessage(
              text: 'How about veggie tacos, lemon pasta, or a quick chickpea curry?',
              source: MessageSource.assistant,
              sentAt: now.subtract(const Duration(hours: 3, minutes: -1)),
            ),
          ]),
      ]);
      active = conversations.first;
    }
    notifyListeners();
  }

  Future<void> select(SessionConversation? chat) async {
    active = chat;
    error = null;
    notifyListeners();
  }

  void beginMockVoiceConversation() {
    final now = DateTime.now();
    final chat =
        active ??
        SessionConversation(
          id: 'demo-${now.microsecondsSinceEpoch}',
          title: 'Voice conversation',
          updatedAt: now,
        );
    if (!_items.contains(chat)) _items.add(chat);
    chat.status = 'Active';
    active = chat;
    notifyListeners();
  }

  void addMockReply() {
    final chat = active;
    if (chat == null) return;
    final now = DateTime.now();
    chat.messages.add(
      ChatMessage(
        text:
            'I’m listening. Voice conversations are currently shown as a demo.',
        source: MessageSource.assistant,
        sentAt: now,
      ),
    );
    chat.updatedAt = now;
    notifyListeners();
  }

  Future<void> end() async {
    if (active case final chat?) chat.status = 'Completed';
    notifyListeners();
  }
}
