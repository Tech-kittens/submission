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
    required this.onReconnect,
    required this.hasSavedSpeaker,
    required this.busy,
    required this.onConfigureWifi,
    required this.onForgetWifi,
    required this.onUnpair,
    this.onFinishConfiguration,
    this.onCancelSetup,
    this.onRetryBackend,
  });
  final bool devicePaired;
  final String? deviceName;
  final String? wifiNetwork;
  final String? wifiState;
  final String? backendState;
  final VoidCallback? onPair;
  final VoidCallback? onReconnect;
  final bool hasSavedSpeaker;
  final bool busy;
  final VoidCallback? onConfigureWifi;
  final VoidCallback? onForgetWifi;
  final VoidCallback? onUnpair;
  final VoidCallback? onFinishConfiguration;
  final VoidCallback? onCancelSetup;
  final VoidCallback? onRetryBackend;
  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        PageHeading(
          title: 'Your devices',
          subtitle: 'Connect and manage your speakers.',
        ),
        ContentPanel(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Container(
                padding: const EdgeInsets.all(18),
                decoration: BoxDecoration(
                  color: const Color(0xFFF6F3EA),
                  borderRadius: BorderRadius.circular(18),
                ),
                child: const Icon(
                  Icons.speaker_rounded,
                  size: 44,
                  color: Color(0xFF243E36),
                ),
              ),
              const SizedBox(height: 22),
              Text(
                devicePaired || hasSavedSpeaker
                    ? (deviceName ?? 'Quacktion Speaker')
                    : 'Meet your first speaker',
                style: Theme.of(context).textTheme.titleLarge
                    ?.copyWith(fontWeight: FontWeight.w700),
              ),
              const SizedBox(height: 10),
              Text(
                devicePaired
                    ? 'Bluetooth configuration mode. Voice resumes when setup closes.'
                    : hasSavedSpeaker
                    ? 'To configure this speaker, hold K2 for 3 seconds and release, then connect below. Bluetooth is off during normal use.'
                    : 'Hold K2 for 3 seconds and release to open setup, then connect over Bluetooth. A new speaker opens setup automatically.',
                style: const TextStyle(color: Color(0xFF69685F), height: 1.5),
              ),
              const SizedBox(height: 24),
              if (devicePaired) ...[
                OutlinedButton.icon(
                  onPressed: busy ? null : onFinishConfiguration,
                  icon: const Icon(Icons.check),
                  label: const Text('Finish setup'),
                ),
                if (onFinishConfiguration == null)
                  const Padding(
                    padding: EdgeInsets.only(bottom: 12),
                    child: Text(
                      'Finish setup once Wi-Fi and your account are connected.',
                    ),
                  ),
                if (backendState == 'failed' || backendState == 'unlinked')
                  TextButton(
                    onPressed: busy ? null : onRetryBackend,
                    child: const Text('Retry account linking'),
                  ),
                TextButton(
                  onPressed: busy ? null : onCancelSetup,
                  child: const Text('Cancel setup'),
                ),
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
                            switch (wifiState) {
                              'connected' => 'Wi-Fi connected',
                              'connecting' => 'Connecting to Wi-Fi…',
                              'scanning' => 'Searching for networks…',
                              'awaiting_dpp' => 'Waiting for Wi-Fi sharing…',
                              'failed' => 'Wi-Fi disconnected',
                              _ => 'Wi-Fi not configured',
                            },
                            style: const TextStyle(fontWeight: FontWeight.w700),
                          ),
                          const SizedBox(height: 6),
                          Text(
                            wifiState != 'connected'
                                ? 'Set up Wi-Fi to get your speaker online.'
                                : '${wifiNetwork ?? 'Connected'} · ${switch (backendState) {
                                    'online' => 'Account linked',
                                    'connecting' => 'Connecting to your account…',
                                    'failed' => 'Account connection unavailable',
                                    _ => 'Account not linked',
                                  }}',
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
                  onPressed: busy ? null : onConfigureWifi,
                  icon: const Icon(Icons.wifi),
                  label: Text(
                    wifiState == 'connected' ? 'Change Wi-Fi' : 'Configure now',
                  ),
                ),
                if (wifiState == 'connected') ...[
                  const SizedBox(height: 12),
                  OutlinedButton.icon(
                    onPressed: busy ? null : onForgetWifi,
                    icon: const Icon(Icons.wifi_off_rounded),
                    label: const Text('Disconnect Wi-Fi'),
                  ),
                ],
                const SizedBox(height: 12),
                OutlinedButton(
                  onPressed: busy ? null : onUnpair,
                  child: const Text('Forget saved speaker'),
                ),
              ] else if (hasSavedSpeaker) ...[
                FilledButton.icon(
                  onPressed: busy ? null : onReconnect,
                  icon: const Icon(Icons.bluetooth_connected),
                  label: Text(
                    busy ? 'Opening configuration…' : 'Configure speaker',
                  ),
                ),
                TextButton.icon(
                  onPressed: busy ? null : onPair,
                  icon: const Icon(Icons.bluetooth_searching),
                  label: const Text('Scan for another speaker'),
                ),
                TextButton.icon(
                  onPressed: busy ? null : onUnpair,
                  icon: const Icon(Icons.link_off_rounded),
                  label: const Text('Forget saved speaker'),
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
