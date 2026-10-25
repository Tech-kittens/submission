import 'dart:async';

import 'package:flutter/material.dart';

import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';
import 'package:quacktion_frontend/features/voice/presentation/widgets/voice_controls.dart';

/// One inline audio control shared by empty chats and saved conversations.
class ConversationVoiceControls extends StatefulWidget {
  const ConversationVoiceControls({
    super.key,
    required this.store,
    this.voice,
    this.createVoice,
  });
  final ConversationController store;
  final VoiceController? voice;
  final VoiceController Function()? createVoice;
  @override
  State<ConversationVoiceControls> createState() =>
      _ConversationVoiceControlsState();
}

class _ConversationVoiceControlsState extends State<ConversationVoiceControls>
    with WidgetsBindingObserver {
  late final VoiceController voice = widget.voice ?? widget.createVoice!.call();
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    voice.conversationToResume = () {
      final chat = widget.store.active;
      return chat?.canResume == true ? chat : null;
    };
    voice.conversationName = () {
      final chat = widget.store.active;
      return chat != null && chat.status == 'Created'
          ? chat.title
          : 'Voice conversation';
    };
    voice.onSessionCreated = widget.store.addVoiceSession;
    voice.onSessionUpdated = widget.store.updateVoiceSession;
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.paused ||
        state == AppLifecycleState.detached) {
      debugPrint('Voice stopped: lifecycle ${state.name}');
      unawaited(voice.stop());
    }
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    if (widget.voice == null) voice.dispose();
    super.dispose();
  }

  Future<void> _end() async {
    debugPrint('Voice stopped: end button');
    await voice.stop();
  }

  @override
  Widget build(BuildContext context) => VoiceControls(
    voice: voice,
    onEnd: _end,
    enabled: !widget.store.busy && !widget.store.loading,
  );
}
