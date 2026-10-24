import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';

import '../../support/auth_test_helpers.dart' show MemoryStore;

void main() {
  test(
    'live API registration, login, restore, password change and logout',
    () async {
      final store = MemoryStore();
      final auth = AuthController(store: store);
      final email =
          'flutter-test-${DateTime.now().microsecondsSinceEpoch}@example.com';
      const password = 'Integration-password-123';
      const replacement = 'Replacement-password-456';
      try {
        await auth.register(email, password, 'Flutter Test');
        await auth.login(email, password);
        expect(auth.profile?['name'], 'Flutter Test');
        final restored = AuthController(store: store);
        try {
          await restored.restore();
          expect(restored.profile?['name'], 'Flutter Test');
        } finally {
          restored.dispose();
        }
        await auth.changePassword(password, replacement);
        expect(auth.profile, isNull);
        await auth.login(email, replacement);
        await auth.authorized('DELETE', '/api/accounts/me');
        await auth.logout();
        expect(store.value, isNull);
      } finally {
        if (auth.profile != null) {
          await auth.authorized('DELETE', '/api/accounts/me');
          await auth.logout();
        }
        auth.dispose();
      }
    },
    skip: !const bool.fromEnvironment('RUN_AUTH_INTEGRATION'),
  );
}
