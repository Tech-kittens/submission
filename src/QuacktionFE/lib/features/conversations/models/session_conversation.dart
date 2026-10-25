import 'package:quacktion_frontend/features/conversations/models/conversation.dart';

class SessionConversation extends Conversation {
  SessionConversation({
    required super.id,
    required super.title,
    required super.updatedAt,
  });
  String status = 'Created';
  bool get canEnd => status == 'Active' || status == 'Suspended';
  bool get canResume =>
      status == 'Active' || status == 'Suspended' || status == 'Completed';
  bool get shouldPoll =>
      status == 'Ready' ||
      status == 'Starting' ||
      status == 'Connecting' ||
      status == 'Active' ||
      status == 'Suspended' ||
      status == 'Ending';
}
