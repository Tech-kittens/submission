import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';

import 'fake_voice_transport.dart';

void main() {
  test(
    'voice controller reports the live transport state and ends the session',
    () async {
      final transport = FakeVoiceTransport();
      final voice = VoiceController(transport: transport);
      addTearDown(voice.dispose);

      final starting = voice.start();
      expect(voice.running, isTrue);
      expect(voice.status, 'Connecting…');
      await starting;
      expect(voice.connected, isTrue);
      expect(voice.status, 'Listening');
      await voice.stop();
      expect(voice.running, isFalse);
      expect(voice.status, 'Voice conversation ended');
    },
  );
}
