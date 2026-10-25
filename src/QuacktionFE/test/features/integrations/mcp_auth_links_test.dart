import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/features/integrations/data/mcp_auth_links.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  const channel = MethodChannel('quacktion/external_auth');
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
  late McpAuthLinks links;
  late bool nativePending;

  Future<void> notifyLink() async {
    final done = Completer<void>();
    await messenger.handlePlatformMessage(
      channel.name,
      const StandardMethodCodec().encodeMethodCall(
        const MethodCall('completionAvailable'),
      ),
      (_) => done.complete(),
    );
    await done.future;
  }

  setUp(() {
    links = McpAuthLinks();
    nativePending = false;
    messenger.setMockMethodCallHandler(channel, (call) async {
      expect(call.method, 'takePendingCompletion');
      final result = nativePending;
      nativePending = false;
      return result;
    });
  });
  tearDown(() {
    links.dispose();
    messenger.setMockMethodCallHandler(channel, null);
  });

  test(
    'cold-start completion stays queued until the signed-in shell consumes it',
    () async {
      nativePending = true;
      await links.initialize();
      expect(links.pending, isTrue);
      await Future<void>.value();
      expect(links.pending, isTrue);
      links.consume();
      expect(links.pending, isFalse);
      await notifyLink();
      expect(links.pending, isFalse);
    },
  );

  test('warm completions notify once while navigation is pending', () async {
    await links.initialize();
    var notifications = 0;
    links.addListener(() => notifications++);
    nativePending = true;
    await notifyLink();
    nativePending = true;
    await notifyLink();
    expect(notifications, 1);
    links.consume();
    nativePending = true;
    await notifyLink();
    expect(notifications, 2);
  });

  test('unsupported platforms do not fail app startup', () async {
    messenger.setMockMethodCallHandler(channel, null);
    await links.initialize();
    expect(links.pending, isFalse);
  });
}
