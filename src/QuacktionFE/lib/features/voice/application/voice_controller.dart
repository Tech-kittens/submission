import 'dart:async';

import 'package:quacktion_frontend/features/voice/data/wake_phrase_detector.dart';

import 'package:flutter/foundation.dart';
import 'package:quacktion_frontend/features/meetings/application/meeting_mode_controller.dart';
import 'package:quacktion_frontend/features/conversations/data/voice_transport.dart';
import 'package:quacktion_frontend/features/conversations/data/session_audio_transport.dart';
import 'package:quacktion_frontend/features/conversations/models/session_conversation.dart';

/// Coordinates the authenticated session socket and phone audio engine.
class VoiceController extends ChangeNotifier {
  VoiceController({required this.transport, this.meetings, this.wakeDetector}) {
    meetings?.addListener(_syncMeeting);
  }

  final MeetingModeController? meetings;
  final WakePhraseDetector? wakeDetector;
  bool wakeReady = false, wakeLoading = false;
  String? wakeError;
  bool _wakeListening = false;
  bool get wakeListening => _wakeListening;

  void acceptMicrophoneFrame(Uint8List pcm16) {
    if (_wakeListening && canAskDuringMeeting && pausedForMeeting) {
      wakeDetector?.addAudio(pcm16);
    }
  }

  void _syncWakeDetector() {
    final wanted = !_disposed && canAskDuringMeeting && pausedForMeeting;
    if (wanted &&
        wakeDetector != null &&
        !wakeReady &&
        !wakeLoading &&
        wakeError == null) {
      wakeLoading = true;
      unawaited(_loadWakeDetector());
    }
    final listening = wanted && wakeReady;
    if (listening != _wakeListening) {
      _wakeListening = listening;
      wakeDetector?.reset();
    }
  }

  Future<void> _loadWakeDetector() async {
    try {
      await wakeDetector!.start(
        onWake: () {
          if (_wakeListening && canAskDuringMeeting && pausedForMeeting) {
            unawaited(askDuringMeeting());
          }
        },
        onUnavailable: () {
          if (_disposed) return;
          wakeReady = false;
          wakeError = 'Wake phrase unavailable. Use Ask assistant.';
          _emit();
        },
      );
      if (!_disposed) wakeReady = true;
    } catch (_) {
      if (!_disposed) wakeError = 'Wake phrase unavailable. Use Ask assistant.';
    } finally {
      wakeLoading = false;
      _emit();
    }
  }

  bool _manuallyPaused = false;
  bool _ending = false;
  bool pausedForMeeting = false;
  bool changingPause = false;
  int _pauseRevision = 0;
  bool _askingDuringMeeting = false;
  bool get askingDuringMeeting => _askingDuringMeeting;
  bool get paused =>
      !_askingDuringMeeting && (_manuallyPaused || pausedForMeeting);

  bool get _meetingReadyForQuestions {
    final id = transport.session?.id;
    return id != null &&
        meetings?.meeting(id)?.recording == true &&
        meetings?.capturing(id) == true &&
        meetings?.busy(id) == false &&
        meetings?.stale(id) == false;
  }

  bool get canAskDuringMeeting =>
      running &&
      connected &&
      !muted &&
      !changingPause &&
      !_ending &&
      _meetingReadyForQuestions;

  Future<void> askDuringMeeting() async {
    if (!canAskDuringMeeting || _askingDuringMeeting) return;
    _askingDuringMeeting = true;
    pausedForMeeting = false;
    await _applyPause();
  }

  Future<void> returnToMeeting() async {
    if (!_askingDuringMeeting || changingPause) return;
    _askingDuringMeeting = false;
    final id = transport.session?.id;
    pausedForMeeting = id != null && meetings?.blocksConversation(id) == true;
    await _applyPause();
  }

  void _syncMeeting() {
    final id = transport.session?.id;
    final blocked = id != null && meetings?.blocksConversation(id) == true;
    final wasPaused = paused;
    if (!_meetingReadyForQuestions) _askingDuringMeeting = false;
    pausedForMeeting = blocked && !_askingDuringMeeting;
    if (running && !_ending && !_disposed && paused != wasPaused) {
      unawaited(_applyPause());
    } else {
      _emit();
    }
  }

