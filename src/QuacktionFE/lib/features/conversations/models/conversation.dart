enum MessageSource { app, speaker, assistant }

class ChatMessage {
  const ChatMessage({
    required this.text,
    required this.source,
    required this.sentAt,
    this.voiceDuration,
  });
  final Duration? voiceDuration;
  final String text;
  final MessageSource source;
  final DateTime sentAt;
}

class Conversation {
  Conversation({
    required this.id,
    required this.title,
    required this.updatedAt,
    List<ChatMessage>? messages,
  }) : messages = messages ?? [];
  final String id;
  String title;
  DateTime updatedAt;
  final List<ChatMessage> messages;
  String draft = '';
  bool replying = false;
}
