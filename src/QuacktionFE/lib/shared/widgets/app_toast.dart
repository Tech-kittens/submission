import 'dart:async';

import 'package:flutter/material.dart';

/// A single, accessible notification above routes, drawers, and setup sheets.
abstract final class AppToast {
  static OverlayEntry? _entry;
  static String? _message;

  static void show(
    BuildContext context,
    String message, {
    bool isError = false,
    String? actionLabel,
    VoidCallback? onAction,
  }) {
    if (!context.mounted || message.trim().isEmpty) return;
    final overlay = Navigator.maybeOf(context, rootNavigator: true)?.overlay;
    if (overlay == null) return;
    if (_entry?.mounted == true && _message == message) return;
    dismiss();
    _message = message;
    late final OverlayEntry entry;
    entry = OverlayEntry(
      builder: (context) => _ToastLifetime(
        duration: Duration(seconds: isError ? 6 : 4),
        onTimeout: () {
          if (_entry == entry) dismiss();
        },
        child: Positioned(
          top: 0,
          left: 0,
          right: 0,
          child: SafeArea(
            bottom: false,
            minimum: const EdgeInsets.fromLTRB(16, 12, 16, 0),
            child: Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 520),
                child: Semantics(
                  liveRegion: true,
                  child: Material(
                    color: const Color(0xFF243E36),
                    elevation: 8,
                    shadowColor: Colors.black26,
                    borderRadius: BorderRadius.circular(18),
                    child: Padding(
                      padding: const EdgeInsets.fromLTRB(16, 8, 4, 8),
                      child: Row(
                        children: [
                          Icon(
                            isError
                                ? Icons.info_outline_rounded
                                : Icons.check_circle_outline_rounded,
                            color: isError
                                ? const Color(0xFFFFD565)
                                : Colors.white,
                            size: 22,
                          ),
                          const SizedBox(width: 12),
                          Expanded(
                            child: Text(
                              message,
                              style: const TextStyle(
                                color: Colors.white,
                                fontSize: 14,
                                height: 1.4,
                              ),
                            ),
                          ),
                          if (actionLabel != null && onAction != null)
                            TextButton(
                              style: TextButton.styleFrom(
                                foregroundColor: const Color(0xFFFFD565),
                              ),
                              onPressed: () {
                                dismiss();
                                onAction();
                              },
                              child: Text(actionLabel),
                            ),
                          IconButton(
                            tooltip: 'Dismiss notification',
                            color: Colors.white70,
                            onPressed: dismiss,
                            icon: const Icon(Icons.close_rounded, size: 18),
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
    _entry = entry;
    overlay.insert(entry);
  }

  static void dismiss() {
    if (_entry?.mounted == true) {
      _entry!.remove();
      _entry!.dispose();
    }
    _entry = null;
    _message = null;
  }
}

class _ToastLifetime extends StatefulWidget {
  const _ToastLifetime({
    required this.duration,
    required this.onTimeout,
    required this.child,
  });
  final Duration duration;
  final VoidCallback onTimeout;
  final Widget child;

  @override
  State<_ToastLifetime> createState() => _ToastLifetimeState();
}

class _ToastLifetimeState extends State<_ToastLifetime> {
  late final Timer _timer;

  @override
  void initState() {
    super.initState();
    _timer = Timer(widget.duration, widget.onTimeout);
  }

  @override
  void dispose() {
    _timer.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}

/// Reports a new controller error once, rather than on every rebuild or tick.
class ErrorToastListener extends StatefulWidget {
  const ErrorToastListener({
    super.key,
    required this.listenable,
    required this.message,
    required this.child,
    this.onRetry,
  });

  final Listenable listenable;
  final String? Function() message;
  final Widget child;
  final VoidCallback? onRetry;

  @override
  State<ErrorToastListener> createState() => _ErrorToastListenerState();
}

class _ErrorToastListenerState extends State<ErrorToastListener> {
  String? _lastMessage;

  @override
  void initState() {
    super.initState();
    widget.listenable.addListener(_changed);
    _changed();
  }

  @override
  void didUpdateWidget(ErrorToastListener oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.listenable != widget.listenable) {
      oldWidget.listenable.removeListener(_changed);
      widget.listenable.addListener(_changed);
      _lastMessage = null;
    }
    _changed();
  }

  void _changed() {
    final message = widget.message();
    if (message == _lastMessage) return;
    _lastMessage = message;
    if (message == null || message.isEmpty) return;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || widget.message() != message) return;
      AppToast.show(
        context,
        message,
        isError: true,
        actionLabel: widget.onRetry == null ? null : 'Retry',
        onAction: widget.onRetry == null
            ? null
            : () {
                if (mounted) widget.onRetry!();
              },
      );
    });
    WidgetsBinding.instance.ensureVisualUpdate();
  }

  @override
  void dispose() {
    widget.listenable.removeListener(_changed);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
