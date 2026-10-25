import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';
import 'package:quacktion_frontend/shared/widgets/app_toast.dart';

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
  Widget build(BuildContext context) => ErrorToastListener(
    listenable: voice,
    message: () => voice.error,
    child: ListenableBuilder(
      listenable: voice,
      builder: (context, _) => Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (voice.running && voice.connected) ...[
            if (voice.pausedForMeeting || voice.askingDuringMeeting) ...[
              FilledButton.icon(
                onPressed: voice.changingPause
                    ? null
                    : voice.askingDuringMeeting
                    ? voice.returnToMeeting
                    : voice.canAskDuringMeeting
                    ? voice.askDuringMeeting
                    : null,
                icon: voice.changingPause
                    ? const SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Icon(
                        voice.askingDuringMeeting
                            ? Icons.groups_outlined
                            : Icons.record_voice_over,
                      ),
                label: Text(
                  voice.changingPause
                      ? voice.askingDuringMeeting
                            ? 'Connecting to assistant…'
                            : 'Returning to meeting…'
                      : voice.askingDuringMeeting
                      ? 'Back to meeting'
                      : 'Ask assistant',
                ),
              ),
              const SizedBox(height: 8),
              Text(
                voice.wakeListening
                    ? 'Say “Hey Quacktion”, then wait for Listening to ask your question.'
                    : voice.wakeLoading
                    ? 'Preparing wake phrase…'
                    : voice.wakeError ?? 'Meeting recording continues.',
                textAlign: TextAlign.center,
                style: const TextStyle(fontSize: 12),
              ),
              const SizedBox(height: 8),
            ],
            _MicrophoneWaveform(voice: voice),
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: voice.connected
                      ? Semantics(
                          liveRegion: true,
                          child: Text(
                            voice.askingDuringMeeting
                                ? 'Assistant listening · Meeting recording continues'
                                : voice.pausedForMeeting
                                ? 'Conversation paused for meeting'
                                : voice.paused
                                ? 'Conversation paused'
                                : voice.muted
                                ? 'Microphone muted'
                                : voice.status,
                            style: const TextStyle(fontSize: 12),
                          ),
                        )
                      : const SizedBox.shrink(),
                ),
                IconButton(
                  tooltip: voice.paused
                      ? 'Resume conversation'
                      : 'Pause conversation',
                  onPressed: voice.changingPause || voice.pausedForMeeting
                      ? null
                      : voice.togglePause,
                  icon: Icon(
                    voice.paused
                        ? Icons.play_arrow_rounded
                        : Icons.pause_rounded,
                  ),
                ),
                if (voice.connected)
                  IconButton(
                    tooltip: voice.muted
                        ? 'Unmute microphone'
                        : 'Mute microphone',
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
              onPressed: !enabled || voice.running ? null : voice.start,
              style: voice.running
                  ? FilledButton.styleFrom(
                      disabledBackgroundColor: Theme.of(context)
                          .filledButtonTheme
                          .style
                          ?.backgroundColor
                          ?.resolve({}),
                      disabledForegroundColor: Theme.of(context)
                          .filledButtonTheme
                          .style
                          ?.foregroundColor
                          ?.resolve({}),
                    )
                  : null,
              icon: voice.running
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        color: Color(0xFF292516),
                      ),
                    )
                  : const Icon(Icons.graphic_eq_rounded),
              label: Text(
                voice.running
                    ? 'Starting conversation…'
                    : voice.conversationToResume?.call()?.canResume == true
                    ? 'Resume conversation'
                    : 'Start conversation',
              ),
            ),
          ],
        ],
      ),
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
