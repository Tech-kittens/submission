import 'dart:async';
import 'dart:typed_data';

import 'package:quacktion_frontend/features/meetings/data/meeting_links.dart';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/meetings/application/meeting_mode_controller.dart';
import 'package:quacktion_frontend/features/meetings/presentation/widgets/meeting_mode_control.dart';

import '../../helpers/fake_api.dart';

Map<String, dynamic> row({
  String session = 'session-1',
  String status = 'Active',
  String recording = 'Recording',
}) => {
  'id': 'meeting-1',
  if (session != 'session-1') 'sessionId': session,
  'deviceId': '',
  'status': status,
  'recordingStatus': recording,
};

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late Api client;
  late MeetingModeController controller;
  late MemoryMeetingLinks links;
  setUp(() {
    client = Api();
    links = MemoryMeetingLinks();
    controller = MeetingModeController(
      BackendApi(client),
      autoPoll: false,
      links: links,
    );
    controller.captureStarted('session-1');
  });
  tearDown(() {
    controller.dispose();
    client.dispose();
  });

  test(
    'consent is required and duplicate starts do not create requests',
    () async {
      final response = Completer<Map<String, dynamic>>();
      client.respond = (_, _) => response.future;
      expect(
        await controller.start(
          'session-1',
          name: 'Planning',
          type: 'Planning',
          consent: false,
        ),
        isFalse,
      );
      expect(client.calls, isEmpty);
      final start = controller.start(
        'session-1',
        name: 'Planning',
        type: 'Planning',
        consent: true,
      );
      expect(
        await controller.start(
          'session-1',
          name: 'Planning',
          type: 'Planning',
          consent: true,
        ),
        isFalse,
      );
      expect(client.calls, hasLength(1));
      expect(client.calls.single.path, '/api/meetings/start');
      expect(client.calls.single.body, {
        'name': 'Planning',
        'type': 'Planning',
        'recordingConsent': true,
      });
      response.complete(row(recording: 'Pending'));
      expect(await start, isTrue);
      expect(controller.meeting('session-1')!.recording, isFalse);
      client.respond = (_, _) async => row();
      await controller.refresh('session-1');
      expect(controller.meeting('session-1')!.recording, isTrue);
      expect(controller.meeting('other-session'), isNull);
    },
  );

  test(
    'unsupported phone API does not fabricate recording or call device routes',
    () async {
      client.respond = (_, _) async =>
          throw const ApiException('Not found', 404);
      expect(
        await controller.start(
          'session-1',
          name: 'Meeting',
          type: 'General',
          consent: true,
        ),
        isFalse,
      );
      expect(controller.meeting('session-1'), isNull);
      expect(
        controller.error('session-1'),
        contains('not available on the server'),
      );
      expect(client.calls.any((c) => c.path.contains('/devices/')), isFalse);
    },
  );

  test(
    'status recovers per session and stale results are clearly flagged',
    () async {
      await links.save('session-1', 'meeting-1');
      client.respond = (_, _) async => row();
      await controller.refresh('session-1');
      expect(controller.meeting('session-1')!.recording, isTrue);
      client.respond = (_, _) async => throw const ApiException('Offline', 503);
      await controller.refresh('session-1');
      expect(controller.stale('session-1'), isTrue);
      expect(controller.error('session-1'), contains('confirm meeting status'));
      client.respond = (_, _) async => row(session: 'someone-elses-session');
      await controller.refresh('session-1');
      expect(controller.stale('session-1'), isTrue);
      expect(controller.meeting('someone-elses-session'), isNull);
    },
  );

  test('ending is single flight and does not end the voice session', () async {
    await links.save('session-1', 'meeting-1');
    client.respond = (_, _) async => row();
    await controller.refresh('session-1');
    final response = Completer<Map<String, dynamic>>();
    client.respond = (_, _) => response.future;
    final end = controller.end('session-1');
    expect(await controller.end('session-1'), isFalse);
    expect(
      client.calls.where((c) => c.method == 'POST').single.path,
      '/api/meetings/meeting-1/end',
    );
    response.complete(row(status: 'Ending', recording: 'Processing'));
    expect(await end, isTrue);
    expect(controller.meeting('session-1')!.recording, isFalse);
    expect(
      controller.meeting('session-1')!.label,
      contains('Preparing report'),
    );
    expect(await controller.end('session-1'), isFalse);
    expect(
      client.calls.any((c) => c.path == '/api/sessions/session-1/end'),
      isFalse,
    );
  });

  test(
    'end flushes pending audio and only finalizes after a successful retry',
    () async {
      var failUpload = true;
      client.respond = (_, path) async {
        if (path.contains('/audio/segments')) {
          if (failUpload) throw const ApiException('Offline', 503);
          return {'id': 'segment-0'};
        }
        if (path.contains('/segments')) return {'items': []};
        if (path.endsWith('/end')) return row(status: 'Ending');
        return row();
      };
      expect(
        await controller.start(
          'session-1',
          name: 'Meeting',
          type: 'General',
          consent: true,
        ),
        isTrue,
      );
      controller.acceptAudio(
        'session-1',
        Uint8List.fromList(List.filled(700, 5)),
      );
      expect(controller.capturing('session-1'), isTrue);
      expect(await controller.end('session-1'), isFalse);
      expect(controller.capturing('session-1'), isFalse);
      expect(client.calls.any((c) => c.path.endsWith('/end')), isFalse);
      expect(
        client.binaryCalls.single.path,
        '/api/meetings/meeting-1/audio/segments?startMs=0',
      );
      expect(client.binaryCalls.single.bytes.length, 1280);
      failUpload = false;
      expect(await controller.end('session-1'), isTrue);
      expect(client.binaryCalls[0].bytes, client.binaryCalls[1].bytes);
      expect(client.calls.last.path, '/api/meetings/meeting-1/end');
    },
  );

  test(
    'capture interruption keeps meeting active until explicit end',
    () async {
      final ended = Completer<void>();
      client.respond = (_, path) async {
        if (path.endsWith('/end')) {
          ended.complete();
          return row(status: 'Ending');
        }
        if (path.contains('/segments')) return {'items': []};
        return row();
      };
      await controller.start(
        'session-1',
        name: 'Meeting',
        type: 'General',
        consent: true,
      );
      controller.acceptAudio('session-1', Uint8List(1280));
      controller.captureStopped('session-1');
      expect(controller.capturing('session-1'), isFalse);
      await Future<void>.delayed(Duration.zero);
      expect(ended.isCompleted, isFalse);
      expect(controller.meeting('session-1')!.canEnd, isTrue);
      controller.captureStarted('session-1');
      controller.acceptAudio('session-1', Uint8List(1280));
      expect(controller.capturing('session-1'), isTrue);
      await controller.end('session-1');
      await ended.future;
      expect(client.binaryCalls, hasLength(1));
      expect(client.calls.last.path, '/api/meetings/meeting-1/end');
    },
  );

  Future<void> showControl(WidgetTester tester) => tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: MeetingModeControl(
          controller: controller,
          sessionId: 'session-1',
          name: 'Planning',
          canStart: true,
        ),
      ),
    ),
  );

  testWidgets('recording consent is separate from opening meeting mode', (
    tester,
  ) async {
    client.respond = (_, _) async => throw const ApiException('Not found', 404);
    await showControl(tester);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Meeting mode'));
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<FilledButton>(
            find.widgetWithText(FilledButton, 'Start meeting'),
          )
          .onPressed,
      isNull,
    );
    expect(client.calls.where((c) => c.method == 'POST'), isEmpty);
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(client.calls.where((c) => c.method == 'POST'), isEmpty);
    await tester.tap(find.text('Meeting mode'));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(Checkbox));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('Start meeting'));
    await tester.tap(find.text('Start meeting'));
    await tester.pumpAndSettle();
    expect(
      find.text('Phone meeting mode is not available on the server yet.'),
      findsWidgets,
    );
    expect(find.text('Meeting mode on · Recording'), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets(
    'banner waits for recording confirmation and reflects processing',
    (tester) async {
      var current = row(recording: 'Pending');
      client.respond = (_, path) async =>
          path.contains('/segments') ? {'items': []} : current;
      await controller.start(
        'session-1',
        name: 'Planning',
        type: 'Planning',
        consent: true,
      );
      await showControl(tester);
      await tester.pumpAndSettle();
      expect(find.text('Starting meeting mode…'), findsOneWidget);
      expect(find.text('Meeting mode on · Recording'), findsNothing);
      current = row();
      controller.acceptAudio('session-1', Uint8List(1280));
      await controller.refresh('session-1');
      await tester.pumpAndSettle();
      expect(find.text('Meeting mode on · Recording'), findsOneWidget);
      current = row(status: 'Ending', recording: 'Processing');
      await tester.tap(find.text('End meeting'));
      await tester.pumpAndSettle();
      expect(find.text('Meeting ended · Preparing report'), findsOneWidget);
      expect(find.text('Meeting mode on · Recording'), findsNothing);
      current = row(status: 'Failed', recording: 'Ready');
      await controller.refresh('session-1');
      await tester.pumpAndSettle();
      expect(find.text('Meeting processing failed'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    },
  );
}
