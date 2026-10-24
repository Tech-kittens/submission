import 'package:flutter/foundation.dart';

import 'package:quacktion_frontend/features/conversations/models/conversation.dart';

abstract class ConversationHistory extends ChangeNotifier {
  List<Conversation> get conversations;
}
