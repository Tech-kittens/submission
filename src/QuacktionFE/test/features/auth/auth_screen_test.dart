import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:quacktion_frontend/features/auth/presentation/screens/auth_screen.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';

void main() {
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
