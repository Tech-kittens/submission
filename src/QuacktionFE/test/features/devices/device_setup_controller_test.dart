import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/features/devices/application/device_setup_controller.dart';
import 'package:quacktion_frontend/features/devices/data/speaker_provisioning.dart';

void main() {
  late FakeProvisioning provisioning;
  late DeviceSetupController controller;

  setUp(() {
    provisioning = FakeProvisioning();
    controller = DeviceSetupController(
      provisioning,
      pollInterval: Duration.zero,
    );
  });

  tearDown(() => controller.dispose());

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
  int scanBusyStatusReads = 0;
  String? openedDppUri;
  bool connected = false;
  int statusReads = 0;
  int wifiStatuses = 0;
  final endpoints = <String>[];
  final requests = <Map<String, dynamic>>[];

  @override
  Future<List<NearbySpeaker>> scan() async => const [
    NearbySpeaker(
      address: 'ephemeral-address',
      name: 'Quacktion-A1B2',
      rssi: -42,
    ),
  ];

  @override
  Future<Map<String, dynamic>> connect(NearbySpeaker speaker) async {
    connected = true;
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
    endpoints.add(endpoint);
    requests.add({'endpoint': endpoint, 'payload': payload});
    if (endpoint == 'device.info') {
      return {
        'ok': true,
        'deviceIdentifier': 'quack-a1b2c3d4e5f6',
        'capabilities': {'dpp': dppAvailable},
      };
    }
    if (endpoint == 'setup.status') {
      statusReads++;
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
