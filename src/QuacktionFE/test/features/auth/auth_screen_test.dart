import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:quacktion_frontend/features/auth/presentation/screens/auth_screen.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:quacktion_frontend/shared/widgets/app_toast.dart';

void main() {
  testWidgets('sign-in failures use a dismissible toast instead of form text', (
    tester,
  ) async {
    final auth = AuthController(
      client: MockClient((_) async => http.Response('{}', 401)),
    );
    addTearDown(auth.dispose);
    addTearDown(AppToast.dismiss);
    await tester.pumpWidget(MaterialApp(home: AuthScreen(auth: auth)));
    await tester.enterText(
      find.byType(TextFormField).at(0),
      'test@example.com',
    );
    await tester.enterText(find.byType(TextFormField).at(1), 'test-password');
    await tester.ensureVisible(find.text('Sign in'));
    await tester.tap(find.text('Sign in'));
    await tester.pumpAndSettle();

    expect(
      find.textContaining('Your credentials are incorrect'),
      findsOneWidget,
    );
    expect(find.byTooltip('Dismiss notification'), findsOneWidget);
    await tester.tap(find.byTooltip('Dismiss notification'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Your credentials are incorrect'), findsNothing);
    expect(find.text('Sign in'), findsOneWidget);
  });

  testWidgets(
    'login validates input, exposes registration and explains recovery',
    (tester) async {
      var requests = 0;
      final auth = AuthController(
        client: MockClient((_) async {
          requests++;
          return http.Response('{}', 401);
        }),
      );
      await tester.pumpWidget(MaterialApp(home: AuthScreen(auth: auth)));
      await tester.tap(find.text('Sign in'));
      await tester.pumpAndSettle();
      expect(find.text('Enter a valid email address.'), findsOneWidget);
      expect(find.text('Enter your password.'), findsOneWidget);
      expect(requests, 0);
      await tester.ensureVisible(find.text('Forgot password?'));
      await tester.tap(find.text('Forgot password?'));
      await tester.pumpAndSettle();
      expect(
        find.textContaining('Password recovery is not available yet.'),
        findsOneWidget,
      );
      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();
      await tester.ensureVisible(find.text('Create an account'));
      await tester.tap(find.text('Create an account'));
      await tester.pumpAndSettle();
      expect(find.text('Display name'), findsNothing);
      expect(find.text('Username'), findsNothing);
      expect(find.byType(TextFormField), findsNWidgets(4));
      expect(find.text('Confirm password'), findsOneWidget);
      expect(find.text('Name'), findsOneWidget);
    },
  );
}
