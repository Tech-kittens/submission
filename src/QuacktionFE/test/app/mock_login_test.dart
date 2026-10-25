import 'package:flutter_test/flutter_test.dart';
import 'package:flutter/material.dart';
import 'package:quacktion_frontend/core/storage/token_store.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:quacktion_frontend/features/auth/presentation/screens/auth_screen.dart';

void main() {
  testWidgets('the app opens on the real authentication screen', (
    tester,
  ) async {
    final auth = AuthController(store: _MemoryTokenStore());
    addTearDown(auth.dispose);
    await tester.pumpWidget(MaterialApp(home: AuthScreen(auth: auth)));
    await tester.pumpAndSettle();
    expect(find.text('Welcome back'), findsOneWidget);
    expect(find.text('Sign in'), findsOneWidget);
    expect(find.text('Create an account'), findsOneWidget);
  });
}

class _MemoryTokenStore implements TokenStore {
  @override
  Future<String?> read() async => null;
  @override
  Future<void> write(String value) async {}
  @override
  Future<void> clear() async {}
}
