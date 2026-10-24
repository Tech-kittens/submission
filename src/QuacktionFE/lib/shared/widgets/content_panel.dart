import 'package:flutter/material.dart';

class ContentPanel extends StatelessWidget {
  const ContentPanel({super.key, required this.child});
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(22),
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(24),
        border: Border.all(color: const Color(0xFFE8E4DA)),
      ),
      child: child,
    );
  }
}
