import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/create_intent_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Adaptive shell that switches between sidebar, rail, and bottom navigation.
///
/// - Desktop (>1024px): Full sidebar (260px) with labels, user profile
/// - Tablet (600–1024px): Collapsed NavigationRail (72px) with icons
/// - Mobile (<600px): BottomNavigationBar
class AdaptiveShell extends StatelessWidget {
  final Widget child;

  const AdaptiveShell({required this.child, super.key});

  int _currentIndex(BuildContext context) {
    final location = GoRouterState.of(context).matchedLocation;
    if (location.startsWith('/orders')) return 1;
    if (location.startsWith('/deliveries')) return 2;
    if (location.startsWith('/payments')) return 3;
    return 0;
  }

  void _onFabPressed(BuildContext context, UserRole role) {
    Widget screen;
    switch (role) {
      case UserRole.farmer:
        screen = const CreateCropListingScreen();
      case UserRole.trucker:
        screen = const CreateTripScreen();
      case UserRole.buyer:
        screen = const CreateIntentScreen();
    }
    Navigator.push(
      context,
      MaterialPageRoute<void>(builder: (_) => screen),
    );
  }

  String _fabTooltip(AppLocalizations l10n, UserRole role) {
    return switch (role) {
      UserRole.farmer => l10n.sellCrops,
      UserRole.trucker => l10n.postTrip,
      UserRole.buyer => l10n.newBuyingIntent,
    };
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
    final windowClass = Breakpoints.of(context);
    final selectedIndex = _currentIndex(context);
    final l10n = AppLocalizations.of(context)!;

    final destinations = [
      _NavItem(Icons.home_outlined, Icons.home, l10n.home),
      _NavItem(Icons.receipt_long_outlined, Icons.receipt_long, l10n.orders),
      _NavItem(
        Icons.local_shipping_outlined,
        Icons.local_shipping,
        l10n.deliveries,
      ),
      _NavItem(Icons.payments_outlined, Icons.payments, l10n.payments),
    ];

    final authState = context.watch<AuthBloc>().state;
    final role =
        authState is AuthAuthenticated ? authState.role : UserRole.farmer;

    if (windowClass == WindowClass.mobile) {
      return Scaffold(
        body: child,
        floatingActionButton: FloatingActionButton(
          onPressed: () => _onFabPressed(context, role),
          backgroundColor: AppTheme.primaryGreen,
          tooltip: _fabTooltip(l10n, role),
          child: const Icon(Icons.add, color: Colors.white),
        ),
        bottomNavigationBar: BottomNavigationBar(
          currentIndex: selectedIndex,
          onTap: (i) => _onDestinationSelected(context, i),
          type: BottomNavigationBarType.fixed,
          selectedItemColor: AppTheme.primaryGreen,
          unselectedItemColor: AppTheme.textMuted,
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

    final isDesktop = windowClass == WindowClass.desktop;

    return Scaffold(
      body: Row(
        children: [
          // Sidebar / Rail
          _Sidebar(
            expanded: isDesktop,
            selectedIndex: selectedIndex,
            destinations: destinations,
            onSelected: (i) => _onDestinationSelected(context, i),
            onProfileTap: () => context.go('/profile'),
            onSettingsTap: () => context.go('/settings'),
          ),
          // Content
          Expanded(child: child),
        ],
      ),
      floatingActionButton: FloatingActionButton(
        onPressed: () => _onFabPressed(context, role),
        tooltip: _fabTooltip(l10n, role),
        child: const Icon(Icons.add),
      ),
    );
  }
}

/// Full sidebar (desktop) or compact rail (tablet).
class _Sidebar extends StatelessWidget {
  final bool expanded;
  final int selectedIndex;
  final List<_NavItem> destinations;
  final ValueChanged<int> onSelected;
  final VoidCallback onProfileTap;
  final VoidCallback onSettingsTap;

  const _Sidebar({
    required this.expanded,
    required this.selectedIndex,
    required this.destinations,
    required this.onSelected,
    required this.onProfileTap,
    required this.onSettingsTap,
  });

  @override
  Widget build(BuildContext context) {
    final width =
        expanded ? Breakpoints.sidebarExpanded : Breakpoints.sidebarCollapsed;

    return AnimatedContainer(
      duration: const Duration(milliseconds: 200),
      width: width,
      decoration: const BoxDecoration(
        color: AppTheme.primaryGreenDark,
        boxShadow: [
          BoxShadow(
            color: Color(0x1A000000),
            blurRadius: 8,
            offset: Offset(2, 0),
          ),
        ],
      ),
      child: Column(
        children: [
          // App branding
          _SidebarHeader(expanded: expanded),
          const SizedBox(height: 8),
          // Navigation items
          Expanded(
            child: ListView(
              padding: const EdgeInsets.symmetric(horizontal: 12),
              children: [
                for (int i = 0; i < destinations.length; i++)
                  _SidebarItem(
                    item: destinations[i],
                    selected: selectedIndex == i,
                    expanded: expanded,
                    onTap: () => onSelected(i),
                  ),
              ],
            ),
          ),
          // Bottom section: profile + settings
          const Divider(color: Colors.white12, height: 1),
          _SidebarFooter(
            expanded: expanded,
            onProfileTap: onProfileTap,
            onSettingsTap: onSettingsTap,
          ),
        ],
      ),
    );
  }
}

class _SidebarHeader extends StatelessWidget {
  final bool expanded;
  const _SidebarHeader({required this.expanded});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: EdgeInsets.fromLTRB(
        expanded ? 20 : 12,
        MediaQuery.of(context).padding.top + 20,
        expanded ? 20 : 12,
        16,
      ),
      child: Row(
        children: [
          Container(
            width: 40,
            height: 40,
            decoration: BoxDecoration(
              color: AppTheme.accentGold,
              borderRadius: BorderRadius.circular(10),
            ),
            child: const Icon(
              Icons.agriculture,
              color: Colors.white,
              size: 24,
            ),
          ),
          if (expanded) ...[
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    AppLocalizations.of(context)!.appName,
                    style: const TextStyle(
                      color: Colors.white,
                      fontSize: 16,
                      fontWeight: FontWeight.w700,
                      letterSpacing: -0.3,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    AppLocalizations.of(context)!.aboutTagline,
                    style: const TextStyle(
                      color: Colors.white54,
                      fontSize: 11,
                      fontWeight: FontWeight.w400,
                    ),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _SidebarItem extends StatefulWidget {
  final _NavItem item;
  final bool selected;
  final bool expanded;
  final VoidCallback onTap;

  const _SidebarItem({
    required this.item,
    required this.selected,
    required this.expanded,
    required this.onTap,
  });

  @override
  State<_SidebarItem> createState() => _SidebarItemState();
}

class _SidebarItemState extends State<_SidebarItem> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final bgColor = widget.selected
        ? Colors.white.withAlpha(25)
        : _hovered
            ? Colors.white.withAlpha(13)
            : Colors.transparent;
    final iconColor =
        widget.selected ? AppTheme.accentGold : Colors.white.withAlpha(180);
    final textColor =
        widget.selected ? Colors.white : Colors.white.withAlpha(180);

    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: MouseRegion(
        onEnter: (_) => setState(() => _hovered = true),
        onExit: (_) => setState(() => _hovered = false),
        child: GestureDetector(
          onTap: widget.onTap,
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 150),
            padding: EdgeInsets.symmetric(
              horizontal: widget.expanded ? 14 : 0,
              vertical: 12,
            ),
            decoration: BoxDecoration(
              color: bgColor,
              borderRadius: BorderRadius.circular(10),
              border: widget.selected
                  ? Border(
                      left: BorderSide(
                        color: AppTheme.accentGold,
                        width: widget.expanded ? 3 : 0,
                      ),
                    )
                  : null,
            ),
            child: widget.expanded
                ? Row(
                    children: [
                      Icon(
                        widget.selected
                            ? widget.item.selectedIcon
                            : widget.item.icon,
                        color: iconColor,
                        size: 22,
                      ),
                      const SizedBox(width: 14),
                      Text(
                        widget.item.label,
                        style: TextStyle(
                          color: textColor,
                          fontSize: 14,
                          fontWeight: widget.selected
                              ? FontWeight.w600
                              : FontWeight.w400,
                        ),
                      ),
                    ],
                  )
                : Center(
                    child: Tooltip(
                      message: widget.item.label,
                      child: Icon(
                        widget.selected
                            ? widget.item.selectedIcon
                            : widget.item.icon,
                        color: iconColor,
                        size: 24,
                      ),
                    ),
                  ),
          ),
        ),
      ),
    );
  }
}

class _SidebarFooter extends StatelessWidget {
  final bool expanded;
  final VoidCallback onProfileTap;
  final VoidCallback onSettingsTap;

  const _SidebarFooter({
    required this.expanded,
    required this.onProfileTap,
    required this.onSettingsTap,
  });

  @override
  Widget build(BuildContext context) {
    final authState = context.watch<AuthBloc>().state;
    final name = authState is AuthAuthenticated ? authState.userName : '';
    final initials = name.isNotEmpty ? name[0].toUpperCase() : '?';

    return Padding(
      padding: EdgeInsets.symmetric(
        horizontal: expanded ? 16 : 8,
        vertical: 12,
      ),
      child: Column(
        children: [
          // Settings
          _SidebarFooterButton(
            icon: Icons.settings_outlined,
            label: AppLocalizations.of(context)!.settings,
            expanded: expanded,
            onTap: onSettingsTap,
          ),
          const SizedBox(height: 8),
          // User profile
          GestureDetector(
            onTap: onProfileTap,
            child: MouseRegion(
              cursor: SystemMouseCursors.click,
              child: Container(
                padding: EdgeInsets.all(expanded ? 10 : 8),
                decoration: BoxDecoration(
                  color: Colors.white.withAlpha(13),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: expanded
                    ? Row(
                        children: [
                          CircleAvatar(
                            radius: 16,
                            backgroundColor: AppTheme.accentGold,
                            child: Text(
                              initials,
                              style: const TextStyle(
                                color: Colors.white,
                                fontSize: 13,
                                fontWeight: FontWeight.w700,
                              ),
                            ),
                          ),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Text(
                              name,
                              style: const TextStyle(
                                color: Colors.white,
                                fontSize: 13,
                                fontWeight: FontWeight.w500,
                              ),
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                          Icon(
                            Icons.chevron_right,
                            size: 18,
                            color: Colors.white.withAlpha(120),
                          ),
                        ],
                      )
                    : Center(
                        child: Tooltip(
                          message: name,
                          child: CircleAvatar(
                            radius: 16,
                            backgroundColor: AppTheme.accentGold,
                            child: Text(
                              initials,
                              style: const TextStyle(
                                color: Colors.white,
                                fontSize: 13,
                                fontWeight: FontWeight.w700,
                              ),
                            ),
                          ),
                        ),
                      ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _SidebarFooterButton extends StatefulWidget {
  final IconData icon;
  final String label;
  final bool expanded;
  final VoidCallback onTap;

  const _SidebarFooterButton({
    required this.icon,
    required this.label,
    required this.expanded,
    required this.onTap,
  });

  @override
  State<_SidebarFooterButton> createState() => _SidebarFooterButtonState();
}

class _SidebarFooterButtonState extends State<_SidebarFooterButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: widget.onTap,
        child: Container(
          padding: EdgeInsets.symmetric(
            horizontal: widget.expanded ? 14 : 0,
            vertical: 10,
          ),
          decoration: BoxDecoration(
            color: _hovered ? Colors.white.withAlpha(13) : Colors.transparent,
            borderRadius: BorderRadius.circular(10),
          ),
          child: widget.expanded
              ? Row(
                  children: [
                    Icon(
                      widget.icon,
                      color: Colors.white.withAlpha(180),
                      size: 20,
                    ),
                    const SizedBox(width: 14),
                    Text(
                      widget.label,
                      style: TextStyle(
                        color: Colors.white.withAlpha(180),
                        fontSize: 13,
                      ),
                    ),
                  ],
                )
              : Center(
                  child: Tooltip(
                    message: widget.label,
                    child: Icon(
                      widget.icon,
                      color: Colors.white.withAlpha(180),
                      size: 22,
                    ),
                  ),
                ),
        ),
      ),
    );
  }
}

class _NavItem {
  final IconData icon;
  final IconData selectedIcon;
  final String label;

  const _NavItem(this.icon, this.selectedIcon, this.label);
}
