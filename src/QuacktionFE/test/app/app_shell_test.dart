import 'package:flutter/material.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/app/app_shell.dart';
import 'package:quacktion_frontend/app/theme/app_theme.dart';

void main() {
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

Future<void> openSection(WidgetTester tester, String title) async {
  await tester.tap(find.byTooltip('Conversation history'));
  await tester.pumpAndSettle();
  await tester.tap(
    find.descendant(of: find.byType(Drawer), matching: find.text(title)),
  );
  await tester.pumpAndSettle();
}
