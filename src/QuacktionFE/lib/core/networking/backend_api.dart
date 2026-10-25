import 'dart:typed_data';

import 'package:quacktion_frontend/core/networking/api_client.dart';

/// Authenticated wrappers for the account, device, agent, session and
/// Quacktion endpoints published by the backend Swagger document.
class BackendApi {
  BackendApi(this.client);
  final ApiClient client;

  Future<List<Map<String, dynamic>>> meetingSegments(String id) =>
      _items('/api/meetings/${_id(id)}/segments');
  Future<Map<String, dynamic>> meetingStatus(String id) =>
      client.authorized('GET', '/api/meetings/${_id(id)}/status');
  Future<Map<String, dynamic>> startPhoneMeeting({
    required String name,
    required String type,
    required bool recordingConsent,
  }) => client.authorized(
    'POST',
    '/api/meetings/start',
    body: {'name': name, 'type': type, 'recordingConsent': recordingConsent},
  );
  Future<Map<String, dynamic>> uploadMeetingAudio(
    String id,
    int startMs,
    Uint8List bytes,
  ) => client.authorized(
    'POST',
    '/api/meetings/${_id(id)}/audio/segments?startMs=$startMs',
    binaryBody: bytes,
  );
  Future<Map<String, dynamic>> endMeeting(String id) =>
      client.authorized('POST', '/api/meetings/${_id(id)}/end');

  Future<List<Map<String, dynamic>>> mcpProviders() =>
      _mcpList('/api/mcp/providers');
  Future<List<Map<String, dynamic>>> mcpConnections() =>
      _mcpList('/api/mcp/connections');
  Future<Map<String, dynamic>> connectMcp(String provider) => client.authorized(
    'POST',
    '/api/mcp/connections/${_id(provider)}/connect',
  );
  Future<List<Map<String, dynamic>>> mcpTools(String id) =>
      _mcpList('/api/mcp/connections/${_id(id)}/tools');
  Future<Map<String, dynamic>> setMcpTools(String id, List<String> tools) =>
      client.authorized(
        'PATCH',
        '/api/mcp/connections/${_id(id)}/tools',
        body: {'enabledTools': tools},
      );
  Future<Map<String, dynamic>> mcpHealth(String id) =>
      client.authorized('GET', '/api/mcp/connections/${_id(id)}/health');
  Future<void> disconnectMcp(String id) async {
    await client.authorized('DELETE', '/api/mcp/connections/${_id(id)}');
  }

  Future<Map<String, dynamic>> selectMcpAccount(
    String connectionId,
    String taskId,
    List<String> services,
  ) => client.authorized(
    'POST',
    '/api/mcp/connections/${_id(connectionId)}/tasks/${_id(taskId)}/select-account',
    body: {'services': services},
  );

  Future<List<Map<String, dynamic>>> _mcpList(String path) async {
    final response = await client.authorized('GET', path);
    final rows = response['items'];
    if (rows is! List) throw const FormatException('Invalid MCP list response');
    return rows
        .whereType<Map>()
        .map((row) => Map<String, dynamic>.from(row))
        .toList();
  }

  Future<Map<String, dynamic>> getAccount() =>
      client.authorized('GET', '/api/accounts/me');
  Future<Map<String, dynamic>> updateAccount(String name) => client.authorized(
    'PATCH',
    '/api/accounts/me',
    body: {'name': name.trim()},
  );
  Future<void> deleteAccount() async {
    await client.authorized('DELETE', '/api/accounts/me');
  }

  Future<void> changePassword(
    String currentPassword,
    String newPassword,
  ) async {
    await client.authorized(
      'PUT',
      '/api/accounts/me/password',
      body: {'currentPassword': currentPassword, 'newPassword': newPassword},
    );
  }

  Future<List<Map<String, dynamic>>> agents() => _items('/api/agents');
  Future<List<Map<String, dynamic>>> devices() => _items('/api/devices');
  Future<Map<String, dynamic>> device(String id) =>
      client.authorized('GET', '/api/devices/${_id(id)}');
  Future<Map<String, dynamic>> createDevice({
    required String identifier,
    required String name,
    String firmwareVersion = '',
  }) => client.authorized(
    'POST',
    '/api/devices',
    body: {
      'deviceIdentifier': identifier,
      'name': name,
      'firmwareVersion': firmwareVersion,
    },
  );
  Future<Map<String, dynamic>> createPairingChallenge(String id) =>
      client.authorized('POST', '/api/devices/${_id(id)}/pairing-challenges');
  Future<Map<String, dynamic>> pairDevice(
    String id,
    String challengeId,
    String challenge,
  ) => client.authorized(
    'POST',
    '/api/devices/${_id(id)}/pair',
    body: {'challengeId': challengeId, 'challenge': challenge},
  );
  Future<Map<String, dynamic>> deviceStatus(String id) =>
      client.authorized('GET', '/api/devices/${_id(id)}/status');
  Future<Map<String, dynamic>> updateDevice(
    String id, {
    required String name,
    required String firmwareVersion,
  }) => client.authorized(
    'PATCH',
    '/api/devices/${_id(id)}',
    body: {'name': name.trim(), 'firmwareVersion': firmwareVersion},
  );
  Future<void> unpairDevice(String id) async {
    await client.authorized('POST', '/api/devices/${_id(id)}/unpair');
  }

