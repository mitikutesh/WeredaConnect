import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/create_intent_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Adaptive shell that switches between sidebar, rail, and bottom navigation.
///
/// - Desktop (>1024px): Full sidebar (260px) with labels, user profile
/// - Tablet (600–1024px): Collapsed NavigationRail (72px) with icons
/// - Mobile (<600px): BottomNavigationBar
///
/// Navigation is **role-shaped** and comes from [tabsForRole] — the same
/// single source of truth the mobile shell uses, so both platforms always
/// present identical destination sets per role. The sidebar
/// (tablet/desktop) appends [sidebarExtrasForRole] so destinations that
/// didn't earn a bar slot stay one click away on wide screens.
///
/// The role FAB shows only on a tab root: it hides on non-tab routes
/// (profile, settings) and while [pushedRoutes] reports a screen pushed on
/// top of the shell's navigator, so it never covers that screen's own
/// actions (SIM-44).
class AdaptiveShell extends StatelessWidget {
  final Widget child;

  /// Tracks screens pushed imperatively onto the shell navigator. Null (tests,
  /// standalone use) means none are ever reported.
  final ShellPushedRoutesObserver? pushedRoutes;

  const AdaptiveShell({required this.child, this.pushedRoutes, super.key});

  static final ValueNotifier<int> _noPushedRoutes = ValueNotifier<int>(0);

  // ── Destination helpers ──────────────────────────────────────────────

  List<_NavDestination> _destinations(
    AppLocalizations l10n,
    UserRole role, {
    required bool includeSidebarExtras,
  }) {
    final tabs = [
      ...tabsForRole(role),
      if (includeSidebarExtras) ...sidebarExtrasForRole(role),
    ];
    return [
      for (final tab in tabs)
        _NavDestination(
          tab,
          tab.icon,
          tab.activeIcon,
          tab.label(l10n),
          tab.route,
        ),
    ];
  }

  int _currentIndex(BuildContext context, List<_NavDestination> dests) {
    final location = GoRouterState.of(context).matchedLocation;
    for (int i = 0; i < dests.length; i++) {
      final route = dests[i].route;
      if (route == '/') {
        if (location == '/' || location.isEmpty) return i;
      } else if (location.startsWith(route)) {
        return i;
      }
    }
    // Routes like /settings and /profile are not in the nav destinations.
    // Return -1 so no nav item is highlighted.
    return -1;
  }

  void _onDestinationSelected(
    BuildContext context,
    int index,
    List<_NavDestination> dests,
  ) {
    if (index >= 0 && index < dests.length) {
      context.go(dests[index].route);
    }
  }

  // ── FAB ──────────────────────────────────────────────────────────────

  Future<void> _onFabPressed(BuildContext context, UserRole role) async {
    Widget screen;
    switch (role) {
      case UserRole.farmer:
        screen = const CreateCropListingScreen();
      case UserRole.trucker:
        screen = const CreateTripScreen();
      case UserRole.buyer:
        screen = const CreateIntentScreen();
    }
    await Navigator.push(
      context,
      MaterialPageRoute<void>(builder: (_) => screen),
    );
    // Re-navigate to current route to force child rebuild (refreshes overview stats)
    if (context.mounted) {
      final location = GoRouterState.of(context).matchedLocation;
      // ignore: use_build_context_synchronously
      context.go(location);
    }
  }

