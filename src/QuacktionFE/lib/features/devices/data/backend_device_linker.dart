import 'package:quacktion_frontend/core/networking/backend_api.dart';

class BackendDeviceLink {
  const BackendDeviceLink({required this.deviceId, this.credential});

  final String deviceId;

  /// Returned once by the backend. Keep it in memory and immediately pass it
  /// to the speaker over the encrypted BLE setup channel.
  final String? credential;
}

class BackendDeviceLinker {
  BackendDeviceLinker(this.api);

  final BackendApi api;

  Future<BackendDeviceLink> link({
    required String identifier,
    required String name,
    required String firmwareVersion,
    required String speakerBackendState,
  }) async {
    final devices = await api.devices();
    Map<String, dynamic>? existing;
    for (final device in devices) {
      if (device['deviceIdentifier']?.toString() == identifier) {
        existing = device;
        break;
      }
    }

    if (existing != null) {
      final id = existing['id']?.toString();
      if (id == null || id.isEmpty) {
        throw StateError('The backend returned a speaker without an ID.');
      }
      final unpaired = existing['status']?.toString() == 'Unpaired';
      if (!unpaired && speakerBackendState != 'unlinked') {
        // The speaker already holds a backend credential. The backend never
        // returns an existing credential, so do not rotate a working link.
        return BackendDeviceLink(deviceId: id);
      }
      if (!unpaired) await api.unpairDevice(id);
      final challenge = await api.createPairingChallenge(id);
      return _completePairing(id, challenge);
    }

    final created = await api.createDevice(
      identifier: identifier,
      name: name,
      firmwareVersion: firmwareVersion,
    );
    final device = _map(created['device']);
    final pairing = _map(created['pairing']);
    final id = device?['id']?.toString();
    if (id == null || id.isEmpty) {
      throw StateError('The backend did not return the new speaker ID.');
    }
    final challenge = pairing ?? await api.createPairingChallenge(id);
    return _completePairing(id, challenge);
  }

  Future<BackendDeviceLink> _completePairing(
    String deviceId,
    Map<String, dynamic> challenge,
  ) async {
    final challengeId = challenge['challengeId']?.toString();
    final secret = challenge['challenge']?.toString();
    if (challengeId == null || secret == null) {
      throw StateError(
        'The backend did not return a speaker pairing challenge.',
      );
    }
    final paired = await api.pairDevice(deviceId, challengeId, secret);
    final credential = paired['credential']?.toString();
    if (credential == null || credential.isEmpty) {
      throw StateError('The backend did not return the speaker credential.');
    }
    return BackendDeviceLink(deviceId: deviceId, credential: credential);
  }

  Map<String, dynamic>? _map(Object? value) =>
      value is Map ? Map<String, dynamic>.from(value) : null;
}
