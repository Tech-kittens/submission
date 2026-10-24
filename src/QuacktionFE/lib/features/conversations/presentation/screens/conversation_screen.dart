import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/conversations/presentation/widgets/conversation_voice_controls.dart';
import 'package:quacktion_frontend/features/conversations/presentation/widgets/message_bubble.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';

class ConversationScreen extends StatelessWidget {
  const ConversationScreen({
    super.key,
    required this.store,
    required this.drawer,
    required this.scaffoldKey,
    this.createVoice,
  });
  final VoiceController Function()? createVoice;
  final ConversationController store;
  final Widget drawer;
  final GlobalKey<ScaffoldState> scaffoldKey;

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: store,
    builder: (context, _) {
      final chat = store.active;
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
                  Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 16,
                      vertical: 8,
                    ),
                    child: Row(
                      children: [
                        Expanded(
                          child: Text(
                            chat == null
                                ? 'Voice conversation'
                                : '${chat.status} · Demo mode',
                            style: const TextStyle(fontSize: 12),
                          ),
                        ),
                        if (chat?.canEnd == true)
                          TextButton(
                            onPressed: store.busy ? null : store.end,
                            child: const Text('End conversation'),
                          ),
                      ],
                    ),
                  ),
                  if (store.error != null)
                    Padding(
                      padding: const EdgeInsets.all(12),
                      child: Text(
                        store.error!,
                        textAlign: TextAlign.center,
                        style: TextStyle(
                          color: Theme.of(context).colorScheme.error,
                        ),
                      ),
                    ),
                  if (store.loading || store.busy)
                    const LinearProgressIndicator(),
                  Expanded(
                    child: chat == null || chat.messages.isEmpty
                        ? const Center(
                            child: Padding(
                              padding: EdgeInsets.all(32),
                              child: Column(
                                mainAxisSize: MainAxisSize.min,
                                children: [
                                  Icon(
                                    Icons.graphic_eq_rounded,
                                    size: 48,
                                    color: Colors.black38,
                                  ),
                                  SizedBox(height: 20),
                                  Text(
                                    'Let’s talk',
                                    style: TextStyle(
                                      fontSize: 28,
                                      fontWeight: FontWeight.w500,
                                    ),
                                  ),
                                  SizedBox(height: 8),
                                  Text(
                                    'Tap below to speak. Your conversation will appear here.',
                                    textAlign: TextAlign.center,
                                    style: TextStyle(color: Colors.black54),
                                  ),
                                ],
                              ),
                            ),
                          )
                        : ListView.builder(
                            reverse: true,
                            padding: const EdgeInsets.all(16),
                            itemCount: chat.messages.length,
                            itemBuilder: (_, index) => MessageBubble(
                              message: chat
                                  .messages[chat.messages.length - 1 - index],
                            ),
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
