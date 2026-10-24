import 'package:flutter/material.dart';

import 'package:quacktion_frontend/shared/widgets/content_panel.dart';
import 'package:quacktion_frontend/shared/widgets/page_heading.dart';

class AccountPage extends StatelessWidget {
  const AccountPage({
    super.key,
    required this.name,
    required this.email,
    required this.busy,
    required this.onChangePassword,
    required this.onLogout,
    this.showSecuritySettings = true,
  });
  final String name;
  final String email;
  final bool busy;
  final VoidCallback? onChangePassword;
  final VoidCallback? onLogout;
  final bool showSecuritySettings;
  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        PageHeading(
          title: 'Your account',
          subtitle: 'The personal side of Quacktion.',
        ),
        ContentPanel(
          child: Column(
            children: [
              const CircleAvatar(
                radius: 36,
                backgroundColor: Color(0xFFFFE69B),
                child: Icon(
                  Icons.person_rounded,
                  size: 38,
                  color: Color(0xFF493B16),
                ),
              ),
              const SizedBox(height: 16),
              Text(
                name,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.titleLarge
                    ?.copyWith(fontWeight: FontWeight.w700),
              ),
              const SizedBox(height: 6),
              Text(
                email,
                textAlign: TextAlign.center,
                style: const TextStyle(color: Color(0xFF69685F)),
              ),
            ],
          ),
        ),
        if (showSecuritySettings) ...[
          const SizedBox(height: 28),
          Text(
            'Settings',
            style: Theme.of(context).textTheme.titleLarge
                ?.copyWith(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 14),
          Card(
            margin: EdgeInsets.zero,
            elevation: 0,
            color: Colors.white,
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(20),
              side: const BorderSide(color: Color(0xFFE8E4DA)),
            ),
            clipBehavior: Clip.antiAlias,
            child: Column(
              children: [
                ListTile(
                  contentPadding: const EdgeInsets.symmetric(
                    horizontal: 20,
                    vertical: 8,
                  ),
                  leading: const Icon(Icons.lock_outline_rounded),
                  title: const Text('Change password'),
                  subtitle: const Text('Keep your account secure'),
                  trailing: const Icon(Icons.chevron_right),
                  onTap: onChangePassword,
                ),
                const Divider(height: 1, indent: 20, endIndent: 20),
                ListTile(
                  contentPadding: const EdgeInsets.symmetric(
                    horizontal: 20,
                    vertical: 8,
                  ),
                  leading: const Icon(Icons.logout_rounded),
                  title: Text(busy ? 'Signing out…' : 'Sign out'),
                  onTap: onLogout,
                ),
              ],
            ),
          ),
        ],
      ],
    );
  }
}
