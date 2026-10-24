import 'package:quacktion_frontend/core/networking/api_client.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/features/devices/data/device_repository.dart';
import 'package:quacktion_frontend/features/devices/models/speaker_device.dart';

class ApiDeviceRepository implements DeviceRepository {
  ApiDeviceRepository(this.api);
  final ApiClient api;
  @override
  Future<DeviceList> load() async {
    final result = <SpeakerDevice>[];
    for (var offset = 0; ; offset += 50) {
      final page = await api.authorized(
        'GET',
        '/api/devices?offset=$offset&limit=50',
      );
      final rows = page['items'] as List;
      result.addAll(
        rows.map((x) => SpeakerDevice.fromJson(x as Map<String, dynamic>)),
      );
      if (rows.length < 50) break;
    }
    var statusFailed = false;
    for (final device in result) {
      try {
        final status = await api.authorized(
          'GET',
          '/api/devices/${device.id}/status',
        );
        device.connected = status['connected'] as bool?;
        device.status = status['status']?.toString() ?? device.status;
        device.lastSeen =
            DateTime.tryParse(status['lastSeenAt']?.toString() ?? '')
                ?.toLocal() ??
            device.lastSeen;
      } catch (e) {
        if (e is ApiException && e.status == 401) rethrow;
        statusFailed = true;
      }
    }
    return DeviceList(result, statusIncomplete: statusFailed);
  }

  @override
  Future<void> rename(SpeakerDevice device, String name) async {
    await api.authorized(
      'PATCH',
      '/api/devices/${device.id}',
      body: {'name': name.trim(), 'firmwareVersion': device.firmwareVersion},
    );
  }

  @override
  Future<void> remove(SpeakerDevice device) async {
    await api.authorized('DELETE', '/api/devices/${device.id}');
  }
}
