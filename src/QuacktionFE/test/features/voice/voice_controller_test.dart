import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';

void main() {
  test(
    'voice controls simulate connecting, listening, and stopping locally',
    () async {
      final voice = VoiceController();
      addTearDown(voice.dispose);

    final starting = voice.start();
      expect(voice.running, isTrue);
      expect(voice.status, 'Connecting…');
    await starting;
      expect(voice.connected, isTrue);
      expect(voice.status, 'Listening · Demo mode');
      await voice.stop();
      expect(voice.running, isFalse);
      expect(voice.status, 'Voice conversation ended');
    },
  );
}
