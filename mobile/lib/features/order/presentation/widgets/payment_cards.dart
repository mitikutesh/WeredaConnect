import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

// The pieces the buyer's Telebirr payment boards share (PaymentM,
// PaymentFailedM, PaymentOfflineM): the header with its close button, the
// raised amount card, "Confirm it's you", the dashed status card, and the
// two full states (payment failed, no network).

/// The round close button the payment boards carry instead of Back.
class PaymentCloseButton extends StatelessWidget {
  final VoidCallback onClose;

  const PaymentCloseButton({required this.onClose, super.key});

  @override
  Widget build(BuildContext context) {
    final label = AppLocalizations.of(context)!.close;
    return Tooltip(
      message: label,
      child: FwSquareButton(
        key: const Key('payment_close'),
        semanticLabel: label,
        onTap: onClose,
        child: const Icon(Icons.close, size: 20, color: AppTheme.ink),
      ),
    );
  }
}

/// The payment page's header: the close button, the title (30 px) and the
/// buyer's sketch at the end when the title leaves it room.
class PaymentHeader extends StatelessWidget {
  final String title;
  final VoidCallback onClose;

  const PaymentHeader({required this.title, required this.onClose, super.key});

  static const TextStyle _titleStyle = TextStyle(
    fontSize: 30,
    fontWeight: FontWeight.w800,
    letterSpacing: -1,
    height: 1.05,
    color: AppTheme.ink,
  );

  static const double _sketchWidth = 64;
  static const double _sketchMinWidth = 40;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 24, 20, 0),
      child: LayoutBuilder(
        builder: (context, constraints) {
          // Button (48 hit area) and gap, then the title; the sketch only
          // when the title fits on one line beside it, drawn smaller when
          // the room is short.
          final room = constraints.maxWidth - 48 - 12;
          double? sketchWidth;
          if (RoleSketch.sketchesShown(context)) {
            final painter = TextPainter(
              text: TextSpan(
                text: title,
                style: DefaultTextStyle.of(context).style.merge(_titleStyle),
              ),
              textDirection: Directionality.of(context),
              textScaler: MediaQuery.textScalerOf(context),
              maxLines: 1,
            )..layout();
            final left = room - painter.width - 8;
            painter.dispose();
            if (left >= _sketchMinWidth) {
              sketchWidth = left < _sketchWidth ? left : _sketchWidth;
            }
          }
          return Row(
            children: [
              PaymentCloseButton(onClose: onClose),
              const SizedBox(width: 12),
              Expanded(
                child: Semantics(
                  header: true,
                  child: Text(title, style: _titleStyle),
                ),
              ),
              if (sketchWidth != null) ...[
                const SizedBox(width: 8),
                RoleSketch(role: UserRole.buyer, width: sketchWidth),
              ],
            ],
          );
        },
      ),
    );
  }
}

/// The amount on the board's raised card: "AMOUNT", the sum large in the
/// buyer's colour, and what the order is with where the money sits.
class PaymentAmountCard extends StatelessWidget {
  final double amount;

  /// The line under the amount ("Wheat 1,500 kg · held in escrow until you
  /// confirm delivery"); the escrow note alone when null.
  final String? detail;

