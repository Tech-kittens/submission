import 'dart:convert';

List<String> stringList(dynamic value) =>
    value is List ? value.whereType<String>().toList() : [];

String mcpProviderLabel(String provider) => switch (provider.toLowerCase()) {
  'google' => 'Google',
  'github' => 'GitHub',
  'jira' => 'Jira',
  _ => provider,
};

String serviceLabel(String id) => switch (id) {
  'google:calendar' => 'Google Calendar',
  'google:gmail' => 'Gmail',
  'google:meet' => 'Google Meet',
  _ => id,
};

class McpProvider {
  McpProvider(Map<String, dynamic> data)
    : id = data['id'] as String,
      name = data['displayName']?.toString() ?? data['id'].toString();
  final String id, name;
  String get label => switch (id.toLowerCase()) {
    'google' || 'github' || 'jira' => mcpProviderLabel(id),
    _ => name,
  };
}

class McpConnection {
  McpConnection(Map<String, dynamic> data)
    : id = data['id'] as String,
      provider = data['provider']?.toString() ?? '',
      _displayName = data['displayName']?.toString() ?? '',
      status = data['status']?.toString() ?? 'Unknown',
      identityVerified = data['accountIdentityVerified'] == true,
      services = stringList(data['enabledServices']),
      enabledTools = stringList(data['enabledTools']);
  final String id, provider, _displayName, status;
  final bool identityVerified;
  final List<String> services, enabledTools;
  bool get connected => status == 'Connected';
  String get name {
    final label = mcpProviderLabel(provider);
    if (_displayName.trim().isEmpty) {
      return label.isEmpty ? 'Connected account' : '$label account';
    }
    // Older backend versions give every provider this generated Google name.
    // Correct only that exact format; preserve custom names and account labels.
    if (provider.isNotEmpty &&
        provider.toLowerCase() != 'google' &&
        RegExp(r'^Google connection(?: [0-9a-f]{6})?$')
            .hasMatch(_displayName)) {
      return _displayName.replaceFirst('Google', label);
    }
    return _displayName;
  }
}

class McpTool {
  McpTool(Map<String, dynamic> data)
    : name = data['name'] as String,
      serverId = data['serverId'] as String,
      description = data['description']?.toString() ?? '',
      schema = data['inputSchema'],
      readOnly = data['readOnly'] == true,
      destructive = data['destructive'] == true;
  final String name, serverId, description;
  final dynamic schema;
  final bool readOnly, destructive;
  String get key => '$serverId/$name';
}

class McpTask {
  McpTask(Map<String, dynamic> data)
    : id = data['id'] as String,
      sessionId = data['sessionId']?.toString() ?? '',
      actionType = data['actionType']?.toString() ?? '',
      digest = data['actionDigest']?.toString() ?? '',
      status = data['status']?.toString() ?? 'Unknown',
      parameters = _parameters(data['parametersJson']),
      expiresAt = DateTime.tryParse(
        (data['approval'] is Map ? data['approval']['expiresAt'] : null)
                ?.toString() ??
            '',
      ),
      createdAt =
          DateTime.tryParse(data['createdAt']?.toString() ?? '') ??
          DateTime.fromMillisecondsSinceEpoch(0);
  final String id, sessionId, actionType, digest, status;
  final Map<String, dynamic> parameters;
  final DateTime? expiresAt;
  final DateTime createdAt;
  bool get pending => status == 'PendingApproval';
  bool get isAnnouncement => actionType == 'meeting.announce';
  bool get requiresAccount =>
      actionType == 'mcp.request' || actionType == 'internal.mcp_read';
  bool get terminal =>
      const {'Completed', 'Failed', 'Cancelled', 'Rejected'}.contains(status);
  bool expired(DateTime now) => expiresAt != null && !expiresAt!.isAfter(now);
  String get summary => isAnnouncement
      ? 'Spoken announcement'
      : parameters['query']?.toString() ??
            (parameters['toolName'] ?? parameters['ToolName'])?.toString() ??
            actionType;
  List<String> get selectedServices => stringList(parameters['services']);
  String? get connectionId =>
      (parameters['connectionId'] ?? parameters['ConnectionId']) as String?;
  String get statusLabel => switch (status) {
    'PendingApproval' => 'Needs your approval',
    'Queued' => 'Queued',
    'InProgress' => 'In progress',
    'WaitingForTools' => 'Waiting for operation approval',
    _ => status,
  };

  static Map<String, dynamic> _parameters(dynamic value) {
    try {
      final decoded = value is String ? jsonDecode(value) : value;
      if (decoded is Map) return Map<String, dynamic>.from(decoded);
    } on FormatException {
      /* Display an unavailable request instead of interpreting malformed data. */
    }
    return {};
  }
}
