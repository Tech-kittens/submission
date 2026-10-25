import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/integrations/data/external_auth_browser.dart';
import 'package:quacktion_frontend/features/integrations/models/mcp_models.dart';

class IntegrationsController extends ChangeNotifier
    with WidgetsBindingObserver {
  IntegrationsController({
    required this.api,
    ExternalAuthBrowser? browser,
    this.autoPoll = true,
    DateTime Function()? now,
  }) : browser = browser ?? SystemAuthBrowser(),
       now = now ?? DateTime.now;
  final BackendApi api;
  final ExternalAuthBrowser browser;
  final bool autoPoll;
  final DateTime Function() now;
  List<McpProvider> providers = [];
  List<McpConnection> connections = [];
  List<McpTask> tasks = [];
  final busyConnections = <String>{}, busyTasks = <String>{};
  String? error, connectingProvider;
  // Sanitized request metadata for support; never store OAuth URLs or tokens.
  String? lastFailureCode, lastTraceId;
  int? lastFailureStatus;
  bool loading = false,
      providersUnavailable = false,
      connectionsUnavailable = false,
      tasksUnavailable = false;
  DateTime? _authorizationDeadline, _lastTaskPoll;
  int _authorizationGeneration = 0;
  bool _authorizationStarted = false;
  Set<String> _previousConnections = {};
  final _disconnected = <String>{};
  Timer? _poll;
  bool _disposed = false, _initialized = false, _resumed = true;
  Future<void>? _connectionRefresh, _taskRefresh;
  Future<void>? _authorizationReturn;

  McpConnection? toolReviewConnection;
  String? _lastAutoReviewConnectionId;

  McpConnection? takeToolReview() {
    final connection = toolReviewConnection;
    toolReviewConnection = null;
    return connection;
  }

  void _completeAuthorization(McpConnection connection) {
    _queueToolReview(connection);
    cancelAuthorization();
  }

  void _queueToolReview(McpConnection connection) {
    if (_lastAutoReviewConnectionId == connection.id) return;
    _lastAutoReviewConnectionId = connection.id;
    toolReviewConnection = connection;
  }

  int get pendingCount =>
      tasks.where((t) => t.pending && !t.expired(now())).length;
  McpTask? task(String id) {
    for (final item in tasks) {
      if (item.id == id) return item;
    }
    return null;
  }

  void _emit() {
    if (!_disposed) notifyListeners();
  }

  void _reportFailure(Object failure) {
    lastFailureCode = failure is ApiException
        ? failure.code
        : failure.runtimeType.toString();
    lastFailureStatus = failure is ApiException ? failure.status : null;
    lastTraceId = failure is ApiException ? failure.traceId : null;
    error = mcpErrorMessage(failure);
  }

  Future<void> initialize() async {
    if (_initialized || _disposed) return;
    _initialized = true;
    WidgetsBinding.instance.addObserver(this);
    _startPolling();
    await refresh(quiet: true);
  }

  Future<void> refresh({bool quiet = false}) async {
    if (_disposed) return;
    loading = true;
    if (!quiet) error = null;
    _emit();
    await Future.wait([
      _providers(quiet),
      refreshConnections(quiet: quiet),
      refreshTasks(quiet: quiet),
    ]);
    loading = false;
    _emit();
  }

  Future<void> _providers(bool quiet) async {
    try {
      final result = await api.mcpProviders();
      if (_disposed) return;
      providers = result.map(McpProvider.new).toList();
      providersUnavailable = false;
    } catch (failure) {
      providersUnavailable = true;
      if (!quiet) _reportFailure(failure);
    }
    _emit();
  }

  Future<void> refreshConnections({bool quiet = false}) async {
    if (_disposed) return;
    if (_connectionRefresh != null) return _connectionRefresh!;
    final work = _connections(quiet);
    _connectionRefresh = work;
    try {
      await work;
    } finally {
      _connectionRefresh = null;
    }
  }

  Future<void> _connections(bool quiet) async {
    try {
      final result = await api.mcpConnections();
      if (_disposed) return;
      connections = result
          .map(McpConnection.new)
          .where(
            (c) => c.status != 'Disconnected' && !_disconnected.contains(c.id),
          )
          .toList();
      connectionsUnavailable = false;
      if (_authorizationStarted &&
          connectingProvider != null &&
          connections.any(
            (c) =>
                c.connected &&
                c.provider == connectingProvider &&
                !_previousConnections.contains(c.id),
          )) {
        _completeAuthorization(
          connections.firstWhere(
            (c) =>
                c.connected &&
                c.provider == connectingProvider &&
                !_previousConnections.contains(c.id),
          ),
        );
      }
    } catch (failure) {
      connectionsUnavailable = true;
      if (!quiet) _reportFailure(failure);
    }
    _emit();
  }

  Future<void> connect(String provider) async {
    if (connectingProvider != null || _disposed) return;
    error = null;
    _lastAutoReviewConnectionId = null;
    final generation = ++_authorizationGeneration;
    connectingProvider = provider;
    _authorizationStarted = false;
    _previousConnections = connections.map((c) => c.id).toSet();
    _authorizationDeadline = now().add(const Duration(minutes: 3));
    _emit();
    try {
      // Snapshot server state first: an account missing from an old cached list
      // must not be mistaken for completion of this new consent attempt.
      final baseline = await api.mcpConnections();
      if (_disposed || generation != _authorizationGeneration) return;
      _previousConnections = baseline
          .map((row) => row['id'].toString())
          .toSet();
      final response = await api.connectMcp(provider);
      if (_disposed || generation != _authorizationGeneration) return;
      final uri = Uri.tryParse(response['authorizationUrl']?.toString() ?? '');
      if (uri == null ||
          uri.scheme != 'https' ||
          uri.host.isEmpty ||
          uri.userInfo.isNotEmpty) {
        throw const FormatException('Invalid authorization address');
      }
      _authorizationStarted = true;
      await browser.open(uri);
    } catch (failure) {
      if (_disposed || generation != _authorizationGeneration) return;
      cancelAuthorization();
      _reportFailure(failure);
      _emit();
    }
  }

  void cancelAuthorization() {
    _authorizationGeneration++;
    _authorizationStarted = false;
    connectingProvider = null;
    _authorizationDeadline = null;
    _previousConnections = {};
    _emit();
  }

  /// The link is only a hint. Fetch the signed-in user's accounts before
  /// clearing the wait, including when reauthorization keeps the same ID.
  Future<void> authorizationReturned() async {
    if (_disposed) return;
    if (_authorizationReturn != null) return _authorizationReturn!;
    final work = _refreshAfterAuthorization();
    _authorizationReturn = work;
    try {
      await work;
    } finally {
      _authorizationReturn = null;
    }
  }

  Future<void> _refreshAfterAuthorization() async {
    final generation = _authorizationGeneration;
    final provider = connectingProvider;
    await refreshConnections();
    if (_disposed || generation != _authorizationGeneration) return;
    if (!connectionsUnavailable &&
        _authorizationStarted &&
        provider != null &&
        connections.any((c) => c.connected && c.provider == provider)) {
      _completeAuthorization(
        connections.firstWhere((c) => c.connected && c.provider == provider),
      );
    } else if (!connectionsUnavailable &&
        provider == null &&
        connections.where((c) => c.connected).length == 1 &&
        toolReviewConnection == null) {
      // A completion received after app recreation has no in-memory attempt.
      // Only pick a connection when the authenticated result is unambiguous.
      _queueToolReview(connections.singleWhere((c) => c.connected));
      _emit();
    }
  }

  void _startPolling() {
    if (!autoPoll || !_resumed || _disposed) return;
    _poll ??= Timer.periodic(
      const Duration(seconds: 3),
      (_) => unawaited(poll()),
    );
  }

  Future<void> poll() async {
    if (_disposed || !_resumed) return;
    if (connectingProvider != null) {
      await refreshConnections(quiet: true);
      if (connectingProvider != null &&
          _authorizationDeadline != null &&
          !now().isBefore(_authorizationDeadline!)) {
        cancelAuthorization();
        error = 'Account authorization was not completed. You can connect again to retry.';
        _emit();
      }
    }
    if (_lastTaskPoll == null ||
        now().difference(_lastTaskPoll!) >= const Duration(seconds: 5)) {
      _lastTaskPoll = now();
      await refreshTasks(quiet: true);
    }
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    _resumed = state == AppLifecycleState.resumed;
    if (_resumed) {
      _startPolling();
      unawaited(refresh(quiet: true).then((_) => poll()));
    } else {
      _poll?.cancel();
      _poll = null;
    }
  }

  Future<List<McpTool>> tools(String id) async =>
      (await api.mcpTools(id)).map(McpTool.new).toList();
  Future<bool> saveTools(
    String id,
    List<String> selected,
    List<McpTool> discovered,
  ) async {
    if (busyConnections.contains(id)) return false;
    error = null;
    busyConnections.add(id);
    _emit();
    try {
      final keys = discovered.map((t) => t.key).toSet();
      if (selected.toSet().length != selected.length ||
          !selected.every(keys.contains)) {
        throw const FormatException('Unknown tool selection');
      }
      final updated = McpConnection(await api.setMcpTools(id, selected));
      connections = [
        for (final c in connections)
          if (c.id == id) updated else c,
      ];
      return true;
    } catch (failure) {
      _reportFailure(failure);
      await refreshConnections(quiet: true);
      return false;
    } finally {
      busyConnections.remove(id);
      _emit();
    }
  }

  Future<bool> disconnect(String id) async {
    if (busyConnections.contains(id)) return false;
    error = null;
    busyConnections.add(id);
    _emit();
    try {
      await api.disconnectMcp(id);
      _disconnected.add(id);
      connections = connections.where((c) => c.id != id).toList();
      _emit();
      await refreshConnections(quiet: true);
      return true;
    } catch (failure) {
      _reportFailure(failure);
      await refreshConnections(quiet: true);
      return false;
    } finally {
      busyConnections.remove(id);
      _emit();
    }
  }

  Future<void> refreshTasks({bool quiet = false}) async {
    if (_disposed) return;
    if (_taskRefresh != null) return _taskRefresh!;
    final work = _tasks(quiet);
    _taskRefresh = work;
    try {
      await work;
    } finally {
      _taskRefresh = null;
    }
  }

  Future<void> _tasks(bool quiet) async {
    try {
      final rows = await api.quacktions();
      if (_disposed) return;
      tasks = rows.map(McpTask.new).toList()
        ..sort((a, b) => b.createdAt.compareTo(a.createdAt));
      tasksUnavailable = false;
    } catch (failure) {
      tasksUnavailable = true;
      if (!quiet) _reportFailure(failure);
    }
    _emit();
  }

  void _updateTask(McpTask updated) {
    tasks = [updated, ...tasks.where((t) => t.id != updated.id)];
    _emit();
  }

  Future<McpTask?> bindAccount(
    McpTask reviewed,
    McpConnection connection,
    List<String> services,
  ) async {
    if (busyTasks.contains(reviewed.id)) return null;
    error = null;
    busyTasks.add(reviewed.id);
    _emit();
    try {
      if (!connection.connected ||
          services.isEmpty ||
          services.toSet().length != services.length ||
          !services.every(connection.services.contains)) {
        throw const FormatException('Invalid services');
      }
      final updated = McpTask(
        await api.selectMcpAccount(connection.id, reviewed.id, services),
      );
      _updateTask(updated);
      return updated;
    } catch (failure) {
      _reportFailure(failure);
      await refreshTasks(quiet: true);
      return null;
    } finally {
      busyTasks.remove(reviewed.id);
      _emit();
    }
  }

  Future<bool> decide(McpTask reviewed, {required bool approve}) async {
    if (busyTasks.contains(reviewed.id)) return false;
    error = null;
    busyTasks.add(reviewed.id);
    _emit();
    try {
      final current = McpTask(await api.quacktion(reviewed.id));
      _updateTask(current);
      if (!current.pending || current.expired(now())) {
        error = 'This approval has expired or already been resolved. Refresh your requests.';
        return false;
      }
      if (current.digest.isEmpty || current.digest != reviewed.digest) {
        error = 'This request has changed. Review its updated details before deciding.';
        return false;
      }
      if (approve && current.parameters.isEmpty) {
        error =
            'The request details are unavailable. Refresh before approving.';
        return false;
      }
      if (approve &&
          current.requiresAccount &&
          (current.connectionId == null || current.selectedServices.isEmpty)) {
        error =
            'Select a connected account and services before approving access.';
        return false;
      }
      final response = approve
          ? await api.approveQuacktion(current.id, current.digest)
          : await api.rejectQuacktion(current.id, current.digest);
      _updateTask(McpTask(response));
      return true;
    } catch (failure) {
      _reportFailure(failure);
      // Refresh for a new explicit review; never retry a decision with a changed digest.
      await refreshTasks(quiet: true);
      return false;
    } finally {
      busyTasks.remove(reviewed.id);
      _emit();
    }
  }

  @override
  void dispose() {
    _disposed = true;
    _poll?.cancel();
    if (_initialized) WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }
}

String mcpErrorMessage(Object failure) {
  if (failure is ApiException) {
    return switch (failure.code) {
      'mcp_not_enabled' || 'mcp_provider_not_found' =>
        'Account connections are not available right now. Try again later.',
      'action_changed' ||
      'approval_expired' ||
      'mcp_task_not_bindable' ||
      'mcp_services_invalid' ||
      'mcp_tool_schema_changed' ||
      'unknown_mcp_tool' => 'This request or its permissions changed. Refresh and review the latest details.',
      'mcp_authorization_denied' || 'oauth_state_expired' => 'Account authorization was cancelled or expired. Connect again to retry.',
      _ => switch (failure.status) {
        401 => 'Please sign in again to manage connections and approvals.',
        403 || 404 => 'This connection or request is no longer available. Refresh to continue.',
        409 => 'This request changed. Refresh and review it again before continuing.',
        _ => 'Could not reach account connections. Check your connection and try again.',
      },
    };
  }
  return 'Could not complete this connection request. Please try again.';
}
