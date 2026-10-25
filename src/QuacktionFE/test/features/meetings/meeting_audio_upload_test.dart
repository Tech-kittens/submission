import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:quacktion_frontend/core/networking/api_client.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/core/networking/http_transport.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/meetings/data/meeting_audio_upload.dart';

void main() {
  testWidgets('ordinary requests retain the 20 second timeout', (tester) async {
    final transport = HttpTransport(
      baseUrl: 'https://backend.test',
      client: MockClient((_) async {
        await Future<void>.delayed(const Duration(seconds: 75));
        return http.Response('{}', 200);
      }),
    );
    addTearDown(transport.dispose);
    Object? failure;
    final pending = transport
        .request('GET', '/api/meetings/m/status')
        .then<void>(
          (_) {},
          onError: (Object error) {
            failure = error;
          },
        );
    await tester.pump();
    await tester.pump(const Duration(seconds: 21));
    await pending;
    expect(
      failure,
      isA<ApiException>().having((e) => e.code, 'code', 'request_timeout'),
    );
    await tester.pump(const Duration(seconds: 54));
  });

  testWidgets('temporary upload failure retries while retaining new audio', (
    tester,
  ) async {
    var fail = true;
    var recovered = false;
    final offsets = <int>[];
    final upload = MeetingAudioUpload(
      chunkBytes: 1280,
      onFailure: () {},
      onRecovered: () => recovered = true,
      upload: (offset, bytes) async {
        offsets.add(offset);
        if (fail) throw StateError('offline');
      },
    );
    upload.add(Uint8List(1280));
    await tester.pump();
    expect(upload.failed, isTrue);
    expect(upload.add(Uint8List(1280)), isTrue);
    fail = false;
    await tester.pump(const Duration(seconds: 5));
    expect(offsets, [0, 0, 40]);
    expect(recovered, isTrue);
    expect(upload.failed, isFalse);
    await upload.finish();
    upload.discard();
  });

  testWidgets('meeting upload waits for transcription beyond 20 seconds', (
    tester,
  ) async {
    final transport = HttpTransport(
      baseUrl: 'https://backend.test',
      client: MockClient((_) async {
        await Future<void>.delayed(const Duration(seconds: 75));
        return http.Response('{}', 200);
      }),
    );
    addTearDown(transport.dispose);
    var completed = false;
    final pending = transport
        .request(
          'POST',
          '/api/meetings/m/audio/segments?startMs=0',
          binaryBody: Uint8List(1280),
        )
        .then((_) => completed = true);
    await tester.pump();
    await tester.pump(const Duration(seconds: 21));
    expect(completed, isFalse);
    await tester.pump(const Duration(seconds: 54));
    await pending;
    expect(completed, isTrue);
  });

  test(
    'uploads sequential whole frames and pads only the final tail',
    () async {
      final chunks = <({int offset, Uint8List bytes})>[];
      final first = Completer<void>();
      final upload = MeetingAudioUpload(
        chunkBytes: 2560,
        onFailure: () {},
        upload: (offset, bytes) async {
          chunks.add((offset: offset, bytes: Uint8List.fromList(bytes)));
          if (offset == 0) await first.future;
        },
      );
      upload.add(Uint8List.fromList(List.filled(3000, 17)));
      upload.add(Uint8List.fromList(List.filled(2500, 29)));
      expect(chunks, hasLength(1));
      final finish = upload.finish();
      first.complete();
      await finish;
      expect(chunks.map((c) => c.offset), [0, 80, 160]);
      expect(chunks.map((c) => c.bytes.length), [2560, 2560, 1280]);
      final all = chunks.expand((c) => c.bytes).toList();
      expect(all.take(5500), [
        ...List.filled(3000, 17),
        ...List.filled(2500, 29),
      ]);
      expect(all.skip(5500).every((v) => v == 0), isTrue);
      expect(chunks.every((c) => c.bytes.length % 1280 == 0), isTrue);
    },
  );

  test('failed uploads retain exact bytes and offset for retry', () async {
    var fail = true, errors = 0;
    final attempts = <({int offset, List<int> bytes})>[];
    final upload = MeetingAudioUpload(
      chunkBytes: 1280,
      onFailure: () => errors++,
      upload: (offset, bytes) async {
        attempts.add((offset: offset, bytes: bytes.toList()));
        if (fail) throw StateError('offline');
      },
    );
    upload.add(Uint8List.fromList(List.filled(1280, 7)));
    await Future<void>.delayed(Duration.zero);
    expect(errors, 1);
    expect(upload.failed, isTrue);
    expect(upload.add(Uint8List(1280)), isTrue);
    fail = false;
    await upload.finish();
    expect(attempts.map((a) => a.offset), [0, 0, 40]);
    expect(attempts[0].bytes, attempts[1].bytes);
  });

  test(
    'discard stops queued uploads after an in-flight request completes',
    () async {
      final pending = Completer<void>();
      var calls = 0;
      final upload = MeetingAudioUpload(
        chunkBytes: 1280,
        onFailure: () {},
        upload: (_, _) async {
          calls++;
          await pending.future;
        },
      );
      upload.add(Uint8List(2560));
      expect(calls, 1);
      upload.discard();
      pending.complete();
      await Future<void>.delayed(Duration.zero);
      expect(calls, 1);
      expect(upload.add(Uint8List(1280)), isFalse);
    },
  );

  test('binary audio survives a bearer-token refresh byte for byte', () async {
    var token = 'old', refreshes = 0;
    final received = <List<int>>[];
    final transport = HttpTransport(
      baseUrl: 'https://backend.test',
      client: MockClient((request) async {
        expect(request.url.path, '/api/meetings/meeting-1/audio/segments');
        expect(request.url.queryParameters['startMs'], '80');
        expect(request.headers['Content-Type'], 'application/octet-stream');
        received.add(request.bodyBytes.toList());
        if (request.headers['Authorization'] == 'Bearer old') {
          return http.Response('{}', 401);
        }
        expect(request.headers['Authorization'], 'Bearer fresh');
        return http.Response(jsonEncode({'id': 'segment'}), 200);
      }),
    );
    addTearDown(transport.dispose);
    final client = AuthenticatedApiClient(
      transport: transport,
      token: () async => token,
      currentToken: () => token,
      refresh: () async {
        refreshes++;
        token = 'fresh';
      },
      invalidate: () async {},
    );
    final bytes = Uint8List.fromList(List.generate(1280, (i) => i % 256));
    await BackendApi(client).uploadMeetingAudio('meeting-1', 80, bytes);
    expect(refreshes, 1);
    expect(received, [bytes.toList(), bytes.toList()]);
  });
}
