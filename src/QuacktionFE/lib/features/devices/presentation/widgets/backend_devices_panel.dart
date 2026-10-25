import 'package:flutter/material.dart';
import 'package:quacktion_frontend/features/devices/application/device_controller.dart';
import 'package:quacktion_frontend/features/devices/application/device_setup_controller.dart';
import 'package:quacktion_frontend/features/devices/data/saved_speaker_store.dart';

class BackendDevicesPanel extends StatefulWidget {
  const BackendDevicesPanel({
    super.key,
    required this.controller,
    required this.setupController,
  });
  final DeviceController controller;
  final DeviceSetupController setupController;

  @override
  State<BackendDevicesPanel> createState() => _BackendDevicesPanelState();
}

class _BackendDevicesPanelState extends State<BackendDevicesPanel> {
  @override
  void initState() {
    super.initState();
    widget.setupController.addListener(_changed);
    widget.controller.refresh();
  }

  void _changed() {
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    widget.setupController.removeListener(_changed);
    super.dispose();
  }

  Future<void> _rename(String id, String current) async {
    final input = TextEditingController(text: current);
    final name = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Rename speaker'),
        content: TextField(
          controller: input,
          autofocus: true,
          maxLength: 128,
          decoration: const InputDecoration(labelText: 'Name'),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, input.text.trim()),
            child: const Text('Save'),
          ),
        ],
      ),
    );
    input.dispose();
    if (name == null || name.isEmpty || !mounted) return;
    final matches = widget.controller.devices.where((item) => item.id == id);
    if (matches.isNotEmpty) await widget.controller.rename(matches.first, name);
  }

  Future<void> _remove(SpeakerDevice device) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Remove speaker?'),
        content: const Text(
          'This removes the speaker from your account and unpairs it from the backend.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Remove'),
          ),
        ],
      ),
    );
    if (confirmed == true && mounted) await widget.controller.remove(device);
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.controller,
    builder: (context, _) => Padding(
      padding: const EdgeInsets.only(top: 24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Your speakers',
                  style: Theme.of(context).textTheme.titleLarge
                      ?.copyWith(fontWeight: FontWeight.w700),
                ),
              ),
              IconButton(
                tooltip: 'Refresh speakers',
                onPressed: widget.controller.loading
                    ? null
                    : widget.controller.refresh,
                icon: const Icon(Icons.refresh),
              ),
            ],
          ),
          if (widget.controller.loading && widget.controller.devices.isEmpty)
            const LinearProgressIndicator(),
          if (!widget.controller.loading &&
              widget.controller.devices.isEmpty &&
              widget.setupController.savedSpeaker == null)
            const Padding(
              padding: EdgeInsets.symmetric(vertical: 8),
              child: Text(
                'No speakers are saved on this phone or linked to your account yet.',
              ),
            ),
          if (widget.setupController.savedSpeaker case final local?)
            _localRow(local),
          for (final device in widget.controller.devices)
            if (widget.setupController.savedSpeaker?.identifier !=
                device.identifier)
              Card(
                child: ListTile(
                  leading: const Icon(Icons.speaker_rounded),
                  title: Text(device.name),
                  subtitle: Text(
                    '${device.statusLabel}${device.firmwareVersion.isEmpty ? '' : ' · Firmware ${device.firmwareVersion}'} · Account linked',
                  ),
                  trailing: _menu(device),
                ),
              ),
        ],
      ),
    ),
  );

  Widget _localRow(SavedSpeaker local) {
    final setup = widget.setupController;
    final matches = widget.controller.devices.where(
      (device) => device.identifier == local.identifier,
    );
    final backendDevice = matches.isEmpty ? null : matches.first;
    final accountLinked =
        backendDevice != null || local.backendDeviceId != null;
    final details = <String>[
      accountLinked ? 'Account linked' : 'Not linked to an account',
      setup.paired ? 'Connected over Bluetooth' : 'Saved on this phone',
      if (backendDevice != null) backendDevice.statusLabel,
      if (local.firmwareVersion.isNotEmpty) 'Firmware ${local.firmwareVersion}',
    ];
    return Card(
      child: ListTile(
        leading: const Icon(Icons.speaker_rounded),
        title: Text(local.name),
        subtitle: Text(details.join(' · ')),
        trailing: accountLinked && backendDevice != null
            ? _menu(backendDevice)
            : const Icon(Icons.bluetooth_connected),
      ),
    );
  }

  Widget _menu(SpeakerDevice device) => PopupMenuButton<String>(
    enabled: !widget.controller.busy,
    onSelected: (action) async {
      if (action == 'rename') await _rename(device.id, device.name);
      if (action == 'remove') await _remove(device);
    },
    itemBuilder: (_) => const [
      PopupMenuItem(value: 'rename', child: Text('Rename')),
      PopupMenuItem(value: 'remove', child: Text('Remove from account')),
    ],
  );
}
