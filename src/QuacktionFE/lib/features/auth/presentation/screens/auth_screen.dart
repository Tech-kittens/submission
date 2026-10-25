import 'package:flutter/material.dart';

import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';
import 'package:quacktion_frontend/shared/widgets/app_toast.dart';

class AuthScreen extends StatefulWidget {
  const AuthScreen({
    super.key,
    required this.auth,
    this.initialMessage,
    this.changePassword = false,
    this.onBack,
    this.onPasswordChanged,
  });
  final AuthController auth;
  final String? initialMessage;
  final bool changePassword;
  final VoidCallback? onBack;
  final VoidCallback? onPasswordChanged;
  @override
  State<AuthScreen> createState() => _AuthScreenState();
}

class _AuthScreenState extends State<AuthScreen> {
  final _form = GlobalKey<FormState>();
  final _email = TextEditingController();
  final _name = TextEditingController();
  final _password = TextEditingController();
  final _confirm = TextEditingController();
  final _current = TextEditingController();
  bool _register = false;
  bool _busy = false;
  bool _showPassword = false;

  @override
  void initState() {
    super.initState();
    if (widget.initialMessage case final message?) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) AppToast.show(context, message, isError: true);
      });
    }
  }

  @override
  void dispose() {
    for (final controller in [_email, _name, _password, _confirm, _current]) {
      controller.dispose();
    }
    super.dispose();
  }

  Future<void> _submit() async {
    if (_busy || !_form.currentState!.validate()) return;
    FocusScope.of(context).unfocus();
    AppToast.dismiss();
    setState(() => _busy = true);
    try {
      if (widget.changePassword) {
        await widget.auth.changePassword(_current.text, _password.text);
        if (!mounted) return;
        AppToast.show(context, 'Password updated.');
        widget.onPasswordChanged?.call();
      } else if (_register) {
        final email = _email.text;
        final password = _password.text;
        await widget.auth.register(email, password, _name.text);
        if (!mounted) return;
        setState(() {
          _register = false;
          _password.clear();
          _confirm.clear();
        });
        AppToast.show(context, 'Account created. Signing you in…');
        await widget.auth.login(email, password);
      } else {
        await widget.auth.login(_email.text, _password.text);
      }
    } on ApiException catch (error) {
      if (mounted) AppToast.show(context, error.message, isError: true);
    } catch (_) {
      if (mounted) {
        AppToast.show(
          context,
          'Could not complete the request. Please try again.',
          isError: true,
        );
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Widget _field(
    String label,
    TextEditingController controller, {
    bool password = false,
    bool email = false,
    int? maxLength,
    String? Function(String?)? validator,
    Iterable<String>? autofillHints,
    bool last = false,
  }) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: TextFormField(
        controller: controller,
        enabled: !_busy,
        obscureText: password && !_showPassword,
        autocorrect: !password && !email,
        enableSuggestions: !password,
        autofillHints: autofillHints,
        keyboardType: email ? TextInputType.emailAddress : TextInputType.text,
        textInputAction: last ? TextInputAction.done : TextInputAction.next,
        maxLength: maxLength,
        decoration: InputDecoration(
          labelText: label,
          counterText: '',
          suffixIcon: password
              ? IconButton(
                  tooltip: _showPassword ? 'Hide passwords' : 'Show passwords',
                  onPressed: () =>
                      setState(() => _showPassword = !_showPassword),
                  icon: Icon(
                    _showPassword ? Icons.visibility_off : Icons.visibility,
                  ),
                )
              : null,
        ),
        validator:
            validator ??
            (value) => value == null || value.trim().isEmpty
                ? 'Enter your ${label.toLowerCase()}.'
                : null,
        onFieldSubmitted: last ? (_) => _submit() : null,
      ),
    );
  }

  String? _validatePassword(String? value) {
    if (value == null || value.isEmpty) return 'Enter your password.';
    if ((_register || widget.changePassword) && value.length < 12) {
      return 'Use at least 12 characters.';
    }
    if (value.length > 128) return 'Use no more than 128 characters.';
    return null;
  }

  void _toggleMode() {
    setState(() {
      _register = !_register;
      _password.clear();
      _confirm.clear();
      _showPassword = false;
      _form.currentState?.reset();
    });
  }

  @override
  Widget build(BuildContext context) {
    final changing = widget.changePassword;
    final title = changing
        ? 'Change password'
        : _register
        ? 'Create account'
        : 'Welcome back';
    return Scaffold(
      appBar: changing
          ? AppBar(
              leading: IconButton(
                icon: const Icon(Icons.arrow_back),
                tooltip: 'Back',
                onPressed: _busy ? null : widget.onBack,
              ),
            )
          : null,
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 400),
              child: AutofillGroup(
                child: Form(
                  key: _form,
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
                        style: TextStyle(
                          fontSize: 32,
                          fontWeight: FontWeight.w800,
                        ),
                      ),
                      const SizedBox(height: 12),
                      Text(
                        title,
                        textAlign: TextAlign.center,
                        style: Theme.of(context).textTheme.titleLarge,
                      ),
                      const SizedBox(height: 24),
                      if (changing)
                        _field(
                          'Current password',
                          _current,
                          password: true,
                          autofillHints: [AutofillHints.password],
                        ),
                      if (!changing)
                        _field(
                          'Email',
                          _email,
                          email: true,
                          maxLength: 254,
                          autofillHints: [AutofillHints.email],
                          validator: (value) {
                            final email = value?.trim() ?? '';
                            if (!RegExp(r'^[^\s@]+@[^\s@]+\.[^\s@]+$')
                                .hasMatch(email)) {
                              return 'Enter a valid email address.';
                            }
                            return null;
                          },
                        ),
                      if (_register)
                        _field(
                          'Name',
                          _name,
                          maxLength: 128,
                          autofillHints: [AutofillHints.name],
                        ),
                      _field(
                        changing ? 'New password' : 'Password',
                        _password,
                        password: true,
                        validator: _validatePassword,
                        autofillHints: [
                          _register || changing
                              ? AutofillHints.newPassword
                              : AutofillHints.password,
                        ],
                        last: !_register && !changing,
                      ),
                      if (_register || changing) ...[
                        const Padding(
                          padding: EdgeInsets.only(bottom: 16),
                          child: Text('Use 12–128 characters.'),
                        ),
                        _field(
                          'Confirm password',
                          _confirm,
                          password: true,
                          last: true,
                          validator: (value) =>
                              value == null ||
                                  value.isEmpty ||
                                  value != _password.text
                              ? 'Passwords must match.'
                              : null,
                        ),
                      ],
                      FilledButton(
                        onPressed: _busy ? null : _submit,
                        child: _busy
                            ? const SizedBox(
                                width: 22,
                                height: 22,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                ),
                              )
                            : Text(
                                changing
                                    ? 'Update password'
                                    : _register
                                    ? 'Create account'
                                    : 'Sign in',
                              ),
                      ),
                      if (!changing) ...[
                        const SizedBox(height: 12),
                        TextButton(
                          onPressed: _busy ? null : _toggleMode,
                          child: Text(
                            _register
                                ? 'Already have an account? Sign in'
                                : 'Create an account',
                          ),
                        ),
                        if (!_register)
                          TextButton(
                            onPressed: _busy
                                ? null
                                : () => showDialog<void>(
                                    context: context,
                                    builder: (context) => AlertDialog(
                                      title: const Text('Forgot password?'),
                                      content: const Text(
                                        'Password recovery is not available yet. If you know your current password, sign in and choose Change password.',
                                      ),
                                      actions: [
                                        TextButton(
                                          onPressed: () =>
                                              Navigator.pop(context),
                                          child: const Text('Close'),
                                        ),
                                      ],
                                    ),
                                  ),
                            child: const Text('Forgot password?'),
                          ),
                      ],
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
