import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

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
      // excludeSemantics drops the InkWell's tap action: give it back.
      onTap: onTap,
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
              // Each tab gets a quarter of the bar. A long am/om label or
              // large text wraps at a space; only a word wider than the
              // slot shrinks, and never breaks.
              FwWordFitText(
                label,
                style: TextStyle(
                  fontSize: 12,
                  height: 1.2,
                  fontWeight: selected ? FontWeight.w700 : FontWeight.w600,
                  color: selected ? accent : inactive,
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
          ).copyWith(
            // A visible ring for keyboard focus (A-7): the default overlay
            // barely changes a dark fill.
            side: WidgetStateProperty.resolveWith(
              (states) => states.contains(WidgetState.focused)
                  ? BorderSide(color: foreground, width: 2)
                  : null,
            ),
          ),
          icon: const Icon(Icons.add, size: 20),
          label: label,
        ),
      );
    }

    // At 200% zoom or with large text the labelled pill covered a third of
    // a phone screen: with room for fewer than 20 label-sized letters, only
    // the plus shows, named by its tooltip.
    final letter = MediaQuery.textScalerOf(context).scale(15);
    if (MediaQuery.sizeOf(context).width < letter * 20) {
      return FloatingActionButton(
        heroTag: 'role_fab',
        onPressed: onPressed,
        tooltip: labelFor(l10n, role),
        backgroundColor: background,
        foregroundColor: foreground,
        elevation: 4,
        highlightElevation: 6,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
        ),
        child: const Icon(Icons.add, size: 24),
      );
    }

    // BUG-27: folded to its plus while the tab scrolls toward its end, so
    // at rest it no longer hides the status chip of the last card in view.
    final showLabel = CreateButtonScrollScope.showsLabel(context);
    final name = labelFor(l10n, role);
    return Tooltip(
      // Names the plus on a long press or hover. The button's own
      // semantics carry the name, so a screen reader hears it once.
      message: name,
      excludeFromSemantics: true,
      child: FloatingActionButton.extended(
        heroTag: 'role_fab',
        onPressed: onPressed,
        backgroundColor: background,
        foregroundColor: foreground,
        elevation: 4,
        highlightElevation: 6,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
        ),
        // 17 + 22 + 17: folded, a 56 dp square like the plain button.
        // Unfolded, as wide as before (the label adds 7 and 3).
        extendedPadding: const EdgeInsetsDirectional.symmetric(horizontal: 17),
        extendedIconLabelSpacing: 0,
        icon: Icon(
          Icons.add,
          size: 22,
          semanticLabel: showLabel ? null : name,
        ),
        label: _FoldingLabel(
          shown: showLabel,
          child: Padding(
            padding: const EdgeInsetsDirectional.only(start: 7, end: 3),
            child: label,
          ),
        ),
      ),
    );
  }
}

/// The create button's label, sliding shut from its end when [shown] turns
/// false and open again when it turns true.
class _FoldingLabel extends StatelessWidget {
  final bool shown;
  final Widget child;

  const _FoldingLabel({required this.shown, required this.child});

  @override
  Widget build(BuildContext context) {
    final still = MediaQuery.maybeDisableAnimationsOf(context) ?? false;
    return ExcludeSemantics(
      // Folded, the plus carries the name instead.
      excluding: !shown,
      child: TweenAnimationBuilder<double>(
        tween: Tween<double>(end: shown ? 1 : 0),
        duration: still ? Duration.zero : const Duration(milliseconds: 200),
        curve: Curves.easeOutCubic,
        child: child,
        builder: (context, open, child) => ClipRect(
          child: Align(
            alignment: AlignmentDirectional.centerStart,
            widthFactor: open,
            child: child,
          ),
        ),
      ),
    );
  }
}

