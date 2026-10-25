import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/features/voice/data/wake_phrase_detector.dart';

void main() {
  final library = Platform.environment['QUACKTION_SHERPA_LIB_DIR'];
  test(
    'real worker detects Hey Quacktion and ignores ordinary speech',
    () async {
      final detector = OfflineWakePhraseDetector(
        modelDirectory: '${Directory.current.path}/assets/wake_word',
        nativeLibraryDirectory: library,
      );
      var detections = 0;
      var unavailable = false;
      try {
        await detector.start(
          onWake: () {
            detections++;
          },
          onUnavailable: () {
            unavailable = true;
          },
        );
        Future<void> feed(String file) async {
          final wav = await File('test/fixtures/wake_word/$file.wav')
              .readAsBytes();
          // Fixtures are synthesized mono PCM16 at 16 kHz with a 44-byte WAV header.
          expect(ByteData.sublistView(wav).getUint32(24, Endian.little), 16000);
          final pcm = Uint8List.fromList([
            ...wav.skip(44),
            ...Uint8List(32000),
          ]);
          for (var offset = 0; offset < pcm.length; offset += 1280) {
            detector.addAudio(
              Uint8List.sublistView(
                pcm,
                offset,
                (offset + 1280).clamp(0, pcm.length),
              ),
            );
            await Future<void>.delayed(const Duration(milliseconds: 40));
          }
          await Future<void>.delayed(const Duration(milliseconds: 500));
        }

        await feed('ordinary_speech');
        expect(detections, 0);
        detector.reset();
        await feed('hey_question');
        expect(detections, 0);
        detector.reset();
        await feed('hey_quacktion');
        expect(detections, 1);
        expect(unavailable, isFalse);
        detector.reset();
        await feed('hey_quacktion');
        expect(detections, 2);
      } finally {
        detector.dispose();
      }
    },
    skip: library == null
        ? 'Set QUACKTION_SHERPA_LIB_DIR for native model validation.'
        : false,
  );
}
