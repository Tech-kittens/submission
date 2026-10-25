import 'package:flutter/material.dart';
import 'package:quacktion_frontend/features/integrations/application/integrations_controller.dart';
import 'package:quacktion_frontend/features/integrations/models/mcp_models.dart';
import 'package:quacktion_frontend/features/integrations/presentation/widgets/connection_editor.dart';
import 'package:quacktion_frontend/shared/widgets/app_toast.dart';

class IntegrationsPage extends StatefulWidget {
  const IntegrationsPage({super.key, required this.controller});
  final IntegrationsController controller;
  @override
  State<IntegrationsPage> createState() => _IntegrationsPageState();
}

class _IntegrationsPageState extends State<IntegrationsPage> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) widget.controller.refresh(quiet: true);
    });
  }

  Future<void> _manage(McpConnection connection) => showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    showDragHandle: true,
    builder: (_) =>
        ConnectionEditor(controller: widget.controller, connection: connection),
  );

  Future<void> _disconnect(McpConnection connection) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Disconnect account?'),
        content: Text(
          'Your assistant will no longer be able to use ${connection.name}. You can connect it again later.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Disconnect'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    if (await widget.controller.disconnect(connection.id) && mounted) {
      AppToast.show(context, 'Account disconnected.');
    }
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.controller,
    builder: (context, _) {
      final controller = widget.controller;
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Connections',
                  style: Theme.of(context).textTheme.headlineMedium
                      ?.copyWith(fontWeight: FontWeight.w700),
                ),
              ),
              IconButton(
                tooltip: 'Refresh connections',
                onPressed: controller.loading ? null : controller.refresh,
                icon: const Icon(Icons.refresh),
              ),
            ],
          ),
          const SizedBox(height: 8),
          const Text(
            'Connect your accounts and choose the tools your assistant can use.',
            style: TextStyle(color: Color(0xFF69685F), height: 1.5),
          ),
          if (controller.loading)
            const Padding(
              padding: EdgeInsets.symmetric(vertical: 16),
              child: LinearProgressIndicator(),
            ),
          if (controller.connectingProvider != null)
            Card(
              child: Padding(
                padding: const EdgeInsets.all(18),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    const Text(
                      'Finish connecting in your browser',
                      style: TextStyle(fontWeight: FontWeight.w700),
                    ),
                    const SizedBox(height: 8),
                    const Text(
                      'Complete authorization, then return to Quacktion. The browser may show a confirmation response; you can close it once authorization is complete.',
                    ),
                    const SizedBox(height: 8),
                    Row(
                      children: [
                        TextButton(
                          onPressed: controller.poll,
                          child: const Text('Check connection'),
                        ),
                        TextButton(
                          onPressed: controller.cancelAuthorization,
                          child: const Text('Cancel'),
                        ),
                      ],
                    ),
                  ],
                ),
              ),
            ),
          const SizedBox(height: 20),
          if (controller.providersUnavailable ||
              controller.connectionsUnavailable) ...[
            const Text(
              'Connections are unavailable right now. Your voice conversations can still be used.',
            ),
            TextButton.icon(
              onPressed: controller.loading ? null : controller.refresh,
              icon: const Icon(Icons.refresh),
              label: const Text('Try again'),
            ),
          ],
          for (final connection in controller.connections) ...[
            Card(
              child: Padding(
                padding: const EdgeInsets.all(18),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        const Icon(Icons.account_circle_outlined),
                        const SizedBox(width: 12),
                        Expanded(
                          child: Text(
                            connection.name,
                            style: const TextStyle(
                              fontWeight: FontWeight.w700,
                              fontSize: 17,
                            ),
                          ),
                        ),
                        Text(connection.status),
                      ],
                    ),
                    if (!connection.identityVerified)
                      const Padding(
                        padding: EdgeInsets.only(top: 8),
                        child: Text(
                          'Account identity is not verified.',
                          style: TextStyle(
                            fontSize: 12,
                            color: Color(0xFF69685F),
                          ),
                        ),
                      ),
                    const SizedBox(height: 12),
                    Wrap(
                      spacing: 6,
                      runSpacing: 4,
                      children: [
                        for (final service in connection.services)
                          Chip(label: Text(serviceLabel(service))),
                      ],
                    ),
                    const SizedBox(height: 8),
                    Text(
                      '${connection.enabledTools.length} tools enabled',
                      style: const TextStyle(color: Color(0xFF69685F)),
                    ),
                    const SizedBox(height: 12),
                    Row(
                      children: [
                        Expanded(
                          child: OutlinedButton(
                            onPressed:
                                controller.busyConnections.contains(
                                  connection.id,
                                )
                                ? null
                                : () => _manage(connection),
                            child: const Text('Manage tools'),
                          ),
                        ),
                        const SizedBox(width: 8),
                        TextButton(
                          onPressed:
                              controller.busyConnections.contains(connection.id)
                              ? null
                              : () => _disconnect(connection),
                          child: const Text('Disconnect'),
                        ),
                      ],
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 12),
          ],
          if (!controller.loading &&
              controller.connections.isEmpty &&
              !controller.connectionsUnavailable)
            const Padding(
              padding: EdgeInsets.only(bottom: 20),
              child: Text('No accounts connected yet.'),
            ),
          for (final provider in controller.providers)
            Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: FilledButton.icon(
                onPressed:
                    !controller.loading && controller.connectingProvider == null
                    ? () => controller.connect(provider.id)
                    : null,
                icon: const Icon(Icons.add_link),
                label: Text(
                  controller.connections.any((c) => c.provider == provider.id)
                      ? 'Add another ${provider.label} account'
                      : 'Connect ${provider.label} account',
                ),
              ),
            ),
          if (!controller.loading &&
              !controller.providersUnavailable &&
              controller.providers.isEmpty)
            const Text('No account providers are available yet.'),
        ],
      );
    },
  );
}
