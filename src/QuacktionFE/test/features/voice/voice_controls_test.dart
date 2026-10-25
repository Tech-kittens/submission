import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/app/theme/app_theme.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/conversations/presentation/widgets/conversation_voice_controls.dart';
import 'package:quacktion_frontend/features/conversations/presentation/screens/conversation_screen.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';

import 'fake_voice_transport.dart';

void main() {
  testWidgets('start opens the inline voice indicator and end restores start', (
    tester,
  ) async {
    final store = ConversationController();
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light,
        home: Scaffold(
          body: ConversationVoiceControls(
            store: store,
            createVoice: () => VoiceController(transport: FakeVoiceTransport()),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Start conversation'));
    await tester.pump();
    expect(find.text('Starting conversation…'), findsOneWidget);
    expect(find.bySemanticsLabel('Microphone input level'), findsNothing);
    expect(find.byTooltip('Mute microphone'), findsNothing);
    await tester.pump(const Duration(milliseconds: 60));
    expect(find.text('Starting conversation…'), findsNothing);
    expect(find.bySemanticsLabel('Microphone input level'), findsOneWidget);
    expect(find.text('Listening'), findsOneWidget);
    expect(find.byTooltip('Mute microphone'), findsOneWidget);
    expect(store.active?.status, 'Active');

    await tester.tap(find.byTooltip('Pause conversation'));
    await tester.pumpAndSettle();
    expect(find.text('Conversation paused'), findsOneWidget);
    expect(store.active?.status, 'Active');
    await tester.tap(find.byTooltip('Resume conversation'));
    await tester.pumpAndSettle();
    expect(find.text('Listening'), findsOneWidget);

    await tester.tap(find.byTooltip('End conversation'));
    await tester.pumpAndSettle();
    expect(find.text('Resume conversation'), findsOneWidget);
    expect(store.active?.status, 'Completed');
    final saved = store.active;
    await tester.tap(find.text('Resume conversation'));
    await tester.pumpAndSettle();
    expect(store.active, same(saved));
    expect(find.text('Listening'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    store.dispose();
  });

  testWidgets(
    'start button becomes a starting button in the same place, then a waveform',
    (tester) async {
      final store = ConversationController();
      addTearDown(store.dispose);
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light,
          home: ConversationScreen(
            store: store,
            drawer: const Drawer(),
            scaffoldKey: GlobalKey<ScaffoldState>(),
            createVoice: () => VoiceController(transport: FakeVoiceTransport()),
          ),
        ),
      );
      expect(find.text('Let’s talk'), findsOneWidget);
      final startPosition = tester.getCenter(find.byType(FilledButton));
      await tester.tap(find.text('Start conversation'));
      await tester.pump();
      expect(find.text('Starting conversation…'), findsOneWidget);
      expect(find.text('Let’s talk'), findsOneWidget);
      expect(find.bySemanticsLabel('Microphone input level'), findsNothing);
      expect(tester.getCenter(find.byType(FilledButton)), startPosition);
      expect(
        tester.widget<FilledButton>(find.byType(FilledButton)).onPressed,
        isNull,
      );

      await tester.pump(const Duration(milliseconds: 60));
      expect(find.text('Starting conversation…'), findsNothing);
      expect(find.bySemanticsLabel('Microphone input level'), findsOneWidget);
      await tester.tap(find.byTooltip('End conversation'));
      await tester.pumpAndSettle();
      await tester.pumpWidget(const SizedBox());
    },
  );
}
