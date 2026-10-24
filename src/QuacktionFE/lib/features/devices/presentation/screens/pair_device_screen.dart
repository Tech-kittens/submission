import 'dart:async';

import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/devices/application/device_setup_controller.dart';

class PairDeviceScreen extends StatefulWidget {
  const PairDeviceScreen({
    super.key,
    required this.controller,
    required this.onClose,
    required this.onConfigure,
  });

  final DeviceSetupController controller;
  final VoidCallback onClose;
  final VoidCallback onConfigure;

  @override
  State<PairDeviceScreen> createState() => _PairDeviceScreenState();
}

class _PairDeviceScreenState extends State<PairDeviceScreen> {
  Timer? _approvalPoll;

  @override
  void initState() {
    super.initState();
    widget.controller.addListener(_changed);
    _startApprovalPolling();
  }

  void _changed() {
    if (mounted) setState(() {});
    _startApprovalPolling();
  }

  void _startApprovalPolling() {
    if (widget.controller.stage == PairingStage.awaitingApproval &&
        _approvalPoll == null) {
      _approvalPoll = Timer.periodic(const Duration(seconds: 1), (_) {
        widget.controller.refreshStatus().catchError((_) {});
      });
    } else if (widget.controller.stage != PairingStage.awaitingApproval) {
      _approvalPoll?.cancel();
      _approvalPoll = null;
    }
  }

  @override
  void dispose() {
    _approvalPoll?.cancel();
    widget.controller.removeListener(_changed);
    widget.controller.cancelScan();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final controller = widget.controller;
    final scanning = controller.stage == PairingStage.scanning;
    final connecting =
        controller.stage == PairingStage.connecting || controller.busy;
    final pendingApproval = controller.stage == PairingStage.awaitingApproval;
    final paired = controller.approved;

    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, result) {
        if (!didPop) widget.onClose();
      },
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Connect your speaker'),
          leading: IconButton(
            tooltip: 'Back',
            onPressed: widget.onClose,
            icon: const Icon(Icons.arrow_back),
          ),
        ),
        body: SafeArea(
          child: Center(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(24),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 440),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Icon(
                      paired
                          ? Icons.check_circle_outline
                          : Icons.bluetooth_searching,
                      size: 68,
                      color: Theme.of(context).colorScheme.primary,
                    ),
                    const SizedBox(height: 20),
                    Text(
                      scanning
                          ? 'Looking for your speaker…'
                          : connecting
                          ? 'Connecting securely…'
                          : pendingApproval
                          ? 'Approve this phone'
                          : paired
                          ? 'Bluetooth connected'
                          : 'Find your speaker',
                      textAlign: TextAlign.center,
                      style: Theme.of(context).textTheme.headlineSmall,
                    ),
                    const SizedBox(height: 10),
                    Text(
                      scanning
                          ? 'Put your speaker in setup mode by holding its setup button for 3 seconds.'
                          : connecting
                          ? 'Keep the speaker nearby while the encrypted setup link is created.'
                          : pendingApproval
                          ? 'Briefly press the setup button on ${controller.selected?.name ?? 'your speaker'} to approve this connection.'
                          : paired
                          ? 'Your speaker is connected over Bluetooth and ready for Wi-Fi setup.'
                          : 'Scan for a Quacktion speaker in setup mode.',
                      textAlign: TextAlign.center,
                      style: const TextStyle(
                        color: Color(0xFF69685F),
                        height: 1.5,
                      ),
                    ),
                    const SizedBox(height: 24),
                    if (scanning || connecting) ...[
                      const Center(child: CircularProgressIndicator()),
                      const SizedBox(height: 20),
                    ],
                    if (controller.error != null)
                      Card(
                        color: Theme.of(context).colorScheme.errorContainer,
                        child: Padding(
                          padding: const EdgeInsets.all(14),
                          child: Text(controller.error!),
                        ),
                      ),
                    if (controller.nearby.isNotEmpty &&
                        !paired &&
                        !pendingApproval &&
                        !connecting)
                      for (final speaker in controller.nearby)
                        Card(
                          child: ListTile(
                            leading: const Icon(Icons.speaker_rounded),
                            title: Text(speaker.name),
                            subtitle: Text(
                              'Nearby · ${_signal(speaker.rssi)} signal',
                            ),
                            trailing: const Icon(Icons.chevron_right),
                            onTap: () => controller.pair(speaker),
                          ),
                        ),
                    if (pendingApproval) ...[
                      const SizedBox(height: 12),
                      OutlinedButton.icon(
                        onPressed: controller.busy ? null : controller.identify,
                        icon: const Icon(Icons.volume_up_outlined),
                        label: const Text('Flash and sound speaker'),
                      ),
                      const SizedBox(height: 8),
                      const Center(child: CircularProgressIndicator()),
                    ],
                    if (paired) ...[
                      Card(
                        child: ListTile(
                          leading: const Icon(Icons.wifi_outlined),
                          title: Text(
                            controller.wifiNetwork ?? 'Wi-Fi not configured',
                          ),
                          subtitle: Text(
                            controller.wifiState == 'connected'
                                ? 'Speaker joined this Wi-Fi network'
                                : 'Choose a Wi-Fi network to finish setup',
                          ),
                        ),
                      ),
                      const SizedBox(height: 16),
                      FilledButton.icon(
                        onPressed: widget.onConfigure,
                        icon: const Icon(Icons.wifi),
                        label: Text(
                          controller.wifiState == 'connected'
                              ? 'Change Wi-Fi'
                              : 'Configure Wi-Fi',
                        ),
                      ),
                      const SizedBox(height: 8),
                      TextButton(
                        onPressed: widget.onClose,
                        child: const Text('Done for now'),
                      ),
                    ] else if (!scanning &&
                        !connecting &&
                        !pendingApproval) ...[
                      const SizedBox(height: 16),
                      FilledButton.icon(
                        onPressed: controller.scan,
                        icon: const Icon(Icons.bluetooth_searching),
                        label: Text(
                          controller.nearby.isEmpty
                              ? 'Scan for speaker'
                              : 'Scan again',
                        ),
                      ),
                    ],
                    if (!paired) ...[
                      const SizedBox(height: 8),
                      TextButton(
                        onPressed: () async {
                          await controller.cancelConnection();
                          widget.onClose();
                        },
                        child: const Text('Cancel'),
                      ),
                    ],
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  String _signal(int rssi) => rssi > -55
      ? 'Strong'
      : rssi > -75
      ? 'Good'
      : 'Weak';
}
