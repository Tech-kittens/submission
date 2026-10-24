import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/app/app_shell.dart';
import 'package:quacktion_frontend/app/theme/app_theme.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';

void main() {
  testWidgets('conversation history and new chat use local demo state', (
    tester,
  ) async {
    final auth = AuthController()..profile = {'name': 'Alex'};
    final store = ConversationController();
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light,
        home: AppShell(auth: auth, sessionStore: store, onMessage: (_) {}),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Planning the weekend'), findsOneWidget);
    expect(find.text('Start conversation'), findsOneWidget);
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
    expect(find.text('Listening · Demo mode'), findsOneWidget);
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
