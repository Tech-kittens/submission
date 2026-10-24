import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/integrations/models/mock_connection.dart';
import 'package:quacktion_frontend/features/integrations/presentation/widgets/connection_editor.dart';

class IntegrationsPage extends StatelessWidget {
  const IntegrationsPage({
    super.key,
    required this.connections,
    required this.onChanged,
  });
  final List<MockConnection> connections;
  final VoidCallback onChanged;

  Future<void> _configure(
    BuildContext context,
    MockConnection connection, {
    bool isNew = false,
  }) async {
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      showDragHandle: true,
      backgroundColor: const Color(0xFFFFFCF4),
      builder: (_) => ConnectionEditor(connection: connection),
    );
    if (saved != true || !context.mounted) return;
    if (isNew) connections.add(connection);
    onChanged();
  }

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          'Configuration',
          style: Theme.of(context).textTheme.headlineMedium
              ?.copyWith(fontWeight: FontWeight.w800, letterSpacing: -0.8),
        ),
        const SizedBox(height: 8),
        const Text(
          'Give your assistant the tools you use every day.',
          style: TextStyle(fontSize: 16, color: Color(0xFF69685F), height: 1.5),
        ),
        const SizedBox(height: 24),
        for (final connection in connections) ...[
          Container(
            padding: const EdgeInsets.all(20),
            decoration: BoxDecoration(
              color: Colors.white,
              borderRadius: BorderRadius.circular(22),
              border: Border.all(color: const Color(0xFFE8E4DA)),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Container(
                      padding: const EdgeInsets.all(12),
                      decoration: BoxDecoration(
                        color: const Color(0xFFF6F3EA),
                        borderRadius: BorderRadius.circular(14),
                      ),
                      child: Icon(
                        connection.icon,
                        color: const Color(0xFF243E36),
                      ),
                    ),
                    const SizedBox(width: 14),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            connection.name,
                            style: const TextStyle(
                              fontSize: 18,
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                          const SizedBox(height: 5),
                          Text(
                            connection.description,
                            style: const TextStyle(
                              color: Color(0xFF69685F),
                              height: 1.4,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 18),
                Row(
                  children: [
                    Icon(
                      connection.connected
                          ? Icons.check_circle_rounded
                          : Icons.circle_outlined,
                      size: 16,
                      color: connection.connected
                          ? const Color(0xFF356247)
                          : const Color(0xFF69685F),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        connection.connected
                            ? 'Connected · Demo'
                            : 'Not connected',
                        style: const TextStyle(
                          fontSize: 13,
                          color: Color(0xFF69685F),
                        ),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 14),
                OutlinedButton(
                  onPressed: () => _configure(context, connection),
                  child: Text(
                    connection.connected ? 'Manage connection' : 'Connect',
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: 12),
        ],
        const SizedBox(height: 8),
        FilledButton.icon(
          onPressed: () => _configure(
            context,
            MockConnection(
              'Custom MCP server',
              'Your own tools, connected to Quacktion.',
              Icons.dns_outlined,
              'custom',
            ),
            isNew: true,
          ),
          icon: const Icon(Icons.add_rounded),
          label: const Text('Add custom connection'),
        ),
      ],
    );
  }
}
