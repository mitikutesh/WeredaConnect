import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Adaptive shell that switches between side navigation and bottom navigation.
///
/// - > 768px: Side NavigationRail + expanded content area (web/tablet)
/// - <= 768px: Scaffold with BottomNavigationBar (phone)
class AdaptiveShell extends StatelessWidget {
  final Widget child;

  const AdaptiveShell({required this.child, super.key});

  static const double _breakpoint = 768;

  int _currentIndex(BuildContext context) {
    final location = GoRouterState.of(context).matchedLocation;
    if (location.startsWith('/orders')) return 1;
    if (location.startsWith('/deliveries')) return 2;
    if (location.startsWith('/payments')) return 3;
    return 0;
  }

  void _onDestinationSelected(BuildContext context, int index) {
    switch (index) {
      case 0:
        context.go('/');
      case 1:
        context.go('/orders');
      case 2:
        context.go('/deliveries');
      case 3:
        context.go('/payments');
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final width = MediaQuery.sizeOf(context).width;
    final selectedIndex = _currentIndex(context);

    final destinations = [
      _NavItem(
        icon: Icons.home_outlined,
        selectedIcon: Icons.home,
        label: l10n.home,
      ),
      _NavItem(
        icon: Icons.receipt_long_outlined,
        selectedIcon: Icons.receipt_long,
        label: l10n.orders,
      ),
      _NavItem(
        icon: Icons.local_shipping_outlined,
        selectedIcon: Icons.local_shipping,
        label: l10n.deliveries,
      ),
      _NavItem(
        icon: Icons.payments_outlined,
        selectedIcon: Icons.payments,
        label: l10n.payments,
      ),
    ];

    if (width > _breakpoint) {
      // Wide layout: NavigationRail + content
      return Scaffold(
        body: Row(
          children: [
            NavigationRail(
              selectedIndex: selectedIndex,
              onDestinationSelected: (i) => _onDestinationSelected(context, i),
              labelType: NavigationRailLabelType.all,
              backgroundColor: AppTheme.primaryGreen,
              selectedIconTheme:
                  const IconThemeData(color: AppTheme.accentGold),
              unselectedIconTheme:
                  IconThemeData(color: Colors.white.withAlpha(180)),
              selectedLabelTextStyle:
                  const TextStyle(color: AppTheme.accentGold, fontSize: 12),
              unselectedLabelTextStyle: TextStyle(
                color: Colors.white.withAlpha(180),
                fontSize: 12,
              ),
              leading: const Padding(
                padding: EdgeInsets.symmetric(vertical: 16),
                child: Icon(
                  Icons.agriculture,
                  size: 36,
                  color: Colors.white,
                ),
              ),
              destinations: destinations
                  .map(
                    (d) => NavigationRailDestination(
                      icon: Icon(d.icon),
                      selectedIcon: Icon(d.selectedIcon),
                      label: Text(d.label),
                    ),
                  )
                  .toList(),
            ),
            const VerticalDivider(thickness: 1, width: 1),
            Expanded(child: child),
          ],
        ),
      );
    }

    // Narrow layout: BottomNavigationBar
    return Scaffold(
      body: child,
      bottomNavigationBar: BottomNavigationBar(
        currentIndex: selectedIndex,
        onTap: (i) => _onDestinationSelected(context, i),
        type: BottomNavigationBarType.fixed,
        selectedItemColor: AppTheme.primaryGreen,
        unselectedItemColor: Colors.grey,
        items: destinations
            .map(
              (d) => BottomNavigationBarItem(
                icon: Icon(d.icon),
                activeIcon: Icon(d.selectedIcon),
                label: d.label,
              ),
            )
            .toList(),
      ),
    );
  }
}

class _NavItem {
  final IconData icon;
  final IconData selectedIcon;
  final String label;

  const _NavItem({
    required this.icon,
    required this.selectedIcon,
    required this.label,
  });
}
