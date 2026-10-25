import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:quacktion_frontend/core/networking/api_client.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/core/networking/http_transport.dart';
import 'package:quacktion_frontend/features/meetings/application/meeting_mode_controller.dart';
import 'package:quacktion_frontend/features/meetings/presentation/widgets/meeting_mode_control.dart';

void main() {
  testWidgets(
    'phone recording survives upload retry without a speaker or meeting end',
    (tester) async {
      var failUpload = true;
      var ended = false;
      final uploads = <({String offset, List<int> bytes})>[];
      final paths = <String>[];
      final requests = <http.Request>[];
      final transport = HttpTransport(
        baseUrl: 'https://backend.test',
        client: MockClient((request) async {
          final path = request.url.path;
          paths.add(path);
          requests.add(request);
          if (path == '/api/meetings/start') {
            // Response is returned below.
          } else if (path.endsWith('/audio/segments')) {
            uploads.add((
              offset: request.url.queryParameters['startMs']!,
              bytes: request.bodyBytes.toList(),
            ));
            if (failUpload) throw http.ClientException('offline');
            return http.Response('{"id":"segment"}', 200);
          } else if (path.endsWith('/segments')) {
            return http.Response('{"items":[]}', 200);
          } else if (path.endsWith('/end')) {
            ended = true;
          } else if (!path.endsWith('/status')) {
            throw StateError('Unexpected phone meeting route: $path');
          }
          return http.Response(
            jsonEncode({
              'id': 'phone-meeting', 'deviceId': '',
              'status': ended ? 'Ending' : 'Active',
              // Server metadata may lag actual phone capture.
              'recordingStatus': ended ? 'Processing' : 'Pending',
            }),
            200,
          );
        }),
      );
      final client = AuthenticatedApiClient(
        transport: transport,
        token: () async => 'phone-token',
        currentToken: () => 'phone-token',
        refresh: () async {},
        invalidate: () async {},
      );
      final controller = MeetingModeController(
        BackendApi(client),
        autoPoll: false,
      );
      addTearDown(() {
        controller.dispose();
        transport.dispose();
      });
      controller.captureStarted('conversation');
      expect(
        await controller.start(
          'conversation',
          name: 'Phone planning',
          type: 'Planning',
          consent: true,
        ),
        isTrue,
      );
      controller.acceptAudio('conversation', Uint8List(960000));
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: MeetingModeControl(
              controller: controller,
              sessionId: 'conversation',
              name: 'Phone planning',
              canStart: true,
            ),
          ),
        ),
      );
      await tester.pump();
      expect(controller.capturing('conversation'), isTrue);
      expect(ended, isFalse);
      expect(find.text('Meeting recording · Retrying upload'), findsOneWidget);
      expect(find.text('Meeting recording paused'), findsNothing);
      expect(
        find.text('Waiting for microphone audio. Recording is not active.'),
        findsNothing,
      );
      failUpload = false;
      await tester.tap(find.text('Retry audio upload'));
      await tester.pumpAndSettle();
      expect(controller.uploadIssue('conversation'), isFalse);
      expect(controller.capturing('conversation'), isTrue);
      expect(controller.meeting('conversation')!.canEnd, isTrue);
      expect(ended, isFalse);
      expect(find.text('Meeting mode on · Recording'), findsOneWidget);
      expect(uploads.map((u) => u.offset), ['0', '0']);
      expect(uploads.first.bytes, uploads.last.bytes);
      // Silence is still recorded, and ending flushes its exact next offset.
      controller.acceptAudio('conversation', Uint8List(1280));
      expect(await controller.end('conversation'), isTrue);
      expect(uploads.last.offset, '30000');
      expect(ended, isTrue);
      expect(paths.any((p) => p.contains('/devices/')), isFalse);
      expect(
        requests.every(
          (r) => r.headers['Authorization'] == 'Bearer phone-token',
        ),
        isTrue,
      );
      expect(jsonDecode(requests.first.body), {
        'name': 'Phone planning',
        'type': 'Planning',
        'recordingConsent': true,
      });
      expect(
        requests
            .where((r) => r.url.path.endsWith('/audio/segments'))
            .every(
              (r) => r.headers['Content-Type'] == 'application/octet-stream',
            ),
        isTrue,
      );
      await tester.pumpWidget(const SizedBox());
    },
  );
}
