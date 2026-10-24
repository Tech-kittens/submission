import 'dart:async';
import 'dart:math';

import 'package:flutter/foundation.dart';

/// Simulates voice controls for the local-only conversation prototype.
class VoiceController extends ChangeNotifier {
  String status = 'Ready to talk';
  String? error;
  bool running = false, connected = false, muted = false;
  bool _disposed = false;
  Timer? _timer;
  int _ticks = 0;
  double inputLevel = 0;
  final List<double> levels = List.filled(28, 0, growable: true);
  VoidCallback? onDemoStart;
  VoidCallback? onDemoReply;

  void _emit() {
    if (!_disposed) notifyListeners();
  }

  Future<void> start() async {
    if (running || _disposed) return;
    running = true;
    connected = false;
    muted = false;
    error = null;
    status = 'Connecting…';
    _ticks = 0;
    onDemoStart?.call();
    _emit();
    await Future<void>.delayed(const Duration(milliseconds: 900));
    if (!running || _disposed) return;
    connected = true;
    status = 'Listening · Demo mode';
    _timer = Timer.periodic(const Duration(milliseconds: 90), (_) {
      _ticks++;
      inputLevel = muted ? 0 : (0.08 + Random().nextDouble() * 0.72);
      levels.removeAt(0);
      levels.add(inputLevel);
      if (_ticks == 90) onDemoReply?.call();
      _emit();
    });
    _emit();
  }

  Future<void> toggleMute() async {
    if (!connected) return;
    muted = !muted;
    if (muted) inputLevel = 0;
    _emit();
  }

  Future<void> stop() async {
    _timer?.cancel();
    _timer = null;
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
    _timer?.cancel();
    super.dispose();
  }
}
