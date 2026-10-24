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

  Future<void> _pair() async {
    unawaited(widget.controller.scan());
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

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.controller,
    builder: (context, _) => DevicesPage(
      devicePaired: widget.controller.paired,
      deviceName: widget.controller.deviceName,
      wifiNetwork: widget.controller.wifiNetwork,
      wifiState: widget.controller.wifiState,
      backendState: widget.controller.backendState,
      onPair: _pair,
      onConfigureWifi: _configureWifi,
      onRemove: widget.controller.remove,
    ),
  );
}
