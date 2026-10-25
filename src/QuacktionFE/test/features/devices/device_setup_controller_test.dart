import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/features/devices/application/device_setup_controller.dart';
import 'package:quacktion_frontend/features/devices/data/speaker_provisioning.dart';
import 'package:quacktion_frontend/features/devices/data/saved_speaker_store.dart';
import 'package:quacktion_frontend/features/devices/data/backend_device_linker.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';

import '../../helpers/fake_api.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late FakeProvisioning provisioning;
  late MemorySpeakerStore speakerStore;
  late DeviceSetupController controller;

  setUp(() {
    provisioning = FakeProvisioning();
    speakerStore = MemorySpeakerStore();
    controller = DeviceSetupController(
      provisioning,
      speakerStore: speakerStore,
      pollInterval: Duration.zero,
    );
  });

  tearDown(() => controller.dispose());

  test('registers an approved speaker and installs its one-time backend credential', () async {
    final api = Api();
    api.respond = (method, path) async {
      if (method == 'GET' && path.startsWith('/api/devices')) {
        return {'items': <dynamic>[]};
      }
      if (method == 'POST' && path == '/api/devices') {
        return {
          'device': {'id': 'backend-device-1'},
          'pairing': {
            'challengeId': 'challenge-id',
            'challenge': 'one-time-challenge',
          },
        };
      }
      if (method == 'POST' && path == '/api/devices/backend-device-1/pair') {
        return {
          'deviceId': 'backend-device-1',
          'credential': List.filled(64, 'a').join(),
        };
      }
      return <String, dynamic>{};
    };
    final integrated = DeviceSetupController(
      provisioning,
      speakerStore: speakerStore,
      backendLinker: BackendDeviceLinker(BackendApi(api)),
      pollInterval: Duration.zero,
    );
    try {
      await integrated.scan();
      await integrated.pair(integrated.nearby.single);
      expect(api.calls, isEmpty);
      await integrated.refreshStatus();

      expect(provisioning.endpoints, isNot(contains('device.pair')));
      expect(api.calls.map((call) => call.path), [
        '/api/devices?offset=0&limit=50',
        '/api/devices',
        '/api/devices/backend-device-1/pair',
      ]);
      final configure = provisioning.requests.singleWhere(
        (request) => request['endpoint'] == 'backend.configure',
      );
      expect(
        configure['payload'],
        containsPair('deviceId', 'backend-device-1'),
      );
      expect(
        configure['payload'],
        containsPair('credential', List.filled(64, 'a').join()),
      );
      expect(speakerStore.speaker?.backendDeviceId, 'backend-device-1');
      expect(speakerStore.speaker?.firmwareVersion, '0.2.0');
    } finally {
      integrated.dispose();
      api.dispose();
    }
  });

  test(
    'discovers, connects securely, and waits for physical approval',
    () async {
      await controller.scan();
      expect(controller.nearby.single.name, 'Quacktion-A1B2');

      await controller.pair(controller.nearby.single);

      expect(controller.stage, PairingStage.awaitingApproval);
      expect(controller.approved, isFalse);
      expect(provisioning.connected, isTrue);
      expect(provisioning.endpoints, contains('device.info'));

      await controller.refreshStatus();
      expect(controller.stage, PairingStage.paired);
      expect(controller.approved, isTrue);
    },
  );

  test(
    'remembers the speaker identity and reconnects to that device',
    () async {
      await controller.scan();
      await controller.pair(controller.nearby.single);
      expect(speakerStore.speaker?.identifier, 'quack-a1b2c3d4e5f6');
      expect(speakerStore.speaker!.toJson(), isNot(contains('appToken')));

      final reopened = DeviceSetupController(
        provisioning,
        speakerStore: speakerStore,
        pollInterval: Duration.zero,
      );
      await Future<void>.delayed(Duration.zero);
      expect(reopened.savedSpeaker?.name, 'Quacktion-A1B2');

      await reopened.reconnect();

      expect(reopened.deviceIdentifier, 'quack-a1b2c3d4e5f6');
      expect(reopened.selected?.name, 'Quacktion-A1B2');
      expect(reopened.approved, isFalse);
      await reopened.refreshStatus();
      expect(reopened.approved, isTrue);
      expect(
        provisioning.endpoints
            .where((endpoint) => endpoint == 'device.pair')
            .length,
        0,
      );
      reopened.dispose();
    },
  );

  test('loading a saved speaker does not connect in the background', () async {
    speakerStore.speaker = const SavedSpeaker(
      identifier: 'quack-a1b2c3d4e5f6',
      name: 'Quacktion-A1B2',
    );
    final reopened = DeviceSetupController(
      provisioning,
      speakerStore: speakerStore,
    );
    await Future<void>.delayed(Duration.zero);
    expect(reopened.savedSpeaker, isNotNull);
    expect(provisioning.connected, isFalse);
    expect(provisioning.scans, 0);
    reopened.dispose();
  });

  test('old saved metadata loads without using its obsolete pairing token', () {
    final speaker = SavedSpeaker.fromJson({
      'identifier': 'quack-a1b2c3d4e5f6',
      'name': 'Quacktion-A1B2',
      'appToken': 'legacy-token',
      'backendDeviceId': 'backend-id',
    });
    expect(speaker.backendDeviceId, 'backend-id');
    expect(speaker.toJson(), isNot(contains('appToken')));
  });

  test(
    'finish requires Wi-Fi and backend online, then closes via setup.complete',
    () async {
      await controller.scan();
      await controller.pair(controller.nearby.single);
      await controller.refreshStatus();
      await controller.finishConfiguration();
      expect(provisioning.endpoints, isNot(contains('setup.complete')));
      expect(provisioning.endpoints, isNot(contains('setup.cancel')));
      provisioning.wifiConnected = true;
      await controller.refreshStatus();
      expect(controller.readyToComplete, isTrue);
      await controller.finishConfiguration();
      expect(provisioning.endpoints, contains('setup.complete'));
      expect(provisioning.connected, isFalse);
      expect(speakerStore.speaker, isNotNull);
    },
  );

  test(
    'cancel explicitly uses setup.cancel and preserves remembered identity',
    () async {
      await controller.scan();
      await controller.pair(controller.nearby.single);
      await controller.refreshStatus();
      await controller.cancelSetup();
      expect(provisioning.endpoints, contains('setup.cancel'));
      expect(provisioning.connected, isFalse);
      expect(speakerStore.speaker, isNotNull);
    },
  );

  test('closing the device screen disconnects BLE without unpairing', () async {
    await controller.scan();
    await controller.pair(controller.nearby.single);
    await controller.refreshStatus();

    await controller.remove();

    expect(provisioning.connected, isFalse);
    expect(provisioning.endpoints, isNot(contains('setup.cancel')));
    expect(speakerStore.speaker?.identifier, 'quack-a1b2c3d4e5f6');
  });

  test('forget only removes the locally remembered speaker without unsupported firmware calls', () async {
    await controller.scan();
    await controller.pair(controller.nearby.single);
    await controller.refreshStatus();
    await controller.remove();
    final forgotten = await controller.forgetSavedSpeaker();
    expect(forgotten, isTrue);
    expect(provisioning.endpoints, isNot(contains('device.unpair')));
    expect(speakerStore.speaker, isNull);
    expect(provisioning.connected, isFalse);
  });

  test(
    'scans speaker-visible Wi-Fi and waits for a real connected status',
    () async {
      await controller.scan();
      await controller.pair(controller.nearby.single);
      await controller.refreshStatus();
      await controller.scanWifi();

      expect(controller.networks.single.ssid, 'Kitchen Wi-Fi');
      expect(controller.networks.single.supported, isTrue);

      await controller.configureWifi('Kitchen Wi-Fi', 'correct horse battery');

      expect(controller.stage, PairingStage.wifiConfigured);
      expect(controller.wifiState, 'connected');
      expect(controller.wifiNetwork, 'Kitchen Wi-Fi');
      final configure = provisioning.requests.singleWhere(
        (request) => request['endpoint'] == 'wifi.configure',
      );
      expect(
        configure['payload'],
        containsPair('password', 'correct horse battery'),
      );
    },
  );

  test('uses DPP URI from speaker and waits for Wi-Fi connection', () async {
    provisioning.dppAvailable = true;
    await controller.scan();
    await controller.pair(controller.nearby.single);
    await controller.refreshStatus();

    expect(controller.dppSupported, isTrue);
    await controller.configureWifiWithDpp();

    expect(provisioning.openedDppUri, startsWith('DPP:'));
    expect(
      provisioning.requests.map((request) => request['endpoint']),
      contains('wifi.dpp.start'),
    );
    expect(controller.wifiState, 'connected');
    expect(controller.wifiNetwork, 'Kitchen Wi-Fi');
    expect(controller.stage, PairingStage.wifiConfigured);
  });

  test(
    'disconnects Wi-Fi and forgets the saved network through firmware',
    () async {
      await controller.scan();
      await controller.pair(controller.nearby.single);
      await controller.refreshStatus();
      provisioning.wifiConnected = true;
      await controller.refreshStatus();

      await controller.forgetWifi();

      expect(controller.wifiState, 'unconfigured');
      expect(controller.wifiNetwork, isNull);
      expect(controller.stage, PairingStage.paired);
      expect(provisioning.endpoints, contains('wifi.forget'));
      expect(provisioning.connected, isTrue);
    },
  );

  test(
    'recovers when firmware reports a Wi-Fi scan is already running',
    () async {
      await controller.scan();
      await controller.pair(controller.nearby.single);
      await controller.refreshStatus();
      provisioning.busyOnScanStart = true;

      await controller.scanWifi();

      expect(controller.error, isNull);
      expect(controller.networks.single.ssid, 'Kitchen Wi-Fi');
      expect(provisioning.endpoints, contains('wifi.scan'));
    },
  );
}

