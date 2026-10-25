import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/integrations/application/integrations_controller.dart';
import 'package:quacktion_frontend/features/integrations/models/mcp_models.dart';
import 'package:quacktion_frontend/features/integrations/presentation/screens/integrations_page.dart';
import 'package:quacktion_frontend/features/integrations/presentation/widgets/connection_editor.dart';
import 'package:quacktion_frontend/features/integrations/presentation/screens/requests_page.dart';
import 'package:quacktion_frontend/shared/widgets/app_toast.dart';

import '../../helpers/fake_api.dart';
import 'integrations_controller_test.dart'
    show FakeBrowser, connectionRow, taskRow;

void main() {
  testWidgets(
    'compact tools expand details and bulk selection saves exact keys',
    (tester) async {
      final client = Api();
      final connection = connectionRow();
      client.respond = (method, path) async {
        if (path.endsWith('/health')) return {'status': 'available'};
        if (method == 'PATCH') return connection;
        if (path.endsWith('/tools')) {
          return {
            'items': [
              {
                'name': 'calendar_list_events',
                'serverId': 'google:calendar',
                'description': 'Find calendar appointments.',
                'inputSchema': {'type': 'object'},
              },
              {
                'name': 'calendar_get_event',
                'serverId': 'google:calendar',
                'description': 'Get one event.',
                'inputSchema': {'type': 'object'},
              },
              {
                'name': 'gmail_send_message',
                'serverId': 'google:gmail',
                'description': 'Send an email.',
                'inputSchema': {'type': 'object'},
              },
            ],
          };
        }
        return {'items': []};
      };
      final controller = IntegrationsController(
        api: BackendApi(client),
        autoPoll: false,
      );
      addTearDown(controller.dispose);
      addTearDown(client.dispose);
      addTearDown(AppToast.dismiss);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Builder(
              builder: (context) => TextButton(
                onPressed: () => showModalBottomSheet<void>(
                  context: context,
                  isScrollControlled: true,
                  builder: (_) => ConnectionEditor(
                    controller: controller,
                    connection: McpConnection(connection),
                  ),
                ),
                child: const Text('Open tools'),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('Open tools'));
      await tester.pumpAndSettle();
      expect(find.text('Allowed tools'), findsOneWidget);
      expect(find.text('List events'), findsNothing);
      expect(find.text('Send message'), findsNothing);
      final calendarGroup = find.byKey(
        const ValueKey('tool-group-google:calendar'),
      );
      final gmailGroup = find.byKey(const ValueKey('tool-group-google:gmail'));
      final groupCheckbox = find.byKey(
        const ValueKey('group-select-google:calendar'),
      );
      final tile = tester.widget<ExpansionTile>(calendarGroup);
      expect(tile.controlAffinity, ListTileControlAffinity.leading);
      expect(tile.trailing, isA<Checkbox>());
      await tester.tap(groupCheckbox);
      await tester.pumpAndSettle();
      expect(find.text('2 of 3 selected'), findsOneWidget);
      expect(find.text('List events'), findsNothing);
      expect(
        find.descendant(of: gmailGroup, matching: find.text('0 of 1 selected')),
        findsOneWidget,
      );
      await tester.tap(find.text('Google Calendar'));
      await tester.pumpAndSettle();
      expect(find.text('List events'), findsOneWidget);
      expect(find.text('Send message'), findsNothing);
      final eventCheckbox = find.byKey(
        const ValueKey('tool-select-google:calendar/calendar_list_events'),
      );
      await tester.tap(eventCheckbox);
      await tester.pumpAndSettle();
      expect(tester.widget<Checkbox>(groupCheckbox).value, isNull);
      // A partially selected group selects all on tap, then clears on the next tap.
      await tester.tap(groupCheckbox);
      await tester.pumpAndSettle();
      expect(tester.widget<Checkbox>(groupCheckbox).value, isTrue);
      await tester.tap(groupCheckbox);
      await tester.pumpAndSettle();
      expect(tester.widget<Checkbox>(groupCheckbox).value, isFalse);
      expect(find.text('0 of 3 selected'), findsOneWidget);
      expect(
        tester
            .widget<ExpansionTile>(
              find.byKey(
                const ValueKey('google:calendar/calendar_list_events'),
              ),
            )
            .controlAffinity,
        ListTileControlAffinity.leading,
      );
      expect(find.text('Find calendar appointments.'), findsNothing);
      expect(find.text('Google Calendar'), findsOneWidget);
      expect(find.text('Approval required'), findsNothing);
      expect(find.text('Tool details'), findsNothing);
      expect(find.text('Technical details'), findsNothing);
      await tester.tap(find.text('List events'));
      await tester.pumpAndSettle();
      expect(find.text('Find calendar appointments.'), findsOneWidget);
      expect(find.text('Google Calendar'), findsNWidgets(2));
      expect(find.text('Technical details'), findsNothing);
      expect(find.text('Approval required'), findsOneWidget);
      await tester.tap(find.text('List events'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Select all').first);
      await tester.pumpAndSettle();
      expect(find.text('3 of 3 selected'), findsOneWidget);
      await tester.tap(find.text('Google Calendar'));
      await tester.pumpAndSettle();
      expect(find.text('List events'), findsNothing);
      expect(find.text('3 of 3 selected'), findsOneWidget);
      await tester.tap(find.text('Deselect all').first);
      await tester.pumpAndSettle();
      expect(find.text('0 of 3 selected'), findsOneWidget);
      await tester.tap(find.text('Select all').first);
      await tester.ensureVisible(find.text('Save tool permissions'));
      await tester.tap(find.text('Save tool permissions'));
      await tester.pumpAndSettle();
      expect(client.calls.where((c) => c.method == 'PATCH').single.body, {
        'enabledTools': [
          'google:calendar/calendar_get_event',
          'google:calendar/calendar_list_events',
          'google:gmail/gmail_send_message',
        ],
      });
      expect(find.byType(ConnectionEditor), findsNothing);
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'only backend providers are offered and browser consent is followed by connection refresh',
    (tester) async {
      final client = Api();
      final browser = FakeBrowser();
      var connections = <Map<String, dynamic>>[];
      client.respond = (method, path) async {
        if (path == '/api/mcp/providers') {
          return {
            'items': [
              {'id': 'google', 'displayName': 'google'},
            ],
          };
        }
        if (path == '/api/mcp/connections') return {'items': connections};
        if (path.endsWith('/connect')) {
          return {'authorizationUrl': 'https://auth.example.com/authorize'};
        }
        return {'items': []};
      };
      final controller = IntegrationsController(
        api: BackendApi(client),
        browser: browser,
        autoPoll: false,
      );
      addTearDown(controller.dispose);
      addTearDown(client.dispose);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: SingleChildScrollView(
              child: IntegrationsPage(controller: controller),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Connect Google account'), findsOneWidget);
      expect(find.text('GitHub'), findsNothing);
      expect(find.textContaining('Preview only'), findsNothing);
      await tester.tap(find.text('Connect Google account'));
      await tester.pumpAndSettle();
      expect(browser.opened, hasLength(1));
      expect(find.text('Finish connecting in your browser'), findsOneWidget);
      connections = [connectionRow()];
      await controller.refreshConnections();
      await tester.pumpAndSettle();
      expect(find.text('Google connection A'), findsOneWidget);
      expect(find.text('Gmail'), findsOneWidget);
      expect(find.text('Account identity is not verified.'), findsOneWidget);
      expect(find.text('grant:not-an-email'), findsNothing);
      expect(find.text('Finish connecting in your browser'), findsNothing);
    },
  );

  testWidgets(
    'access selection requires separate review then approval of the new digest',
    (tester) async {
      tester.view.physicalSize = const Size(1200, 2400);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final now = DateTime.utc(2026, 10, 25);
      final client = Api();
      var row = taskRow(now);
      client.respond = (method, path) async {
        if (path == '/api/mcp/connections') {
          return {
            'items': [connectionRow()],
          };
        }
        if (path.startsWith('/api/quacktions?')) {
          return {
            'items': [row],
          };
        }
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
        if (path == '/api/quacktions/task-1') return row;
        if (path.endsWith('/approve')) return {...row, 'status': 'Queued'};
        return {'items': []};
      };
      final controller = IntegrationsController(
        api: BackendApi(client),
        browser: FakeBrowser(),
        autoPoll: false,
        now: () => now,
      )..connections = [McpConnection(connectionRow())];
      addTearDown(controller.dispose);
      addTearDown(client.dispose);
      addTearDown(AppToast.dismiss);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: RequestsPage(controller: controller)),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Check my schedule'));
      await tester.pumpAndSettle();
      expect(find.text('Approve access'), findsNothing);
      await tester.tap(find.text('Google connection A'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Google Calendar'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Review access'));
      await tester.pumpAndSettle();
      expect(
        find.text('Selected account: Google connection A'),
        findsOneWidget,
      );
      expect(client.calls.where((c) => c.path.endsWith('/approve')), isEmpty);
      await tester.tap(find.text('Approve access'));
      await tester.pumpAndSettle();
      expect(client.calls.last.body, {'actionDigest': 'bound'});
      expect(find.text('Request approved.'), findsOneWidget);
      AppToast.dismiss();
      await tester.pumpAndSettle();
    },
  );

  testWidgets('spoken results require a clearly labelled separate approval', (
    tester,
  ) async {
    final now = DateTime.utc(2026, 10, 25);
    final row = taskRow(
      now,
      actionType: 'meeting.announce',
      parameters: {'content': 'Private calendar details'},
    );
    final client = Api()
      ..respond = (_, _) async => {
        'items': [row],
      };
    final controller = IntegrationsController(
      api: BackendApi(client),
      autoPoll: false,
      now: () => now,
    );
    addTearDown(controller.dispose);
    addTearDown(client.dispose);
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(body: RequestsPage(controller: controller)),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.textContaining('Private calendar details'), findsNothing);
    await tester.tap(find.text('Spoken announcement'));
    await tester.pumpAndSettle();
    expect(find.text('Review spoken announcement'), findsOneWidget);
    expect(find.text('Allow spoken announcement'), findsOneWidget);
    expect(find.textContaining('read this result aloud'), findsOneWidget);
    expect(client.calls.where((c) => c.method == 'POST'), isEmpty);
  });

  testWidgets('closing a protected operation review never approves it', (
    tester,
  ) async {
    final now = DateTime.utc(2026, 10, 25);
    final row = taskRow(
      now,
      actionType: 'mcp.invoke',
      parameters: {
        'toolName': 'ACTUAL_TOOL',
        'arguments': {'to': 'person@example.com', 'subject': 'Draft'},
      },
    );
    final client = Api()
      ..respond = (_, _) async => {
        'items': [row],
      };
    final controller = IntegrationsController(
      api: BackendApi(client),
      autoPoll: false,
      now: () => now,
    );
    addTearDown(controller.dispose);
    addTearDown(client.dispose);
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(body: RequestsPage(controller: controller)),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('ACTUAL_TOOL'));
    await tester.pumpAndSettle();
    expect(find.text('Review external operation'), findsOneWidget);
    await tester.ensureVisible(find.text('Close'));
    await tester.tap(find.text('Close'));
    await tester.pumpAndSettle();
    expect(client.calls.where((c) => c.method == 'POST'), isEmpty);
  });
}
