import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// The Fieldway bottom bar (every phone board): white, a whisper rule on
/// top, the selected tab's icon in the role's tint pill. The device shell
/// (MainNavigation) and the web shell (AdaptiveShell) both draw it, so the
/// app looks the same in the browser and on the phone.
class RoleNavBar extends StatelessWidget {
  final UserRole role;
  final List<RoleTab> tabs;

  /// The selected tab, or null on a page outside the bar (profile).
  final int? currentIndex;
  final ValueChanged<int> onSelected;

  const RoleNavBar({
    required this.role,
    required this.tabs,
    required this.currentIndex,
    required this.onSelected,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return DecoratedBox(
      decoration: const BoxDecoration(
        color: AppTheme.surfaceWhite,
        border: Border(top: BorderSide(color: AppTheme.whisperBorder)),
      ),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(8, 8, 8, 8),
          child: Row(
            children: [
              for (var i = 0; i < tabs.length; i++)
                Expanded(
                  child: RoleNavItem(
                    icon: tabs[i].icon,
                    activeIcon: tabs[i].activeIcon,
                    label: tabs[i].label(l10n),
                    selected: i == currentIndex,
                    role: role,
                    onTap: () => onSelected(i),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// One bar destination: icon over label, the icon in a 56 by 30 pill when
/// selected.
class RoleNavItem extends StatelessWidget {
  final IconData icon;
  final IconData activeIcon;
  final String label;
  final bool selected;
  final UserRole role;
  final VoidCallback onTap;

  const RoleNavItem({
    required this.icon,
    required this.activeIcon,
    required this.label,
    required this.selected,
    required this.role,
    required this.onTap,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final accent = AppTheme.roleAccent(role);
    const inactive = AppTheme.inkSecondary;

    return Semantics(
      button: true,
      selected: selected,
      label: label,
      excludeSemantics: true,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 2, vertical: 4),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              AnimatedContainer(
                duration: const Duration(milliseconds: 150),
                width: 56,
                height: 30,
                decoration: BoxDecoration(
                  color: selected
                      ? AppTheme.roleAccentTint(role)
                      : Colors.transparent,
                  borderRadius: BorderRadius.circular(999),
                ),
                child: Icon(
                  selected ? activeIcon : icon,
                  color: selected ? accent : inactive,
                  size: 20,
                ),
              ),
              const SizedBox(height: 4),
              // Each tab gets a quarter of the bar; a long am/om label
              // shrinks a little rather than lose its end to an ellipsis.
              FittedBox(
                fit: BoxFit.scaleDown,
                child: Text(
                  label,
                  maxLines: 1,
                  style: TextStyle(
                    fontSize: 11,
                    fontWeight: selected ? FontWeight.w700 : FontWeight.w600,
                    color: selected ? accent : inactive,
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The role's one create verb as the boards draw it: a labelled pill
/// floating centred over the bar, soil dark for farmers and buyers, gold
/// for truckers.
class RoleCreateButton extends StatelessWidget {
  final UserRole role;
  final VoidCallback onPressed;

  /// Full width inside the web sidebar instead of floating.
  final bool expanded;

  const RoleCreateButton({
    required this.role,
    required this.onPressed,
    this.expanded = false,
    super.key,
  });

  static String labelFor(AppLocalizations l10n, UserRole role) {
    return switch (role) {
      UserRole.farmer => l10n.sellCrops,
      UserRole.trucker => l10n.postTrip,
      UserRole.buyer => l10n.newBuyingIntent,
    };
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final background = AppTheme.createButtonBackground(role);
    final foreground = AppTheme.createButtonForeground(role);
    final label = Text(
      labelFor(l10n, role),
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
      style: TextStyle(
        fontSize: expanded ? 14 : 15,
        fontWeight: FontWeight.w800,
        color: foreground,
      ),
    );

    if (expanded) {
      return SizedBox(
        height: 44,
        width: double.infinity,
        child: FilledButton.icon(
          onPressed: onPressed,
          style: FilledButton.styleFrom(
            backgroundColor: background,
            foregroundColor: foreground,
            minimumSize: const Size.fromHeight(44),
            padding: const EdgeInsets.symmetric(horizontal: 16),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(14),
            ),
          ),
          icon: const Icon(Icons.add, size: 20),
          label: label,
        ),
      );
    }

    return FloatingActionButton.extended(
      heroTag: 'role_fab',
      onPressed: onPressed,
      backgroundColor: background,
      foregroundColor: foreground,
      elevation: 4,
      highlightElevation: 6,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(16),
      ),
      icon: const Icon(Icons.add, size: 22),
      label: label,
    );
  }
}
