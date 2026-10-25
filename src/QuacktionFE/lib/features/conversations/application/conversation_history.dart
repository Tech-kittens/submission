import 'package:flutter/foundation.dart';

import 'package:quacktion_frontend/features/conversations/models/conversation.dart';

abstract class ConversationHistory extends ChangeNotifier {
  List<Conversation> get conversations;
  String? get error;
  Future<void> refresh();
  Future<bool> renameConversation(Conversation conversation, String title);
  Future<bool> deleteConversation(Conversation conversation);
  bool canDelete(Conversation conversation);
}
