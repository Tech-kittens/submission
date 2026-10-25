import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter/services.dart';
import 'package:quacktion_frontend/core/config/app_config.dart';
import 'package:quacktion_frontend/core/networking/api_client.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/conversations/data/phone_audio_engine.dart';
import 'package:quacktion_frontend/features/conversations/models/session_conversation.dart';
import 'package:quacktion_frontend/features/conversations/data/voice_transport.dart';

/// Account-authenticated client for the backend's session audio WebSocket.
class SessionAudioTransport implements VoiceTransport {
  SessionAudioTransport({
    required this.api,
    required this.client,
    PhoneAudioEngine? audio,
    this.onMicrophoneFrame,
    this.onCaptureStarted,
    this.onCaptureStopped,
  }) : audio = audio ?? PhoneAudioEngine();

  final BackendApi api;
  final ApiClient client;
  final PhoneAudioEngine audio;
  final void Function(String, Uint8List)? onMicrophoneFrame;
  final void Function(String)? onCaptureStarted, onCaptureStopped;

  WebSocket? _socket;
  StreamSubscription<dynamic>? _socketSubscription;
  StreamSubscription<Uint8List>? _microphoneSubscription;
  Completer<void>? _ready;
  Completer<void>? _ended;
  bool _muted = false;
  bool _paused = false;
  int _audioGeneration = 0;
  bool _stopping = false;
  bool _audioActive = false;
  @override
  SessionConversation? session;

  @override
  Future<SessionConversation> start({
    required String name,
    SessionConversation? resumeSession,
    required ValueChanged<SessionConversation> onSessionCreated,
    required ValueChanged<String> onStatus,
    required ValueChanged<double> onInputLevel,
    required ValueChanged<String> onClosed,
  }) async {
    await stop(sendEnd: false);
    _stopping = false;
    _muted = false;
    _paused = false;
    onStatus('Creating voice session…');

    _onStatus = onStatus;
    _onInputLevel = onInputLevel;
    _onClosed = onClosed;
    late final SessionConversation created;
    var waitSeconds = 30;
    if (resumeSession != null) {
      await _selectResume(resumeSession.id);
      created = resumeSession;
      created.status = 'Suspended';
      _socketUrl = _resumeUrl(created.id);
    } else {
      final response = await api.startAudioSession(
        name: name,
        requestId: _requestId(),
      );
      final sessionId = response['sessionId']?.toString();
      _socketUrl = response['webSocketUrl']?.toString();
      if (sessionId == null || _socketUrl == null) {
        throw const SessionAudioException(
          'The backend returned an invalid audio session.',
        );
      }
      created = SessionConversation(
        id: sessionId,
        title: name,
        updatedAt: DateTime.now(),
      )..status = response['status']?.toString() ?? 'Ready';
      waitSeconds = (response['expiresInSeconds'] as num?)?.toInt() ?? 60;
    }
    session = created;
    onSessionCreated(created);
    onStatus('Connecting to voice service…');
    await _connect(waitSeconds);
    return created;
  }

  String? _socketUrl;
  late ValueChanged<String> _onStatus, _onClosed;
  late ValueChanged<double> _onInputLevel;
  Future<void> _transition = Future<void>.value();

  String _resumeUrl(String id) {
    final base = Uri.parse(client.baseUrl);
    return base
        .replace(
          scheme: base.scheme == 'https' ? 'wss' : 'ws',
          path: '/ws/sessions/${Uri.encodeComponent(id)}/audio',
          query: null,
          fragment: null,
        )
        .toString();
  }

  Future<void> _selectResume(String id) async {
    try {
      final row = await api.resumeSession(id);
      if (row['id'] != id || row['status'] != 'Suspended') {
        throw const SessionAudioException(
          'The server did not confirm conversation resumption.',
        );
      }
    } on ApiException catch (error) {
      throw SessionAudioException(switch (error.code) {
        'conversation_in_use' => 'This conversation is still connected. Wait a moment and retry resume.',
        'device_busy' => 'The speaker is busy with another conversation.',
        'session_not_resumable' => 'This conversation cannot be resumed.',
        _ =>
          error.status == 404 || error.status == 405
              ? 'Conversation resumption is not available on this server yet.'
              : 'Could not resume this conversation. Please try again.',
      });
    }
  }

  Future<void> _connect(int waitSeconds) async {
    _stopping = false;
    final onStatus = _onStatus;
    final onInputLevel = _onInputLevel;
    final onClosed = _onClosed;
    try {
      final token = await client.accessToken();
      final socket = await WebSocket.connect(
        _socketUrl!,
        headers: {'Authorization': 'Bearer $token'},
        protocols: const ['quacktion.audio.v1'],
      ).timeout(const Duration(seconds: 20));
      _socket = socket;
      socket.pingInterval = const Duration(seconds: 20);
      _ready = Completer<void>();
      _socketSubscription = socket.listen(
        (event) {
          if (_socket == socket) {
            _queueMessage(event, onStatus, onInputLevel, onClosed);
          }
        },
        onError: (Object error, StackTrace stack) => _handleSocketEnd(
          'Voice connection failed. Check your network and try again.',
          onClosed,
        ),
        onDone: () => _handleSocketEnd('Voice session disconnected.', onClosed),
        cancelOnError: true,
      );
      await _ready!.future.timeout(Duration(seconds: min(waitSeconds, 30)));
    } catch (error) {
      await _disconnectConversation();
      if (!_paused) await _stopLocalAudio();
      if (error is SessionAudioException) rethrow;
      throw SessionAudioException(_friendlyError(error));
    }
  }

