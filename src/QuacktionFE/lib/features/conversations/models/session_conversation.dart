import 'package:quacktion_frontend/features/conversations/models/conversation.dart';

class SessionConversation extends Conversation {
  SessionConversation({
    required super.id,
    required super.title,
    required super.updatedAt,
  });
  String status = 'Created';
  bool get canEnd => status == 'Active';
}
