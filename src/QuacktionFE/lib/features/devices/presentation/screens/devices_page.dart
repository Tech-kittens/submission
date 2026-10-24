import 'package:flutter/material.dart';

import 'package:quacktion_frontend/shared/widgets/content_panel.dart';
import 'package:quacktion_frontend/shared/widgets/page_heading.dart';

class DevicesPage extends StatelessWidget {
  const DevicesPage({
    super.key,
    required this.devicePaired,
    required this.deviceName,
    required this.wifiNetwork,
    required this.wifiState,
    required this.backendState,
    required this.onPair,
    required this.onConfigureWifi,
    required this.onRemove,
  });
  final bool devicePaired;
  final String? deviceName;
  final String? wifiNetwork;
  final String? wifiState;
  final String? backendState;
  final VoidCallback? onPair;
  final VoidCallback? onConfigureWifi;
  final VoidCallback? onRemove;
  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        PageHeading(
          title: 'Your devices',
          subtitle: 'A home for your Quacktion speakers.',
        ),
        ContentPanel(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Container(
                padding: const EdgeInsets.all(30),
                decoration: BoxDecoration(
                  color: const Color(0xFFF6F3EA),
                  borderRadius: BorderRadius.circular(18),
                ),
                child: const Icon(
                  Icons.speaker_rounded,
                  size: 76,
                  color: Color(0xFF243E36),
                ),
              ),
              const SizedBox(height: 22),
              Text(
                devicePaired
                    ? (deviceName ?? 'Quacktion Speaker')
                    : 'Meet your first speaker',
                style: Theme.of(context).textTheme.titleLarge
                    ?.copyWith(fontWeight: FontWeight.w700),
              ),
              const SizedBox(height: 10),
              Text(
                devicePaired ? 'Connected over Bluetooth' : 'Bring your assistant into the room. Connect to it over Bluetooth to get started.',
                style: const TextStyle(color: Color(0xFF69685F), height: 1.5),
              ),
              const SizedBox(height: 24),
              if (devicePaired) ...[
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Icon(
                      wifiState != 'connected'
                          ? Icons.wifi_off_outlined
                          : Icons.wifi_rounded,
                      color: const Color(0xFF69685F),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            wifiState != 'connected'
                                ? 'Not configured'
                                : 'Wi-Fi connected',
                            style: const TextStyle(fontWeight: FontWeight.w700),
                          ),
                          const SizedBox(height: 6),
                          Text(
                            wifiState != 'connected'
                                ? 'Set up Wi-Fi to get your speaker online.'
                                : 'Wi-Fi: ${wifiNetwork ?? 'Connected'} · Account link: ${backendState ?? 'unlinked'}',
                            style: const TextStyle(
                              color: Color(0xFF69685F),
                              height: 1.5,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 20),
                FilledButton.icon(
                  onPressed: onConfigureWifi,
                  icon: const Icon(Icons.wifi),
                  label: Text(
                    wifiState == 'connected' ? 'Change Wi-Fi' : 'Configure now',
                  ),
                ),
                const SizedBox(height: 12),
                OutlinedButton(
                  onPressed: onRemove,
                  child: const Text('Disconnect speaker'),
                ),
              ] else
                FilledButton.icon(
                  onPressed: onPair,
                  icon: const Icon(Icons.bluetooth_searching),
                  label: const Text('Connect speaker'),
                ),
            ],
          ),
        ),
      ],
    );
  }
}
