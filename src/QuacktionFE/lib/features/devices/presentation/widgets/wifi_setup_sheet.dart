import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/devices/application/device_setup_controller.dart';
import 'package:quacktion_frontend/features/devices/data/speaker_provisioning.dart';

class WifiSetupSheet extends StatefulWidget {
  const WifiSetupSheet({super.key, required this.controller});
  final DeviceSetupController controller;

  @override
  State<WifiSetupSheet> createState() => _WifiSetupSheetState();
}

class _WifiSetupSheetState extends State<WifiSetupSheet> {
  final _password = TextEditingController();
  final _hiddenSsid = TextEditingController();
  WifiNetwork? _selected;
  bool _showHidden = false;
  bool _showManualSetup = false;
  bool _obscure = true;

  @override
  void initState() {
    super.initState();
    if (!widget.controller.dppSupported) {
      widget.controller.scanWifi();
    }
  }

  Future<void> _showManual() async {
    setState(() => _showManualSetup = true);
    if (widget.controller.networks.isEmpty) {
      await widget.controller.scanWifi();
    }
  }

  @override
  void dispose() {
    _password.dispose();
    _hiddenSsid.dispose();
    super.dispose();
  }

  Future<void> _configure() async {
    final ssid = _showHidden ? _hiddenSsid.text.trim() : _selected?.ssid;
    if (ssid == null ||
        ssid.isEmpty ||
        (!_showHidden && _selected?.supported != true)) {
      return;
    }
    debugPrint(
      {
        'ssid': ssid,
        'password': _selected?.auth == 'open' && !_showHidden
            ? ''
            : _password.text,
        'hidden': _showHidden,
      }.toString(),
    );
    await widget.controller.configureWifi(
      ssid,
      _selected?.auth == 'open' && !_showHidden ? '' : _password.text,
      hidden: _showHidden,
    );
    if (!mounted) return;
    if (widget.controller.wifiState == 'connected') {
      Navigator.pop(context, ssid);
    }
  }

