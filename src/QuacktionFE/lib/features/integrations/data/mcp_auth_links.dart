import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

/// Native code validates the completion URL; only a navigation signal crosses
/// the channel. Keep it pending until a signed-in shell can consume it.
class McpAuthLinks extends ChangeNotifier {
  static const _channel = MethodChannel('quacktion/external_auth');
  bool _pending = false, _disposed = false, _initialized = false;

  bool get pending => _pending;

  Future<void> initialize() async {
    if (_initialized || _disposed) return;
    _initialized = true;
    _channel.setMethodCallHandler((call) async {
      if (call.method == 'completionAvailable') await _readPending();
    });
    await _readPending();
  }

  Future<void> _readPending() async {
    if (_disposed) return;
    try {
      final available = await _channel.invokeMethod<bool>(
        'takePendingCompletion',
      );
      if (!_disposed && available == true && !_pending) {
        _pending = true;
        notifyListeners();
      }
    } on MissingPluginException {
      // Android-only feature; other runtimes can still refresh on resume.
    } on PlatformException {
      // A failed link handshake must not prevent sign-in or app startup.
    }
  }

  void consume() => _pending = false;

  @override
  void dispose() {
    _disposed = true;
    _channel.setMethodCallHandler(null);
    super.dispose();
  }
}
