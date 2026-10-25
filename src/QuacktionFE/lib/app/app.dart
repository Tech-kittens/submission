import 'package:flutter/material.dart';

import 'package:quacktion_frontend/app/app_shell.dart';
import 'package:quacktion_frontend/app/dependencies.dart';
import 'package:quacktion_frontend/app/theme/app_theme.dart';
import 'package:quacktion_frontend/features/auth/presentation/screens/auth_screen.dart';
import 'package:quacktion_frontend/features/integrations/data/mcp_auth_links.dart';
import 'package:quacktion_frontend/shared/widgets/app_toast.dart';

class MainApp extends StatefulWidget {
  const MainApp({super.key});
  @override
  State<MainApp> createState() => _MainAppState();
}

class _MainAppState extends State<MainApp> {
  final _auth = AppDependencies.createAuth();
  final _authLinks = McpAuthLinks();
  bool _loading = true;
  String? _startupError;

  @override
  void initState() {
    super.initState();
    _authLinks.initialize();
    _restore();
  }

  Future<void> _restore() async {
    try {
      await _auth.restore();
    } catch (_) {
      _startupError = 'Could not restore your session. Please sign in again.';
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  void dispose() {
    _authLinks.dispose();
    _auth.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Quacktion',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.light,
      home: _loading
          ? const Scaffold(body: Center(child: CircularProgressIndicator()))
          : ListenableBuilder(
              listenable: _auth,
              builder: (context, _) {
                return _auth.profile == null
                    ? AuthScreen(auth: _auth, initialMessage: _startupError)
                    : AppShell(
                        auth: _auth,
                        authLinks: _authLinks,
                        onMessage: (message) => AppToast.show(context, message),
                      );
              },
            ),
    );
  }
}