  Future<void> _configureDpp() async {
    await widget.controller.configureWifiWithDpp();
    if (!mounted) return;
    if (widget.controller.wifiState == 'connected') {
      Navigator.pop(context, widget.controller.wifiNetwork);
    }
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.controller,
    builder: (context, _) {
      final controller = widget.controller;
      final busy = controller.busy;
      final selected = _selected;
      final canSubmit = _showHidden
          ? _hiddenSsid.text.trim().isNotEmpty
          : selected?.supported == true;
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
            const Icon(Icons.wifi_rounded, size: 44, color: Color(0xFF243E36)),
            const SizedBox(height: 12),
            Text(
              'Connect speaker to Wi-Fi',
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.headlineSmall
                  ?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 8),
            Text(
              controller.dppSupported
                  ? 'Let Android share your current Wi-Fi network with the speaker securely. No QR code or password entry is needed.'
                  : 'The speaker connects directly to a 2.4 GHz network. Its Wi-Fi password is sent over the encrypted Bluetooth setup link.',
              textAlign: TextAlign.center,
              style: TextStyle(color: Color(0xFF69685F), height: 1.45),
            ),
            if (controller.dppSupported) ...[
              const SizedBox(height: 22),
              FilledButton.icon(
                onPressed: busy ? null : _configureDpp,
                icon: const Icon(Icons.phonelink_setup),
                label: const Text('Share Wi-Fi with speaker'),
              ),
              TextButton.icon(
                onPressed: busy ? null : _showManual,
                icon: const Icon(Icons.password_rounded),
                label: const Text('Set up Wi-Fi manually'),
              ),
              if (!_showManualSetup) ...[
                if (controller.error != null) ...[
                  const SizedBox(height: 8),
                  Text(
                    controller.error!,
                    textAlign: TextAlign.center,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                ],
                if (busy) ...[
                  const SizedBox(height: 12),
                  const Center(child: CircularProgressIndicator()),
                  const SizedBox(height: 8),
                  const Text(
                    'Waiting for Android to share Wi-Fi with the speaker…',
                    textAlign: TextAlign.center,
                  ),
                ],
                const SizedBox(height: 8),
                TextButton(
                  onPressed: busy ? null : () => Navigator.pop(context),
                  child: const Text('Cancel'),
                ),
              ],
            ],
            if (!controller.dppSupported || _showManualSetup) ...[
              if (controller.dppSupported) const SizedBox(height: 8),
              if (!controller.dppSupported) const SizedBox(height: 18),
              Row(
                children: [
                  Expanded(
                    child: Text(
                      '2.4 GHz networks nearby',
                      style: Theme.of(context).textTheme.titleMedium,
                    ),
                  ),
                  IconButton(
                    tooltip: 'Rescan networks',
                    onPressed: busy ? null : controller.scanWifi,
                    icon: const Icon(Icons.refresh),
                  ),
                ],
              ),
              if (controller.networks.isEmpty &&
                  !busy &&
                  controller.error == null)
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 16),
                  child: Text(
                    'No networks found. Try scanning again or enter a hidden network below.',
                    textAlign: TextAlign.center,
                  ),
                ),
              for (final network in controller.networks)
                Card(
                  elevation: 0,
                  color: selected?.ssid == network.ssid
                      ? const Color(0xFFFFE69B)
                      : Colors.white,
                  child: ListTile(
                    enabled: !busy && network.supported,
                    leading: Icon(
                      network.auth == 'open' ? Icons.wifi : Icons.wifi_lock,
                    ),
                    title: Text(network.ssid),
                    subtitle: Text(
                      network.supported
                          ? '${network.auth} · ${_signal(network.rssi)} signal'
                          : '${network.auth} · not supported',
                    ),
                    trailing: selected?.ssid == network.ssid
                        ? const Icon(Icons.radio_button_checked)
                        : const Icon(Icons.radio_button_off),
                    onTap: network.supported
                        ? () => setState(() {
                            _selected = network;
                            _showHidden = false;
                          })
                        : null,
                  ),
                ),
              TextButton.icon(
                onPressed: busy
                    ? null
                    : () => setState(() {
                        _showHidden = !_showHidden;
                        _selected = null;
                      }),
                icon: Icon(_showHidden ? Icons.wifi_find : Icons.add),
                label: Text(
                  _showHidden
                      ? 'Choose a scanned network'
                      : 'Add a hidden network',
                ),
              ),
              if (_showHidden)
                TextField(
                  controller: _hiddenSsid,
                  enabled: !busy,
                  decoration: const InputDecoration(
                    labelText: 'Wi-Fi name (SSID)',
                    border: OutlineInputBorder(),
                  ),
                  onChanged: (_) => setState(() {}),
                ),
              if (_showHidden ||
                  (selected != null && selected.auth != 'open')) ...[
                const SizedBox(height: 12),
                TextField(
                  controller: _password,
                  enabled: !busy,
                  obscureText: _obscure,
                  decoration: InputDecoration(
                    labelText: 'Wi-Fi password',
                    border: const OutlineInputBorder(),
                    suffixIcon: IconButton(
                      tooltip: _obscure ? 'Show password' : 'Hide password',
                      onPressed: () => setState(() => _obscure = !_obscure),
                      icon: Icon(
                        _obscure ? Icons.visibility : Icons.visibility_off,
                      ),
                    ),
                  ),
                ),
              ],
              if (controller.error != null) ...[
                const SizedBox(height: 12),
                Text(
                  controller.error!,
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
              ],
              if (busy) ...[
                const SizedBox(height: 16),
                const Center(child: CircularProgressIndicator()),
                const SizedBox(height: 8),
                Text(
                  controller.stage == PairingStage.configuringWifi
                      ? 'Connecting speaker to Wi-Fi…'
                      : 'Scanning Wi-Fi…',
                  textAlign: TextAlign.center,
                ),
              ],
            ],
            if (!controller.dppSupported || _showManualSetup) ...[
              const SizedBox(height: 16),
              FilledButton(
                onPressed: busy || !canSubmit ? null : _configure,
                child: Text(
                  busy && controller.stage == PairingStage.configuringWifi
                      ? 'Connecting…'
                      : 'Connect speaker',
                ),
              ),
              TextButton(
                onPressed: busy ? null : () => Navigator.pop(context),
                child: const Text('Cancel'),
              ),
            ],
          ],
        ),
      );
    },
  );

  String _signal(int rssi) => rssi > -55
      ? 'Strong'
      : rssi > -75
      ? 'Good'
      : 'Weak';
}
