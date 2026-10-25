import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';

import '../helpers/fake_api.dart';

void main() {
  late Api client;
  late BackendApi backend;

  setUp(() {
    client = Api()..respond = (_, _) async => <String, dynamic>{};
    backend = BackendApi(client);
  });

  tearDown(() => client.dispose());

  test(
    'account session creation sends the idempotency key and uses bearer API',
    () async {
      await backend.createSession(
        title: 'New chat',
        idempotencyKey: 'request-1',
      );
      expect(client.calls.single.method, 'POST');
      expect(client.calls.single.path, '/api/sessions');
      expect(client.calls.single.body?['title'], 'New chat');
      expect(client.calls.single.headers, {'Idempotency-Key': 'request-1'});
    },
  );

  test('device pairing and session routes match Swagger contracts', () async {
    await backend.createPairingChallenge('speaker/one');
    await backend.pairDevice('speaker/one', 'challenge-id', 'challenge');
    await backend.deviceStatus('speaker/one');
    expect(client.calls.map((call) => call.path), [
      '/api/devices/speaker%2Fone/pairing-challenges',
      '/api/devices/speaker%2Fone/pair',
      '/api/devices/speaker%2Fone/status',
    ]);
    expect(client.calls[1].body, {
      'challengeId': 'challenge-id',
      'challenge': 'challenge',
    });
  });

  test('Quacktion approval actions carry the action digest', () async {
    await backend.approveQuacktion('q-1', 'digest');
    await backend.rejectQuacktion('q-2', 'digest-2');
    await backend.cancelQuacktion('q-3');
    await backend.retryQuacktion('q-4');
    expect(client.calls.map((call) => call.path), [
      '/api/quacktions/q-1/approve',
      '/api/quacktions/q-2/reject',
      '/api/quacktions/q-3/cancel',
      '/api/quacktions/q-4/retry',
    ]);
    expect(client.calls[0].body, {'actionDigest': 'digest'});
    expect(client.calls[1].body, {'actionDigest': 'digest-2'});
  });
}
