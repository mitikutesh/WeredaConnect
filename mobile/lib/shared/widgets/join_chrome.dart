import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// Pieces the Join boards share (Welcome and login, the Register steps,
/// Confirm your phone) and the account screens borrow: the square back
/// button, the title block, the tinted note, the small status pill and the
/// page whose action sits at the foot of the screen.

/// The board title: 32 px, extra bold, tight letters.
const TextStyle joinTitleStyle = FwText.detailTitle;

/// The line under a board title.
const TextStyle joinSubtitleStyle = TextStyle(
  fontSize: 14,
  height: 1.5,
  color: AppTheme.slate,
);

/// The boards' back button: a 44 by 44 circle on paper with a chevron. Pops
/// the route unless [onTap] says otherwise. Carries the back tooltip so
/// `tester.pageBack()` and screen readers find it like a Material back.
class JoinBackButton extends StatelessWidget {
  final VoidCallback? onTap;
  final IconData icon;

  const JoinBackButton({
    this.onTap,
    this.icon = Icons.arrow_back_ios_new,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final tooltip = MaterialLocalizations.of(context).backButtonTooltip;
    return Tooltip(
      message: tooltip,
      child: FwSquareButton(
        onTap: onTap ?? () => Navigator.of(context).maybePop(),
        semanticLabel: tooltip,
        child: Icon(icon, size: 18, color: AppTheme.ink),
      ),
    );
  }
}

/// The step title and its one-line explanation.
class JoinTitle extends StatelessWidget {
  final String title;
  final String? subtitle;

  const JoinTitle({required this.title, this.subtitle, super.key});

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Semantics(header: true, child: Text(title, style: joinTitleStyle)),
        if (subtitle != null) ...[
          const SizedBox(height: 8),
          Text(subtitle!, style: joinSubtitleStyle),
        ],
      ],
    );
  }
}

/// A note box, radius 24: the sand note (with or without an icon), the
/// orange tint "required" note, the red error.
class JoinNote extends StatelessWidget {
  final String text;
  final Color background;
  final Color foreground;
  final IconData? icon;
  final Color? borderColor;

  const JoinNote({
    required this.text,
    this.background = AppTheme.sand,
    this.foreground = AppTheme.inkSoft,
    this.icon,
    this.borderColor,
    super.key,
  });

  /// The sand information note with its icon.
  const JoinNote.info({required this.text, super.key})
      : background = AppTheme.sand,
        foreground = AppTheme.inkSoft,
        icon = Icons.info_outline,
        borderColor = null;

  /// The orange tint "required" note.
  const JoinNote.warning({required this.text, super.key})
      : background = AppTheme.orangeTint,
        foreground = AppTheme.orangeInk,
        icon = Icons.info_outline,
        borderColor = null;

  /// The red error note.
  const JoinNote.error({required this.text, super.key})
      : background = AppTheme.statusErrorContainer,
        foreground = AppTheme.onStatusErrorContainer,
        icon = Icons.error_outline,
        borderColor = null;

  /// The sand remark, no icon.
  const JoinNote.plain({required this.text, super.key})
      : background = AppTheme.sand,
        foreground = AppTheme.inkSoft,
        icon = null,
        borderColor = null;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(24),
        border: borderColor == null ? null : Border.all(color: borderColor!),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (icon != null) ...[
            Padding(
              padding: const EdgeInsets.only(top: 1),
              child: Icon(icon, size: 18, color: foreground),
            ),
            const SizedBox(width: 10),
          ],
          Expanded(
            child: Text(
              text,
              style: TextStyle(
                fontSize: 13,
                height: 1.5,
                fontWeight: FontWeight.w500,
                color: foreground,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The boards' small round pill ("Offline Ready", "Identity Verified"):
/// 26 tall, a check and a bold 12 px label on the role's tint.
class JoinStatusPill extends StatelessWidget {
  final String label;
  final IconData icon;
  final Color? background;
  final Color? foreground;

  /// The check's colour when it differs from the label's (the login
  /// header's white pill: black words, the role's check).
  final Color? iconColor;

  const JoinStatusPill({
    required this.label,
    this.icon = Icons.check,
    this.background,
    this.foreground,
    this.iconColor,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final background = this.background ?? context.roleColors.tint;
    final foreground = this.foreground ?? context.roleColors.primary;
    // 26 tall at the default text size; large text makes it taller instead
    // of clipping.
    return Container(
      constraints: const BoxConstraints(minHeight: 26),
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(999),
      ),
      // A Wrap, not a Row: in a narrow column a long label wraps rather
      // than overflows, and beside other widgets it still hugs its label.
      child: Wrap(
        crossAxisAlignment: WrapCrossAlignment.center,
        spacing: 5,
        children: [
          Icon(icon, size: 13, color: iconColor ?? foreground),
          Text(
            label,
            style: TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w800,
              color: foreground,
            ),
          ),
        ],
      ),
    );
  }
}

/// The boards' icon tile above a title (the phone on Confirm your phone):
/// 52 square, radius 14, black on the orange tint.
class JoinIconTile extends StatelessWidget {
  final IconData icon;

  const JoinIconTile({required this.icon, super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 52,
      height: 52,
      decoration: BoxDecoration(
        color: AppTheme.orangeTint,
        borderRadius: BorderRadius.circular(14),
      ),
      child: Icon(icon, size: 26, color: AppTheme.ink),
    );
  }
}

/// A board page whose action sits at the foot: [body] scrolls, and while it
/// is shorter than the screen the [footer] stays at the bottom (the boards'
/// `margin-top: auto`). With the keyboard open everything scrolls.
class JoinPage extends StatelessWidget {
  final Widget body;
  final Widget? footer;
  final EdgeInsets padding;

  const JoinPage({
    required this.body,
    this.footer,
    this.padding = const EdgeInsets.fromLTRB(20, 20, 20, 28),
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        return SingleChildScrollView(
          padding: padding,
          child: ConstrainedBox(
            constraints: BoxConstraints(
              minHeight: constraints.maxHeight - padding.vertical,
            ),
            child: IntrinsicHeight(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  body,
                  if (footer != null) ...[
                    const SizedBox(height: 24),
                    const Spacer(),
                    footer!,
                  ],
                ],
              ),
            ),
          ),
        );
      },
    );
  }
}

