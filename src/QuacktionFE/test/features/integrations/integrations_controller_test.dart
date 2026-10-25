import 'dart:async';
import 'dart:convert';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/integrations/application/integrations_controller.dart';
import 'package:quacktion_frontend/features/integrations/data/external_auth_browser.dart';
import 'package:quacktion_frontend/features/integrations/models/mcp_models.dart';

import '../../helpers/fake_api.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late Api client;
  late IntegrationsController controller;
  late FakeBrowser browser;
  late List<Map<String, dynamic>> connections;
  late List<Map<String, dynamic>> tasks;
  late DateTime clock;
  late Future<Map<String, dynamic>> Function(String, String) base;

  setUp(() {
    clock = DateTime.utc(2026, 10, 25);
    connections = [];
    tasks = [];
    client = Api();
    browser = FakeBrowser();
    base = (method, path) async {
      if (path == '/api/mcp/providers') {
        return {
          'items': [
            {'id': 'google', 'displayName': 'google'},
          ],
        };
      }
      if (path == '/api/mcp/connections') return {'items': connections};
      if (path == '/api/mcp/connections/google/connect') {
        return {
          'authorizationUrl':
              'https://auth.example.com/authorize?state=private',
        };
      }
      if (path.startsWith('/api/quacktions?')) return {'items': tasks};
      if (method == 'GET' && path == '/api/quacktions/task-1') {
        return tasks.single;
      }
      if (method == 'POST' &&
          (path.endsWith('/approve') || path.endsWith('/reject'))) {
        final updated = {
          ...tasks.single,
          'status': path.endsWith('/approve') ? 'Queued' : 'Rejected',
        };
        tasks = [updated];
        return updated;
      }
      if (method == 'DELETE') return {};
      throw const ApiException('unavailable', 404);
    };
    client.respond = base;
    controller = IntegrationsController(
      api: BackendApi(client),
      browser: browser,
      autoPoll: false,
      now: () => clock,
    );
  });
  tearDown(() {
    controller.dispose();
    client.dispose();
  });

  test(
    'successful consent queues only the new account for one tool review',
    () async {
      connections = [connectionRow()];
      await controller.connect('google');
      connections.add({...connectionRow(), 'id': 'new-connection'});
      await controller.authorizationReturned();
      expect(controller.takeToolReview()?.id, 'new-connection');
      expect(controller.takeToolReview(), isNull);
      await controller.authorizationReturned();
      expect(controller.takeToolReview(), isNull);
    },
  );

  test('duplicate completion does not reopen a consumed tool review', () async {
    connections = [connectionRow()];
    await controller.authorizationReturned();
    expect(controller.takeToolReview()?.id, 'connection-1');
    await controller.authorizationReturned();
    expect(controller.takeToolReview(), isNull);
  });

  test(
    'completion refreshes accounts even with no pending authorization',
    () async {
      connections = [connectionRow()];
      await controller.authorizationReturned();
      expect(controller.connections.single.id, 'connection-1');
      expect(browser.opened, isEmpty);
    },
  );

  test('completion accepts reauthorization of an existing account after fetching it', () async {
    connections = [connectionRow()];
    await controller.connect('google');
    expect(controller.connectingProvider, 'google');
    await controller.authorizationReturned();
    expect(controller.connectingProvider, isNull);
    expect(controller.connections.single.id, 'connection-1');
    expect(controller.takeToolReview()?.id, 'connection-1');
  });

  test(
    'completion alone cannot mark a missing connection successful',
    () async {
      await controller.connect('google');
      await controller.authorizationReturned();
      expect(controller.connections, isEmpty);
      expect(controller.connectingProvider, 'google');
    },
  );

  test(
    'failed completion refresh preserves wait and exposes a retryable error',
    () async {
      await controller.connect('google');
      client.respond = (method, path) async =>
          throw const ApiException('unavailable', 503);
      await controller.authorizationReturned();
      expect(controller.connectionsUnavailable, isTrue);
      expect(controller.error, isNotNull);
      expect(controller.connectingProvider, 'google');
      client.respond = base;
      connections = [connectionRow()];
      await controller.authorizationReturned();
      expect(controller.connectingProvider, isNull);
    },
  );

  test('availability and services come from backend, without inferring account identity', () async {
    connections = [connectionRow()];
    await controller.initialize();
    expect(controller.providers.map((p) => p.id), ['google']);
    expect(controller.connections.single.services, [
      'google:gmail',
      'google:calendar',
    ]);
    expect(controller.connections.single.identityVerified, isFalse);
    expect(controller.connections.single.name, 'Google connection A');
  });

  test(
    'OAuth uses external browser and resume detects a newly linked connection',
    () async {
      await controller.initialize();
      await controller.connect('google');
      expect(browser.opened.single.scheme, 'https');
      expect(controller.connectingProvider, 'google');
      connections = [connectionRow()];
      controller.didChangeAppLifecycleState(AppLifecycleState.resumed);
      await controller.refreshConnections(quiet: true);
      expect(controller.connectingProvider, isNull);
      expect(controller.connections, hasLength(1));
      expect(
        client.calls.where((c) => c.path.endsWith('/connect')),
        hasLength(1),
      );
    },
  );

  test(
    'an existing account absent from local cache is not new consent',
    () async {
      connections = [connectionRow()];
      await controller.connect('google');
      await controller.poll();
      expect(controller.connectingProvider, 'google');
      connections.add({...connectionRow(), 'id': 'new-connection'});
      await controller.refreshConnections();
      expect(controller.connectingProvider, isNull);
    },
  );

  test('unsafe consent URL is not opened or exposed as an error', () async {
    client.respond = (method, path) async => path.endsWith('/connect')
        ? {'authorizationUrl': 'http://unsafe.example.com/?code=secret'}
        : base(method, path);
    await controller.connect('google');
    expect(browser.opened, isEmpty);
    expect(controller.connectingProvider, isNull);
    expect(controller.error, isNot(contains('secret')));
  });

  test(
    'authorization wait is bounded and retry needs another user action',
    () async {
      await controller.connect('google');
      clock = clock.add(const Duration(minutes: 4));
      await controller.poll();
      expect(controller.connectingProvider, isNull);
      expect(controller.error, contains('not completed'));
      expect(
        client.calls.where((c) => c.path.endsWith('/connect')),
        hasLength(1),
      );
    },
  );

  test('cancelled consent cannot launch an outdated response', () async {
    final response = Completer<Map<String, dynamic>>();
    client.respond = (_, _) => response.future;
    final pending = controller.connect('google');
    controller.cancelAuthorization();
    response.complete({
      'authorizationUrl': 'https://auth.example.com/authorize',
    });
    await pending;
    expect(browser.opened, isEmpty);
  });

  test('tool selection sends exact discovered qualified keys only', () async {
    final tool = McpTool({
      'name': 'ACTUAL_TOOL',
      'serverId': 'google:gmail',
      'readOnly': true,
      'inputSchema': {},
    });
    client.respond = (method, path) async => method == 'PATCH'
        ? {
            ...connectionRow(),
            'enabledTools': [tool.key],
          }
        : base(method, path);
    controller.connections = [McpConnection(connectionRow())];
    expect(
      await controller.saveTools('connection-1', [tool.key], [tool]),
      isTrue,
    );
    expect(client.calls.single.body, {
      'enabledTools': ['google:gmail/ACTUAL_TOOL'],
    });
    client.calls.clear();
    expect(
      await controller.saveTools('connection-1', ['guessed-tool'], [tool]),
      isFalse,
    );
    expect(client.calls.where((c) => c.method == 'PATCH'), isEmpty);
  });

  test(
    'disconnected accounts disappear even if a stale list response arrives',
    () async {
      connections = [connectionRow()];
      await controller.refreshConnections();
      expect(await controller.disconnect('connection-1'), isTrue);
      expect(controller.connections, isEmpty);
      await controller.refreshConnections();
      expect(controller.connections, isEmpty);
    },
  );

  test(
    'account selection updates digest and is separate from explicit approval',
    () async {
      tasks = [taskRow(clock)];
      client.respond = (method, path) async {
        if (path.endsWith('/select-account')) {
          tasks = [
            taskRow(
              clock,
              digest: 'bound-digest',
              parameters: {
                'query': 'Check my schedule',
                'connectionId': 'connection-1',
                'services': ['google:calendar'],
              },
            ),
          ];
          return tasks.single;
        }
        return base(method, path);
      };
      final bound = await controller.bindAccount(
        McpTask(tasks.single),
        McpConnection(connectionRow()),
        ['google:calendar'],
      );
      expect(bound?.digest, 'bound-digest');
      expect(client.calls.where((c) => c.path.endsWith('/approve')), isEmpty);
      expect(await controller.decide(bound!, approve: true), isTrue);
      expect(client.calls.last.body, {'actionDigest': 'bound-digest'});
    },
  );

  test(
    'changed action requires a new review; approval never retries silently',
    () async {
      final reviewed = McpTask(taskRow(clock, actionType: 'mcp.invoke'));
      tasks = [
        taskRow(clock, digest: 'changed-digest', actionType: 'mcp.invoke'),
      ];
      expect(await controller.decide(reviewed, approve: true), isFalse);
      expect(client.calls.where((c) => c.path.endsWith('/approve')), isEmpty);
      expect(controller.error, contains('changed'));
      expect(
        await controller.decide(controller.task('task-1')!, approve: true),
        isTrue,
      );
      expect(client.calls.last.body, {'actionDigest': 'changed-digest'});
    },
  );

  test('expired or unbound requests cannot be approved', () async {
    tasks = [
      taskRow(clock, expires: clock.subtract(const Duration(seconds: 1))),
    ];
    expect(
      await controller.decide(McpTask(tasks.single), approve: true),
      isFalse,
    );
    tasks = [taskRow(clock)];
    expect(
      await controller.decide(McpTask(tasks.single), approve: true),
      isFalse,
    );
    expect(client.calls.where((c) => c.path.endsWith('/approve')), isEmpty);
  });

  test('double submission sends one approval', () async {
    tasks = [taskRow(clock, actionType: 'mcp.invoke')];
    final current = Completer<Map<String, dynamic>>();
    client.respond = (method, path) async =>
        method == 'GET' && path == '/api/quacktions/task-1'
        ? current.future
        : base(method, path);
    final reviewed = McpTask(tasks.single);
    final first = controller.decide(reviewed, approve: true);
    expect(await controller.decide(reviewed, approve: true), isFalse);
    current.complete(tasks.single);
    expect(await first, isTrue);
    expect(
      client.calls.where((c) => c.path.endsWith('/approve')),
      hasLength(1),
    );
  });

  test('409 refreshes state without approving a replacement digest', () async {
    tasks = [taskRow(clock, actionType: 'mcp.invoke')];
    client.respond = (method, path) async {
      if (path.endsWith('/approve')) {
        tasks = [
          taskRow(clock, digest: 'new-digest', actionType: 'mcp.invoke'),
        ];
        throw const ApiException(
          'private server details',
          409,
          'action_changed',
        );
      }
      return base(method, path);
    };
    expect(
      await controller.decide(McpTask(tasks.single), approve: true),
      isFalse,
    );
    expect(
      client.calls.where((c) => c.path.endsWith('/approve')),
      hasLength(1),
    );
    expect(controller.task('task-1')?.digest, 'new-digest');
    expect(controller.error, isNot(contains('private server details')));
  });

  test('decline uses current digest without binding an account', () async {
    tasks = [taskRow(clock)];
    expect(
      await controller.decide(McpTask(tasks.single), approve: false),
      isTrue,
    );
    expect(client.calls.last.path, '/api/quacktions/task-1/reject');
    expect(client.calls.last.body, {'actionDigest': 'original-digest'});
  });
}

class FakeBrowser implements ExternalAuthBrowser {
  final opened = <Uri>[];
  @override
  Future<void> open(Uri uri) async => opened.add(uri);
}

Map<String, dynamic> connectionRow() => {
  'id': 'connection-1',
  'provider': 'google',
  'displayName': 'Google connection A',
  'providerAccountId': 'grant:not-an-email',
  'accountIdentityVerified': false,
  'status': 'Connected',
  'enabledServices': ['google:gmail', 'google:calendar'],
  'enabledTools': <String>[],
};
Map<String, dynamic> taskRow(
  DateTime now, {
  String digest = 'original-digest',
  String actionType = 'mcp.request',
  DateTime? expires,
  Map<String, dynamic>? parameters,
}) => {
  'id': 'task-1',
  'sessionId': 'session-1',
  'actionType': actionType,
  'actionDigest': digest,
  'parametersJson': jsonEncode(parameters ?? {'query': 'Check my schedule'}),
  'status': 'PendingApproval',
  'createdAt': now.toIso8601String(),
  'approval': {
    'expiresAt': (expires ?? now.add(const Duration(minutes: 30)))
        .toIso8601String(),
  },
};
