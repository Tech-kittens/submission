import 'dart:collection';
import 'dart:async';
import 'dart:typed_data';

/// Ordered PCM16 uploads. Retains failed bytes and offsets for an idempotent retry.
class MeetingAudioUpload {
  MeetingAudioUpload({
    required this.upload,
    required this.onFailure,
    this.onRecovered,
    this.retryDelay = const Duration(seconds: 5),
    this.chunkBytes = 960000,
    this.maxBufferedBytes = 3840000,
  });
  final Future<void> Function(int offsetMs, Uint8List bytes) upload;
  final void Function() onFailure;
  final void Function()? onRecovered;
  final Duration retryDelay;
  Timer? _retry;
  bool bufferFull = false;
  final int chunkBytes, maxBufferedBytes;
  final _queue = ListQueue<({int offset, Uint8List bytes})>();
  final _buffer = BytesBuilder(copy: true);
  Future<void>? _work;
  int _nextOffset = 0, _queuedBytes = 0;
  bool failed = false, closed = false, _discarded = false;
  Object? lastError;

  bool add(Uint8List bytes) {
    if (closed || bufferFull) return false;
    if (_queuedBytes + _buffer.length + bytes.length > maxBufferedBytes) {
      failed = true;
      bufferFull = true;
      lastError = StateError('meeting_audio_buffer_full');
      onFailure();
      return false;
    }
    var index = 0;
    while (index < bytes.length) {
      final remaining = chunkBytes - _buffer.length;
      final count = bytes.length - index < remaining
          ? bytes.length - index
          : remaining;
      _buffer.add(Uint8List.sublistView(bytes, index, index + count));
      index += count;
      if (_buffer.length == chunkBytes) _enqueue(_buffer.takeBytes());
    }
    if (!failed) unawaited(_startDrain());
    return true;
  }

  void _enqueue(Uint8List bytes) {
    _queue.add((offset: _nextOffset, bytes: bytes));
    _nextOffset += bytes.length ~/ 32;
    _queuedBytes += bytes.length;
  }

  Future<void> _startDrain() => _work ??= _drain().whenComplete(() {
    _work = null;
  });

  Future<void> _drain() async {
    while (!_discarded && _queue.isNotEmpty) {
      final segment = _queue.first;
      try {
        await upload(segment.offset, segment.bytes);
      } catch (error) {
        if (!_discarded) {
          failed = true;
          lastError = error;
          onFailure();
          if (!closed) {
            _retry ??= Timer(retryDelay, () {
              _retry = null;
              if (!_discarded && !closed) unawaited(_startDrain());
            });
          }
        }
        return;
      }
      if (_discarded) return;
      _queue.removeFirst();
      _queuedBytes -= segment.bytes.length;
      if (failed && !bufferFull) {
        failed = false;
        lastError = null;
        onRecovered?.call();
      }
    }
  }

  /// Retries the same queued offsets without closing microphone capture.
  Future<void> retry() async {
    if (_discarded || closed) return;
    await _work;
    if (_discarded || closed) return;
    _retry?.cancel();
    _retry = null;
    await _startDrain();
  }

  Future<void> finish() async {
    if (_discarded) throw StateError('meeting_audio_discarded');
    closed = true;
    _retry?.cancel();
    _retry = null;
    await _work;
    if (_buffer.isNotEmpty) {
      // The endpoint requires whole 40ms (1280-byte) frames. Pad the final tail
      // with at most 39.97ms of silence instead of dropping the end of speech.
      final tail = _buffer.takeBytes();
      final padded = Uint8List(((tail.length + 1279) ~/ 1280) * 1280)
        ..setAll(0, tail);
      _enqueue(padded);
    }
    failed = false;
    lastError = null;
    await _startDrain();
    if (failed || _queue.isNotEmpty) {
      throw StateError('meeting_audio_upload_pending');
    }
  }

  void discard() {
    closed = true;
    _discarded = true;
    _retry?.cancel();
    _retry = null;
    _buffer.clear();
    _queue.clear();
    _queuedBytes = 0;
    // An in-flight upload may finish; no further chunks are sent after logout.
  }
}
