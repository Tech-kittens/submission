import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

class SavedSpeaker {
  const SavedSpeaker({
    required this.identifier,
    required this.name,
    this.backendDeviceId,
    this.firmwareVersion = '',
  });

  final String identifier;
  final String name;
  final String? backendDeviceId;
  final String firmwareVersion;

  Map<String, dynamic> toJson() => {
    'identifier': identifier,
    'name': name,
    'backendDeviceId': backendDeviceId,
    'firmwareVersion': firmwareVersion,
  };

  factory SavedSpeaker.fromJson(Map<String, dynamic> json) => SavedSpeaker(
    identifier: json['identifier'] as String,
    name: json['name'] as String,
    backendDeviceId: json['backendDeviceId'] as String?,
    firmwareVersion: json['firmwareVersion'] as String? ?? '',
  );
}

abstract interface class SavedSpeakerStore {
  Future<SavedSpeaker?> read();
  Future<void> write(SavedSpeaker speaker);
  Future<void> delete();
}

class SecureSavedSpeakerStore implements SavedSpeakerStore {
  SecureSavedSpeakerStore({this._storage = const FlutterSecureStorage()});

  static const _key = 'quacktion.savedSpeaker';
  final FlutterSecureStorage _storage;

  @override
  Future<SavedSpeaker?> read() async {
    final value = await _storage.read(key: _key);
    if (value == null) return null;
    return SavedSpeaker.fromJson(jsonDecode(value) as Map<String, dynamic>);
  }

  @override
  Future<void> write(SavedSpeaker speaker) =>
      _storage.write(key: _key, value: jsonEncode(speaker.toJson()));

  @override
  Future<void> delete() => _storage.delete(key: _key);
}
