import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// Pieces the Join boards share (Welcome and login, the Register steps,
/// Confirm your phone) and the account screens borrow: the square back
/// button, the title block, the tinted note, the small status pill and the
/// page whose action sits at the foot of the screen.

/// The board title: 28 px, extra bold, tight letters.
const TextStyle joinTitleStyle = TextStyle(
  fontSize: 28,
  fontWeight: FontWeight.w800,
  letterSpacing: -0.8,
  height: 1.15,
  color: AppTheme.soilDark,
);

/// The line under a board title.
const TextStyle joinSubtitleStyle = TextStyle(
  fontSize: 15,
  height: 1.4,
  color: AppTheme.mutedSteel,
);

/// The boards' back button: a 44 by 44 white square with a chevron. Pops
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
        child: Icon(icon, size: 18, color: AppTheme.soilDark),
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

/// A tinted note with a leading icon: the blue Fayda note, the amber
/// license note, the plain white remark.
class JoinNote extends StatelessWidget {
  final String text;
  final Color background;
  final Color foreground;
  final IconData? icon;
  final Color? borderColor;

  const JoinNote({
    required this.text,
    this.background = AppTheme.surfaceWhite,
    this.foreground = AppTheme.unselectedNav,
    this.icon,
    this.borderColor,
    super.key,
  });

  /// The blue information note.
  const JoinNote.info({required this.text, super.key})
      : background = AppTheme.dropoffBlueTint,
        foreground = AppTheme.dropoffBlue,
        icon = Icons.info_outline,
        borderColor = null;

  /// The amber "required" note.
  const JoinNote.warning({required this.text, super.key})
      : background = AppTheme.actionCream,
        foreground = AppTheme.actionInk,
        icon = Icons.info_outline,
        borderColor = null;

  /// The red error note.
  const JoinNote.error({required this.text, super.key})
      : background = AppTheme.statusErrorContainer,
        foreground = AppTheme.onStatusErrorContainer,
        icon = Icons.error_outline,
        borderColor = null;

  /// The white remark with a whisper border.
  const JoinNote.plain({required this.text, super.key})
      : background = AppTheme.surfaceWhite,
        foreground = AppTheme.unselectedNav,
        icon = null,
        borderColor = AppTheme.whisperBorder;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(16),
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
/// 30 tall, a check and a bold 12 px label on a green tint.
class JoinStatusPill extends StatelessWidget {
  final String label;
  final IconData icon;
  final Color background;
  final Color foreground;

  const JoinStatusPill({
    required this.label,
    this.icon = Icons.check,
    this.background = AppTheme.fieldGreenTint,
    this.foreground = AppTheme.fieldGreen,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 30,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 14, color: foreground),
          const SizedBox(width: 6),
          Text(
            label,
            style: TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w700,
              color: foreground,
            ),
          ),
        ],
      ),
    );
  }
}

/// The boards' icon tile above a title (the phone on Confirm your phone):
/// 56 square, radius 18, on the green tint.
class JoinIconTile extends StatelessWidget {
  final IconData icon;

  const JoinIconTile({required this.icon, super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 56,
      height: 56,
      decoration: BoxDecoration(
        color: AppTheme.fieldGreenTint,
        borderRadius: BorderRadius.circular(18),
      ),
      child: Icon(icon, size: 28, color: AppTheme.fieldGreen),
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