  const PaymentAmountCard({required this.amount, this.detail, super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return FwCard(
      raised: true,
      padding: const EdgeInsets.all(20),
      child: SizedBox(
        width: double.infinity,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const ExcludeSemantics(
                  child: Icon(
                    Icons.payments_outlined,
                    size: 16,
                    color: AppTheme.slate,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(child: FwSectionLabel(l10n.paymentAmount)),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              l10n.etbCurrency(formatEtbAmount(amount)),
              style: TextStyle(
                fontSize: 42,
                fontWeight: FontWeight.w800,
                letterSpacing: -1.4,
                height: 1.1,
                color: context.roleColors.primary,
              ),
            ),
            const SizedBox(height: 4),
            Text(
              detail ?? l10n.checkoutHeldInEscrowNote,
              style: const TextStyle(
                fontSize: 13,
                height: 1.45,
                color: AppTheme.slate,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// "Confirm it's you" on the sand tile: payments over 10,000 ETB need
/// Fayda, smaller ones the password (AUTH_041 / AUTH_040). [confirmedWith]
/// is how the buyer confirmed for this payment, once they did: the board's
/// "Confirmed with Fayda" pill under the title. Null before that (only the
/// server knows which one a payment needs, so nothing is named ahead).
class ConfirmItsYouCard extends StatelessWidget {
  final StepUpMethod? confirmedWith;

  const ConfirmItsYouCard({this.confirmedWith, super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final method = switch (confirmedWith) {
      StepUpMethod.fayda => l10n.checkoutConfirmedWithFayda,
      StepUpMethod.password => l10n.checkoutConfirmedWithPassword,
      null => null,
    };
    return FwCard.sand(
      key: const Key('checkout_confirm_its_you'),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                width: 40,
                height: 40,
                decoration: BoxDecoration(
                  color: context.roleColors.tint,
                  borderRadius: BorderRadius.circular(14),
                ),
                child: Icon(
                  Icons.verified_user_outlined,
                  size: 20,
                  color: context.roleColors.primary,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(l10n.stepUpTitle, style: FwText.cardTitle),
                    if (method != null) ...[
                      const SizedBox(height: 4),
                      FwPill(
                        key: const Key('checkout_confirmed_with'),
                        icon: Icons.check,
                        label: method,
                      ),
                    ],
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Text(
            l10n.checkoutConfirmItsYouNote,
            style: const TextStyle(
              fontSize: 13,
              height: 1.5,
              color: AppTheme.inkSoft,
            ),
          ),
        ],
      ),
    );
  }
}

/// The board's status card, inside a dashed edge: the step (a ring, a
/// check or an error glyph) in bold, lines under it, and the card's own
/// actions.
class PaymentStatusCard extends StatelessWidget {
  final String text;
  final bool busy;
  final bool error;
  final bool done;
  final List<Widget> children;

  const PaymentStatusCard({
    required this.text,
    this.busy = false,
    this.error = false,
    this.done = false,
    this.children = const [],
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final Widget leading;
    if (busy) {
      leading = const SizedBox(
        height: 22,
        width: 22,
        child: CircularProgressIndicator(
          strokeWidth: 3,
          color: AppTheme.orange,
          backgroundColor: AppTheme.track,
        ),
      );
    } else if (done) {
      leading = const Icon(
        Icons.check_circle,
        size: 22,
        color: AppTheme.statusSuccess,
      );
    } else {
      leading = Icon(
        error ? Icons.error_outline : Icons.info_outline,
        size: 22,
        color: error ? AppTheme.statusError : AppTheme.ink,
      );
    }
    return Semantics(
      liveRegion: true,
      child: CustomPaint(
        painter: const _DashedEdgePainter(),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  leading,
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(
                      text,
                      style: TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w800,
                        letterSpacing: -0.15,
                        height: 1.4,
                        color: error ? AppTheme.statusError : AppTheme.ink,
                      ),
                    ),
                  ),
                ],
              ),
              for (final child in children) ...[
                const SizedBox(height: 12),
                child,
              ],
            ],
          ),
        ),
      ),
    );
  }
}

/// The status card's edge: 1.5 px dashes on a radius 24 rectangle.
class _DashedEdgePainter extends CustomPainter {
  const _DashedEdgePainter();

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = AppTheme.fieldBorder
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5;
    final path = Path()
      ..addRRect(
        RRect.fromRectAndRadius(
          Rect.fromLTWH(0.75, 0.75, size.width - 1.5, size.height - 1.5),
          const Radius.circular(24),
        ),
      );
    const dash = 5.0;
    const gap = 4.0;
    for (final metric in path.computeMetrics()) {
      var distance = 0.0;
      while (distance < metric.length) {
        final end = (distance + dash).clamp(0.0, metric.length);
        canvas.drawPath(metric.extractPath(distance, end), paint);
        distance += dash + gap;
      }
    }
  }

  @override
  bool shouldRepaint(_DashedEdgePainter oldDelegate) => false;
}

/// The text style of the payment pages' buttons that are not the theme's
/// own (a guard test wants the font named on every such style).
const TextStyle _buttonTextStyle = TextStyle(
  fontFamily: AppTheme.fontFamily,
  fontFamilyFallback: AppTheme.ethiopicFontFallback,
  fontSize: 14,
  fontWeight: FontWeight.w700,
);

/// A button inside a card (an outlined pill, 44 tall): the board's "Check
/// payment status".
ButtonStyle paymentCardButtonStyle() => OutlinedButton.styleFrom(
      minimumSize: const Size.fromHeight(44),
      side: const BorderSide(color: AppTheme.fieldBorder),
      textStyle: _buttonTextStyle,
    );

/// The second action under the orange one (an outlined pill, 48 tall):
/// "Pay Later", "Back to order".
ButtonStyle paymentSecondaryButtonStyle() => OutlinedButton.styleFrom(
      minimumSize: const Size.fromHeight(48),
      side: const BorderSide(color: AppTheme.fieldBorder),
      textStyle: _buttonTextStyle,
    );

/// A full payment state (PaymentFailedM, PaymentOfflineM): the close
/// button, the buyer's large sketch, a pill, the title and what it means,
/// then the next actions at the foot. Fills the page; put it in a
/// Scaffold's body.
class PaymentStateView extends StatelessWidget {
  /// The pill above the title ("Not paid", "No network").
  final String pillLabel;
  final IconData pillIcon;
  final Color pillBackground;
  final Color pillForeground;

  final String title;
  final String message;

  /// The orange action ("Try payment again"); [onPrimary] null disables it
  /// (a request in flight).
  final String primaryLabel;
  final VoidCallback? onPrimary;

  /// The outlined action under it ("Pay later", "Back to order").
  final String secondaryLabel;
  final VoidCallback onSecondary;

