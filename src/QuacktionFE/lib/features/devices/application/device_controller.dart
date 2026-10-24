import 'package:flutter/foundation.dart';

import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/features/devices/data/device_repository.dart';
import 'package:quacktion_frontend/features/devices/models/speaker_device.dart';

export 'package:quacktion_frontend/features/devices/models/speaker_device.dart';

class DeviceController extends ChangeNotifier {
  DeviceController(this.repository);
  final DeviceRepository repository;
  List<SpeakerDevice> devices = [];
  bool loading = false, busy = false, _disposed = false;
  String? error;
  Future<void>? _refreshing;
  void _emit() {
    if (!_disposed) notifyListeners();
  }

  String _message(Object e) {
    if (e is ApiException && e.status == 409) {
      return 'The speaker has an active or reserved session. It cannot be removed until that session is released.';
    }
    return e is ApiException
        ? e.message
        : 'Could not load speakers. Please try again.';
  }

  Future<void> refresh() =>
      _refreshing ??= _load().whenComplete(() => _refreshing = null);
  Future<void> _load() async {
    loading = true;
    error = null;
    _emit();
    try {
      final result = await repository.load();
      devices = result.devices;
      if (result.statusIncomplete) error = 'Some connection statuses could not be checked. Refresh to try again.';
    } catch (e) {
      error = _message(e);
    } finally {
      loading = false;
      _emit();
    }
  }

  Future<bool> rename(SpeakerDevice device, String name) =>
      _change(() => repository.rename(device, name));
  Future<bool> remove(SpeakerDevice device) =>
      _change(() => repository.remove(device));
  Future<bool> _change(Future<void> Function() operation) async {
    if (busy || loading) return false;
    busy = true;
    error = null;
    _emit();
    try {
      await operation();
      await refresh();
      return true;
    } catch (e) {
      error = _message(e);
      return false;
    } finally {
      busy = false;
      _emit();
    }
  }

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }
}
