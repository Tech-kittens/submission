import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/widgets.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/meetings/models/conversation_meeting.dart';
import 'package:quacktion_frontend/features/meetings/data/meeting_audio_upload.dart';
import 'package:quacktion_frontend/features/meetings/data/meeting_links.dart';

/// Shares the phone voice microphone; never starts another recorder.
class MeetingModeController extends ChangeNotifier with WidgetsBindingObserver {
  MeetingModeController(
    this.api, {
    this.autoPoll = true,
    MeetingLinks? links,
    this.sourceSampleRate = 16000,
  }) : links = links ?? MemoryMeetingLinks() {
    WidgetsBinding.instance.addObserver(this);
  }
  final BackendApi api;
  final MeetingLinks links;
  final bool autoPoll;
  final int sourceSampleRate;
  final _meetings = <String, ConversationMeeting>{};
  final _errors = <String, String>{};
  final _stale = <String>{},
      _busy = <String>{},
      _sources = <String>{},
      _capturing = <String>{};
  final _refreshes = <String, Future<void>>{};
  final _audio = <String, MeetingAudioUpload>{};
  final _ids = <String, String>{};
  final _ending = <String>{};
  String? _visibleSession;
  Timer? _poll;
  bool _disposed = false, _resumed = true;

  ConversationMeeting? meeting(String id) => _meetings[id];
  String? error(String id) => _errors[id];
  bool stale(String id) => _stale.contains(id);
  bool busy(String id) => _busy.contains(id);
  // Block before the start request and through upload/finalization failures.
  bool blocksConversation(String id) =>
      busy(id) || stale(id) || meeting(id)?.canEnd == true;
  bool capturing(String id) => _capturing.contains(id);
  bool uploadIssue(String id) => _audio[id]?.failed == true;
  bool canRetryUpload(String id) =>
      uploadIssue(id) && _audio[id]?.closed == false && !busy(id);

  Future<void> retryUpload(String sessionId) async {
    if (!_alive || !canRetryUpload(sessionId)) return;
    await _audio[sessionId]?.retry();
    _emit();
  }

  bool get _alive => !_disposed;
  void _emit() {
    if (_alive) notifyListeners();
  }

  void captureStarted(String sessionId) {
    if (_alive) _sources.add(sessionId);
  }

  void captureStopped(String sessionId) {
    _sources.remove(sessionId);
    _capturing.remove(sessionId);
    _emit();
  }

  void acceptAudio(String sessionId, Uint8List frame) {
    if (!_alive ||
        !_sources.contains(sessionId) ||
        _ending.contains(sessionId)) {
      return;
    }
    final upload = _audio[sessionId];
    if (upload == null || upload.closed || frame.isEmpty) return;
    if (upload.add(frame) && _capturing.add(sessionId)) _emit();
  }

  void watch(String? sessionId) {
    if (_visibleSession == sessionId) return;
    _visibleSession = sessionId;
    _restartPolling();
    if (sessionId != null) unawaited(refresh(sessionId));
  }

  void _restartPolling() {
    _poll?.cancel();
    _poll = null;
    if (!autoPoll || !_resumed || !_alive || _visibleSession == null) return;
    _poll = Timer.periodic(const Duration(seconds: 5), (_) {
      final id = _visibleSession;
      if (id != null && !busy(id)) unawaited(refresh(id));
    });
  }

  Future<void> refresh(String sessionId) =>
      _refreshes[sessionId] ??= _refresh(sessionId).whenComplete(() {
        _refreshes.remove(sessionId);
      });

  Future<void> _refresh(String sessionId) async {
    try {
      final id = _ids[sessionId] ?? await links.read(sessionId);
      if (!_alive || id == null || busy(sessionId)) return;
      _ids[sessionId] = id;
      final row = await api.meetingStatus(id);
      if (!_alive || busy(sessionId)) return;
      final current = ConversationMeeting(
        row,
        sessionId,
        expectedMeetingId: id,
      );
      _meetings[sessionId] = current;
      _stale.remove(sessionId);
      if (!uploadIssue(sessionId)) _errors.remove(sessionId);
      if (!current.canEnd) {
        _capturing.remove(sessionId);
        _audio.remove(sessionId)?.discard();
      } else if (!_audio.containsKey(sessionId)) {
        // After process loss, do not resume at offset zero or infer consent
        // for a new capture. The owner can finalize the interrupted meeting.
        _errors[sessionId] = 'Meeting recovered. Recording is paused; end this meeting before starting another.';
      }
    } catch (_) {
      if (!_alive) return;
      _stale.add(sessionId);
      _errors[sessionId] =
          'Could not confirm meeting status. Retry to check recording.';
    }
    _emit();
  }