  Future<void> _incoming = Future<void>.value();

  void _queueMessage(
    dynamic event,
    ValueChanged<String> onStatus,
    ValueChanged<double> onInputLevel,
    ValueChanged<String> onClosed,
  ) {
    // Drop responses received during a pause, including frames queued before
    // pause that would otherwise be played after a quick resume.
    if (event is List<int> && _paused) return;
    final generation = _audioGeneration;
    final sourceSocket = _socket;
    _incoming = _incoming
        .then((_) async {
          if (sourceSocket != _socket) return;
          if (event is List<int> && generation != _audioGeneration) return;
          await _handleMessage(event, onStatus, onInputLevel, onClosed);
        })
        .catchError((Object error, StackTrace stack) {
          if (!(_ready?.isCompleted ?? true)) {
            _ready!.completeError(SessionAudioException(_friendlyError(error)));
          }
          onStatus(_friendlyError(error));
        });
  }

  Future<void> _handleMessage(
    dynamic event,
    ValueChanged<String> onStatus,
    ValueChanged<double> onInputLevel,
    ValueChanged<String> onClosed,
  ) async {
    if (event is List<int>) {
      final frame = Uint8List.fromList(event);
      if (!_paused) await audio.playFrame(frame);
      return;
    }
    if (event is! String) return;

    final control = jsonDecode(event);
    if (control is! Map) return;
    final type = control['type']?.toString();
    switch (type) {
      case 'session.started':
        _socket?.add(
          jsonEncode({
            'version': 1,
            'type': 'audio.start',
            'audio': {
              'protocolVersion': 1,
              'input': {
                'codec': 'pcm_16000',
                'sampleRate': AppConfig.voiceInputRate,
                'channels': 1,
              },
              'output': {
                'codec': 'pcm_16000',
                'sampleRate': AppConfig.voiceOutputRate,
                'channels': 1,
              },
            },
          }),
        );
        if (!_audioActive) onStatus('Preparing microphone…');
      case 'audio.ready':
        try {
          if (!_audioActive) {
            await audio.startPlayback(AppConfig.voiceOutputRate);
            _audioActive = true;
            _microphoneSubscription = audio.microphoneFrames.listen(
              (frame) {
                if (_muted) {
                  return;
                }
                if (session case final current?) {
                  onMicrophoneFrame?.call(current.id, frame);
                }
                if (!_paused && _socket?.readyState == WebSocket.open) {
                  _socket!.add(frame);
                }
                onInputLevel(_rms(frame));
              },
              onError: (_) {
                if (session case final current?) {
                  onCaptureStopped?.call(current.id);
                }
                onClosed('Microphone capture stopped.');
              },
            );
            await audio.startCapture(AppConfig.voiceInputRate);
            if (session case final current?) onCaptureStarted?.call(current.id);
          }
          session?.status = 'Active';
          onStatus('Listening');
          if (!(_ready?.isCompleted ?? true)) _ready!.complete();
        } catch (error) {
          if (!(_ready?.isCompleted ?? true)) {
            _ready!.completeError(SessionAudioException(_friendlyError(error)));
          }
          await stop(sendEnd: true);
          onClosed(_friendlyError(error));
        }
      case 'audio.clear':
        unawaited(audio.clearPlayback());
      case 'session.suspended':
        session?.status = 'Suspended';
        await stop(sendEnd: false);
        onClosed('Conversation paused. Resume to reconnect.');
      case 'session.ended':
        if (session != null) session!.status = 'Completed';
        onStatus('Conversation ended');
        await _stopLocalAudio();
        if (!(_ended?.isCompleted ?? true)) _ended!.complete();
        onClosed('Conversation ended');
        if (_ended == null) await stop(sendEnd: false);
      case 'session.failed' || 'session.start_failed':
        final payload = control['payload'];
        final code = payload is Map ? payload['code']?.toString() : null;
        if (session != null) session!.status = 'Failed';
        final message = _friendlyCode(code ?? 'session_failed');
        if (!(_ready?.isCompleted ?? true)) {
          _ready!.completeError(SessionAudioException(message));
        }
        onStatus(message);
        await _stopLocalAudio();
        onClosed(message);
        await stop(sendEnd: false);
      case 'error':
        final code = control['code']?.toString() ?? 'voice_error';
        final message = _friendlyCode(code);
        if (!(_ready?.isCompleted ?? true)) {
          _ready!.completeError(SessionAudioException(message));
        }
        onStatus(message);
        onClosed(message);
        await stop(sendEnd: false);
    }
  }

