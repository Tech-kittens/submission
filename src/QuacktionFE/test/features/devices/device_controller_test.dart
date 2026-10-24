import 'package:quacktion_frontend/features/devices/data/api_device_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/features/devices/application/device_controller.dart';

import '../../helpers/fake_api.dart';

void main() {
  late Api api;
  late DeviceController store;
  final device = {
    'id': 'speaker',
    'deviceIdentifier': 'physical-id',
    'name': 'Duck',
    'firmwareVersion': '23ddd7d',
    'status': 'Online',
  };
  setUp(() {
    api = Api();
    store = DeviceController(ApiDeviceRepository(api));
  });
  tearDown(() {
    store.dispose();
    api.dispose();
  });
  test(
    'uses actual control connection status instead of cached Online state',
    () async {
      api.respond = (_, path) async => path.endsWith('/status')
          ? {'connected': false, 'status': 'Offline'}
          : {
              'items': [device],
            };
      await store.refresh();
      expect(store.devices.single.statusLabel, 'Offline');
      expect(store.devices.single.firmwareVersion, '23ddd7d');
    },
  );
  test(
    'failed status check is unknown, not falsely offline or online',
    () async {
      api.respond = (_, path) async {
        if (path.endsWith('/status')) {
          throw const ApiException('Unavailable', 503);
        }
        return {
          'items': [device],
        };
      };
      await store.refresh();
      expect(store.devices.single.statusLabel, 'Connection unknown');
      expect(store.error, isNotNull);
    },
  );
  test(
    'rename preserves firmware metadata and blocked removal keeps the device',
    () async {
      api.respond = (method, path) async {
        if (method == 'DELETE') throw const ApiException('Conflict', 409);
        if (method == 'PATCH') return device;
        if (path.endsWith('/status')) {
          return {'connected': false, 'status': 'Offline'};
        }
        return {
          'items': [device],
        };
      };
      await store.refresh();
      expect(await store.rename(store.devices.single, ' Kitchen '), true);
      final patch = api.calls.singleWhere((x) => x.method == 'PATCH');
      expect(patch.body, {'name': 'Kitchen', 'firmwareVersion': '23ddd7d'});
      expect(await store.remove(store.devices.single), false);
      expect(store.devices.length, 1);
      expect(store.error, contains('reserved session'));
    },
  );
}
