import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

abstract interface class MeetingLinks {
  Future<String?> read(String sessionId);
  Future<void> save(String sessionId, String meetingId);
}

class MemoryMeetingLinks implements MeetingLinks {
  final _links = <String, String>{};
  @override
  Future<String?> read(String sessionId) async => _links[sessionId];
  @override
  Future<void> save(String sessionId, String meetingId) async {
    _links[sessionId] = meetingId;
  }
}

/// Session/meeting IDs only, segregated by signed-in account and API origin.
class SecureMeetingLinks implements MeetingLinks {
  SecureMeetingLinks(this.scope);
  final String scope;
  final _storage = const FlutterSecureStorage();
  String _key(String sessionId) =>
      'quacktion.meeting.${base64Url.encode(utf8.encode('$scope:$sessionId'))}';
  @override
  Future<String?> read(String sessionId) => _storage.read(key: _key(sessionId));
  @override
  Future<void> save(String sessionId, String meetingId) =>
      _storage.write(key: _key(sessionId), value: meetingId);
}
