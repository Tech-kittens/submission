import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/app/app.dart';

void main() {
  testWidgets('any entered value continues into the demo app', (tester) async {
    await tester.pumpWidget(const MainApp());
    expect(find.text('Welcome'), findsOneWidget);

    await tester.enterText(find.byType(TextField), 'not-an-email');
    await tester.tap(find.text('Continue'));
    await tester.pumpAndSettle();

    expect(find.text('Planning the weekend'), findsOneWidget);
    expect(find.text('Welcome'), findsNothing);
    await tester.tap(find.byTooltip('Conversation history'));
    await tester.pumpAndSettle();
    await tester.tap(
      find.descendant(
        of: find.byType(Drawer),
        matching: find.text('Account settings'),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('not-an-email'), findsNWidgets(2));
  });
}
