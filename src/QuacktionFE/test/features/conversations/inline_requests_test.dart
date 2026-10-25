import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/conversations/presentation/screens/conversation_screen.dart';
import 'package:quacktion_frontend/features/integrations/application/integrations_controller.dart';
import 'package:quacktion_frontend/features/integrations/models/mcp_models.dart';
import 'package:quacktion_frontend/features/integrations/presentation/widgets/request_review.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';

import '../../helpers/fake_api.dart';
import '../integrations/integrations_controller_test.dart'
    show connectionRow, taskRow;
import '../voice/fake_voice_transport.dart';

void main() {
  late Api client;
  late IntegrationsController integrations;
  late ConversationController chats;
  final now = DateTime.utc(2026, 10, 25);

  setUp(() {
    client = Api();
    integrations = IntegrationsController(
      api: BackendApi(client),
      autoPoll: false,
      now: () => now,
    );
    chats = ConversationController()
      ..active = SessionConversation(
        id: 'session-1',
        title: 'Current chat',
        updatedAt: now,
      );
  });
  tearDown(() {
    integrations.dispose();
    chats.dispose();
    client.dispose();
  });

  Future<void> openChat(WidgetTester tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: ConversationScreen(
          store: chats,
          integrations: integrations,
          drawer: const Drawer(),
          scaffoldKey: GlobalKey<ScaffoldState>(),
          createVoice: () => VoiceController(transport: FakeVoiceTransport()),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets(
    'failed request loading is visible and retry displays a newly arrived approval',
    (tester) async {
      final row = taskRow(
        now,
        actionType: 'mcp.invoke',
        parameters: {'toolName': 'calendar.create'},
      );
      var unavailable = true;
      client.respond = (_, _) async {
        if (unavailable) throw Exception('Unavailable');
        return {'items': [row]};
      };
      await openChat(tester);
      expect(find.byType(RequestReview), findsNothing);
      await integrations.refreshTasks(quiet: true);
      await tester.pumpAndSettle();
      expect(
        find.text('Approval requests could not be loaded.'),
        findsOneWidget,
      );
      unavailable = false;
      await tester.tap(find.text('Retry requests'));
      await tester.pumpAndSettle();
      expect(find.text('Approval requests could not be loaded.'), findsNothing);
      expect(find.byType(RequestReview), findsOneWidget);
      expect(find.text('calendar.create'), findsOneWidget);
      expect(
        client.calls.where((call) => call.path.endsWith('/approve')),
        isEmpty,
      );
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'requests appear inline only in their own conversation, with no requests button',
    (tester) async {
      final row = taskRow(
        now,
        actionType: 'mcp.invoke',
        parameters: {'toolName': 'calendar.create', 'title': 'Team meeting'},
      );
      integrations.tasks = [
        McpTask(row),
        McpTask({
          ...row,
          'id': 'other-task',
          'sessionId': 'other-session',
          'parametersJson': '{"toolName":"private.other"}',
        }),
      ];
      client.respond = (method, path) async {
        if (method == 'GET') return row;
        return {...row, 'status': 'Queued'};
      };
      await openChat(tester);
      expect(find.byType(RequestReview), findsOneWidget);
      expect(find.text('calendar.create'), findsOneWidget);
      expect(find.text('private.other'), findsNothing);
      expect(find.byTooltip('Review requests'), findsNothing);
      await tester.ensureVisible(find.text('Approve this operation'));
      await tester.tap(find.text('Approve this operation'));
      await tester.pumpAndSettle();
      expect(
        client.calls
            .where((call) => call.path.endsWith('/approve'))
            .single
            .body,
        {'actionDigest': 'original-digest'},
      );
      expect(find.text('Current chat'), findsOneWidget);
      expect(find.text('Queued'), findsOneWidget);
      expect(find.byType(RequestReview), findsNothing);
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'inline account binding still needs a separate approval and keeps selection during updates',
    (tester) async {
      tester.view.physicalSize = const Size(1200, 2400);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      var row = taskRow(now);
      integrations.tasks = [McpTask(row)];
      integrations.connections = [McpConnection(connectionRow())];
      client.respond = (method, path) async {
        if (path.endsWith('/select-account')) {
          row = taskRow(
            now,
            digest: 'bound',
            parameters: {
              'query': 'Check my schedule',
              'connectionId': 'connection-1',
              'services': ['google:calendar'],
            },
          );
          return row;
        }
        if (path.endsWith('/approve')) return {...row, 'status': 'Queued'};
        if (path.startsWith('/api/quacktions?')) {
          return {
            'items': [
              row,
              {
                ...row,
                'id': 'later-task',
                'createdAt': now
                    .add(const Duration(seconds: 1))
                    .toIso8601String(),
                'status': 'Queued',
              },
            ],
          };
        }
        return row;
      };
      await openChat(tester);
      expect(find.text('Approve access'), findsNothing);
      await tester.tap(find.text('Google connection A'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Google Calendar'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Review access'));
      await tester.pumpAndSettle();
      expect(find.text('Approve access'), findsOneWidget);
      expect(
        client.calls.where((call) => call.path.endsWith('/approve')),
        isEmpty,
      );
      // A newly arriving request moves this card to another list index.
      integrations.tasks = [
        McpTask(row),
        McpTask({
          ...row,
          'id': 'later-task',
          'createdAt': now.add(const Duration(seconds: 1)).toIso8601String(),
          'status': 'Queued',
        }),
      ];
      await integrations.refreshTasks();
      await tester.pumpAndSettle();
      expect(find.text('Approve access'), findsOneWidget);
      await tester.tap(find.text('Approve access'));
      await tester.pumpAndSettle();
      expect(
        client.calls
            .where((call) => call.path.endsWith('/approve'))
            .single
            .body,
        {'actionDigest': 'bound'},
      );
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'a changed operation requires review again instead of approving stale details',
    (tester) async {
      final row = taskRow(
        now,
        actionType: 'mcp.invoke',
        parameters: {'toolName': 'calendar.create'},
      );
      integrations.tasks = [McpTask(row)];
      client.respond = (_, _) async => {...row, 'actionDigest': 'changed'};
      await openChat(tester);
      await tester.ensureVisible(find.text('Approve this operation'));
      await tester.tap(find.text('Approve this operation'));
      await tester.pumpAndSettle();
      expect(
        client.calls.where((call) => call.path.endsWith('/approve')),
        isEmpty,
      );
      expect(integrations.error, contains('changed'));
      expect(find.text('Current chat'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    },
  );
}
