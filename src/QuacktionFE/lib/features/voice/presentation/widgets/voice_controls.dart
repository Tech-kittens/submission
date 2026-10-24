import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';

/// Displays a supplied voice controller; owns no conversation or audio resources.
class VoiceControls extends StatelessWidget {
  const VoiceControls({
    super.key,
    required this.voice,
    required this.onEnd,
    this.enabled = true,
  });
  final VoiceController voice;
  final VoidCallback onEnd;
  final bool enabled;
  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: voice,
    builder: (context, _) => Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        if (voice.running) ...[
          _MicrophoneWaveform(voice: voice),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: Semantics(
                  liveRegion: true,
                  child: Text(
                    voice.muted ? 'Microphone muted' : voice.status,
                    style: const TextStyle(fontSize: 12),
                  ),
                ),
              ),
              IconButton(
                tooltip: voice.muted ? 'Unmute microphone' : 'Mute microphone',
                onPressed: voice.connected ? voice.toggleMute : null,
                icon: Icon(
                  voice.muted ? Icons.mic_off_rounded : Icons.mic_rounded,
                ),
              ),
              IconButton.filled(
                tooltip: 'End conversation',
                onPressed: onEnd,
                icon: const Icon(Icons.stop_rounded),
              ),
            ],
          ),
        ] else ...[
          FilledButton.icon(
            onPressed: !enabled ? null : voice.start,
            icon: const Icon(Icons.graphic_eq_rounded),
            label: const Text('Start conversation'),
          ),
          const SizedBox(height: 8),
          const Text(
            'Starts a new voice session',
            style: TextStyle(fontSize: 12, color: Colors.black54),
          ),
        ],
        if (voice.error != null)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Text(
              voice.error!,
              textAlign: TextAlign.center,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
      ],
    ),
  );
}

class _MicrophoneWaveform extends StatelessWidget {
  const _MicrophoneWaveform({required this.voice});
  final VoiceController voice;

  @override
  Widget build(BuildContext context) => Semantics(
    label: 'Microphone input level',
    value: voice.muted
        ? 'Muted'
        : '${(voice.inputLevel * 100).round()} percent',
    child: Container(
      height: 100,
      padding: const EdgeInsets.symmetric(horizontal: 18),
      decoration: BoxDecoration(
        color: const Color(0xFFF5F5F3),
        borderRadius: BorderRadius.circular(28),
      ),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.center,
        children: List.generate(
          voice.levels.length,
          (index) => Expanded(
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 2),
              child: Center(
                child: AnimatedContainer(
                  duration: const Duration(milliseconds: 80),
                  curve: Curves.easeOut,
                  height: 6 + voice.levels[index] * 72,
                  decoration: BoxDecoration(
                    color: voice.connected && !voice.muted
                        ? Theme.of(context).colorScheme.primary
                        : Colors.black26,
                    borderRadius: BorderRadius.circular(5),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    ),
  );
}
