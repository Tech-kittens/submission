import 'package:flutter/material.dart';
import 'package:quacktion_frontend/features/meetings/application/meeting_mode_controller.dart';
import 'package:quacktion_frontend/features/meetings/presentation/widgets/meeting_mode_control.dart';

import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/conversations/presentation/widgets/conversation_voice_controls.dart';
import 'package:quacktion_frontend/features/conversations/presentation/widgets/message_bubble.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';
import 'package:quacktion_frontend/features/conversations/models/conversation.dart';
import 'package:quacktion_frontend/features/integrations/application/integrations_controller.dart';
import 'package:quacktion_frontend/features/integrations/models/mcp_models.dart';
import 'package:quacktion_frontend/features/integrations/presentation/widgets/request_review.dart';

class ConversationScreen extends StatelessWidget {
  const ConversationScreen({
    super.key,
    required this.store,
    required this.drawer,
    required this.scaffoldKey,
    required this.createVoice,
    this.integrations,
    this.meetings,
  });
  final VoiceController Function() createVoice;
  final ConversationController store;
  final Widget drawer;
  final GlobalKey<ScaffoldState> scaffoldKey;
  final IntegrationsController? integrations;
  final MeetingModeController? meetings;

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: Listenable.merge([store, ?integrations]),
    builder: (context, _) {
      final chat = store.active;
      final requests = chat == null
          ? <McpTask>[]
          : integrations?.tasks
                    .where((task) => task.sessionId == chat.id)
                    .toList() ??
                <McpTask>[];
      final entries = <({DateTime at, ChatMessage? message, McpTask? task})>[
        for (final message in chat?.messages ?? <ChatMessage>[])
          (at: message.sentAt, message: message, task: null),
        for (final task in requests)
          (at: task.createdAt, message: null, task: task),
      ]..sort((a, b) => a.at.compareTo(b.at));
      final historyUnavailable = store.error != null && chat == null;
      return Scaffold(
        key: scaffoldKey,
        drawer: drawer,
        backgroundColor: Colors.white,
        appBar: AppBar(
          title: Text(
            chat?.title ?? 'New conversation',
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
          leading: IconButton(
            tooltip: 'Conversation history',
            icon: const Icon(Icons.menu_rounded),
            onPressed: () => scaffoldKey.currentState?.openDrawer(),
          ),
          actions: [
            IconButton(
              tooltip: 'New conversation',
              onPressed: store.busy ? null : () => store.select(null),
              icon: const Icon(Icons.edit_square),
            ),
          ],
        ),
        body: SafeArea(
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 720),
              child: Column(
                children: [
                  if (meetings != null && chat != null)
                    MeetingModeControl(
                      controller: meetings!,
                      sessionId: chat.id,
                      name: chat.title,
                      canStart: chat.canEnd,
                    ),
                  if (chat?.canEnd == true)
                    Align(
                      alignment: Alignment.centerRight,
                      child: TextButton(
                        onPressed: store.busy ? null : store.end,
                        child: const Text('End conversation'),
                      ),
                    ),
                  if (store.loading || store.busy)
                    const LinearProgressIndicator(),
                  if (integrations?.tasksUnavailable == true)
                    Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 16),
                      child: Row(
                        children: [
                          const Expanded(
                            child: Text(
                              'Approval requests could not be loaded.',
                            ),
                          ),
                          TextButton(
                            onPressed: () => integrations!.refreshTasks(),
                            child: const Text('Retry requests'),
                          ),
                        ],
                      ),
                    ),
                  Expanded(
                    child: entries.isEmpty
                        ? Center(
                            child: SingleChildScrollView(
                              child: Padding(
                                padding: const EdgeInsets.all(32),
                                child: Column(
                                  mainAxisSize: MainAxisSize.min,
                                  children: [
                                    Icon(
                                      historyUnavailable
                                          ? Icons.cloud_off_rounded
                                          : Icons.graphic_eq_rounded,
                                      size: 48,
                                      color: Colors.black38,
                                    ),
                                    const SizedBox(height: 20),
                                    Text(
                                      historyUnavailable
                                          ? 'History unavailable'
                                          : 'Let’s talk',
                                      style: const TextStyle(
                                        fontSize: 28,
                                        fontWeight: FontWeight.w500,
                                      ),
                                    ),
                                    const SizedBox(height: 8),
                                    Text(
                                      historyUnavailable
                                          ? 'Try loading your conversations again.'
                                          : 'Tap below to speak. Your conversation will appear here.',
                                      textAlign: TextAlign.center,
                                      style: const TextStyle(
                                        color: Colors.black54,
                                      ),
                                    ),
                                    if (historyUnavailable)
                                      TextButton(
                                        onPressed: store.loading
                                            ? null
                                            : store.refresh,
                                        child: const Text(
                                          'Reload conversations',
                                        ),
                                      ),
                                  ],
                                ),
                              ),
                            ),
                          )
                        : ListView.builder(
                            reverse: true,
                            padding: const EdgeInsets.all(16),
                            key: ValueKey(chat?.id),
                            itemCount: entries.length,
                            findChildIndexCallback: (key) {
                              if (key is! ValueKey<String>) return null;
                              final index = entries.indexWhere(
                                (entry) =>
                                    entry.task != null &&
                                    key.value == 'request-${entry.task!.id}',
                              );
                              return index < 0
                                  ? null
                                  : entries.length - 1 - index;
                            },
                            itemBuilder: (_, index) {
                              final entry = entries[entries.length - 1 - index];
                              if (entry.message != null) {
                                return MessageBubble(message: entry.message!);
                              }
                              final task = entry.task!;
                              return Card(
                                key: ValueKey('request-${task.id}'),
                                margin: const EdgeInsets.symmetric(
                                  vertical: 12,
                                ),
                                child: task.pending
                                    ? RequestReview(
                                        controller: integrations!,
                                        task: task,
                                        inline: true,
                                      )
                                    : ListTile(
                                        leading: const Icon(Icons.task_alt),
                                        title: Text(task.summary),
                                        subtitle: Text(task.statusLabel),
                                      ),
                              );
                            },
                          ),
                  ),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 12, 20, 20),
                    child: ConversationVoiceControls(
                      store: store,
                      createVoice: createVoice,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      );
    },
  );
}