class FakeProvisioning implements DeviceProvisioning {
  bool dppAvailable = false;
  bool dppWifiReady = false;
  bool busyOnScanStart = false;
  bool wifiConnected = false;
  int scanBusyStatusReads = 0;
  String? openedDppUri;
  bool connected = false;
  int statusReads = 0;
  bool lastScanWasAutomatic = false;
  int wifiStatuses = 0;
  final endpoints = <String>[];
  final requests = <Map<String, dynamic>>[];
  int scans = 0;

  @override
  Future<List<NearbySpeaker>> scan({bool autoReconnect = false}) async {
    lastScanWasAutomatic = autoReconnect;
    scans++;
    return const [
      NearbySpeaker(
        address: 'ephemeral-address',
        name: 'Quacktion-A1B2',
        rssi: -42,
      ),
    ];
  }

  @override
  Future<Map<String, dynamic>> connect(NearbySpeaker speaker) async {
    connected = true;
    statusReads = 0;
    return {
      'prov': {'sec_ver': 2, 'sec_patch_ver': 1},
      'quacktion': {'protocolVersion': 1},
    };
  }

  @override
  Future<Map<String, dynamic>> exchange(
    String endpoint,
    Map<String, dynamic> payload,
  ) async {
    const supported = {
      'device.info',
      'device.identify',
      'setup.status',
      'wifi.scan',
      'wifi.configure',
      'wifi.dpp.start',
      'wifi.dpp.cancel',
      'backend.configure',
      'setup.complete',
      'setup.cancel',
      'wifi.forget',
    };
    if (!supported.contains(endpoint)) {
      throw StateError('Unsupported endpoint: $endpoint');
    }
    if (payload.containsKey('appToken')) {
      throw StateError('Unexpected app token');
    }
    endpoints.add(endpoint);
    requests.add({'endpoint': endpoint, 'payload': payload});
    if (endpoint == 'device.info') {
      return {
        'ok': true,
        'deviceIdentifier': 'quack-a1b2c3d4e5f6',
        'firmwareVersion': '0.2.0',
        'capabilities': {'dpp': dppAvailable},
      };
    }
    if (endpoint == 'setup.status') {
      statusReads++;
      if (wifiConnected) {
        return {
          'ok': true,
          'approval': 'approved',
          'wifiState': 'connected',
          'backendState': 'online',
          'ssid': 'Kitchen Wi-Fi',
        };
      }
      if (dppWifiReady) {
        return {
          'ok': true,
          'approval': 'approved',
          'wifiState': 'connected',
          'backendState': 'unlinked',
          'ssid': 'Kitchen Wi-Fi',
        };
      }
      if (statusReads == 1) {
        return {
          'ok': true,
          'approval': 'pending',
          'wifiState': 'unconfigured',
          'backendState': 'unlinked',
        };
      }
      if (busyOnScanStart && scanBusyStatusReads < 3) {
        scanBusyStatusReads++;
        return {
          'ok': true,
          'approval': 'approved',
          'wifiState': scanBusyStatusReads == 2 ? 'scanning' : 'unconfigured',
          'backendState': 'unlinked',
        };
      }
      if (payload['id'] != null && wifiStatuses >= 2) {
        return {
          'ok': true,
          'approval': 'approved',
          'wifiState': 'connected',
          'backendState': 'unlinked',
          'ssid': 'Kitchen Wi-Fi',
        };
      }
      wifiStatuses++;
      return {
        'ok': true,
        'approval': 'approved',
        'wifiState': wifiStatuses == 1 ? 'scanning' : 'unconfigured',
        'backendState': 'unlinked',
      };
    }
    if (endpoint == 'wifi.scan' && payload['action'] == 'results') {
      return {
        'ok': true,
        'networks': [
          {
            'ssid': 'Kitchen Wi-Fi',
            'rssi': -49,
            'auth': 'wpa2_psk',
            'supported': true,
          },
        ],
        'next': null,
      };
    }
    if (endpoint == 'wifi.dpp.start') {
      return {'ok': true, 'uri': 'DPP:C:6;M:001122334455;K:abc;;'};
    }
    if (endpoint == 'wifi.forget') {
      wifiConnected = false;
      return {'ok': true};
    }
    if (endpoint == 'wifi.scan' &&
        payload['action'] == 'start' &&
        busyOnScanStart) {
      return {'ok': false, 'error': 'busy'};
    }
    return {'ok': true};
  }

  @override
  Future<void> disconnect() async => connected = false;

  @override
  Future<bool> isDppSupported() async => dppAvailable;

  @override
  Future<void> openDpp(String uri) async {
    openedDppUri = uri;
    dppWifiReady = true;
  }
}

class MemorySpeakerStore implements SavedSpeakerStore {
  SavedSpeaker? speaker;

  @override
  Future<SavedSpeaker?> read() async => speaker;

  @override
  Future<void> write(SavedSpeaker value) async => speaker = value;

  @override
  Future<void> delete() async => speaker = null;
}
