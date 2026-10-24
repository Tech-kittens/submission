import 'dart:convert';

import '../../support/auth_test_helpers.dart';

import 'package:quacktion_frontend/core/networking/api_exception.dart';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';

void main() {
  test(
    'registration sends required fields and does not invent a session',
    () async {
      final store = MemoryStore();
      final auth = AuthController(
        store: store,
        client: MockClient((request) async {
          expect(request.url.path, '/api/accounts');
          expect(jsonDecode(request.body), {
            'email': 'alex@example.com',
            'password': 'long-password',
            'name': 'Alex',
          });
          return jsonResponse({'name': 'Alex'}, 201);
        }),
      );
      await auth.register(' alex@example.com ', 'long-password', ' Alex ');
      expect(store.value, isNull);
      expect(auth.profile, isNull);
    },
  );

  test(
    'concurrent protected calls rotate once and persist the replacement',
    () async {
      final store = MemoryStore()..value = jsonEncode(pair());
      var refreshes = 0;
      var rejectOldToken = false;
      final auth = AuthController(
        store: store,
        client: MockClient((request) async {
          if (request.url.path == '/api/auth/refresh') {
            refreshes++;
            expect(store.value, isNull);
            await Future<void>.delayed(const Duration(milliseconds: 20));
            return jsonResponse(pair(refresh: 'new'));
          }
          if (rejectOldToken &&
              request.headers['Authorization'] == 'Bearer access-old') {
            return jsonResponse({}, 401);
          }
          return jsonResponse({'name': 'Alex'});
        }),
      );
      await auth.restore();
      rejectOldToken = true;
      await Future.wait([
        auth.authorized('GET', '/api/accounts/me'),
        auth.authorized('GET', '/api/accounts/me'),
      ]);
      expect(refreshes, 1);
      expect(jsonDecode(store.value!)['refreshToken'], 'new');
    },
  );

  test(
    'lost refresh response clears credentials and never retries old token',
    () async {
      final store = MemoryStore()..value = jsonEncode(pair(expired: true));
      var calls = 0;
      final auth = AuthController(
        store: store,
        client: MockClient((request) async {
          calls++;
          throw http.ClientException('offline');
        }),
      );
      await expectLater(auth.restore(), throwsA(isA<ApiException>()));
      expect(store.value, isNull);
      await expectLater(
        auth.authorized('GET', '/api/accounts/me'),
        throwsA(isA<ApiException>()),
      );
      expect(calls, 1);
    },
  );

  test(
    'wrong current password preserves session; successful change clears it',
    () async {
      final store = MemoryStore()..value = jsonEncode(pair());
      var wrongPassword = true;
      final auth = AuthController(
        store: store,
        client: MockClient((request) async {
          if (request.method == 'PUT') {
            return http.Response('', wrongPassword ? 401 : 204);
          }
          return jsonResponse({'name': 'Alex'});
        }),
      );
      await auth.restore();
      await expectLater(
        auth.changePassword('wrong', 'new-long-password'),
        throwsA(isA<ApiException>()),
      );
      expect(auth.profile, isNotNull);
      wrongPassword = false;
      await auth.changePassword('correct', 'new-long-password');
      expect(store.value, isNull);
      expect(auth.profile, isNull);
    },
  );

  test('logout clears local credentials even when offline', () async {
    final store = MemoryStore()..value = jsonEncode(pair());
    final auth = AuthController(
      store: store,
      client: MockClient((request) async {
        if (request.url.path.endsWith('logout')) {
          throw http.ClientException('offline');
        }
        return jsonResponse({'name': 'Alex'});
      }),
    );
    await auth.restore();
    await expectLater(auth.logout(), throwsA(isA<ApiException>()));
    expect(store.value, isNull);
    expect(auth.profile, isNull);
  });
}
