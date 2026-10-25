import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:quacktion_frontend/core/networking/http_transport.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';

void main() {
  test(
    'plain MCP list responses are retained without changing paginated objects',
    () async {
      final client = HttpTransport(
        baseUrl: 'https://backend.example',
        client: MockClient((request) async {
          expect(request.headers['Authorization'], 'Bearer account-token');
          return http.Response(
            jsonEncode(
              request.url.path.endsWith('providers')
                  ? [
                      {'id': 'google'},
                    ]
                  : {
                      'items': [
                        {'id': 'session'},
                      ],
                      'offset': 0,
                    },
            ),
            200,
          );
        }),
      );
      addTearDown(client.dispose);
      final providers = await client.request(
        'GET',
        '/api/mcp/providers',
        accessToken: 'account-token',
      );
      expect(providers['items'], [
        {'id': 'google'},
      ]);
      final sessions = await client.request(
        'GET',
        '/api/sessions',
        accessToken: 'account-token',
      );
      expect(sessions['offset'], 0);
    },
  );
  test(
    'Problem Details title is a stable code, never a raw UI exception',
    () async {
      final client = HttpTransport(
        baseUrl: 'https://backend.example',
        client: MockClient(
          (_) async => http.Response(
            '{"title":"action_changed","detail":"private server exception"}',
            409,
          ),
        ),
      );
      addTearDown(client.dispose);
      await expectLater(
        client.request('POST', '/api/quacktions/task/approve'),
        throwsA(
          isA<ApiException>()
              .having((e) => e.code, 'code', 'action_changed')
              .having(
                (e) => e.message,
                'message',
                isNot(contains('private server exception')),
              ),
        ),
      );
    },
  );
}
