import 'dart:async';

import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/devices/application/device_setup_controller.dart';
import 'package:quacktion_frontend/features/devices/presentation/screens/devices_page.dart';
import 'package:quacktion_frontend/features/devices/presentation/screens/pair_device_screen.dart';
import 'package:quacktion_frontend/features/devices/presentation/widgets/wifi_setup_sheet.dart';

/// Coordinates the device screens; setup state lives in the feature controller.
class DeviceSetupScreen extends StatefulWidget {
  const DeviceSetupScreen({
    super.key,
    required this.controller,
    required this.onMessage,
  });
  final DeviceSetupController controller;
  final ValueChanged<String> onMessage;
  @override
  State<DeviceSetupScreen> createState() => _DeviceSetupScreenState();
}

class _DeviceSetupScreenState extends State<DeviceSetupScreen> {
  Future<void> _configureWifi() async {
    final network = await showModalBottomSheet<String>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      showDragHandle: true,
      backgroundColor: const Color(0xFFFFFCF4),
      builder: (_) => WifiSetupSheet(controller: widget.controller),
    );
    if (mounted && network != null) {
      widget.onMessage('Speaker connected to $network over Wi-Fi.');
    }
  }

  Future<void> _pair({bool saved = false}) async {
    unawaited(
      saved
          ? widget.controller.configureSavedSpeaker()
          : widget.controller.scan(),
    );
    final configure = await Navigator.of(context).push<bool>(
      MaterialPageRoute(
        builder: (context) => PairDeviceScreen(
          controller: widget.controller,
          onClose: () {
            if (!widget.controller.approved) {
              unawaited(widget.controller.cancelConnection());
            }
            Navigator.pop(context, false);
          },
          onConfigure: () => Navigator.pop(context, true),
        ),
      ),
    );
    if (mounted && configure == true) await _configureWifi();
  }

  Future<void> _forgetWifi() async {
    final confirm = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Disconnect Wi-Fi?'),
        content: const Text(
          'This disconnects the speaker and removes its saved Wi-Fi name and password.',
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
    if (!mounted || confirm != true) return;
    await widget.controller.forgetWifi();
    if (!mounted) return;
    if (widget.controller.error == null) {
      widget.onMessage('Wi-Fi disconnected. Saved network removed.');
    }
  }

  Future<void> _unpair() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Forget saved speaker?'),
        content: const Text(
          'This removes the saved speaker from this phone. Its Wi-Fi settings and backend account registration remain unchanged.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Forget'),
          ),
        ],
      ),
    );
    if (!mounted || confirmed != true) return;
    final unpaired = await widget.controller.forgetSavedSpeaker();
    if (!mounted) return;
    if (unpaired) widget.onMessage('Speaker removed from this phone.');
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.controller,
    builder: (context, _) => DevicesPage(
      devicePaired: widget.controller.paired,
      deviceName:
          widget.controller.deviceName ?? widget.controller.savedSpeaker?.name,
      wifiNetwork: widget.controller.wifiNetwork,
      wifiState: widget.controller.wifiState,
      backendState: widget.controller.backendState,
      onPair: () => _pair(),
      onReconnect: () => unawaited(_pair(saved: true)),
      hasSavedSpeaker: widget.controller.savedSpeaker != null,
      busy: widget.controller.configurationBusy,
      onFinishConfiguration: widget.controller.readyToComplete
          ? () => unawaited(widget.controller.finishConfiguration())
          : null,
      onCancelSetup: () => unawaited(widget.controller.cancelSetup()),
      onRetryBackend: () => unawaited(widget.controller.retryBackendLink()),
      onConfigureWifi: _configureWifi,
      onForgetWifi: () => unawaited(_forgetWifi()),
      onUnpair: () => unawaited(_unpair()),
    ),
  );
}
