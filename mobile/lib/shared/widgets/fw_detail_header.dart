import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// The pieces the create and detail boards share (Create trip, Create
/// listing, Place order, Payment, the order detail boards): the round back
/// button in the page instead of an app bar, the 32 px title beside it, and
/// the action area pinned to the foot of the page.

/// The boards' back button: a 44 by 44 circle on the paper grey, in a 48 by
/// 48 hit area. [dark] draws it on a black header.
class FwBackButton extends StatelessWidget {
  final VoidCallback? onPressed;
  final bool dark;
  final IconData icon;

  const FwBackButton({
    this.onPressed,
    this.dark = false,
    this.icon = Icons.arrow_back_ios_new_rounded,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final tooltip = MaterialLocalizations.of(context).backButtonTooltip;
    final onTap = onPressed ?? () => Navigator.of(context).maybePop();
    final child = Icon(
      icon,
      size: 20,
      color: dark ? Colors.white : AppTheme.ink,
    );
    if (!dark) {
      return FwSquareButton(
        semanticLabel: tooltip,
        onTap: onTap,
        child: child,
      );
    }
    return Semantics(
      button: true,
      label: tooltip,
      child: FwTapTarget(
        onTap: onTap,
        child: Material(
          color: AppTheme.inkCard,
          shape: const CircleBorder(),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onTap,
            child: SizedBox(width: 44, height: 44, child: Center(child: child)),
          ),
        ),
      ),
    );
  }
}

/// A create or detail page's header: Back, the title (32 px) with an
/// optional line under it, and an optional trailing widget (a status pill).
/// With no [title] it is the back button alone (Create intent, where the
/// page's own question is the title).
class FwDetailHeader extends StatelessWidget {
  final String? title;
  final String? subtitle;
  final Widget? trailing;
  final VoidCallback? onBack;
  final EdgeInsetsGeometry padding;

  const FwDetailHeader({
    this.title,
    this.subtitle,
    this.trailing,
    this.onBack,
    this.padding = const EdgeInsets.fromLTRB(20, 16, 20, 8),
    super.key,
  });

  static const TextStyle titleStyle = FwText.detailTitle;

  @override
  Widget build(BuildContext context) {
    final title = this.title;
    final subtitle = this.subtitle;
    return Padding(
      padding: padding,
      child: Row(
        children: [
          FwBackButton(onPressed: onBack),
          if (title != null) ...[
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Semantics(
                    header: true,
                    child: Text(title, style: titleStyle),
                  ),
                  if (subtitle != null && subtitle.isNotEmpty)
                    Text(
                      subtitle,
                      style: const TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w500,
                        color: AppTheme.slate,
                      ),
                    ),
                ],
              ),
            ),
          ] else
            const Spacer(),
          if (trailing != null) ...[
            const SizedBox(width: 12),
            trailing!,
          ],
        ],
      ),
    );
  }
}

/// A form or detail page's header as the Register boards stack it: the
/// round back button on its own row (with an optional [trailing] at the
/// end), then the small [eyebrow], the 32 px title and an optional line
/// under it. The title gets the page's full width, so a long Amharic or
/// Afaan Oromo title wraps by words instead of being squeezed beside the
/// button. [showBack] false leaves the button out (a page that is a route
/// of its own on the web).
class FwStackedHeader extends StatelessWidget {
  final String title;
  final String? eyebrow;
  final String? subtitle;
  final Widget? trailing;
  final VoidCallback? onBack;
  final bool showBack;
  final EdgeInsetsGeometry padding;

