import 'package:flutter/material.dart';
import 'package:quacktion_frontend/features/integrations/application/integrations_controller.dart';
import 'package:quacktion_frontend/features/integrations/models/mcp_models.dart';
import 'package:quacktion_frontend/features/integrations/presentation/widgets/request_review.dart';

class RequestsPage extends StatefulWidget {
  const RequestsPage({super.key, required this.controller});
  final IntegrationsController controller;
  @override
  State<RequestsPage> createState() => _RequestsPageState();
}

class _RequestsPageState extends State<RequestsPage> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      widget.controller.refreshTasks(quiet: true);
      widget.controller.refreshConnections(quiet: true);
    });
  }

  Future<void> _review(McpTask task) => showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    showDragHandle: true,
    builder: (_) => RequestReview(controller: widget.controller, task: task),
  );
  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.controller,
    builder: (context, _) {
      final controller = widget.controller;
      final ordered = [...controller.tasks]
        ..sort((a, b) {
          if (a.pending != b.pending) return a.pending ? -1 : 1;
          return b.createdAt.compareTo(a.createdAt);
        });
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Requests',
                  style: Theme.of(context).textTheme.headlineMedium
                      ?.copyWith(fontWeight: FontWeight.w700),
                ),
              ),
              IconButton(
                tooltip: 'Refresh requests',
                onPressed: controller.refreshTasks,
                icon: const Icon(Icons.refresh),
              ),
            ],
          ),
          const SizedBox(height: 8),
          const Text(
            'Review what your assistant wants to do. Account access and protected operations require separate approvals.',
            style: TextStyle(color: Color(0xFF69685F), height: 1.5),
          ),
          const SizedBox(height: 20),
          if (controller.tasksUnavailable) ...[
            const Text('Requests could not be loaded.'),
            TextButton(
              onPressed: controller.refreshTasks,
              child: const Text('Try again'),
            ),
          ] else if (ordered.isEmpty)
            const Text('No requests yet.'),
          for (final task in ordered)
            Card(
              child: ListTile(
                leading: Icon(
                  task.pending ? Icons.fact_check_outlined : Icons.task_alt,
                ),
                title: Text(
                  task.summary,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                ),
                subtitle: Text(
                  task.expired(controller.now()) && task.pending
                      ? 'Approval expired'
                      : task.statusLabel,
                ),
                trailing: const Icon(Icons.chevron_right),
                onTap: () => _review(task),
              ),
            ),
        ],
      );
    },
  );
}
