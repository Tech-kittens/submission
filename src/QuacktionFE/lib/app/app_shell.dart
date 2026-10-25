import 'package:flutter/material.dart';

import 'dart:async';

import 'package:quacktion_frontend/app/dependencies.dart';
import 'package:quacktion_frontend/app/navigation/app_section.dart';
import 'package:quacktion_frontend/features/account/presentation/screens/account_page.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:quacktion_frontend/features/auth/presentation/screens/auth_screen.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_controller.dart';
import 'package:quacktion_frontend/features/conversations/models/conversation.dart';
import 'package:quacktion_frontend/features/conversations/presentation/screens/conversation_screen.dart';
import 'package:quacktion_frontend/features/conversations/presentation/widgets/history_sidebar.dart';
import 'package:quacktion_frontend/features/devices/presentation/screens/device_setup_screen.dart';
import 'package:quacktion_frontend/features/devices/presentation/widgets/backend_devices_panel.dart';
import 'package:quacktion_frontend/features/integrations/presentation/screens/integrations_page.dart';
import 'package:quacktion_frontend/features/integrations/presentation/widgets/connection_editor.dart';
import 'package:quacktion_frontend/features/integrations/data/mcp_auth_links.dart';
import 'package:quacktion_frontend/features/integrations/presentation/screens/requests_page.dart';
import 'package:quacktion_frontend/features/voice/application/voice_controller.dart';
import 'package:quacktion_frontend/shared/widgets/app_toast.dart';

class AppShell extends StatefulWidget {
  const AppShell({
    super.key,
    required this.auth,
    required this.onMessage,
    this.sessionStore,
    this.createVoice,
    this.authLinks,
  });
  final AuthController auth;
  final McpAuthLinks? authLinks;
  final ConversationController? sessionStore;
  final VoiceController Function()? createVoice;
  final ValueChanged<String> onMessage;
  @override
  State<AppShell> createState() => _AppShellState();
}

class _AppShellState extends State<AppShell> {
  AppSection? _section;
  late final _dependencies = AppDependencies(
    widget.auth.api,
    conversations: widget.sessionStore,
    accountId:
        widget.auth.profile?['id']?.toString() ??
        widget.auth.profile?['email']?.toString(),
  );
  ConversationController get _chats => _dependencies.conversations;
  final _scaffold = GlobalKey<ScaffoldState>();
  bool _completionScheduled = false, _connectionsRouteOpen = false;
  @override
  void initState() {
    super.initState();
    _chats.addListener(_chatChanged);
    _chats.initialize();
    _dependencies.integrations.addListener(_chatChanged);
    _dependencies.integrations.initialize();
    widget.authLinks?.addListener(_authCompleted);
    _authCompleted();
  }

