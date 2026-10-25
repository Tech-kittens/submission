import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:quacktion_frontend/features/integrations/application/integrations_controller.dart';
import 'package:quacktion_frontend/features/integrations/models/mcp_models.dart';
import 'package:quacktion_frontend/shared/widgets/app_toast.dart';

class RequestReview extends StatefulWidget {
  const RequestReview({
    super.key,
    required this.controller,
    required this.task,
    this.inline = false,
  });
  final bool inline;
  final IntegrationsController controller;
  final McpTask task;
  @override
  State<RequestReview> createState() => _RequestReviewState();
}

class _RequestReviewState extends State<RequestReview> {
  late McpTask _reviewed = widget.task;
  String? _connectionId;
  final _services = <String>{};
  bool _bound = false, _unavailable = false;

  @override
  void didUpdateWidget(covariant RequestReview oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.task.digest != _reviewed.digest ||
        widget.task.status != _reviewed.status) {
      _reviewed = widget.task;
      _bound = false;
      _services.clear();
    }
  }

  Future<void> _bind() async {
    final selected = widget.controller.connections
        .where((c) => c.id == _connectionId)
        .firstOrNull;
    if (selected == null) return;
    final result = await widget.controller.bindAccount(
      _reviewed,
      selected,
      _services.toList()..sort(),
    );
    if (!mounted) return;
    if (result != null) {
      setState(() {
        _reviewed = result;
        _bound = true;
      });
    }
  }

  Future<void> _decide(bool approve) async {
    final success = await widget.controller.decide(_reviewed, approve: approve);
    if (!mounted) return;
    if (success) {
      AppToast.show(
        context,
        approve ? 'Request approved.' : 'Request declined.',
      );
      if (widget.inline) {
        setState(
          () => _reviewed = widget.controller.task(_reviewed.id) ?? _reviewed,
        );
      } else {
        Navigator.pop(context);
      }
    } else {
      final latest = widget.controller.task(_reviewed.id);
      setState(() {
        if (latest != null) _reviewed = latest;
        _unavailable = latest == null && !widget.controller.tasksUnavailable;
        if (_reviewed.requiresAccount) _bound = false;
      });
    }
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.controller,
    builder: (context, _) {
      final controller = widget.controller;
      final task = _reviewed;
      final busy = controller.busyTasks.contains(task.id);
      final eligible =
          !_unavailable && task.pending && !task.expired(controller.now());
      final accounts = controller.connections
          .where((c) => c.connected && c.services.isNotEmpty)
          .toList();
      final selected = accounts.where((c) => c.id == _connectionId).firstOrNull;
      final boundAccount = accounts
          .where((c) => c.id == task.connectionId)
          .firstOrNull;
      final validBinding =
          boundAccount != null &&
          task.selectedServices.isNotEmpty &&
          task.selectedServices.every(boundAccount.services.contains);
      return SingleChildScrollView(
        primary: false,
        physics: widget.inline ? const NeverScrollableScrollPhysics() : null,
        padding: EdgeInsets.fromLTRB(
          24,
          8,
          24,
          24 + MediaQuery.paddingOf(context).bottom,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              task.requiresAccount
                  ? 'Review account access'
                  : task.isAnnouncement
                  ? 'Review spoken announcement'
                  : task.actionType == 'mcp.invoke'
                  ? 'Review external operation'
                  : 'Review request',
              style: Theme.of(context).textTheme.headlineSmall
                  ?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 16),
            SelectableText(
              task.summary,
              style: const TextStyle(fontSize: 17, height: 1.5),
            ),
            const SizedBox(height: 12),
            Text(
              _unavailable
                  ? 'This request is no longer available.'
                  : task.expired(controller.now()) && task.pending
                  ? 'Approval expired. This request cannot be approved.'
                  : task.statusLabel,
            ),
            if (task.expiresAt != null &&
                task.pending &&
                !task.expired(controller.now()))
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text(
                  'Approval expires ${TimeOfDay.fromDateTime(task.expiresAt!.toLocal()).format(context)}',
                  style: const TextStyle(color: Color(0xFF69685F)),
                ),
              ),
            if (task.requiresAccount && eligible && !_bound) ...[
              const SizedBox(height: 20),
              const Text(
                'Choose a connected account',
                style: TextStyle(fontWeight: FontWeight.w700),
              ),
              if (accounts.isEmpty) ...[
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 12),
                  child: Text('Connect an account from Connections first.'),
                ),
                TextButton(
                  onPressed: busy
                      ? null
                      : () => controller.refreshConnections(),
                  child: const Text('Refresh connected accounts'),
                ),
              ],
              for (final connection in accounts)
                Card(
                  child: ListTile(
                    title: Text(connection.name),
                    subtitle: Text(
                      connection.services.map(serviceLabel).join(', '),
                    ),
                    trailing: Icon(
                      _connectionId == connection.id
                          ? Icons.radio_button_checked
                          : Icons.radio_button_off,
                    ),
                    onTap: busy
                        ? null
                        : () => setState(() {
                            _connectionId = connection.id;
                            _services.clear();
                          }),
                  ),
                ),
              if (selected != null) ...[
                const SizedBox(height: 12),
                const Text(
                  'Allow this request to use:',
                  style: TextStyle(fontWeight: FontWeight.w700),
                ),
                for (final service in selected.services)
                  CheckboxListTile(
                    contentPadding: EdgeInsets.zero,
                    title: Text(serviceLabel(service)),
                    value: _services.contains(service),
                    onChanged: busy
                        ? null
                        : (checked) => setState(() {
                            if (checked == true) {
                              _services.add(service);
                            } else {
                              _services.remove(service);
                            }
                          }),
                  ),
              ],
              const SizedBox(height: 12),
              FilledButton(
                onPressed: busy || selected == null || _services.isEmpty
                    ? null
                    : _bind,
                child: Text(busy ? 'Updating request…' : 'Review access'),
              ),
            ],
            if (task.requiresAccount && _bound) ...[
              const SizedBox(height: 20),
              Text(
                'Selected account: ${boundAccount?.name ?? 'Unavailable'}',
                style: const TextStyle(fontWeight: FontWeight.w700),
              ),
              for (final service in task.selectedServices)
                Text(serviceLabel(service)),
              const SizedBox(height: 12),
              const Text(
                'Allow this request to access these services. Sending messages or changing data will require a separate approval.',
              ),
              TextButton(
                onPressed: busy
                    ? null
                    : () => setState(() {
                        _bound = false;
                        _services.clear();
                      }),
                child: const Text('Change account or services'),
              ),
            ],
            if (!task.requiresAccount) ...[
              const SizedBox(height: 16),
              if (task.isAnnouncement)
                const Text(
                  'Approving allows the assistant to read this result aloud to everyone in the meeting. Review the exact content below.',
                ),
              if (task.actionType == 'mcp.invoke')
                const Text(
                  'Approval applies only to the exact operation shown below. No other changes are authorized.',
                ),
              const SizedBox(height: 12),
              ConstrainedBox(
                constraints: const BoxConstraints(maxHeight: 320),
                child: SingleChildScrollView(
                  child: SelectableText(
                    const JsonEncoder.withIndent('  ').convert(task.parameters),
                    style: const TextStyle(fontSize: 13, height: 1.5),
                  ),
                ),
              ),
            ],
            if (eligible && (!task.requiresAccount || _bound)) ...[
              const SizedBox(height: 20),
              FilledButton(
                onPressed: busy || (task.requiresAccount && !validBinding)
                    ? null
                    : () => _decide(true),
                child: Text(
                  busy
                      ? 'Submitting…'
                      : task.requiresAccount
                      ? 'Approve access'
                      : task.isAnnouncement
                      ? 'Allow spoken announcement'
                      : 'Approve this operation',
                ),
              ),
            ],
            if (eligible)
              TextButton(
                onPressed: busy ? null : () => _decide(false),
                child: const Text('Decline request'),
              ),
            if (!widget.inline) ...[
              const SizedBox(height: 8),
              TextButton(
                onPressed: busy ? null : () => Navigator.pop(context),
                child: const Text('Close'),
              ),
            ],
          ],
        ),
      );
    },
  );
}
