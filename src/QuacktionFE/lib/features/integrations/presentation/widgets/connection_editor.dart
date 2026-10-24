import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/integrations/models/mock_connection.dart';

class ConnectionEditor extends StatefulWidget {
  const ConnectionEditor({super.key, required this.connection});
  final MockConnection connection;
  @override
  State<ConnectionEditor> createState() => _ConnectionEditorState();
}

class _ConnectionEditorState extends State<ConnectionEditor> {
  final _form = GlobalKey<FormState>();
  late final _name = TextEditingController(text: widget.connection.name);
  late final _endpoint = TextEditingController(
    text:
        widget.connection.endpoint ??
        'https://mcp.example.com/${widget.connection.slug}',
  );
  late bool _allowActions = widget.connection.allowActions;
  bool _busy = false;

  @override
  void dispose() {
    _name.dispose();
    _endpoint.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    if (_busy || !_form.currentState!.validate()) return;
    setState(() => _busy = true);
    await Future<void>.delayed(const Duration(milliseconds: 900));
    if (!mounted) return;
    widget.connection
      ..name = _name.text.trim()
      ..endpoint = _endpoint.text.trim()
      ..allowActions = _allowActions
      ..connected = true;
    Navigator.pop(context, true);
  }

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      padding: EdgeInsets.fromLTRB(
        24,
        8,
        24,
        24 +
            MediaQuery.viewInsetsOf(context).bottom +
            MediaQuery.paddingOf(context).bottom,
      ),
      child: Form(
        key: _form,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              widget.connection.connected
                  ? 'Manage connection'
                  : 'Connect a tool',
              style: Theme.of(context).textTheme.headlineSmall
                  ?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 10),
            const Text(
              'Mock setup only. No sign-in, server requests, or real tool access.',
              style: TextStyle(color: Color(0xFF69685F), height: 1.5),
            ),
            const SizedBox(height: 24),
            TextFormField(
              controller: _name,
              enabled: !_busy,
              maxLength: 64,
              decoration: const InputDecoration(labelText: 'Connection name'),
              validator: (value) => value == null || value.trim().isEmpty
                  ? 'Enter a connection name.'
                  : null,
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _endpoint,
              enabled: !_busy,
              autocorrect: false,
              keyboardType: TextInputType.url,
              decoration: const InputDecoration(
                labelText: 'Demo server URL',
                helperText: 'Use a sample URL; no credentials are needed.',
              ),
              validator: (value) {
                final uri = Uri.tryParse(value?.trim() ?? '');
                if (uri == null ||
                    !['https', 'http'].contains(uri.scheme) ||
                    uri.host.isEmpty) {
                  return 'Enter an HTTP or HTTPS URL.';
                }
                return null;
              },
            ),
            const SizedBox(height: 16),
            SwitchListTile.adaptive(
              contentPadding: EdgeInsets.zero,
              title: const Text('Allow tool actions'),
              subtitle: const Text(
                'Mock preference for creating or updating items.',
              ),
              value: _allowActions,
              onChanged: _busy
                  ? null
                  : (value) => setState(() => _allowActions = value),
            ),
            const SizedBox(height: 20),
            FilledButton(
              onPressed: _busy ? null : _save,
              child: Text(
                _busy
                    ? 'Simulating connection…'
                    : widget.connection.connected
                    ? 'Save changes'
                    : 'Connect demo',
              ),
            ),
            if (widget.connection.connected) ...[
              const SizedBox(height: 8),
              TextButton(
                onPressed: _busy
                    ? null
                    : () {
                        widget.connection.connected = false;
                        Navigator.pop(context, true);
                      },
                child: const Text('Disconnect'),
              ),
            ],
            const SizedBox(height: 8),
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: const Text('Cancel'),
            ),
          ],
        ),
      ),
    );
  }
}
