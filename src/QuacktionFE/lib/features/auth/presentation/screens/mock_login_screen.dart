import 'package:flutter/material.dart';

/// Temporary local entry screen. It accepts any value and does not authenticate.
class MockLoginScreen extends StatefulWidget {
  const MockLoginScreen({super.key, required this.onContinue});
  final ValueChanged<String> onContinue;

  @override
  State<MockLoginScreen> createState() => _MockLoginScreenState();
}

class _MockLoginScreenState extends State<MockLoginScreen> {
  final _email = TextEditingController();

  @override
  void dispose() {
    _email.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    body: SafeArea(
      child: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 400),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                const Text(
                  '🦆',
                  textAlign: TextAlign.center,
                  style: TextStyle(fontSize: 56),
                ),
                const Text(
                  'Quacktion',
                  textAlign: TextAlign.center,
                  style: TextStyle(fontSize: 32, fontWeight: FontWeight.w800),
                ),
                const SizedBox(height: 12),
                Text(
                  'Welcome',
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.titleLarge,
                ),
                const SizedBox(height: 24),
                TextField(
                  controller: _email,
                  keyboardType: TextInputType.emailAddress,
                  decoration: const InputDecoration(labelText: 'Email'),
                  onSubmitted: widget.onContinue,
                ),
                const SizedBox(height: 16),
                FilledButton(
                  onPressed: () => widget.onContinue(_email.text),
                  child: const Text('Continue'),
                ),
                const SizedBox(height: 12),
                const Text(
                  'Demo mode · no account required',
                  textAlign: TextAlign.center,
                  style: TextStyle(color: Colors.black54),
                ),
              ],
            ),
          ),
        ),
      ),
    ),
  );
}
