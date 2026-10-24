import 'package:flutter/material.dart';

import 'package:quacktion_frontend/app/dependencies.dart';
import 'package:quacktion_frontend/app/navigation/app_section.dart';
import 'package:quacktion_frontend/features/account/presentation/screens/account_page.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/conversations/models/conversation.dart';
import 'package:quacktion_frontend/features/conversations/presentation/screens/conversation_screen.dart';
import 'package:quacktion_frontend/features/conversations/presentation/widgets/history_sidebar.dart';
import 'package:quacktion_frontend/features/devices/presentation/screens/device_setup_screen.dart';
import 'package:quacktion_frontend/features/integrations/presentation/screens/integrations_page.dart';

class AppShell extends StatefulWidget {
  const AppShell({
    super.key,
    required this.auth,
    required this.onMessage,
    this.sessionStore,
  });
  final AuthController auth;
  final ConversationController? sessionStore;
  final ValueChanged<String> onMessage;
  @override
  State<AppShell> createState() => _AppShellState();
}

class _AppShellState extends State<AppShell> {
  AppSection? _section;
  late final _dependencies = AppDependencies(
    widget.auth.api,
    conversations: widget.sessionStore,
  );
  ConversationController get _chats => _dependencies.conversations;
  final _scaffold = GlobalKey<ScaffoldState>();
  @override
  void initState() {
    super.initState();
    _chats.addListener(_chatChanged);
    _chats.initialize();
    _dependencies.integrations.addListener(_chatChanged);
  }

  void _chatChanged() {
    if (mounted) setState(() {});
  }

  void _newConversation() {
    if (_chats.busy) return;
    _chats.select(null);
    setState(() => _section = null);
  }

  void _openConversation(Conversation conversation) {
    setState(() {
      if (!_chats.busy) {
        _chats.select(conversation as SessionConversation);
      }
      _section = null;
    });
  }

  Widget _sidebar() => HistorySidebar(
    store: _chats,
    activeId: _chats.active?.id ?? '',
    onOpen: _openConversation,
    onNew: _newConversation,
    onDevices: () => setState(() => _section = AppSection.devices),
    onConfiguration: () => setState(() => _section = AppSection.integrations),
    onAccount: () => setState(() => _section = AppSection.account),
  );

  @override
  void dispose() {
    _chats.removeListener(_chatChanged);
    _dependencies.integrations.removeListener(_chatChanged);
    _dependencies.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (_section == null) {
      return ConversationScreen(
        store: _chats,
        createVoice: _dependencies.createVoice,
        scaffoldKey: _scaffold,
        drawer: _sidebar(),
      );
    }
    final title = switch (_section!) {
      AppSection.devices => 'Devices',
      AppSection.integrations => 'Configuration',
      AppSection.account => 'Account settings',
    };
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) setState(() => _section = null);
      },
      child: Scaffold(
        key: _scaffold,
        drawer: _sidebar(),
        appBar: AppBar(
          title: Text(title),
          leading: IconButton(
            tooltip: 'Back to conversation',
            onPressed: () => setState(() => _section = null),
            icon: const Icon(Icons.arrow_back),
          ),
          actions: [
            IconButton(
              tooltip: 'Conversation history',
              onPressed: () => _scaffold.currentState?.openDrawer(),
              icon: const Icon(Icons.menu_rounded),
            ),
          ],
        ),
        body: SafeArea(
          child: Align(
            alignment: Alignment.topCenter,
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 640),
              child: SingleChildScrollView(
                key: PageStorageKey(_section),
                padding: const EdgeInsets.fromLTRB(24, 20, 24, 32),
                child: switch (_section!) {
                  AppSection.devices => DeviceSetupScreen(
                    controller: _dependencies.deviceSetup,
                    onMessage: widget.onMessage,
                  ),
                  AppSection.integrations => IntegrationsPage(
                    connections: _dependencies.integrations.connections,
                    onChanged: _dependencies.integrations.changed,
                  ),
                  AppSection.account => AccountPage(
                    name: widget.auth.profile?['name']?.toString() ?? '',
                    email: widget.auth.profile?['email']?.toString() ?? '',
                    busy: false,
                    onChangePassword: null,
                    onLogout: null,
                    showSecuritySettings: false,
                  ),
                },
              ),
            ),
          ),
        ),
      ),
    );
  }
}
