import 'dart:async';

import 'package:signalr_netcore/signalr_client.dart';

abstract interface class SessionEventClient {
  Future<void> connect();
  Future<void> join(String sessionId);
  Future<void> leave(String sessionId);
  Future<void> dispose();
}

/// Authenticated SignalR subscription for live transcript and session updates.
class SignalRSessionEventClient implements SessionEventClient {
  SignalRSessionEventClient({
    required this.baseUrl,
    required this.accessToken,
    required this.onSessionEvent,
    this.onTaskEvent,
  });

  final String baseUrl;
  final Future<String> Function() accessToken;
  final void Function(String sessionId) onSessionEvent;
  final void Function()? onTaskEvent;
  HubConnection? _connection;
  String? _joinedSessionId;
  Future<void>? _connecting;

  @override
  Future<void> connect() async {
    if (_connection?.state == HubConnectionState.Connected) return;
    if (_connecting != null) return _connecting;
    final work = _connect();
    _connecting = work;
    try {
      await work;
    } finally {
      _connecting = null;
    }
  }

  Future<void> _connect() async {
    final hubUrl = '${baseUrl.replaceFirst(RegExp(r'/+$'), '')}/hubs/sessions';
    final hub = HubConnectionBuilder()
        .withUrl(
          hubUrl,
          options: HttpConnectionOptions(
            accessTokenFactory: accessToken,
            transport: HttpTransportType.WebSockets,
          ),
        )
        .withAutomaticReconnect(retryDelays: [0, 2000, 5000, 10000, 30000])
        .build();
    _connection = hub;
    for (final event in [
      'TranscriptReceived',
      'AgentResponseReceived',
      'AgentStatusChanged',
      'SessionStatusChanged',
    ]) {
      hub.on(event, (arguments) {
        final payload = arguments != null && arguments.isNotEmpty
            ? arguments.first
            : null;
        if (payload is Map) {
          final id =
              (payload['sessionId'] ??
                      payload['SessionId'] ??
                      payload['id'] ??
                      payload['Id'])
                  ?.toString() ??
              _joinedSessionId;
          if (id != null) onSessionEvent(id);
        }
      });
    }
    for (final event in [
      'ApprovalRequested',
      'ApprovalResolved',
      'ApprovalExpired',
      'QuacktionStatusChanged',
    ]) {
      hub.on(event, (_) => onTaskEvent?.call());
    }
    hub.onreconnected(({connectionId}) async {
      final sessionId = _joinedSessionId;
      if (sessionId != null) {
        try {
          await hub.invoke('JoinSession', args: [sessionId]);
        } catch (_) {
          /* API polling recovers missed events. */
        }
        onSessionEvent(sessionId);
      }
      onTaskEvent?.call();
    });
    await hub.start();
    onTaskEvent?.call();
    final sessionId = _joinedSessionId;
    if (sessionId != null) await hub.invoke('JoinSession', args: [sessionId]);
  }

  @override
  Future<void> join(String sessionId) async {
    final previous = _joinedSessionId;
    if (previous != null && previous != sessionId) await leave(previous);
    final alreadyConnected = _connection?.state == HubConnectionState.Connected;
    _joinedSessionId = sessionId;
    await connect();
    final hub = _connection;
    if (alreadyConnected && hub?.state == HubConnectionState.Connected) {
      await hub!.invoke('JoinSession', args: [sessionId]);
    }
  }

  @override
  Future<void> leave(String sessionId) async {
    if (_joinedSessionId == sessionId) _joinedSessionId = null;
    final hub = _connection;
    if (hub?.state == HubConnectionState.Connected) {
      await hub!.invoke('LeaveSession', args: [sessionId]);
    }
  }

  @override
  Future<void> dispose() async {
    _joinedSessionId = null;
    final hub = _connection;
    _connection = null;
    if (hub != null) await hub.stop();
  }
}