  Future<void> deleteDevice(String id) async {
    await client.authorized('DELETE', '/api/devices/${_id(id)}');
  }

  Future<List<Map<String, dynamic>>> sessions() => _items('/api/sessions');
  Future<Map<String, dynamic>> session(String id) =>
      client.authorized('GET', '/api/sessions/${_id(id)}');
  Future<Map<String, dynamic>> createSession({
    String? title,
    String? deviceId,
    String? initialPrompt,
    String promptFormat = 'text',
    String? idempotencyKey,
  }) => client.authorized(
    'POST',
    '/api/sessions',
    body: {
      'title': title,
      'deviceId': deviceId,
      'initialPrompt': initialPrompt,
      'promptFormat': promptFormat,
    },
    headers: idempotencyKey == null
        ? null
        : {'Idempotency-Key': idempotencyKey},
  );
  Future<Map<String, dynamic>> startAudioSession({
    required String name,
    required String requestId,
  }) => client.authorized(
    'POST',
    '/api/sessions/start',
    body: {'name': name, 'requestId': requestId},
    headers: {'Idempotency-Key': requestId},
  );
  Future<Map<String, dynamic>> resumeSession(String id) =>
      client.authorized('POST', '/api/sessions/${_id(id)}/resume');

  Future<Map<String, dynamic>> updateSession(String id, String title) =>
      client.authorized(
        'PATCH',
        '/api/sessions/${_id(id)}',
        body: {'title': title.trim()},
      );
  Future<void> deleteSession(String id) async {
    await client.authorized('DELETE', '/api/sessions/${_id(id)}');
  }

  Future<Map<String, dynamic>> startSession(
    String id, {
    String? initialPrompt,
    String promptFormat = 'text',
    String? requestId,
  }) => client.authorized(
    'POST',
    '/api/sessions/${_id(id)}/start',
    body: {
      'initialPrompt': initialPrompt,
      'promptFormat': promptFormat,
      'requestId': requestId,
    },
    headers: requestId == null ? null : {'Idempotency-Key': requestId},
  );

  /// Creates an audio-first session using the account Bearer token.
  Future<Map<String, dynamic>> endSession(String id, {String? requestId}) =>
      client.authorized(
        'POST',
        '/api/sessions/${_id(id)}/end',
        headers: requestId == null ? null : {'Idempotency-Key': requestId},
      );
  Future<List<Map<String, dynamic>>> transcript(String id) =>
      _items('/api/sessions/${_id(id)}/transcript');
  Future<List<Map<String, dynamic>>> sessionQuacktions(String id) =>
      _items('/api/sessions/${_id(id)}/quacktions');
  Future<Map<String, dynamic>> proposeQuacktion(
    String sessionId, {
    required String actionType,
    required String parametersJson,
  }) => client.authorized(
    'POST',
    '/api/sessions/${_id(sessionId)}/quacktions',
    body: {'actionType': actionType, 'parametersJson': parametersJson},
  );

  Future<List<Map<String, dynamic>>> quacktions() => _items('/api/quacktions');
  Future<Map<String, dynamic>> quacktion(String id) =>
      client.authorized('GET', '/api/quacktions/${_id(id)}');
  Future<Map<String, dynamic>> approveQuacktion(String id, String digest) =>
      client.authorized(
        'POST',
        '/api/quacktions/${_id(id)}/approve',
        body: {'actionDigest': digest},
      );
  Future<Map<String, dynamic>> rejectQuacktion(String id, String digest) =>
      client.authorized(
        'POST',
        '/api/quacktions/${_id(id)}/reject',
        body: {'actionDigest': digest},
      );
  Future<Map<String, dynamic>> cancelQuacktion(String id) =>
      client.authorized('POST', '/api/quacktions/${_id(id)}/cancel');
  Future<Map<String, dynamic>> retryQuacktion(String id) =>
      client.authorized('POST', '/api/quacktions/${_id(id)}/retry');
  Future<Map<String, dynamic>> health({bool ready = false}) =>
      client.authorized('GET', ready ? '/health/ready' : '/health/live');

  Future<List<Map<String, dynamic>>> _items(String path) async {
    final result = <Map<String, dynamic>>[];
    for (var offset = 0; ; offset += 50) {
      final page = await client.authorized(
        'GET',
        '$path?offset=$offset&limit=50',
      );
      final rows = page['items'];
      if (rows is! List) return result;
      result.addAll(
        rows.whereType<Map>().map((row) => Map<String, dynamic>.from(row)),
      );
      if (rows.length < 50) return result;
    }
  }

  String _id(String id) => Uri.encodeComponent(id);
}
