import 'package:flutter/material.dart';
import 'package:quacktion_frontend/features/integrations/application/integrations_controller.dart';
import 'package:quacktion_frontend/features/integrations/models/mcp_models.dart';
import 'package:quacktion_frontend/shared/widgets/app_toast.dart';

class ConnectionEditor extends StatefulWidget {
  const ConnectionEditor({
    super.key,
    required this.controller,
    required this.connection,
  });
  final IntegrationsController controller;
  final McpConnection connection;
  @override
  State<ConnectionEditor> createState() => _ConnectionEditorState();
}

class _ConnectionEditorState extends State<ConnectionEditor> {
  List<McpTool> _tools = [];
  final _selected = <String>{};
  bool _loading = true, _failed = false;
  String _health = 'Checking connection…';
  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _failed = false;
    });
    await Future.wait([_discover(), _checkHealth()]);
    if (mounted) setState(() => _loading = false);
  }

  Future<void> _discover() async {
    try {
      final tools = await widget.controller.tools(widget.connection.id);
      if (!mounted) return;
      final current =
          widget.controller.connections
              .where((c) => c.id == widget.connection.id)
              .firstOrNull ??
          widget.connection;
      setState(() {
        _tools = tools;
        _selected
          ..clear()
          ..addAll(
            current.enabledTools.where(
              tools.map((t) => t.key).toSet().contains,
            ),
          );
      });
    } catch (failure) {
      if (!mounted) return;
      setState(() => _failed = true);
      AppToast.show(context, mcpErrorMessage(failure), isError: true);
      await widget.controller.refreshConnections(quiet: true);
    }
  }

  Future<void> _checkHealth() async {
    try {
      final health = await widget.controller.api.mcpHealth(
        widget.connection.id,
      );
      if (mounted) {
        setState(
          () => _health = health['status'] == 'available'
              ? 'Connection available'
              : 'Connection needs attention',
        );
      }
    } catch (_) {
      if (mounted) setState(() => _health = 'Connection health unavailable');
    }
  }

  Future<void> _save() async {
    if (await widget.controller.saveTools(
      widget.connection.id,
      _selected.toList()..sort(),
      _tools,
    )) {
      if (!mounted) return;
      AppToast.show(context, 'Tool permissions updated.');
      Navigator.pop(context);
    } else if (mounted) {
      // A changed discovery response must be reviewed again before another save.
      await _load();
    }
  }

  Widget _group(String service, bool busy) {
    final tools = _tools.where((tool) => tool.serverId == service).toList();
    final keys = tools.map((tool) => tool.key).toSet();
    final selected = keys.where(_selected.contains).length;
    return ExpansionTile(
      key: ValueKey('tool-group-$service'),
      initiallyExpanded: false,
      tilePadding: EdgeInsets.zero,
      controlAffinity: ListTileControlAffinity.leading,
      trailing: Checkbox(
        key: ValueKey('group-select-$service'),
        tristate: true,
        value: selected == 0
            ? false
            : selected == tools.length
            ? true
            : null,
        semanticLabel: 'Allow all ${serviceLabel(service)} tools',
        onChanged: busy
            ? null
            : (_) => setState(() {
                if (selected == tools.length) {
                  _selected.removeAll(keys);
                } else {
                  _selected.addAll(keys);
                }
              }),
      ),
      title: Text(serviceLabel(service)),
      subtitle: Text('$selected of ${tools.length} selected'),
      childrenPadding: const EdgeInsets.only(left: 8),
      children: [for (final tool in tools) _tool(tool, busy)],
    );
  }

  Widget _tool(McpTool tool, bool busy) => ExpansionTile(
    key: ValueKey(tool.key),
    tilePadding: EdgeInsets.zero,
    controlAffinity: ListTileControlAffinity.leading,
    trailing: Checkbox(
      key: ValueKey('tool-select-${tool.key}'),
      value: _selected.contains(tool.key),
      semanticLabel: 'Allow ${_toolLabel(tool)}',
      onChanged: busy
          ? null
          : (value) => setState(() {
              if (value == true) {
                _selected.add(tool.key);
              } else {
                _selected.remove(tool.key);
              }
            }),
    ),
    title: Text(_toolLabel(tool)),
    childrenPadding: const EdgeInsets.fromLTRB(12, 0, 12, 12),
    expandedCrossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(serviceLabel(tool.serverId)),
      const SizedBox(height: 8),
      if (tool.description.isNotEmpty) Text(tool.description),
      const SizedBox(height: 8),
      Text(
        tool.destructive
            ? 'May change or delete data. Approval required.'
            : tool.readOnly
            ? 'Read access'
            : 'Approval required',
      ),
      const SizedBox(height: 8),
      SelectableText(tool.name, style: Theme.of(context).textTheme.bodySmall),
    ],
  );

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.controller,
    builder: (context, _) {
      final busy =
          _loading ||
          widget.controller.busyConnections.contains(widget.connection.id);
      return SingleChildScrollView(
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
              'Allowed tools',
              style: Theme.of(context).textTheme.headlineSmall
                  ?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 8),
            Text('${widget.connection.name} · $_health'),
            const SizedBox(height: 12),
            const Text(
              'Choose what your assistant can use. Protected actions still need approval.',
              style: TextStyle(color: Color(0xFF69685F), height: 1.4),
            ),
            const SizedBox(height: 16),
            if (_loading) const Center(child: CircularProgressIndicator()),
            if (_failed)
              TextButton.icon(
                onPressed: busy ? null : _load,
                icon: const Icon(Icons.refresh),
                label: const Text('Retry tool discovery'),
              ),
            if (!_loading && !_failed && _tools.isEmpty)
              const Text('No tools are available for this connection yet.'),
            if (_tools.isNotEmpty && !_failed) ...[
              Text('${_selected.length} of ${_tools.length} selected'),
              Wrap(
                spacing: 8,
                children: [
                  TextButton(
                    onPressed: busy || _selected.length == _tools.length
                        ? null
                        : () => setState(
                            () => _selected.addAll(_tools.map((t) => t.key)),
                          ),
                    child: const Text('Select all'),
                  ),
                  TextButton(
                    onPressed: busy || _selected.isEmpty
                        ? null
                        : () => setState(_selected.clear),
                    child: const Text('Deselect all'),
                  ),
                ],
              ),
            ],
            for (final service in _tools.map((tool) => tool.serverId).toSet())
              _group(service, busy),
            const SizedBox(height: 16),
            FilledButton(
              onPressed: busy || _failed ? null : _save,
              child: Text(
                widget.controller.busyConnections.contains(widget.connection.id)
                    ? 'Saving…'
                    : 'Save tool permissions',
              ),
            ),
            TextButton(
              onPressed: busy ? null : () => Navigator.pop(context),
              child: const Text('Close'),
            ),
          ],
        ),
      );
    },
  );
}

String _toolLabel(McpTool tool) {
  var name = tool.name;
  final prefix = '${tool.serverId.split(':').last}_';
  if (name.startsWith(prefix)) name = name.substring(prefix.length);
  name = name.replaceAll('_', ' ').replaceAll('-', ' ').trim();
  if (name.isEmpty) return tool.name;
  return name[0].toUpperCase() + name.substring(1);
}
