import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// The bottom bar of every phone board (design canvas page M): a black
/// pill floating over the white ground, three destinations in white with
/// a dot in the role's tint under the selected one, and the role's create
/// verb as the orange button that ends the bar. The device shell
/// (MainNavigation) and the web shell (AdaptiveShell) both draw it, so the
/// app looks the same in the browser and on the phone.
class RoleNavBar extends StatelessWidget {
  final UserRole role;
  final List<RoleTab> tabs;

  /// The selected tab, or null on a page outside the bar (profile).
  final int? currentIndex;
  final ValueChanged<int> onSelected;

  /// The role's create verb; null leaves the button out.
  final VoidCallback? onCreate;

  const RoleNavBar({
    required this.role,
    required this.tabs,
    required this.currentIndex,
    required this.onSelected,
    this.onCreate,
    super.key,
  });

  /// Between the last tab and the create button.
  static const double _createGap = 4;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final onCreate = this.onCreate;
    return ColoredBox(
      color: AppTheme.ground,
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 8, 16, 16),
          child: DecoratedBox(
            decoration: const ShapeDecoration(
              color: AppTheme.ink,
              shape: StadiumBorder(),
              shadows: [
                BoxShadow(
                  color: Color(0x401A1516),
                  blurRadius: 24,
                  offset: Offset(0, 10),
                ),
              ],
            ),
            child: ConstrainedBox(
              // 62 tall on the boards; taller with large text.
              constraints: const BoxConstraints(minHeight: 62),
              child: Padding(
                padding: const EdgeInsetsDirectional.fromSTEB(10, 6, 8, 6),
                child: LayoutBuilder(
                  builder: (context, constraints) {
                    // The tabs share the bar equally, and each keeps a
                    // 48 px target and its label at full size. The
                    // create button gets what is left, and folds to its
                    // plus when its word does not fit there (a long
                    // am/om verb, large text, 200% zoom).
                    var tabWidth = kMinInteractiveDimension;
                    for (final tab in tabs) {
                      final width = RoleNavItem.minWidthFor(
                        context,
                        tab.label(l10n),
                      );
                      if (width > tabWidth) tabWidth = width;
                    }
                    final createRoom = constraints.maxWidth -
                        _createGap -
                        tabs.length * tabWidth;
                    return Row(
                      children: [
                        for (var i = 0; i < tabs.length; i++)
                          Expanded(
                            child: RoleNavItem(
                              icon: tabs[i].icon,
                              activeIcon: tabs[i].activeIcon,
                              label: tabs[i].label(l10n),
                              selected: i == currentIndex,
                              onTap: () => onSelected(i),
                            ),
                          ),
                        if (onCreate != null) ...[
                          const SizedBox(width: _createGap),
                          ConstrainedBox(
                            constraints: BoxConstraints(
                              maxWidth: createRoom < kMinInteractiveDimension
                                  ? kMinInteractiveDimension
                                  : createRoom,
                            ),
                            child: RoleCreateButton(
                              role: role,
                              onPressed: onCreate,
                            ),
                          ),
                        ],
                      ],
                    );
                  },
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// One bar destination: a white icon over its label, and under the
/// selected one a 4 px dot in the role's tint.
class RoleNavItem extends StatelessWidget {
  final IconData icon;
  final IconData activeIcon;
  final String label;
  final bool selected;
  final VoidCallback onTap;

  const RoleNavItem({
    required this.icon,
    required this.activeIcon,
    required this.label,
    required this.selected,
    required this.onTap,
    super.key,
  });

  /// Space on each side of the label.
  static const double _sidePadding = 2;

  static TextStyle _labelStyle({required bool selected}) => TextStyle(
        fontSize: 12,
        height: 1.2,
        fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
        color: Colors.white,
      );

  /// The narrowest a tab can be and still show [label] at full size: its
  /// longest word (the label wraps at spaces) in the selected tab's bold.
  static double minWidthFor(BuildContext context, String label) =>
      FwWordFitText.longestWordWidth(
        context,
        label,
        _labelStyle(selected: true),
      ) +
      2 * _sidePadding;

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: true,
      selected: selected,
      label: label,
      excludeSemantics: true,
      // excludeSemantics drops the InkWell's tap action: give it back.
      onTap: onTap,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onTap,
          customBorder: const StadiumBorder(),
          // A white ring for keyboard focus (A-7) on the black bar.
          focusColor: const Color(0x33FFFFFF),
          hoverColor: const Color(0x1AFFFFFF),
          child: ConstrainedBox(
            constraints: const BoxConstraints(minHeight: 50),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: _sidePadding),
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(
                    selected ? activeIcon : icon,
                    color: Colors.white,
                    size: 20,
                  ),
                  const SizedBox(height: 2),
                  // Each tab gets a share of the bar. A long am/om label
                  // or large text wraps at a space; only a word wider
                  // than the slot shrinks, and never breaks.
                  FwWordFitText(
                    label,
                    style: _labelStyle(selected: selected),
                  ),
                  const SizedBox(height: 2),
                  // The selected tab's dot: the shape and the bold label
                  // say "selected", the role's tint says whose app it is.
                  DecoratedBox(
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      color: selected
                          ? context.roleColors.tint
                          : Colors.transparent,
                    ),
                    child: const SizedBox(width: 4, height: 4),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// The role's one create verb as the boards draw it: the orange pill that
/// ends the bar ("Sell", "Post trip", "Buy"), black ink, a plus before the
/// word. [floating] draws the same pill with a shadow for a shell without
/// the bar (the tablet rail); [expanded] fills the web sidebar's width.
class RoleCreateButton extends StatelessWidget {
  final UserRole role;
  final VoidCallback onPressed;

  /// Full width inside the web sidebar.
  final bool expanded;

  /// Floats over the page (the tablet rail has no bar to sit in).
  final bool floating;

  const RoleCreateButton({
    required this.role,
    required this.onPressed,
    this.expanded = false,
    this.floating = false,
    super.key,
  });

  /// The full name of the verb, for a screen reader and the tooltip.
  static String labelFor(AppLocalizations l10n, UserRole role) {
    return switch (role) {
      UserRole.farmer => l10n.sellCrops,
      UserRole.trucker => l10n.postTrip,
      UserRole.buyer => l10n.newBuyingIntent,
    };
  }

  /// The one word the bar has room for.
  static String shortLabelFor(AppLocalizations l10n, UserRole role) {
    return switch (role) {
      UserRole.farmer => l10n.sell,
      UserRole.trucker => l10n.postTrip,
      UserRole.buyer => l10n.buy,
    };
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final name = labelFor(l10n, role);

    if (expanded) {
      // Web · Buyer deliveries board: the sidebar's button is black with
      // white ink, 44 tall, radius 14. Orange stays the page's one action.
      return SizedBox(
        height: 44,
        width: double.infinity,
        child: FilledButton.icon(
          onPressed: onPressed,
          style: FilledButton.styleFrom(
            backgroundColor: AppTheme.ink,
            foregroundColor: Colors.white,
            minimumSize: const Size.fromHeight(44),
            // 10, not the board's 16: "New Buying Intent" has to fit the
            // 180 px the sidebar leaves it.
            padding: const EdgeInsets.symmetric(horizontal: 10),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(14),
            ),
          ).copyWith(
            // A visible ring for keyboard focus (A-7), in the role's colour.
            side: WidgetStateProperty.resolveWith(
              (states) => states.contains(WidgetState.focused)
                  ? BorderSide(color: context.roleColors.primary, width: 3)
                  : null,
            ),
          ),
          icon: const Icon(Icons.add, size: 18),
          label: Text(
            name,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700),
          ),
        ),
      );
    }

    // Inside the bar the word shows where the bar has room for it beside
    // the tabs (the LayoutBuilder below): at 200% zoom or with large text
    // only the plus shows. Floating over a page there is no bar to
    // measure: with room for fewer than 26 label-sized letters only the
    // plus shows, and (BUG-27) it folds while the page scrolls toward its
    // end. Inside the bar it covers nothing.
    final letter = MediaQuery.textScalerOf(context).scale(14);
    final unfolded = !floating ||
        (MediaQuery.sizeOf(context).width >= letter * 26 &&
            CreateButtonScrollScope.showsLabel(context));
    final word = shortLabelFor(l10n, role);
    final labelledWidth = _labelledWidth(context, word);

    return LayoutBuilder(
      // The bar gives the button what its tabs leave: the word shows only
      // where the whole pill fits.
      builder: (context, constraints) => _pill(
        name: name,
        word: word,
        showLabel: unfolded && labelledWidth <= constraints.maxWidth,
      ),
    );
  }

  static const TextStyle _wordStyle = TextStyle(
    fontSize: 14,
    fontWeight: FontWeight.w800,
    color: AppTheme.ink,
  );

  /// The pill's side padding, its plus, and the gaps around its word.
  static const double _sidePadding = 13;
  static const double _plusSize = 20;
  static const double _wordGap = 5;

  /// How wide the pill is with [word] showing.
  static double _labelledWidth(BuildContext context, String word) {
    final painter = TextPainter(
      text: TextSpan(
        text: word,
        style: DefaultTextStyle.of(context).style.merge(_wordStyle),
      ),
      textDirection: Directionality.of(context),
      textScaler: MediaQuery.textScalerOf(context),
      maxLines: 1,
    )..layout();
    final width = painter.width.ceilToDouble();
    painter.dispose();
    return 2 * _sidePadding + _plusSize + 2 * _wordGap + width;
  }

  Widget _pill({
    required String name,
    required String word,
    required bool showLabel,
  }) {
    const foreground = AppTheme.ink;
    return Tooltip(
      // Names the plus on a long press or hover. The button's own
      // semantics carry the name, so a screen reader hears it once.
      message: name,
      excludeFromSemantics: true,
      child: Semantics(
        button: true,
        label: name,
        excludeSemantics: true,
        onTap: onPressed,
        // The pill is 46 tall on the boards; its hit area is Material's
        // 48 by 48 minimum, and so is the box a screen reader gets.
        child: FwTapTarget(
          onTap: onPressed,
          child: Material(
            color: AppTheme.orange,
            elevation: floating ? 4 : 0,
            shadowColor: const Color(0x661A1516),
            shape: const StadiumBorder(),
            clipBehavior: Clip.antiAlias,
            child: InkWell(
              onTap: onPressed,
              // A black ring for keyboard focus (A-7).
              focusColor: const Color(0x331A1516),
              child: ConstrainedBox(
                constraints: BoxConstraints(
                  minHeight: floating ? 54 : 46,
                  minWidth: floating ? 54 : 46,
                ),
                child: Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: _sidePadding,
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      const Icon(
                        Icons.add,
                        size: _plusSize,
                        color: foreground,
                      ),
                      // Flexible: while the word slides shut in a pill
                      // that has no room for it, it is clipped, not an
                      // overflow.
                      Flexible(
                        child: _FoldingLabel(
                          shown: showLabel,
                          child: Padding(
                            padding: const EdgeInsetsDirectional.only(
                              start: _wordGap,
                              end: _wordGap,
                            ),
                            child: Text(
                              word,
                              maxLines: 1,
                              softWrap: false,
                              style: _wordStyle,
                            ),
                          ),
                        ),
                      ),
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
            // As tall as the label: without it the Align fills the height
            // it is offered, and the bar (or the floating pill) grows to
            // cover the page.
            heightFactor: 1,
            child: child,
          ),
        ),
      ),
    );
  }
}

/// Folds the bar's [RoleCreateButton] to its plus while the tab under it
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
