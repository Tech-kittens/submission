import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/conversations/models/conversation.dart';

class MessageBubble extends StatelessWidget {
  const MessageBubble({super.key, required this.message});
  final ChatMessage message;
  @override
  Widget build(BuildContext context) {
    final assistant = message.source == MessageSource.assistant;
    final label = switch (message.source) {
      MessageSource.app => 'You',
      MessageSource.speaker => 'You · Speaker',
      MessageSource.assistant => 'Quacktion',
    };
    return Align(
      alignment: assistant ? Alignment.centerLeft : Alignment.centerRight,
      child: Container(
        constraints: BoxConstraints(
          maxWidth: MediaQuery.sizeOf(context).width * 0.82,
        ),
        margin: const EdgeInsets.only(bottom: 16),
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          color: assistant ? Colors.transparent : const Color(0xFFF1F1EF),
          borderRadius: BorderRadius.circular(20),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              label,
              style: const TextStyle(
                fontWeight: FontWeight.w700,
                fontSize: 12,
                color: Color(0xFF4E5F53),
              ),
            ),
            const SizedBox(height: 8),
            if (message.voiceDuration != null) ...[
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const Icon(Icons.graphic_eq_rounded, size: 30),
                  const SizedBox(width: 8),
                  Flexible(
                    child: Text(
                      'Voice message · ${message.voiceDuration!.inMinutes}:${(message.voiceDuration!.inSeconds % 60).toString().padLeft(2, '0')}',
                      style: const TextStyle(fontWeight: FontWeight.w600),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 6),
            ],
            SelectableText(
              message.text,
              style: const TextStyle(fontSize: 16, height: 1.45),
            ),
            const SizedBox(height: 8),
            Text(
              TimeOfDay.fromDateTime(message.sentAt).format(context),
              style: const TextStyle(fontSize: 11, color: Color(0xFF69685F)),
            ),
          ],
        ),
      ),
    );
  }
}
