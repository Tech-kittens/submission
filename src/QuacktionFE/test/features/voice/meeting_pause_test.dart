import 'dart:async';
import 'dart:typed_data';

import 'package:quacktion_frontend/features/voice/data/wake_phrase_detector.dart';

import 'package:flutter_test/flutter_test.dart';
import 'package:flutter/material.dart';
import 'package:quacktion_frontend/features/voice/presentation/widgets/voice_controls.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/meetings/application/meeting_mode_controller.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';

import '../../helpers/fake_api.dart';
import 'fake_voice_transport.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late Api client;
  late MeetingModeController meetings;
  late _QuestionTransport transport;
  late VoiceController voice;
  late _FakeWakeDetector wake;
  var failEnd = false;
  Map<String, dynamic> row(String status) => {
    'id': 'meeting-1',
    'status': status,
    'recordingStatus': 'Recording',
  };
  setUp(() async {
    failEnd = false;
    client = Api();
    client.respond = (_, path) async {
      if (path.endsWith('/end')) {
        if (failEnd) throw StateError('offline');
        return row('Ending');
      }
      if (path.endsWith('/segments')) return {'items': []};
      return row('Active');
    };
    meetings = MeetingModeController(BackendApi(client), autoPoll: false);
    transport = _QuestionTransport();
    wake = _FakeWakeDetector();
    voice = VoiceController(
      transport: transport,
      meetings: meetings,
      wakeDetector: wake,
    );
    await voice.start();
    meetings.captureStarted('test-session');
  });
  tearDown(() {
    voice.dispose();
    meetings.dispose();
    client.dispose();
  });
  Future<bool> startMeeting() => meetings.start(
    'test-session',
    name: 'Planning',
    type: 'Planning',
    consent: true,
  );

  test(
    'meeting pauses before request, keeps capture, and resumes same session',
    () async {
      final session = transport.session;
      final started = Completer<Map<String, dynamic>>();
      final respond = client.respond;
      client.respond = (method, path) =>
          path.endsWith('/start') ? started.future : respond(method, path);
      final starting = startMeeting();
      expect(transport.paused, isTrue);
      expect(voice.pausedForMeeting, isTrue);
      await voice.togglePause();
      expect(transport.paused, isTrue);
      started.complete(row('Active'));
      expect(await starting, isTrue);
      meetings.acceptAudio('test-session', Uint8List(1280));
      expect(meetings.capturing('test-session'), isTrue);
      expect(await meetings.end('test-session'), isTrue);
      expect(client.binaryCalls, hasLength(1));
      expect(transport.paused, isFalse);
      expect(transport.session, same(session));
      expect(voice.connected, isTrue);
    },
  );

  test('asking resumes the same conversation without ending or restarting meeting capture', () async {
    final originalSession = transport.session;
    await startMeeting();
    meetings.acceptAudio('test-session', Uint8List(1280));
    expect(voice.canAskDuringMeeting, isTrue);
    await voice.askDuringMeeting();
    expect(voice.askingDuringMeeting, isTrue);
    expect(transport.paused, isFalse);
    expect(transport.session, same(originalSession));
    meetings.acceptAudio('test-session', Uint8List(1280));
    await meetings.refresh('test-session');
    expect(voice.askingDuringMeeting, isTrue);
    expect(transport.paused, isFalse);
    expect(meetings.capturing('test-session'), isTrue);
    await voice.returnToMeeting();
    expect(transport.paused, isTrue);
    expect(meetings.capturing('test-session'), isTrue);
    expect(client.calls.where((c) => c.path.endsWith('/end')), isEmpty);
    expect(client.calls.where((c) => c.path.endsWith('/start')), hasLength(1));
    await meetings.end('test-session');
    expect(client.binaryCalls.single.bytes, hasLength(2560));
  });

  test('wake phrase only resumes during active meeting recording, and rearms on return', () async {
    final pcm = Uint8List(1280);
    voice.acceptMicrophoneFrame(pcm);
    expect(wake.frames, 0);
    await startMeeting();
    meetings.acceptAudio('test-session', pcm);
    await Future<void>.delayed(Duration.zero);
    expect(voice.wakeListening, isTrue);
    voice.acceptMicrophoneFrame(pcm);
    expect(wake.frames, 1);
    wake.detect();
    await Future<void>.delayed(Duration.zero);
    expect(voice.askingDuringMeeting, isTrue);
    expect(voice.wakeListening, isFalse);
    voice.acceptMicrophoneFrame(pcm);
    wake.detect(); // Late worker result cannot retrigger while assistant speaks.
    expect(wake.frames, 1);
    await voice.returnToMeeting();
    expect(voice.wakeListening, isTrue);
    await meetings.end('test-session');
    expect(voice.wakeListening, isFalse);
    wake.detect();
    expect(voice.askingDuringMeeting, isFalse);
  });

  test(
    'wake detector failure leaves tap-to-ask and meeting recording available',
    () async {
      wake.fail = true;
      await startMeeting();
      meetings.acceptAudio('test-session', Uint8List(1280));
      await Future<void>.delayed(Duration.zero);
      expect(voice.wakeError, isNotNull);
      expect(voice.wakeListening, isFalse);
      expect(meetings.capturing('test-session'), isTrue);
      await voice.askDuringMeeting();
      expect(transport.paused, isFalse);
    },
  );

  test('cannot wake assistant before meeting audio is recording', () async {
    await startMeeting();
    expect(voice.canAskDuringMeeting, isFalse);
    await voice.askDuringMeeting();
    expect(transport.paused, isTrue);
    expect(voice.askingDuringMeeting, isFalse);
  });

  test(
    'resume failure leaves recording running and permits a question retry',
    () async {
      await startMeeting();
      meetings.acceptAudio('test-session', Uint8List(1280));
      transport.failResume = true;
      await voice.askDuringMeeting();
      expect(voice.askingDuringMeeting, isFalse);
      expect(voice.pausedForMeeting, isTrue);
      expect(voice.error, isNotNull);
      expect(meetings.capturing('test-session'), isTrue);
      transport.failResume = false;
      await voice.askDuringMeeting();
      expect(voice.askingDuringMeeting, isTrue);
      expect(voice.error, isNull);
    },
  );

  test(
    'temporary question preserves an earlier manual pause after meeting ends',
    () async {
      await voice.togglePause();
      await startMeeting();
      meetings.acceptAudio('test-session', Uint8List(1280));
      await voice.askDuringMeeting();
      expect(transport.paused, isFalse);
      await meetings.end('test-session');
      expect(voice.askingDuringMeeting, isFalse);
      expect(voice.paused, isTrue);
      expect(transport.paused, isTrue);
    },
  );

  testWidgets('meeting offers Ask assistant and Back to meeting controls', (
    tester,
  ) async {
    await startMeeting();
    meetings.acceptAudio('test-session', Uint8List(1280));
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: VoiceControls(voice: voice, onEnd: () {}),
        ),
      ),
    );
    expect(find.text('Ask assistant'), findsOneWidget);
    await tester.tap(find.text('Ask assistant'));
    await tester.pumpAndSettle();
    expect(find.text('Back to meeting'), findsOneWidget);
    expect(meetings.capturing('test-session'), isTrue);
    await tester.tap(find.text('Back to meeting'));
    await tester.pumpAndSettle();
    expect(find.text('Ask assistant'), findsOneWidget);
    expect(transport.paused, isTrue);
    await tester.pumpWidget(const SizedBox());
  });

  test(
    'manually paused conversation stays paused after meeting ends',
    () async {
      await voice.togglePause();
      await startMeeting();
      await meetings.end('test-session');
      expect(voice.pausedForMeeting, isFalse);
      expect(voice.paused, isTrue);
      expect(transport.paused, isTrue);
      await voice.togglePause();
      expect(transport.paused, isFalse);
    },
  );

  test(
    'failed start releases pause; failed finalization keeps pause until retry',
    () async {
      final respond = client.respond;
      client.respond = (_, _) async => throw StateError('offline');
      expect(await startMeeting(), isFalse);
      expect(transport.paused, isFalse);
      client.respond = respond;
      await startMeeting();
      failEnd = true;
      expect(await meetings.end('test-session'), isFalse);
      expect(transport.paused, isTrue);
      await voice.togglePause();
      expect(transport.paused, isTrue);
      failEnd = false;
      expect(await meetings.end('test-session'), isTrue);
      expect(transport.paused, isFalse);
    },
  );
}

class _QuestionTransport extends FakeVoiceTransport {
  bool failResume = false;
  @override
  Future<void> setPaused(bool value) async {
    if (!value && failResume) throw StateError('offline');
    await super.setPaused(value);
  }
}

class _FakeWakeDetector implements WakePhraseDetector {
  VoidCallback? onWake;
  int frames = 0;
  bool fail = false;
  void detect() => onWake?.call();
  @override
  Future<void> start({
    required VoidCallback onWake,
    required VoidCallback onUnavailable,
  }) async {
    if (fail) throw StateError('model unavailable');
    this.onWake = onWake;
  }

  @override
  void addAudio(Uint8List pcm16) {
    frames++;
  }

  @override
  void reset() {}
  @override
  void dispose() {
    onWake = null;
  }
}