  Future<void> togglePause() async {
    if (_askingDuringMeeting) {
      await returnToMeeting();
      return;
    }
    if (!connected || changingPause || pausedForMeeting) return;
    _manuallyPaused = !_manuallyPaused;
    await _applyPause();
  }

  Future<void> _applyPause() async {
    final revision = ++_pauseRevision;
    changingPause = true;
    _emit();
    try {
      await transport.setPaused(paused);
      final session = transport.session;
      if (session != null) onSessionUpdated?.call(session);
      if (revision == _pauseRevision) error = null;
    } catch (failure) {
      if (revision == _pauseRevision) {
        _askingDuringMeeting = false;
        final id = transport.session?.id;
        pausedForMeeting =
            id != null && meetings?.blocksConversation(id) == true;
        _manuallyPaused = true;
        error = failure is SessionAudioException
            ? failure.message
            : 'Could not change conversation audio. Please try again.';
      }
    } finally {
      if (revision == _pauseRevision) changingPause = false;
      _emit();
    }
  }

  final VoiceTransport transport;
  String status = 'Ready to talk';
  String? error;
  bool running = false, connected = false, muted = false;
  bool _disposed = false;
  double inputLevel = 0;
  final List<double> levels = List.filled(28, 0, growable: true);
  ValueChanged<SessionConversation>? onSessionCreated;
  ValueChanged<SessionConversation>? onSessionUpdated;
  String Function()? conversationName;
  SessionConversation? Function()? conversationToResume;

  void _emit() {
    _syncWakeDetector();
    if (!_disposed) notifyListeners();
  }

  Future<void> start() async {
    if (running || _disposed) return;
    _manuallyPaused = false;
    _askingDuringMeeting = false;
    pausedForMeeting = false;
    running = true;
    connected = false;
    muted = false;
    error = null;
    status = 'Connecting…';
    _emit();
    try {
      await transport.start(
        name: conversationName?.call() ?? 'Voice conversation',
        resumeSession: conversationToResume?.call(),
        onSessionCreated: (session) {
          onSessionCreated?.call(session);
          _syncMeeting();
        },
        onStatus: (value) {
          status = value;
          connected = value == 'Listening';
          final session = transport.session;
          if (session != null) onSessionUpdated?.call(session);
          if (!connected) inputLevel = 0;
          _emit();
        },
        onInputLevel: (level) {
          inputLevel = muted ? 0 : level;
          levels.removeAt(0);
          levels.add(inputLevel);
          _emit();
        },
        onClosed: _onClosed,
      );
      if (!_disposed && running) {
        connected = true;
        status = 'Listening';
        _emit();
      }
    } catch (failure) {
      if (!_disposed) {
        running = false;
        connected = false;
        error = failure is SessionAudioException
            ? failure.message
            : 'Could not start voice. Please try again.';
        status = 'Could not start conversation';
        _emit();
      }
    }
  }

  void _onClosed(String message) {
    if (_disposed) return;
    final session = transport.session;
    if (session != null) onSessionUpdated?.call(session);
    running = false;
    connected = false;
    inputLevel = 0;
    status = message;
    if (message != 'Conversation ended') error = message;
    _emit();
  }

  Future<void> toggleMute() async {
    if (!connected) return;
    muted = !muted;
    transport.setMuted(muted);
    if (muted) inputLevel = 0;
    if (muted) levels.fillRange(0, levels.length, 0);
    _emit();
  }

  Future<void> stop() async {
    if (_ending) return;
    _ending = true;
    try {
      await transport.end();
    } catch (_) {
      error = 'Could not end this conversation. Please try again.';
      _ending = false;
      _emit();
      return;
    }
    _ending = false;
    final session = transport.session;
    if (session != null) {
      if (session.canEnd) session.status = 'Completed';
      onSessionUpdated?.call(session);
    }
    running = false;
    connected = false;
    muted = false;
    inputLevel = 0;
    levels.fillRange(0, levels.length, 0);
    status = 'Voice conversation ended';
    _emit();
  }

  @override
  void dispose() {
    _disposed = true;
    wakeDetector?.dispose();
    meetings?.removeListener(_syncMeeting);
    unawaited(transport.stop());
    super.dispose();
  }
}