  void _authCompleted() {
    if (widget.authLinks?.pending != true || _completionScheduled) return;
    _completionScheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _completionScheduled = false;
      if (!mounted || widget.authLinks?.pending != true) return;
      widget.authLinks!.consume();
      unawaited(_dependencies.integrations.authorizationReturned());
      if (_connectionsRouteOpen || _section == AppSection.integrations) return;
      // Keep the existing chat mounted so returning from consent preserves audio.
      _connectionsRouteOpen = true;
      Navigator.of(context)
          .push<void>(
            MaterialPageRoute(
              builder: (_) => Scaffold(
                appBar: AppBar(title: const Text('Connections')),
                body: SafeArea(
                  child: SingleChildScrollView(
                    padding: const EdgeInsets.all(24),
                    child: IntegrationsPage(
                      controller: _dependencies.integrations,
                    ),
                  ),
                ),
              ),
            ),
          )
          .whenComplete(() => _connectionsRouteOpen = false);
    });
    WidgetsBinding.instance.ensureVisualUpdate();
  }

  bool _toolReviewScheduled = false, _toolReviewOpen = false;

  void _chatChanged() {
    if (!mounted) return;
    setState(() {});
    if (_dependencies.integrations.toolReviewConnection == null ||
        _toolReviewScheduled ||
        _toolReviewOpen) {
      return;
    }
    _toolReviewScheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      _toolReviewScheduled = false;
      if (!mounted ||
          _toolReviewOpen ||
          WidgetsBinding.instance.lifecycleState == AppLifecycleState.paused ||
          WidgetsBinding.instance.lifecycleState ==
              AppLifecycleState.detached) {
        return;
      }
      final connection = _dependencies.integrations.takeToolReview();
      if (connection == null) return;
      _toolReviewOpen = true;
      await showModalBottomSheet<void>(
        context: context,
        isScrollControlled: true,
        useSafeArea: true,
        showDragHandle: true,
        builder: (_) => ConnectionEditor(
          controller: _dependencies.integrations,
          connection: connection,
        ),
      );
      _toolReviewOpen = false;
      if (mounted) _chatChanged();
    });
    WidgetsBinding.instance.ensureVisualUpdate();
  }

  Future<void> _newConversation() async {
    if (_chats.busy) return;
    await _chats.select(null);
    if (!mounted) return;
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
    onRequests: _openRequests,
    pendingRequests: _dependencies.integrations.pendingCount,
    onAccount: () => setState(() => _section = AppSection.account),
  );

  void _openRequests() {
    // Push a route over the chat so reviewing approvals keeps live audio running.
    Navigator.of(context).push<void>(
      MaterialPageRoute(
        builder: (_) => Scaffold(
          appBar: AppBar(title: const Text('Requests')),
          body: SafeArea(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(24),
              child: RequestsPage(controller: _dependencies.integrations),
            ),
          ),
        ),
      ),
    );
  }

  @override
  void dispose() {
    widget.authLinks?.removeListener(_authCompleted);
    _chats.removeListener(_chatChanged);
    _dependencies.integrations.removeListener(_chatChanged);
    _dependencies.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => ErrorToastListener(
    listenable: _chats,
    message: () => _chats.error,
    onRetry: _chats.refresh,
    child: ErrorToastListener(
      listenable: _dependencies.devices,
      message: () => _dependencies.devices.error,
      onRetry: _dependencies.devices.refresh,
      child: ErrorToastListener(
        listenable: _dependencies.deviceSetup,
        message: () => _dependencies.deviceSetup.error,
        child: ErrorToastListener(
          listenable: _dependencies.integrations,
          message: () => _dependencies.integrations.error,
          child: _buildPage(context),
        ),
      ),
    ),
  );

  Widget _buildPage(BuildContext context) {
    if (_section == null) {
      return ConversationScreen(
        store: _chats,
        createVoice: widget.createVoice ?? _dependencies.createVoice,
        scaffoldKey: _scaffold,
        drawer: _sidebar(),
        integrations: _dependencies.integrations,
        meetings: _dependencies.meetings,
      );
    }
    final title = switch (_section!) {
      AppSection.devices => 'Devices',
      AppSection.integrations => 'Connections',
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
                  AppSection.devices => Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      DeviceSetupScreen(
                        controller: _dependencies.deviceSetup,
                        onMessage: widget.onMessage,
                      ),
                      BackendDevicesPanel(
                        controller: _dependencies.devices,
                        setupController: _dependencies.deviceSetup,
                      ),
                    ],
                  ),
                  AppSection.integrations => IntegrationsPage(
                    controller: _dependencies.integrations,
                  ),
                  AppSection.account => AccountPage(
                    name: widget.auth.profile?['name']?.toString() ?? '',
                    email: widget.auth.profile?['email']?.toString() ?? '',
                    busy: false,
                    onChangePassword: () => Navigator.of(context).push<void>(
                      MaterialPageRoute(
                        builder: (_) => AuthScreen(
                          auth: widget.auth,
                          changePassword: true,
                          onBack: () => Navigator.of(context).pop(),
                          onPasswordChanged: () => Navigator.of(context).pop(),
                        ),
                      ),
                    ),
                    onLogout: () => widget.auth.logout(),
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
