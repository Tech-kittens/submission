import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/conversations/application/conversation_history.dart';
import 'package:quacktion_frontend/features/conversations/models/conversation.dart';

class HistorySidebar extends StatefulWidget {
  const HistorySidebar({
    super.key,
    required this.store,
    required this.activeId,
    required this.onOpen,
    required this.onNew,
    required this.onDevices,
    required this.onConfiguration,
    required this.onRequests,
    this.pendingRequests = 0,
    required this.onAccount,
  });
  final ConversationHistory store;
  final String activeId;
  final ValueChanged<Conversation> onOpen;
  final VoidCallback onNew, onDevices, onConfiguration, onAccount, onRequests;
  final int pendingRequests;
  @override
  State<HistorySidebar> createState() => _HistorySidebarState();
}

class _HistorySidebarState extends State<HistorySidebar> {
  String _query = '';
  void _select(VoidCallback action) {
    Navigator.of(context).pop();
    action();
  }

  Future<void> _rename(Conversation chat) async {
    final title = await showDialog<String>(
      context: context,
      builder: (_) => _RenameConversationDialog(title: chat.title),
    );
    if (title == null || title.trim().isEmpty || !mounted) return;
    await widget.store.renameConversation(chat, title);
  }

  Future<void> _delete(Conversation chat) async {
    final confirm = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Delete conversation?'),
        content: const Text('This conversation will be permanently removed.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirm == true && mounted) {
      await widget.store.deleteConversation(chat);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Drawer(
      backgroundColor: const Color(0xFFFAFAF7),
      child: SafeArea(
        child: Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 12, 8, 4),
              child: Row(
                children: [
                  const Expanded(
                    child: Text(
                      'Quacktion',
                      style: TextStyle(
                        fontSize: 23,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                  ),
                  IconButton(
                    tooltip: 'Refresh conversations',
                    onPressed: widget.store.refresh,
                    icon: const Icon(Icons.refresh),
                  ),
                ],
              ),
            ),
            ListTile(
              leading: const Icon(Icons.edit_square),
              title: const Text('New conversation'),
              onTap: () => _select(widget.onNew),
            ),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: TextField(
                decoration: const InputDecoration(
                  hintText: 'Search conversations',
                  prefixIcon: Icon(Icons.search),
                ),
                onChanged: (value) =>
                    setState(() => _query = value.trim().toLowerCase()),
              ),
            ),
            const Padding(
              padding: EdgeInsets.symmetric(horizontal: 20, vertical: 8),
              child: Align(
                alignment: Alignment.centerLeft,
                child: Text(
                  'Conversations',
                  style: TextStyle(fontSize: 12, color: Colors.black54),
                ),
              ),
            ),
            Expanded(
              child: ListenableBuilder(
                listenable: widget.store,
                builder: (context, _) {
                  final chats = widget.store.conversations
                      .where(
                        (chat) =>
                            chat.title.toLowerCase().contains(_query) ||
                            chat.messages.any(
                              (message) =>
                                  message.text.toLowerCase().contains(_query),
                            ),
                      )
                      .toList();
                  if (chats.isEmpty) {
                    return const Center(child: Text('No conversations found.'));
                  }
                  return ListView.builder(
                    padding: const EdgeInsets.symmetric(horizontal: 8),
                    itemCount: chats.length,
                    itemBuilder: (context, index) {
                      final chat = chats[index];
                      return ListTile(
                        shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(12),
                        ),
                        selected: chat.id == widget.activeId,
                        selectedTileColor: const Color(0xFFECEDE8),
                        title: Text(
                          chat.title,
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                        ),
                        subtitle: chat.replying
                            ? const Text('Quacktion is typing…')
                            : null,
                        trailing: PopupMenuButton<String>(
                          tooltip: 'Conversation options',
                          onSelected: (action) {
                            if (action == 'rename') _rename(chat);
                            if (action == 'delete') _delete(chat);
                          },
                          itemBuilder: (_) => [
                            const PopupMenuItem(
                              value: 'rename',
                              child: Text('Rename'),
                            ),
                            if (widget.store.canDelete(chat))
                              const PopupMenuItem(
                                value: 'delete',
                                child: Text('Delete'),
                              ),
                          ],
                        ),
                        onTap: () => _select(() => widget.onOpen(chat)),
                      );
                    },
                  );
                },
              ),
            ),
            const Divider(height: 1),
            ListTile(
              leading: const Icon(Icons.speaker_outlined),
              title: const Text('Devices'),
              onTap: () => _select(widget.onDevices),
            ),
            ListTile(
              leading: const Icon(Icons.tune_rounded),
              title: const Text('Connections'),
              onTap: () => _select(widget.onConfiguration),
            ),
            ListTile(
              leading: const Icon(Icons.fact_check_outlined),
              title: const Text('Requests'),
              trailing: widget.pendingRequests > 0
                  ? Text('${widget.pendingRequests}')
                  : null,
              onTap: () => _select(widget.onRequests),
            ),
            ListTile(
              leading: const Icon(Icons.person_outline_rounded),
              title: const Text('Account settings'),
              onTap: () => _select(widget.onAccount),
            ),
          ],
        ),
      ),
    );
  }
}

// The route remains mounted during its exit animation, after showDialog resolves.
// Keep the controller alive for exactly as long as its TextField.
class _RenameConversationDialog extends StatefulWidget {
  const _RenameConversationDialog({required this.title});

  final String title;

  @override
  State<_RenameConversationDialog> createState() =>
      _RenameConversationDialogState();
}

class _RenameConversationDialogState extends State<_RenameConversationDialog> {
  late final TextEditingController _input = TextEditingController(
    text: widget.title,
  );

  @override
  void dispose() {
    _input.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: const Text('Rename conversation'),
    content: TextField(
      controller: _input,
      autofocus: true,
      maxLength: 200,
      decoration: const InputDecoration(labelText: 'Title'),
      onSubmitted: (value) => Navigator.pop(context, value.trim()),
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.pop(context),
        child: const Text('Cancel'),
      ),
      FilledButton(
        onPressed: () => Navigator.pop(context, _input.text.trim()),
        child: const Text('Save'),
      ),
    ],
  );
}