/// Folds the floating [RoleCreateButton] to its plus while the tab under it
/// scrolls toward its end, and unfolds it when the tab scrolls back toward
/// its top or rests there (BUG-27: at rest the labelled pill covered the
/// status chip of the last card in view).
///
/// A shell wraps its Scaffold in this scope and the tab body, only the
/// body, in a [CreateButtonScrollBody]. Only the button rebuilds, and only
/// when the scroll turns, never for each scrolled pixel.
class CreateButtonScrollScope extends StatefulWidget {
  /// A new value (another tab) unfolds the button: that page opens at its
  /// top.
  final Object? resetKey;
  final Widget child;

  const CreateButtonScrollScope({
    required this.child,
    this.resetKey,
    super.key,
  });

  /// Whether the create button under [context] shows its label. Rebuilds
  /// [context] when that changes. True with no scope above it.
  static bool showsLabel(BuildContext context) =>
      context
          .dependOnInheritedWidgetOfExactType<_CreateButtonFoldScope>()
          ?.notifier
          ?.value ??
      true;

  @override
  State<CreateButtonScrollScope> createState() =>
      _CreateButtonScrollScopeState();
}

class _CreateButtonScrollScopeState extends State<CreateButtonScrollScope> {
  final _CreateButtonFold _fold = _CreateButtonFold();

  @override
  void didUpdateWidget(CreateButtonScrollScope oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.resetKey != widget.resetKey) _fold.unfold();
  }

  @override
  void dispose() {
    _fold.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) =>
      _CreateButtonFoldScope(notifier: _fold, child: widget.child);
}

/// Hears the tab body's scrolling for the [CreateButtonScrollScope] above.
class CreateButtonScrollBody extends StatelessWidget {
  /// False while the button is hidden (a screen pushed over the tab, a page
  /// under it): that screen's scrolling says nothing about the tab's list.
  final bool enabled;
  final Widget child;

  const CreateButtonScrollBody({
    required this.child,
    this.enabled = true,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final fold = context
        .getInheritedWidgetOfExactType<_CreateButtonFoldScope>()
        ?.notifier;
    // Only the tab's own vertical list: not a row of chips, not a list
    // inside a card.
    bool own(ViewportNotificationMixin n, ScrollMetrics metrics) =>
        enabled && n.depth == 0 && metrics.axis == Axis.vertical;
    return NotificationListener<ScrollMetricsNotification>(
      // A list that just opened, or grew or shrank, reports where it is.
      onNotification: (n) {
        if (own(n, n.metrics)) fold?.follow(n.metrics, 0);
        return false;
      },
      child: NotificationListener<ScrollUpdateNotification>(
        onNotification: (n) {
          if (own(n, n.metrics)) fold?.follow(n.metrics, n.scrollDelta ?? 0);
          return false;
        },
        child: child,
      ),
    );
  }
}

class _CreateButtonFoldScope extends InheritedNotifier<_CreateButtonFold> {
  const _CreateButtonFoldScope({required super.notifier, required super.child});
}

/// True while the create button shows its label.
class _CreateButtonFold extends ValueNotifier<bool> {
  _CreateButtonFold() : super(true);

  /// How far the list must move one way before the button follows, so a
  /// finger's tremor mid-drag does not flicker it.
  static const double _turn = 12;

  /// Distance scrolled toward the end (positive) or the top (negative)
  /// since the direction last changed.
  double _travel = 0;

  void unfold() {
    _travel = 0;
    value = true;
  }

  /// Follows one scroll of [delta] pixels that left the list at [metrics].
  void follow(ScrollMetrics metrics, double delta) {
    if (metrics.pixels <= metrics.minScrollExtent) {
      // At the top, or pulled past it.
      unfold();
    } else if (delta != 0 &&
        // A bounce past the end settling back is not the user turning.
        metrics.pixels - delta <= metrics.maxScrollExtent) {
      _travel = _travel.sign == delta.sign ? _travel + delta : delta;
      if (_travel.abs() >= _turn) value = _travel < 0;
    }
  }
}