  @override
  Future<void> setPaused(bool paused) {
    if (paused) {
      _paused = true;
      _audioGeneration++;
    }
    final next = _transition.then((_) async {
      if (paused) {
        _paused = true;
        try {
          if (_audioActive) await audio.clearPlayback();
        } finally {
          await _disconnectConversation();
          session?.status = 'Suspended';
        }
      } else {
        final current = session;
        if (current == null) return;
        await _selectResume(current.id);
        _socketUrl ??= _resumeUrl(current.id);
        await _connect(30);
        _paused = false;
      }
    });
    _transition = next.catchError((Object _) {});
    return next;
  }

  Future<void> _disconnectConversation() async {
    _stopping = true;
    final socket = _socket;
    _socket = null;
    await _socketSubscription?.cancel();
    _socketSubscription = null;
    try {
      await socket
          ?.close(WebSocketStatus.normalClosure, 'app_paused')
          .timeout(const Duration(seconds: 3));
    } catch (_) {}
  }

  @override
  void setMuted(bool muted) {
    _muted = muted;
    if (session case final current?) {
      if (muted) {
        onCaptureStopped?.call(current.id);
      } else if (_audioActive) {
        onCaptureStarted?.call(current.id);
      }
    }
  }

  @override
  Future<void> end() async {
    await _transition;
    final current = session;
    if (_socket == null && current != null) {
      await api.endSession(current.id);
      current.status = 'Completed';
    }
    await stop(sendEnd: true);
  }

  @override
  Future<void> stop({bool sendEnd = true}) async {
    // A pending resume must finish before cleanup, otherwise it could reopen
    // the socket and microphone after the owning widget has been disposed.
    await _transition;
    _stopping = true;
    final socket = _socket;
    if (sendEnd && socket?.readyState == WebSocket.open) {
      try {
        _ended = Completer<void>();
        socket!.add(
          jsonEncode({
            'version': 1,
            'type': 'session.end',
            'requestId': _requestId(),
          }),
        );
        await _ended!.future.timeout(
          const Duration(seconds: 2),
          onTimeout: () {},
        );
      } catch (_) {}
    }
    await _stopLocalAudio();
    await _socketSubscription?.cancel();
    _socketSubscription = null;
    _socket = null;
    try {
      await socket?.close(WebSocketStatus.normalClosure, 'app_ended');
    } catch (_) {}
    _ready = null;
    _ended = null;
  }

  Future<void> _stopLocalAudio() async {
    if (session case final current?) onCaptureStopped?.call(current.id);
    await _microphoneSubscription?.cancel();
    _microphoneSubscription = null;
    try {
      await audio.stopCapture();
    } catch (_) {}
    if (_audioActive) {
      try {
        await audio.stopPlayback();
      } catch (_) {}
      _audioActive = false;
    }
  }

  void _handleSocketEnd(String message, ValueChanged<String> onClosed) {
    if (_stopping) return;
    if (!(_ready?.isCompleted ?? true)) {
      _ready!.completeError(SessionAudioException(message));
    }
    unawaited(_stopLocalAudio());
    final completed = session?.status == 'Completed';
    if (!completed) session?.status = 'Suspended';
    onClosed(completed ? 'Conversation ended' : message);
  }

  static double _rms(Uint8List bytes) {
    final data = ByteData.sublistView(bytes);
    var squares = 0.0;
    final samples = bytes.length ~/ 2;
    for (var i = 0; i < samples; i++) {
      final sample = data.getInt16(i * 2, Endian.little) / 32768.0;
      squares += sample * sample;
    }
    if (samples == 0) return 0;
    return (sqrt(squares / samples) * 4).clamp(0.0, 1.0);
  }

  static String _requestId() {
    final random = Random.secure();
    final suffix = List.generate(
      16,
      (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
    ).join();
    return '${DateTime.now().microsecondsSinceEpoch}-$suffix';
  }

  static String _friendlyError(Object error) {
    if (error is SocketException ||
        error is WebSocketException ||
        error is TimeoutException) {
      return 'Could not connect to the voice service. Check your connection and try again.';
    }
    if (error is PlatformException) {
      return 'Could not start audio. Check microphone permissions and try again.';
    }
    return 'Could not start the conversation. Please try again.';
  }

  static String _friendlyCode(String code) => switch (code) {
    'unsupported_audio_format' =>
      'Voice is unavailable on this phone right now. Please try again later.',
    'elevenlabs_not_configured' ||
    'provider_not_configured' => 'The voice service is not configured.',
    'provider_connection_failed' || 'elevenlabs_connection_failed' =>
      'The voice service could not connect. Try again shortly.',
    'session_start_failed' =>
      'The voice session could not start. Try a new conversation.',
    _ => 'The voice connection was interrupted. Start a new conversation to try again.',
  };
}

class SessionAudioException implements Exception {
  const SessionAudioException(this.message);
  final String message;
  @override
  String toString() => message;
}
