import 'dart:convert';
import 'dart:math';

import 'package:flutter/services.dart';

class NearbySpeaker {
  const NearbySpeaker({
    required this.address,
    required this.name,
    required this.rssi,
  });

  /// The setup-window BLE address is only a transient handle, never an identity.
  final String address;
  final String name;
  final int rssi;

  factory NearbySpeaker.fromMap(Map<Object?, Object?> map) => NearbySpeaker(
    address: map['address']! as String,
    name: map['name']! as String,
    rssi: map['rssi']! as int,
  );
}

class WifiNetwork {
  const WifiNetwork({
    required this.ssid,
    required this.rssi,
    required this.auth,
    required this.supported,
  });

  final String ssid;
  final int rssi;
  final String auth;
  final bool supported;
}

/// Encrypted BLE provisioning against the Quacktion firmware's custom endpoints.
abstract interface class DeviceProvisioning {
  Future<List<NearbySpeaker>> scan();
  Future<Map<String, dynamic>> connect(NearbySpeaker speaker);
  Future<Map<String, dynamic>> exchange(
    String endpoint,
    Map<String, dynamic> payload,
  );
  Future<void> disconnect();
  Future<bool> isDppSupported();
  Future<void> openDpp(String uri);
}

class FirmwareDeviceProvisioning implements DeviceProvisioning {
  FirmwareDeviceProvisioning({MethodChannel? channel})
    : _channel =
          channel ?? const MethodChannel('quacktion/speaker_provisioning');

  final MethodChannel _channel;

  @override
  Future<List<NearbySpeaker>> scan() async {
    final rows = await _channel.invokeListMethod<Object?>('scan');
    return (rows ?? const [])
        .whereType<Map<Object?, Object?>>()
        .map(NearbySpeaker.fromMap)
        .toList(growable: false);
  }

  @override
  Future<Map<String, dynamic>> connect(NearbySpeaker speaker) async {
    final result = await _channel.invokeMethod<String>('connect', {
      'address': speaker.address,
    });
    return Map<String, dynamic>.from(jsonDecode(result ?? '{}') as Map);
  }

  @override
  Future<Map<String, dynamic>> exchange(
    String endpoint,
    Map<String, dynamic> payload,
  ) async {
    final result = await _channel.invokeMethod<String>('exchange', {
      'endpoint': endpoint,
      'payload': jsonEncode(payload),
    });
    return Map<String, dynamic>.from(jsonDecode(result ?? '{}') as Map);
  }

  @override
  Future<void> disconnect() => _channel.invokeMethod<void>('disconnect');

  @override
  Future<bool> isDppSupported() async =>
      await _channel.invokeMethod<bool>('isDppSupported') ?? false;

  @override
  Future<void> openDpp(String uri) =>
      _channel.invokeMethod<void>('openDpp', {'uri': uri});
}

String newProvisioningRequestId() {
  const alphabet =
      'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
  final random = Random.secure();
  return List.generate(
    24,
    (_) => alphabet[random.nextInt(alphabet.length)],
  ).join();
}
