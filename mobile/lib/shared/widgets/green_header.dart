import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Reusable dark green curved header used in Home and Profile screens.
///
/// Renders a dark forest green background with a rounded bottom edge.
/// [height] controls the total header height.
/// [child] is placed inside the header area.
class GreenHeader extends StatelessWidget {
  final double height;
  final Widget child;

  /// The Fieldway boards' light header: the page's chalk background with
  /// dark text, no green band. The child picks dark colors to match.
  final bool light;

  const GreenHeader({
    required this.child,
    super.key,
    this.height = 220,
    this.light = false,
  });

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      height: height,
      child: Stack(
        children: [
          // Green background with rounded bottom
          Container(
            height: height,
            decoration: BoxDecoration(
              color: light ? AppTheme.chalkBackground : AppTheme.primaryGreen,
              borderRadius: light
                  ? null
                  : const BorderRadius.only(
                      bottomLeft: Radius.circular(24),
                      bottomRight: Radius.circular(24),
                    ),
            ),
          ),
          // Content
          SafeArea(
            bottom: false,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 20),
              child: child,
            ),
          ),
        ],
      ),
    );
  }
}
