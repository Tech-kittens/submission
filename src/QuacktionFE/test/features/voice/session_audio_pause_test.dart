import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/features/conversations/data/phone_audio_engine.dart';
import 'package:quacktion_frontend/features/conversations/data/session_audio_transport.dart';

import '../../helpers/fake_api.dart';

class TokenApi extends Api {
  String url = '';
  @override
  String get baseUrl => url;
  @override
  Future<String> accessToken() async => 'test-token';
}

class Audio extends PhoneAudioEngine {
  final frames = StreamController<Uint8List>.broadcast(sync: true);
  final played = <Uint8List>[];
  final playback = StreamController<void>.broadcast();
  final cleared = StreamController<void>.broadcast();
  bool capturing = false;
  @override
  Stream<Uint8List> get microphoneFrames => frames.stream;
  @override
  Future<void> startCapture(int sampleRate) async {
    capturing = true;
  }

  @override
  Future<void> stopCapture() async {
    capturing = false;
  }

  @override
  Future<void> startPlayback(int sampleRate) async {}
  @override
  Future<void> stopPlayback() async {}
  @override
  Future<void> clearPlayback() async {
    cleared.add(null);
  }

  @override
  Future<void> playFrame(Uint8List frame) async {
    played.add(frame);
    playback.add(null);
  }
}

void main() {
  test(
    'pause blocks socket input and output, keeps meeting frames, and resumes',
    () async {
      final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      final sockets = <WebSocket>[];
      final controls = <String>[];
      final received = <List<int>>[];
      final incoming = StreamController<void>.broadcast();
      server.listen((request) async {
        final socket = await WebSocketTransformer.upgrade(request);
        sockets.add(socket);
        socket.listen((event) {
          if (event is List<int>) {
            received.add(event);
            incoming.add(null);
          } else {
            final type = jsonDecode(event as String)['type'] as String;
            controls.add(type);
            if (type == 'audio.start') {
              socket.add(jsonEncode({'type': 'audio.ready'}));
            }
          }
        });
        socket.add(jsonEncode({'type': 'session.started'}));
      });
      final client = TokenApi()..url = 'http://127.0.0.1:${server.port}';
      client.respond = (_, path) async => path.endsWith('/resume')
          ? {'id': 'session-1', 'status': 'Suspended'}
          : {
              'sessionId': 'session-1',
              'webSocketUrl': 'ws://127.0.0.1:${server.port}',
            };
      final audio = Audio();
      final meetingFrames = <Uint8List>[];
      final transport = SessionAudioTransport(
        api: BackendApi(client),
        client: client,
        audio: audio,
        onMicrophoneFrame: (_, frame) => meetingFrames.add(frame),
      );
      addTearDown(() async {
        await transport.stop(sendEnd: false);
        await server.close(force: true);
        await audio.frames.close();
        await audio.playback.close();
        await audio.cleared.close();
        await incoming.close();
        client.dispose();
      });
      await transport.start(
        name: 'Test',
        onSessionCreated: (_) {},
        onStatus: (_) {},
        onInputLevel: (_) {},
        onClosed: (_) {},
      );
      final socket = sockets.single;
      final session = transport.session;
      var heard = incoming.stream.first;
      audio.frames.add(Uint8List.fromList([1, 0]));
      await heard.timeout(const Duration(seconds: 2));
      socket.add([3, 0]);
      await transport.setPaused(true);
      expect(audio.capturing, isTrue);
      expect(transport.session?.status, 'Suspended');
      expect(controls, isNot(contains('session.end')));
      audio.frames.add(Uint8List.fromList([2, 0]));
      expect(audio.played, isEmpty);
      expect(meetingFrames, hasLength(2));
      final respond = client.respond;
      client.respond = (_, _) async =>
          throw const ApiException('Busy', 409, 'conversation_in_use');
      await expectLater(
        transport.setPaused(false),
        throwsA(isA<SessionAudioException>()),
      );
      expect(transport.session?.status, 'Suspended');
      expect(audio.capturing, isTrue);
      expect(sockets, hasLength(1));
      client.respond = respond;
      await transport.setPaused(false);
      heard = incoming.stream.first;
      audio.frames.add(Uint8List.fromList([4, 0]));
      await heard.timeout(const Duration(seconds: 2));
      expect(received, [
        [1, 0],
        [4, 0],
      ]);
      final played = audio.playback.stream.first;
      sockets.last.add([5, 0]);
      await played.timeout(const Duration(seconds: 2));
      expect(audio.played, [
        [5, 0],
      ]);
      expect(transport.session, same(session));
      expect(sockets, hasLength(2));
      expect(client.calls.map((c) => c.path), [
        '/api/sessions/start',
        '/api/sessions/session-1/resume',
        '/api/sessions/session-1/resume',
      ]);
      await transport.stop(sendEnd: false);
      session!.status = 'Completed';
      await transport.start(
        name: 'Saved',
        resumeSession: session,
        onSessionCreated: (_) {},
        onStatus: (_) {},
        onInputLevel: (_) {},
        onClosed: (_) {},
      );
      expect(sockets, hasLength(3));
      expect(transport.session, same(session));
      expect(client.calls.last.path, '/api/sessions/session-1/resume');
      await transport.setPaused(true);
      final resuming = transport.setPaused(false);
      final stopping = transport.stop(sendEnd: false);
      await resuming;
      await stopping;
      expect(audio.capturing, isFalse);
      await transport.setPaused(true);
      await transport.end();
      expect(client.calls.last.path, '/api/sessions/session-1/end');
      expect(transport.session?.status, 'Completed');
      expect(audio.capturing, isFalse);
    },
  );
}