  /// The close button; defaults to [onSecondary] (both leave the payment).
  final VoidCallback? onClose;

  const PaymentStateView({
    required this.pillLabel,
    required this.pillIcon,
    required this.pillBackground,
    required this.pillForeground,
    required this.title,
    required this.message,
    required this.primaryLabel,
    required this.onPrimary,
    required this.secondaryLabel,
    required this.onSecondary,
    this.onClose,
    super.key,
  });

  /// PaymentFailedM: the payment did not go through, no money moved, the
  /// order is kept. [onTryAgain] starts the payment again; [onPayLater]
  /// leaves it for later.
  factory PaymentStateView.failed({
    required AppLocalizations l10n,
    required VoidCallback? onTryAgain,
    required VoidCallback onPayLater,
    Key? key,
  }) =>
      PaymentStateView(
        key: key ?? const Key('payment_failed_state'),
        pillLabel: l10n.paymentNotPaidPill,
        pillIcon: Icons.error_outline,
        pillBackground: AppTheme.statusErrorContainer,
        pillForeground: AppTheme.statusError,
        title: l10n.paymentFailedTitle,
        message: l10n.paymentFailedBody,
        primaryLabel: l10n.paymentTryAgain,
        onPrimary: onTryAgain,
        secondaryLabel: l10n.payLater,
        onSecondary: onPayLater,
      );

  /// PaymentOfflineM: no network while paying; the order is saved on the
  /// phone. [onTryAgain] tries the same step again; [onBackToOrder] leaves
  /// the payment.
  factory PaymentStateView.offline({
    required AppLocalizations l10n,
    required VoidCallback? onTryAgain,
    required VoidCallback onBackToOrder,
    Key? key,
  }) =>
      PaymentStateView(
        key: key ?? const Key('payment_offline_state'),
        pillLabel: l10n.offline,
        pillIcon: Icons.wifi_off,
        pillBackground: AppTheme.sand,
        pillForeground: AppTheme.inkSoft,
        title: l10n.noInternetConnection,
        message: l10n.paymentOfflineBody,
        primaryLabel: l10n.retry,
        onPrimary: onTryAgain,
        secondaryLabel: l10n.paymentBackToOrder,
        onSecondary: onBackToOrder,
      );

  @override
  Widget build(BuildContext context) {
    return FwContentWidth(
      child: LayoutBuilder(
        builder: (context, constraints) => SingleChildScrollView(
          child: ConstrainedBox(
            constraints: BoxConstraints(minHeight: constraints.maxHeight),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Padding(
                  padding: const EdgeInsets.fromLTRB(20, 24, 20, 0),
                  child: Align(
                    alignment: AlignmentDirectional.centerStart,
                    child: PaymentCloseButton(onClose: onClose ?? onSecondary),
                  ),
                ),
                Padding(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 28, vertical: 24),
                  // One announcement for the whole state when it appears.
                  child: Semantics(
                    container: true,
                    liveRegion: true,
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        // The large drawing, only on a full state like this
                        // one. Hidden with large text (the widget does it).
                        const RoleSketch.emptyState(role: UserRole.buyer),
                        const SizedBox(height: 16),
                        _StatePill(
                          label: pillLabel,
                          icon: pillIcon,
                          background: pillBackground,
                          foreground: pillForeground,
                        ),
                        const SizedBox(height: 16),
                        Semantics(
                          header: true,
                          child: Text(
                            title,
                            style: FwText.detailTitle,
                          ),
                        ),
                        const SizedBox(height: 16),
                        Text(
                          message,
                          style: const TextStyle(
                            fontSize: 16,
                            height: 1.5,
                            color: AppTheme.inkSoft,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
                Padding(
                  padding: const EdgeInsets.fromLTRB(20, 0, 20, 24),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      FilledButton(
                        key: const Key('payment_state_primary'),
                        onPressed: onPrimary,
                        child: Text(primaryLabel, textAlign: TextAlign.center),
                      ),
                      const SizedBox(height: 8),
                      OutlinedButton(
                        key: const Key('payment_state_secondary'),
                        onPressed: onSecondary,
                        style: paymentSecondaryButtonStyle(),
                        child:
                            Text(secondaryLabel, textAlign: TextAlign.center),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The state's pill: 28 tall, an icon and a bold word, so the state does
/// not rest on colour alone.
class _StatePill extends StatelessWidget {
  final String label;
  final IconData icon;
  final Color background;
  final Color foreground;

  const _StatePill({
    required this.label,
    required this.icon,
    required this.background,
    required this.foreground,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: const BoxConstraints(minHeight: 28),
      padding: const EdgeInsetsDirectional.fromSTEB(8, 4, 12, 4),
      decoration: ShapeDecoration(
        color: background,
        shape: const StadiumBorder(),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 16, color: foreground),
          const SizedBox(width: 6),
          Flexible(
            child: Text(
              label,
              style: TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.w800,
                color: foreground,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