/// The boards' error banner under a title.
class JoinErrorBanner extends StatelessWidget {
  final String message;

  const JoinErrorBanner({required this.message, super.key});

  @override
  Widget build(BuildContext context) {
    return Semantics(
      container: true,
      liveRegion: true,
      child: JoinNote.error(text: message),
    );
  }
}

/// The soft shadow under the card a join screen leads with (the selected
/// role, the Digital ID card, the camera frame), as [FwCard]'s raised card.
const List<BoxShadow> joinRaisedShadow = [
  BoxShadow(
    color: AppTheme.shadowSoft,
    blurRadius: 30,
    offset: Offset(0, 14),
  ),
];

/// The boards' radio: a 22 px ring, filled with a black dot when selected.
class JoinRadio extends StatelessWidget {
  final bool selected;

  const JoinRadio({required this.selected, super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 22,
      height: 22,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        border: Border.all(
          color: selected ? AppTheme.ink : AppTheme.fieldBorder,
          width: 2,
        ),
      ),
      child: selected
          ? Container(
              width: 12,
              height: 12,
              decoration: const BoxDecoration(
                shape: BoxShape.circle,
                color: AppTheme.ink,
              ),
            )
          : null,
    );
  }
}

/// One choice of a join step (a role, a buyer type): an icon tile, a title
/// with an optional line under it, and the radio. Outlined while not
/// picked; the picked one is raised on paper with the soft shadow and a
/// larger title, as on the RegisterRoleM board.
class JoinChoiceCard extends StatelessWidget {
  final Widget icon;
  final Color iconBackground;
  final String title;
  final String? description;
  final bool selected;
  final VoidCallback onTap;

  const JoinChoiceCard({
    required this.icon,
    required this.iconBackground,
    required this.title,
    required this.selected,
    required this.onTap,
    this.description,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final radius = BorderRadius.circular(selected ? 28 : 24);
    final description = this.description;
    return Semantics(
      button: true,
      selected: selected,
      inMutuallyExclusiveGroup: true,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 150),
        decoration: BoxDecoration(
          borderRadius: radius,
          boxShadow: selected ? joinRaisedShadow : const [],
        ),
        child: Material(
          color: selected ? AppTheme.paper : Colors.transparent,
          shape: RoundedRectangleBorder(
            borderRadius: radius,
            side: BorderSide(
              color: selected ? AppTheme.line : AppTheme.fieldBorder,
            ),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onTap,
            child: Padding(
              padding: selected
                  ? const EdgeInsets.symmetric(horizontal: 17, vertical: 18)
                  : const EdgeInsets.all(16),
              child: Row(
                children: [
                  Container(
                    width: 52,
                    height: 52,
                    alignment: Alignment.center,
                    decoration: BoxDecoration(
                      color: iconBackground,
                      borderRadius: BorderRadius.circular(14),
                    ),
                    child: icon,
                  ),
                  const SizedBox(width: 14),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          title,
                          style: TextStyle(
                            fontSize: selected ? 19 : 17,
                            fontWeight: FontWeight.w800,
                            letterSpacing: selected ? -0.4 : -0.2,
                            color: AppTheme.ink,
                          ),
                        ),
                        if (description != null) ...[
                          const SizedBox(height: 3),
                          Text(
                            description,
                            style: const TextStyle(
                              fontSize: 14,
                              height: 1.35,
                              color: AppTheme.slate,
                            ),
                          ),
                        ],
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  JoinRadio(selected: selected),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