  Widget _fab(
    BuildContext context,
    AppLocalizations l10n,
    UserRole role, {
    required bool onTabRoot,
  }) {
    return ValueListenableBuilder<int>(
      valueListenable: pushedRoutes?.count ?? _noPushedRoutes,
      builder: (context, pushed, _) {
        if (!onTabRoot || pushed > 0) return const SizedBox.shrink();
        return SizedBox(
          height: 44,
          child: FloatingActionButton.extended(
            heroTag: 'role_fab',
            elevation: 0,
            highlightElevation: 0,
            onPressed: () => _onFabPressed(context, role),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(14),
            ),
            backgroundColor: role == UserRole.trucker
                ? const Color(0xFFD9922B)
                : AppTheme.soilDark,
            foregroundColor: role == UserRole.trucker
                ? const Color(0xFF15201A)
                : Colors.white,
            icon: const Icon(Icons.add, size: 18),
            label: Text(
              _fabTooltip(l10n, role),
              style: TextStyle(
                fontSize: 14,
                fontWeight: role == UserRole.trucker
                    ? FontWeight.w800
                    : FontWeight.w700,
              ),
            ),
          ),
        );
      },
    );
  }

  String _fabTooltip(AppLocalizations l10n, UserRole role) {
    return switch (role) {
      UserRole.farmer => l10n.sellCrops,
      UserRole.trucker => l10n.postTrip,
      UserRole.buyer => l10n.newBuyingIntent,
    };
  }

  // ── Build ────────────────────────────────────────────────────────────

  @override
  Widget build(BuildContext context) {
    final windowClass = Breakpoints.of(context);
    final l10n = AppLocalizations.of(context)!;

    final authState = context.watch<AuthBloc>().state;
    final role =
        authState is AuthAuthenticated ? authState.role : UserRole.farmer;

    // Role identity accent — decorative only; picks the variant with
    // ≥3:1 icon contrast for the ground it sits on.
    final isDarkTheme = Theme.of(context).brightness == Brightness.dark;
    final navAccent =
        isDarkTheme ? AppTheme.darkRoleAccent(role) : AppTheme.roleAccent(role);
    // The sidebar ground is the dark teal rail in both themes.
    final sidebarAccent = AppTheme.darkRoleAccent(role);

    if (windowClass == WindowClass.mobile) {
      final dests = _destinations(l10n, role, includeSidebarExtras: false);
      final selectedIndex = _currentIndex(context, dests);

      // BottomNavigationBar requires currentIndex in [0, items.length).
      // Clamp to 0 when on non-nav routes (settings, profile) to avoid crash.
      final clampedIndex = selectedIndex < 0 ? 0 : selectedIndex;

      return Scaffold(
        body: child,
        floatingActionButton:
            _fab(context, l10n, role, onTabRoot: selectedIndex >= 0),
        bottomNavigationBar: BottomNavigationBar(
          currentIndex: clampedIndex,
          onTap: (i) => _onDestinationSelected(context, i, dests),
          type: BottomNavigationBarType.fixed,
          selectedItemColor: selectedIndex < 0 ? AppTheme.textMuted : navAccent,
          unselectedItemColor: AppTheme.textMuted,
          items: dests
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

    // Tablet / Desktop — sidebar with 5 items
    final isDesktop = windowClass == WindowClass.desktop;
    final dests = _destinations(l10n, role, includeSidebarExtras: true);
    final selectedIndex = _currentIndex(context, dests);

    final desktopFab = _fab(context, l10n, role, onTabRoot: selectedIndex >= 0);

    return Scaffold(
      backgroundColor: AppTheme.chalkBackground,
      body: Row(
        children: [
          // Sidebar / Rail
          _Sidebar(
            expanded: isDesktop,
            selectedIndex: selectedIndex,
            destinations: dests,
            accent: sidebarAccent,
            currentLocation: GoRouterState.of(context).matchedLocation,
            onSelected: (i) => _onDestinationSelected(context, i, dests),
            onProfileTap: () => context.go('/profile'),
            onSettingsTap: () => context.go('/settings'),
            actionButton: desktopFab,
            role: role,
          ),
          // Content
          Expanded(child: child),
        ],
      ),
    );
  }
}

/// Counts pageless routes (screens opened with `Navigator.push`) sitting on
/// top of the shell navigator's go_router pages. Attach it to the
/// `ShellRoute` via `observers` and pass it to [AdaptiveShell].
///
/// go_router's own pages carry a [Page] as their settings and are ignored, so
/// tab switches never skew the count; pushed routes that go_router drops on a
/// tab switch arrive through [didRemove].
class ShellPushedRoutesObserver extends NavigatorObserver {
  final ValueNotifier<int> count = ValueNotifier<int>(0);

  bool _isPushedScreen(Route<dynamic>? route) =>
      route is PageRoute && route.settings is! Page;

  int _depth = 0;

  void _adjust(Route<dynamic>? route, int delta) {
    if (!_isPushedScreen(route)) return;
    _depth = (_depth + delta).clamp(0, 1 << 20);
    // go_router drops pages during build; notifying the shell then would
    // mark an ancestor dirty mid-build, so publish after the frame.
    if (SchedulerBinding.instance.schedulerPhase ==
        SchedulerPhase.persistentCallbacks) {
      SchedulerBinding.instance.addPostFrameCallback((_) {
        count.value = _depth;
      });
    } else {
      count.value = _depth;
    }
  }

  @override
  void didPush(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      _adjust(route, 1);

  @override
  void didPop(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      _adjust(route, -1);

  @override
  void didRemove(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      _adjust(route, -1);

  @override
  void didReplace({Route<dynamic>? newRoute, Route<dynamic>? oldRoute}) {
    _adjust(oldRoute, -1);
    _adjust(newRoute, 1);
  }
}

// ═══════════════════════════════════════════════════════════════════════
// Private widgets
// ═══════════════════════════════════════════════════════════════════════

/// Full sidebar (desktop) or compact rail (tablet).
class _Sidebar extends StatelessWidget {
  final bool expanded;
  final int selectedIndex;
  final List<_NavDestination> destinations;
  final Color accent;
  final String currentLocation;
  final ValueChanged<int> onSelected;
  final VoidCallback onProfileTap;
  final VoidCallback onSettingsTap;
  final Widget? actionButton;
  final UserRole role;

  const _Sidebar({
    required this.expanded,
    required this.selectedIndex,
    required this.destinations,
    required this.accent,
    required this.currentLocation,
    required this.onSelected,
    required this.onProfileTap,
    required this.onSettingsTap,
    required this.role,
    this.actionButton,
  });

  @override
  Widget build(BuildContext context) {
    final width =
        expanded ? Breakpoints.sidebarExpanded : Breakpoints.sidebarCollapsed;

    return Container(
      width: width,
      decoration: const BoxDecoration(
        color: Colors.white,
        border: Border(
          right: BorderSide(
            color: AppTheme.whisperBorder,
          ),
        ),
      ),
      child: Column(
        children: [
          // App branding
          _SidebarHeader(expanded: expanded, accent: accent),
          const SizedBox(height: 12),
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
                    accent: accent,
                    onTap: () => onSelected(i),
                  ),
                if (expanded && actionButton != null) ...[
                  const SizedBox(height: 16),
                  actionButton!,
                ],
              ],
            ),
          ),
          // Bottom section: profile + settings
          const Divider(color: AppTheme.whisperBorder, height: 1),
          _SidebarFooter(
            expanded: expanded,
            role: role,
            isSettingsActive: currentLocation.startsWith('/settings'),
            isProfileActive: currentLocation.startsWith('/profile'),
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
  final Color accent;

  const _SidebarHeader({required this.expanded, required this.accent});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: EdgeInsets.fromLTRB(
        expanded ? 20 : 12,
        MediaQuery.of(context).padding.top + 20,
        expanded ? 20 : 12,
        8,
      ),
      child: Row(
        children: [
          Container(
            width: 40,
            height: 40,
            decoration: BoxDecoration(
              color: AppTheme.fieldGreen,
              borderRadius: BorderRadius.circular(12),
            ),
            child: const Icon(
              Icons.agriculture,
              color: Colors.white,
              size: 22,
            ),
          ),
          if (expanded) ...[
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                AppLocalizations.of(context)!.appName,
                style: const TextStyle(
                  color: AppTheme.soilDark,
                  fontSize: 18,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -0.4,
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _SidebarItem extends StatefulWidget {
  final _NavDestination item;
  final bool selected;
  final bool expanded;
  final Color accent;
  final VoidCallback onTap;

  const _SidebarItem({
    required this.item,
    required this.selected,
    required this.expanded,
    required this.accent,
    required this.onTap,
  });

  @override
  State<_SidebarItem> createState() => _SidebarItemState();
}

class _SidebarItemState extends State<_SidebarItem> {
  bool _hovered = false;

  Widget _icon(Color color, double size) {
    final icon = Icon(
      widget.selected ? widget.item.selectedIcon : widget.item.icon,
      color: color,
      size: size,
    );
    if (widget.item.tab != RoleTab.messages) return icon;
    return BlocBuilder<InboxBloc, InboxState>(
      builder: (context, state) {
        final unread = state is InboxLoaded ? state.totalUnread : 0;
        if (unread <= 0) return icon;
        return Badge(
          label: Text('$unread'),
          backgroundColor: AppTheme.statusWarning,
          textColor: Colors.white,
          child: icon,
        );
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    final bgColor = widget.selected
        ? AppTheme.fieldGreenTint
        : _hovered
            ? const Color(0xFFF3F1EB)
            : Colors.transparent;
    final iconColor =
        widget.selected ? AppTheme.fieldGreen : AppTheme.unselectedNav;
    final textColor =
        widget.selected ? AppTheme.fieldGreen : AppTheme.unselectedNav;

    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: Semantics(
        button: true,
        label: widget.item.label,
        selected: widget.selected,
        child: MouseRegion(
          cursor: SystemMouseCursors.click,
          onEnter: (_) => setState(() => _hovered = true),
          onExit: (_) => setState(() => _hovered = false),
          child: GestureDetector(
            onTap: widget.onTap,
            child: AnimatedContainer(
              duration: const Duration(milliseconds: 150),
              height: 44,
              padding: EdgeInsets.symmetric(
                horizontal: widget.expanded ? 12 : 0,
              ),
              decoration: BoxDecoration(
                color: bgColor,
                borderRadius: BorderRadius.circular(12),
              ),
              child: widget.expanded
                  ? Row(
                      children: [
                        _icon(iconColor, 20),
                        const SizedBox(width: 12),
                        Text(
                          widget.item.label,
                          style: TextStyle(
                            color: textColor,
                            fontSize: 14,
                            fontWeight: widget.selected
                                ? FontWeight.w700
                                : FontWeight.w600,
                          ),
                        ),
                      ],
                    )
                  : Center(
                      child: Tooltip(
                        message: widget.item.label,
                        child: _icon(iconColor, 22),
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
  final UserRole role;
  final bool isSettingsActive;
  final bool isProfileActive;
  final VoidCallback onProfileTap;
  final VoidCallback onSettingsTap;

  const _SidebarFooter({
    required this.expanded,
    required this.onProfileTap,
    required this.onSettingsTap,
    required this.role,
    this.isSettingsActive = false,
    this.isProfileActive = false,
  });

  @override
  Widget build(BuildContext context) {
    final authState = context.watch<AuthBloc>().state;
    final name = authState is AuthAuthenticated ? authState.userName : '';
    final woreda =
        authState is AuthAuthenticated ? (authState.woreda ?? '') : '';
    final initials = name.isNotEmpty
        ? (name.length > 1
            ? name.substring(0, 2).toUpperCase()
            : name[0].toUpperCase())
        : 'WC';
    final roleName = switch (role) {
      UserRole.farmer => 'Farmer',
      UserRole.trucker => 'Trucker',
      UserRole.buyer => 'Buyer',
    };
    final subtitle = woreda.isNotEmpty ? ' · ' : roleName;

    return Padding(
      padding: EdgeInsets.symmetric(
        horizontal: expanded ? 14 : 8,
        vertical: 12,
      ),
      child: Column(
        children: [
          // Settings Row
          _SidebarFooterButton(
            icon: Icons.settings_outlined,
            selectedIcon: Icons.settings,
            label: AppLocalizations.of(context)!.settings,
            expanded: expanded,
            isActive: isSettingsActive,
            onTap: onSettingsTap,
          ),
          const SizedBox(height: 8),
          // User profile tile
          Semantics(
            button: true,
            label: name.isEmpty ? AppLocalizations.of(context)!.profile : name,
            child: GestureDetector(
              onTap: onProfileTap,
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: Container(
                  padding: EdgeInsets.all(expanded ? 8 : 6),
                  decoration: BoxDecoration(
                    color: isProfileActive
                        ? AppTheme.fieldGreenTint
                        : Colors.transparent,
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: expanded
                      ? Row(
                          children: [
                            Container(
                              width: 36,
                              height: 36,
                              decoration: const BoxDecoration(
                                shape: BoxShape.circle,
                                color: Color(0xFFE6ECF5),
                              ),
                              alignment: Alignment.center,
                              child: Text(
                                initials,
                                style: const TextStyle(
                                  color: Color(0xFF2E4C7D),
                                  fontSize: 13,
                                  fontWeight: FontWeight.w800,
                                ),
                              ),
                            ),
                            const SizedBox(width: 10),
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(
                                    name.isNotEmpty ? name : 'User',
                                    style: const TextStyle(
                                      color: AppTheme.soilDark,
                                      fontSize: 13,
                                      fontWeight: FontWeight.w700,
                                    ),
                                    overflow: TextOverflow.ellipsis,
                                  ),
                                  Text(
                                    subtitle,
                                    style: const TextStyle(
                                      color: AppTheme.mutedSteel,
                                      fontSize: 12,
                                      fontWeight: FontWeight.w500,
                                    ),
                                    overflow: TextOverflow.ellipsis,
                                  ),
                                ],
                              ),
                            ),
                          ],
                        )
                      : Center(
                          child: Tooltip(
                            message: name,
                            child: Container(
                              width: 34,
                              height: 34,
                              decoration: const BoxDecoration(
                                shape: BoxShape.circle,
                                color: Color(0xFFE6ECF5),
                              ),
                              alignment: Alignment.center,
                              child: Text(
                                initials,
                                style: const TextStyle(
                                  color: Color(0xFF2E4C7D),
                                  fontSize: 12,
                                  fontWeight: FontWeight.w800,
                                ),
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
  final IconData? selectedIcon;
  final String label;
  final bool expanded;
  final bool isActive;
  final VoidCallback onTap;

  const _SidebarFooterButton({
    required this.icon,
    required this.label,
    required this.expanded,
    required this.onTap,
    this.selectedIcon,
    this.isActive = false,
  });

  @override
  State<_SidebarFooterButton> createState() => _SidebarFooterButtonState();
}

class _SidebarFooterButtonState extends State<_SidebarFooterButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final color = widget.isActive ? AppTheme.fieldGreen : AppTheme.mutedSteel;
    final displayIcon =
        widget.isActive ? (widget.selectedIcon ?? widget.icon) : widget.icon;
    final bgColor = widget.isActive
        ? AppTheme.fieldGreenTint
        : _hovered
            ? const Color(0xFFF3F1EB)
            : Colors.transparent;

    return Semantics(
      button: true,
      label: widget.label,
      selected: widget.isActive,
      child: MouseRegion(
        onEnter: (_) => setState(() => _hovered = true),
        onExit: (_) => setState(() => _hovered = false),
        cursor: SystemMouseCursors.click,
        child: GestureDetector(
          onTap: widget.onTap,
          child: Container(
            height: 38,
            padding: EdgeInsets.symmetric(
              horizontal: widget.expanded ? 10 : 0,
            ),
            decoration: BoxDecoration(
              color: bgColor,
              borderRadius: BorderRadius.circular(10),
            ),
            child: widget.expanded
                ? Row(
                    children: [
                      Icon(displayIcon, color: color, size: 20),
                      const SizedBox(width: 10),
                      Text(
                        widget.label,
                        style: TextStyle(
                          color: color,
                          fontSize: 13,
                          fontWeight: widget.isActive
                              ? FontWeight.w700
                              : FontWeight.w600,
                        ),
                      ),
                    ],
                  )
                : Center(
                    child: Tooltip(
                      message: widget.label,
                      child: Icon(displayIcon, color: color, size: 20),
                    ),
                  ),
          ),
        ),
      ),
    );
  }
}

/// Navigation destination with route info for role-aware navigation.
class _NavDestination {
  final RoleTab tab;
  final IconData icon;
  final IconData selectedIcon;
  final String label;
  final String route;

  const _NavDestination(
    this.tab,
    this.icon,
    this.selectedIcon,
    this.label,
    this.route,
  );
}
