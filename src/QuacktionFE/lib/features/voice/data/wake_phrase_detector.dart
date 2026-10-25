import 'dart:async';
import 'dart:io';
import 'dart:isolate';
import 'dart:typed_data';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:path_provider/path_provider.dart';
import 'package:sherpa_onnx/sherpa_onnx.dart' as sherpa;

abstract interface class WakePhraseDetector {
  Future<void> start({
    required VoidCallback onWake,
    required VoidCallback onUnavailable,
  });
  void addAudio(Uint8List pcm16);
  void reset();
  void dispose();
}

/// Reuses 16 kHz mono PCM from the phone recorder. Native inference stays off
/// the UI isolate; one bounded batch at a time prevents an audio queue backlog.
class OfflineWakePhraseDetector implements WakePhraseDetector {
  OfflineWakePhraseDetector({this.modelDirectory, this.nativeLibraryDirectory});
  // Optional paths also let desktop tests exercise the real worker and model.
  final String? modelDirectory, nativeLibraryDirectory;
  static const files = [
    'encoder.int8.onnx',
    'decoder.onnx',
    'joiner.int8.onnx',
    'tokens.txt',
    'keywords.txt',
  ];
  Isolate? _worker;
  ReceivePort? _events;
  StreamSubscription<dynamic>? _subscription;
  SendPort? _commands;
  Completer<void>? _ready;
  VoidCallback? _onWake, _onUnavailable;
  bool _disposed = false, _inFlight = false;
  int _generation = 0;
  final _buffer = BytesBuilder(copy: false);

  @override
  Future<void> start({
    required VoidCallback onWake,
    required VoidCallback onUnavailable,
  }) async {
    if (_disposed) return;
    _onWake = onWake;
    _onUnavailable = onUnavailable;
    if (_ready != null) return _ready!.future;
    final ready = _ready = Completer<void>();
    try {
      var directoryPath = modelDirectory;
      if (directoryPath == null) {
        final support = await getApplicationSupportDirectory();
        final directory = Directory(
          '${support.path}/wake-kws-phones-20251220-v1',
        );
        await directory.create(recursive: true);
        for (final name in files) {
          final asset = await rootBundle.load('assets/wake_word/$name');
          await File('${directory.path}/$name').writeAsBytes(
            asset.buffer.asUint8List(asset.offsetInBytes, asset.lengthInBytes),
            flush: true,
          );
        }
        directoryPath = directory.path;
      }
      if (_disposed) return;
      final events = _events = ReceivePort();
      _subscription = events.listen((dynamic event) {
        if (_disposed) return;
        if (event is SendPort) {
          _commands = event;
          if (!ready.isCompleted) ready.complete();
        } else if (event is List && event.first == 'result') {
          _inFlight = false;
          if (event[1] == _generation && event[2] == true) _onWake?.call();
          _sendBatch();
        } else {
          // Includes worker initialization/exit failures. Never stop recording.
          if (!ready.isCompleted) {
            ready.completeError(StateError('Wake phrase detector unavailable'));
          } else {
            _onUnavailable?.call();
            dispose();
          }
        }
      });
      _worker = await Isolate.spawn(
        _runSpotter,
        (events.sendPort, directoryPath, nativeLibraryDirectory),
        onError: events.sendPort,
        onExit: events.sendPort,
      );
      if (_disposed) {
        _worker?.kill();
        return;
      }
      await ready.future.timeout(const Duration(seconds: 20));
    } catch (_) {
      dispose();
      rethrow;
    }
  }

  @override
  void addAudio(Uint8List pcm16) {
    if (_disposed || _commands == null || pcm16.length.isOdd) return;
    // At most half a second pending. Drop stale audio instead of waking late.
    if (_buffer.length + pcm16.length > 16000) reset();
    _buffer.add(Uint8List.fromList(pcm16));
    _sendBatch();
  }

  void _sendBatch() {
    if (_disposed || _commands == null || _inFlight || _buffer.length < 5120) {
      return;
    }
    _inFlight = true;
    _commands!.send(['audio', _generation, _buffer.takeBytes()]);
  }

  @override
  void reset() {
    _generation++;
    _buffer.clear();
    _commands?.send(['reset', _generation]);
  }

  @override
  void dispose() {
    _disposed = true;
    _buffer.clear();
    _commands?.send(['close']);
    // The worker frees native resources before exiting; this bounds failures.
    final worker = _worker;
    if (worker != null) Timer(const Duration(seconds: 1), () => worker.kill());
    _subscription?.cancel();
    _events?.close();
    _commands = null;
    _onWake = null;
  }
}

void _runSpotter((SendPort, String, String?) args) async {
  final (events, directory, nativeLibraryDirectory) = args;
  sherpa.initBindings(nativeLibraryDirectory);
  final spotter = sherpa.KeywordSpotter(
    sherpa.KeywordSpotterConfig(
      model: sherpa.OnlineModelConfig(
        transducer: sherpa.OnlineTransducerModelConfig(
          encoder: '$directory/encoder.int8.onnx',
          decoder: '$directory/decoder.onnx',
          joiner: '$directory/joiner.int8.onnx',
        ),
        tokens: '$directory/tokens.txt',
        numThreads: 1,
        provider: 'cpu',
        debug: false,
      ),
      keywordsFile: '$directory/keywords.txt',
      keywordsThreshold: 0.25,
      keywordsScore: 1.5,
    ),
  );
  var stream = spotter.createStream();
  final commands = ReceivePort();
  events.send(commands.sendPort);
  try {
    await for (final dynamic message in commands) {
      if (message[0] == 'close') break;
      if (message[0] == 'reset') {
        stream.free();
        stream = spotter.createStream();
        continue;
      }
      final pcm = message[2] as Uint8List;
      final bytes = ByteData.sublistView(pcm);
      final samples = Float32List(pcm.length ~/ 2);
      for (var i = 0; i < samples.length; i++) {
        samples[i] = bytes.getInt16(i * 2, Endian.little) / 32768.0;
      }
      stream.acceptWaveform(samples: samples, sampleRate: 16000);
      var detected = false;
      while (spotter.isReady(stream)) {
        spotter.decode(stream);
        if (spotter.getResult(stream).keyword.isNotEmpty) {
          detected = true;
          spotter.reset(stream);
          break;
        }
      }
      events.send(['result', message[1], detected]);
    }
  } finally {
    stream.free();
    spotter.free();
    commands.close();
  }
}
