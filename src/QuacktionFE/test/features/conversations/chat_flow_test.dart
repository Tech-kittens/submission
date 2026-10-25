import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/app/app_shell.dart';
import 'package:quacktion_frontend/app/theme/app_theme.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';

import '../voice/fake_voice_transport.dart';

void main() {
  for (final action in ['Save', 'Cancel', 'submit']) {
    testWidgets('rename dialog closes safely using $action', (tester) async {
      final auth = AuthController()..profile = {'name': 'Alex'};
      final store = ConversationController();
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light,
          home: AppShell(
            auth: auth,
            sessionStore: store,
            onMessage: (_) {},
            createVoice: () => VoiceController(transport: FakeVoiceTransport()),
          ),
        ),
      );
      await tester.pumpAndSettle();
      final chat = store.active!;
      final originalTitle = chat.title;
      await tester.tap(find.byTooltip('Conversation history'));
      await tester.pumpAndSettle();
      await tester.tap(find.byTooltip('Conversation options').first);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Rename'));
      await tester.pumpAndSettle();
      final title = find.widgetWithText(TextField, originalTitle);
      await tester.enterText(title, 'Renamed conversation');
      if (action == 'submit') {
        await tester.testTextInput.receiveAction(TextInputAction.done);
      } else {
        await tester.tap(find.text(action));
      }
      // Pump the closing transition as well as the final disposed route.
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      expect(
        chat.title,
        action == 'Cancel' ? originalTitle : 'Renamed conversation',
      );
      expect(find.byType(AlertDialog), findsNothing);
      await tester.pumpWidget(const SizedBox());
      auth.dispose();
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('conversation history and new chat use local conversation state', (
    tester,
  ) async {
    final auth = AuthController()..profile = {'name': 'Alex'};
    final store = ConversationController();
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light,
        home: AppShell(
          auth: auth,
          sessionStore: store,
          onMessage: (_) {},
          createVoice: () => VoiceController(transport: FakeVoiceTransport()),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Planning the weekend'), findsOneWidget);
    expect(find.text('Resume conversation'), findsOneWidget);
    expect(
      find.text(
        'A walk, a good lunch, and some free time sounds like a nice balance.',
      ),
      findsOneWidget,
    );
    expect(find.byType(TextField), findsNothing);

    await tester.tap(find.byTooltip('New conversation'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Start conversation'));
    await tester.pump(const Duration(milliseconds: 1100));
    expect(find.text('Listening'), findsOneWidget);
    expect(find.byType(TextField), findsNothing);

    await tester.tap(find.byTooltip('Conversation history'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.text('Ideas for dinner'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    auth.dispose();
    expect(tester.takeException(), isNull);
  });
}
