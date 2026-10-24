import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/app/theme/app_theme.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/conversations/presentation/widgets/conversation_voice_controls.dart';

void main() {
  testWidgets(
    'start displays the simulated speech indicator and end restores start',
    (tester) async {
      final store = ConversationController();
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light,
          home: Scaffold(body: ConversationVoiceControls(store: store)),
        ),
      );
      await tester.tap(find.text('Start conversation'));
      await tester.pump();
      expect(find.text('Connecting…'), findsOneWidget);
      await tester.pump(const Duration(seconds: 1));
      expect(find.text('Listening · Demo mode'), findsOneWidget);
      expect(find.byTooltip('Mute microphone'), findsOneWidget);
      expect(store.active?.status, 'Active');

      await tester.tap(find.byTooltip('End conversation'));
      await tester.pumpAndSettle();
      expect(find.text('Start conversation'), findsOneWidget);
      expect(store.active?.status, 'Completed');
      await tester.pumpWidget(const SizedBox());
      store.dispose();
    },
  );
}
