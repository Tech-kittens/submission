import 'package:flutter/services.dart';

/// Android PCM16 capture/playback bridge used by the session audio socket.
class PhoneAudioEngine {
  static const _methods = MethodChannel('quacktion/phone_audio');
  static const _events = EventChannel('quacktion/phone_audio_frames');

  Stream<Uint8List> get microphoneFrames =>
      _events.receiveBroadcastStream().map((frame) => frame as Uint8List);

  Future<void> startCapture(int sampleRate) =>
      _methods.invokeMethod<void>('startCapture', {'sampleRate': sampleRate});

  Future<void> stopCapture() => _methods.invokeMethod<void>('stopCapture');

  Future<void> startPlayback(int sampleRate) =>
      _methods.invokeMethod<void>('startPlayback', {'sampleRate': sampleRate});

  Future<void> playFrame(Uint8List frame) =>
      _methods.invokeMethod<void>('playFrame', frame);

  Future<void> clearPlayback() => _methods.invokeMethod<void>('clearPlayback');

  Future<void> stopPlayback() => _methods.invokeMethod<void>('stopPlayback');
}
