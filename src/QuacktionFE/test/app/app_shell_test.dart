import 'package:flutter/material.dart';
import 'package:quacktion_frontend/features/voice/presentation/widgets/voice_controls.dart';
import 'package:quacktion_frontend/core/networking/api_client.dart';
import 'package:quacktion_frontend/features/integrations/presentation/widgets/connection_editor.dart';

import '../helpers/fake_api.dart';
import '../features/integrations/integrations_controller_test.dart'
    show connectionRow;

import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/app/app_shell.dart';
import 'package:quacktion_frontend/app/theme/app_theme.dart';
import 'package:quacktion_frontend/features/integrations/data/mcp_auth_links.dart';
import 'package:quacktion_frontend/features/integrations/presentation/screens/integrations_page.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';

import '../features/voice/fake_voice_transport.dart';

void main() {
  testWidgets(
    'verified redirect opens allowed tools once over the existing chat',
    (tester) async {
      final auth = _ConnectedAuth()..profile = {'name': 'Alex'};
      auth.respond = (_, path) async {
        if (path == '/api/mcp/connections') {
          return {
            'items': [connectionRow()],
          };
        }
        if (path.endsWith('/health')) return {'status': 'available'};
        if (path.endsWith('/tools')) {
          return {
            'items': [
              {
                'name': 'calendar_list_events',
                'serverId': 'google:calendar',
                'description': 'Find events',
                'inputSchema': {},
              },
            ],
          };
        }
        return {'items': []};
      };
      final links = _AuthLinks();
      final transport = _VoiceTransport();
      var voiceCreations = 0;
      await tester.pumpWidget(
        MaterialApp(
          home: AppShell(
            auth: auth,
            authLinks: links,
            onMessage: (_) {},
            sessionStore: ConversationController(),
            createVoice: () {
              voiceCreations++;
              return VoiceController(transport: transport);
            },
          ),
        ),
      );
      await tester.pumpAndSettle();
      links.complete();
      await tester.pumpAndSettle();
      expect(find.byType(ConnectionEditor), findsOneWidget);
      expect(find.text('Allowed tools'), findsOneWidget);
      expect(
        find.descendant(
          of: find.byType(ConnectionEditor),
          matching: find.text('Google Calendar'),
        ),
        findsOneWidget,
      );
      expect(find.text('List events'), findsNothing);
      expect(voiceCreations, 1);
      expect(transport.stopped, isFalse);
      expect(auth.calls.where((c) => c.method == 'PATCH'), isEmpty);
      await tester.ensureVisible(find.text('Close'));
      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();
      links.complete();
      await tester.pumpAndSettle();
      expect(find.byType(ConnectionEditor), findsNothing);
      await tester.pumpWidget(const SizedBox());
      auth.dispose();
      links.dispose();
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'completion opens connections once without disposing active voice',
    (tester) async {
      final auth = AuthController()..profile = {'name': 'Alex'};
      final links = _AuthLinks();
      final transport = _VoiceTransport();
      var voiceCreations = 0;
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light,
          home: AppShell(
            auth: auth,
            authLinks: links,
            sessionStore: ConversationController(),
            onMessage: (_) {},
            createVoice: () {
              voiceCreations++;
              return VoiceController(transport: transport);
            },
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(
        find.descendant(
          of: find.byType(VoiceControls),
          matching: find.byType(FilledButton),
        ),
      );
      await tester.pump(const Duration(milliseconds: 100));
      await tester.pump(const Duration(milliseconds: 100));
      links.complete();
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));
      await tester.pump(const Duration(milliseconds: 400));
      expect(find.byType(IntegrationsPage), findsOneWidget);
      expect(links.pending, isFalse);
      links.complete();
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));
      expect(find.byType(IntegrationsPage), findsOneWidget);
      expect(voiceCreations, 1);
      expect(transport.stopped, isFalse);
      await tester.pageBack();
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));
      await tester.pump(const Duration(milliseconds: 400));
      expect(find.byType(IntegrationsPage), findsNothing);
      expect(find.text('Listening'), findsOneWidget);
      expect(voiceCreations, 1);
      await tester.pumpWidget(const SizedBox());
      links.dispose();
      auth.dispose();
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'a completion queued before sign-in opens connections on shell mount',
    (tester) async {
      final auth = AuthController()..profile = {'name': 'Alex'};
      final links = _AuthLinks()..complete();
      await tester.pumpWidget(
        MaterialApp(
          home: AppShell(
            auth: auth,
            authLinks: links,
            sessionStore: ConversationController(),
            onMessage: (_) {},
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.byType(IntegrationsPage), findsOneWidget);
      expect(links.pending, isFalse);
      await tester.pumpWidget(const SizedBox());
      links.dispose();
      auth.dispose();
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('device page uses live speaker setup instead of the demo flow', (
    tester,
  ) async {
    final auth = AuthController()
      ..profile = {'name': 'Alex', 'email': 'alex@example.com'};
    addTearDown(auth.dispose);
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light,
        home: AppShell(
          auth: auth,
          onMessage: (_) {},
          sessionStore: ConversationController(),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Planning the weekend'), findsOneWidget);
    await openSection(tester, 'Devices');
    expect(find.text('Connect speaker'), findsOneWidget);
    expect(find.textContaining('simulated'), findsNothing);
    expect(find.text('Not configured'), findsNothing);
    await openSection(tester, 'Account settings');
    expect(find.text('alex@example.com'), findsOneWidget);
    await openSection(tester, 'Devices');
    expect(find.text('Connect speaker'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    expect(tester.takeException(), isNull);
  });
}

class _AuthLinks extends McpAuthLinks {
  bool _waiting = false;
  @override
  bool get pending => _waiting;
  void complete() {
    _waiting = true;
    notifyListeners();
  }

  @override
  void consume() => _waiting = false;
}

class _VoiceTransport extends FakeVoiceTransport {
  bool stopped = false;
  @override
  Future<void> stop({bool sendEnd = true}) async => stopped = true;
}

Future<void> openSection(WidgetTester tester, String title) async {
  await tester.tap(find.byTooltip('Conversation history'));
  await tester.pumpAndSettle();
  await tester.tap(
    find.descendant(of: find.byType(Drawer), matching: find.text(title)),
  );
  await tester.pumpAndSettle();
}

class _ConnectedAuth extends Api {
  @override
  ApiClient get api => this;
}