  const FwStackedHeader({
    required this.title,
    this.eyebrow,
    this.subtitle,
    this.trailing,
    this.onBack,
    this.showBack = true,
    this.padding = EdgeInsets.zero,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final eyebrow = this.eyebrow;
    final subtitle = this.subtitle;
    final trailing = this.trailing;
    return Padding(
      padding: padding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (showBack || trailing != null) ...[
            Row(
              children: [
                if (showBack) FwBackButton(onPressed: onBack),
                const Spacer(),
                if (trailing != null) trailing,
              ],
            ),
            const SizedBox(height: 14),
          ],
          if (eyebrow != null && eyebrow.isNotEmpty) ...[
            Text(
              eyebrow,
              style: const TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.w500,
                color: AppTheme.slate,
              ),
            ),
            const SizedBox(height: 2),
          ],
          Semantics(
            header: true,
            child: Text(title, style: FwText.detailTitle),
          ),
          if (subtitle != null && subtitle.isNotEmpty) ...[
            const SizedBox(height: 6),
            Text(
              subtitle,
              style: const TextStyle(
                fontSize: 14,
                height: 1.4,
                color: AppTheme.slate,
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// The boards' action area pinned to the foot of the page (Save, Place
/// Order, Reject and Accept) with the note under it. Goes in a Scaffold's
/// `bottomNavigationBar`; centred like the page on a wide window.
class FwBottomActions extends StatelessWidget {
  final List<Widget> children;

  const FwBottomActions({required this.children, super.key});

  @override
  Widget build(BuildContext context) {
    return SafeArea(
      top: false,
      child: FwContentWidth(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(20, 8, 20, 16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: children,
          ),
        ),
      ),
    );
  }
}

/// The small centred note under a pinned action ("Saved on your phone
/// first, sent when you have network.").
class FwActionNote extends StatelessWidget {
  final String text;

  const FwActionNote(this.text, {super.key});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: 8),
      child: Text(
        text,
        textAlign: TextAlign.center,
        style: const TextStyle(fontSize: 12, color: AppTheme.slate),
      ),
    );
  }
}

/// The boards' tinted note box (the QR reminder on Trip detail, the map
/// note on the buyer's order, the farmer's answer deadline): an icon and a
/// bold line on the role's tint or the orange tint.
class FwNoteBox extends StatelessWidget {
  final IconData icon;
  final Widget child;
  final Color? background;
  final Color? foreground;

  const FwNoteBox({
    required this.icon,
    required this.child,
    this.background,
    this.foreground,
    super.key,
  });

  const FwNoteBox.cream({
    required this.icon,
    required this.child,
    super.key,
  })  : background = AppTheme.orangeTint,
        foreground = AppTheme.orangeInk;

  @override
  Widget build(BuildContext context) {
    // The role's tint unless the caller names a pair.
    final background = this.background ?? context.roleColors.tint;
    final foreground = this.foreground ?? context.roleColors.primary;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(24),
      ),
      child: Row(
        children: [
          Icon(icon, size: 20, color: foreground),
          const SizedBox(width: 12),
          Expanded(
            child: DefaultTextStyle.merge(
              style: TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.w700,
                height: 1.4,
                color: foreground,
              ),
              child: child,
            ),
          ),
        ],
      ),
    );
  }
}

/// A money line of the boards' price cards: the label left, the amount
/// bold right ("Crop Cost  63,000 ETB"); [total] draws the Total row.
class FwMoneyRow extends StatelessWidget {
  final String label;
  final String amount;
  final bool total;

  const FwMoneyRow({
    required this.label,
    required this.amount,
    this.total = false,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final size = total ? 16.0 : 14.0;
    final row = Row(
      children: [
        Expanded(
          child: Text(
            label,
            style: TextStyle(
              fontSize: size,
              fontWeight: total ? FontWeight.w800 : FontWeight.w500,
              color: total ? AppTheme.ink : AppTheme.inkSoft,
            ),
          ),
        ),
        const SizedBox(width: 12),
        Text(
          amount,
          style: TextStyle(
            fontSize: size,
            fontWeight: total ? FontWeight.w800 : FontWeight.w700,
            color: AppTheme.ink,
          ),
        ),
      ],
    );
    if (!total) return row;
    return Container(
      margin: const EdgeInsets.only(top: 2),
      padding: const EdgeInsets.only(top: 9),
      decoration: const BoxDecoration(
        border: Border(top: BorderSide(color: AppTheme.line)),
      ),
      child: row,
    );
  }
}
