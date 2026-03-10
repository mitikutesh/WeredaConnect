import 'package:flutter/material.dart';

/// Centers its child with a maximum width constraint.
///
/// Useful for keeping form and content areas readable on wide screens
/// (web/tablet) while remaining full-width on narrow screens.
class ResponsiveCenter extends StatelessWidget {
  final double maxWidth;
  final Widget child;

  const ResponsiveCenter({
    required this.child,
    this.maxWidth = 600,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Center(
      child: ConstrainedBox(
        constraints: BoxConstraints(maxWidth: maxWidth),
        child: child,
      ),
    );
  }
}