  Future<bool> start(
    String sessionId, {
    required String name,
    required String type,
    required bool consent,
  }) async {
    if (!_alive ||
        busy(sessionId) ||
        !consent ||
        stale(sessionId) ||
        meeting(sessionId)?.canEnd == true) {
      return false;
    }
    if (_audio.entries.any(
      (entry) => entry.key != sessionId && !entry.value.closed,
    )) {
      _errors[sessionId] = 'A phone meeting is already recording in another conversation. End it there first.';
      _emit();
      return false;
    }
    if (!_sources.contains(sessionId) || sourceSampleRate != 16000) {
      _errors[sessionId] = sourceSampleRate != 16000
          ? 'Meeting recording requires 16 kHz microphone audio.'
          : 'Start the voice conversation and unmute the microphone first.';
      _emit();
      return false;
    }
    _busy.add(sessionId);
    _errors.remove(sessionId);
    _emit();
    try {
      final row = await api.startPhoneMeeting(
        name: name,
        type: type,
        recordingConsent: true,
      );
      if (!_alive) return false;
      final current = ConversationMeeting(row, sessionId);
      _meetings[sessionId] = current;
      _ids[sessionId] = current.id;
      // This API may return an account's existing active mobile meeting. Check
      // for previous audio before attaching a new capture at offset zero.
      final segments = await api.meetingSegments(current.id);
      if (!_alive) return false;
      if (segments.isNotEmpty) {
        _errors[sessionId] = 'An existing phone meeting was recovered. End it before starting a new recording.';
        await links.save(sessionId, current.id);
        return true;
      }
      _audio[sessionId] = MeetingAudioUpload(
        upload: (offset, bytes) async {
          await api.uploadMeetingAudio(current.id, offset, bytes);
        },
        onRecovered: () {
          if (!_alive) return;
          _errors.remove(sessionId);
          _emit();
        },
        onFailure: () {
          if (!_alive) return;
          if (_audio[sessionId]?.bufferFull == true) {
            _capturing.remove(sessionId);
          }
          _errors[sessionId] = _uploadFailureMessage(sessionId);
          _emit();
        },
      );
      _stale.remove(sessionId);
      try {
        await links.save(sessionId, current.id);
      } catch (_) {
        _errors[sessionId] =
            'Meeting is active, but its link could not be saved on this phone.';
      }
      return true;
    } catch (e) {
      if (_alive) {
        _errors[sessionId] =
            e is ApiException && (e.status == 404 || e.status == 405)
            ? 'Phone meeting mode is not available on the server yet.'
            : 'Could not start meeting recording. Refresh the meeting status before trying again.';
        if (_ids.containsKey(sessionId)) _stale.add(sessionId);
      }
      return false;
    } finally {
      _busy.remove(sessionId);
      _emit();
    }
  }

  String _uploadFailureMessage(String sessionId) {
    final failure = _audio[sessionId]?.lastError;
    final detail = switch (failure) {
      ApiException(code: 'request_timeout') =>
        'The server took too long to accept the audio.',
      ApiException(code: 'network_unavailable') =>
        'The server could not be reached. Check your connection.',
      ApiException(status: 401) => 'Your sign-in has expired.',
      ApiException(code: 'segment_offset_mismatch' || 'segment_conflict') =>
        'The server rejected the audio sequence.',
      ApiException(status: 413) => 'The server rejected the audio chunk size.',
      ApiException(status: 404 || 405) =>
        'Meeting audio upload is unavailable on the server.',
      ApiException(status: final int status) when status >= 500 =>
        'The server could not process the audio.',
      StateError(message: 'meeting_audio_buffer_full') =>
        'Audio uploads could not keep up and the recording buffer is full.',
      _ => 'Audio upload failed.',
    };
    if (failure is ApiException) {
      debugPrint(
        'Meeting audio upload failed: HTTP ${failure.status}, code ${failure.code}, trace ${failure.traceId}',
      );
    }
    if (_audio[sessionId]?.bufferFull == true ||
        _audio[sessionId]?.closed == true) {
      return 'Recording paused: $detail Retry upload and end the meeting. Buffered audio is retained while the app stays open.';
    }
    return 'Audio upload delayed: $detail Retrying automatically. Recording continues into the phone buffer while the microphone is available. Keep the app open.';
  }

  Future<bool> end(String sessionId) async {
    final current = meeting(sessionId);
    if (!_alive || busy(sessionId) || current == null || !current.canEnd) {
      return false;
    }
    _busy.add(sessionId);
    _ending.add(sessionId);
    _capturing.remove(sessionId);
    _errors.remove(sessionId);
    _emit();
    try {
      // Never finalize the server meeting until every queued chunk is accepted.
      await _audio[sessionId]?.finish();
      final row = await api.endMeeting(current.id);
      if (!_alive) return false;
      _meetings[sessionId] = ConversationMeeting(
        row,
        sessionId,
        expectedMeetingId: current.id,
      );
      _audio.remove(sessionId)?.discard();
      _stale.remove(sessionId);
      return true;
    } catch (_) {
      if (_alive) {
        _stale.add(sessionId);
        _errors[sessionId] = uploadIssue(sessionId)
            ? _uploadFailureMessage(sessionId)
            : 'Meeting has not been finalized. Retry upload and end meeting to send the remaining audio.';
      }
      return false;
    } finally {
      _busy.remove(sessionId);
      _ending.remove(sessionId);
      _emit();
    }
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    _resumed = state == AppLifecycleState.resumed;
    _restartPolling();
    if (_resumed && _visibleSession != null) {
      unawaited(refresh(_visibleSession!));
    }
  }

  @override
  void dispose() {
    _disposed = true;
    _poll?.cancel();
    WidgetsBinding.instance.removeObserver(this);
    for (final upload in _audio.values) {
      upload.discard();
    }
    _audio.clear();
    _meetings.clear();
    _errors.clear();
    _capturing.clear();
    _sources.clear();
    super.dispose();
  }
}
