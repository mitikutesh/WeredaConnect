import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/initiate_payment_screen.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/device_signing_key_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/verification_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/delivery_error_l10n.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/dispute_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_generate_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_scan_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/photo_evidence_widget.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Full delivery details with status timeline and action buttons.
///
/// Shows participant info, pickup/delivery evidence, dispute info,
/// and role-based action buttons for the delivery lifecycle.
class DeliveryDetailScreen extends StatelessWidget {
  final Delivery delivery;
  final String currentUserId;

  const DeliveryDetailScreen({
    required this.delivery,
    required this.currentUserId,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    final dateFormat = DateFormat.yMMMd().add_jm();

    return BlocListener<DeliveryBloc, DeliveryState>(
      listener: (context, state) {
        if (state is PickupConfirmed) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.deliveryConfirmPickup)),
          );
          Navigator.of(context).pop();
        } else if (state is MarkedInTransit) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.deliveryMarkInTransit)),
          );
          Navigator.of(context).pop();
        } else if (state is DeliveryConfirmed) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.deliveryConfirmDelivery)),
          );
          Navigator.of(context).pop();
        } else if (state is DisputeRaised) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.deliveryDisputeSubmit)),
          );
          Navigator.of(context).pop();
        } else if (state is DeliveryCancelled) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.deliveryCancel)),
          );
          Navigator.of(context).pop();
        } else if (state is DeliveryError) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                localizeDeliveryError(
                  AppLocalizations.of(context)!,
                  state.code,
                ),
              ),
              backgroundColor: AppTheme.statusError,
            ),
          );
        }
      },
      child: Scaffold(
        appBar: AppBar(
          title: Text(
            l10n.deliveryIdLabel(
              delivery.deliveryId.length > 8
                  ? delivery.deliveryId.substring(0, 8)
                  : delivery.deliveryId,
            ),
          ),
          elevation: 0,
        ),
        body: SafeArea(
          child: Column(
            children: [
              const OfflineBanner(),
              Expanded(
                child: SingleChildScrollView(
                  padding: const EdgeInsets.all(24),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      // Status timeline
                      _StatusTimelineWidget(status: delivery.status),
                      const SizedBox(height: 24),

                      // Participants section
                      Text(
                        l10n.deliveryDetails,
                        style: theme.textTheme.titleMedium?.copyWith(
                          fontWeight: FontWeight.bold,
                        ),
                      ),
                      const SizedBox(height: 12),
                      _ParticipantRow(
                        icon: Icons.agriculture,
                        label: l10n.deliveryFarmer,
                        name: delivery.farmerName,
                      ),
                      const SizedBox(height: 8),
                      _ParticipantRow(
                        icon: Icons.local_shipping,
                        label: l10n.deliveryTrucker,
                        name: delivery.truckerName,
                      ),
                      const SizedBox(height: 8),
                      _ParticipantRow(
                        icon: Icons.store,
                        label: l10n.deliveryBuyer,
                        name: delivery.buyerName,
                      ),
                      const SizedBox(height: 24),

                      // Pickup section
                      _buildPickupSection(theme, dateFormat, l10n),
                      const SizedBox(height: 16),

                      // Delivery section
                      _buildDeliverySection(theme, dateFormat, l10n),

                      // Dispute section
                      if (delivery.status == DeliveryStatus.disputed) ...[
                        const SizedBox(height: 16),
                        _buildDisputeSection(context, theme),
                      ],

                      const SizedBox(height: 32),

                      // Action buttons
                      ..._buildActionButtons(context, theme),
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildPickupSection(
    ThemeData theme,
    DateFormat dateFormat,
    AppLocalizations l10n,
  ) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          l10n.deliveryPickupSection,
          style: theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.bold,
          ),
        ),
        const SizedBox(height: 8),
        if (delivery.pickedUpAt != null) ...[
          _InfoRow(
            icon: Icons.access_time,
            label: l10n.deliveryTimeLabel,
            value: dateFormat.format(delivery.pickedUpAt!),
          ),
          const SizedBox(height: 8),
          if (delivery.gpsPickup != null)
            _InfoRow(
              icon: Icons.location_on,
              label: l10n.deliveryGpsLabel,
              value:
                  '${delivery.gpsPickup!.latitude.toStringAsFixed(4)}, ${delivery.gpsPickup!.longitude.toStringAsFixed(4)}',
            )
          else
            _InfoRow(
              icon: Icons.location_off,
              label: l10n.deliveryGpsLabel,
              value: l10n.deliveryGpsNotCaptured,
            ),
          const SizedBox(height: 8),
          PhotoEvidenceWidget(
            photoRef: delivery.pickupPhotoRef,
            photoType: PhotoType.pickup,
            deliveryId: delivery.deliveryId,
            canCapture: false,
          ),
        ] else
          Text(
            l10n.deliveryNotYetPickedUp,
            style: theme.textTheme.bodyMedium?.copyWith(
              color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
            ),
          ),
      ],
    );
  }

  Widget _buildDeliverySection(
    ThemeData theme,
    DateFormat dateFormat,
    AppLocalizations l10n,
  ) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          l10n.deliveryDeliverySection,
          style: theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.bold,
          ),
        ),
        const SizedBox(height: 8),
        if (delivery.confirmedAt != null) ...[
          _InfoRow(
            icon: Icons.access_time,
            label: l10n.deliveryTimeLabel,
            value: dateFormat.format(delivery.confirmedAt!),
          ),
          const SizedBox(height: 8),
          if (delivery.gpsDropoff != null)
            _InfoRow(
              icon: Icons.location_on,
              label: l10n.deliveryGpsLabel,
              value:
                  '${delivery.gpsDropoff!.latitude.toStringAsFixed(4)}, ${delivery.gpsDropoff!.longitude.toStringAsFixed(4)}',
            )
          else
            _InfoRow(
              icon: Icons.location_off,
              label: l10n.deliveryGpsLabel,
              value: l10n.deliveryGpsNotCaptured,
            ),
          const SizedBox(height: 8),
          PhotoEvidenceWidget(
            photoRef: delivery.dropoffPhotoRef,
            photoType: PhotoType.dropoff,
            deliveryId: delivery.deliveryId,
            canCapture: false,
          ),
        ] else
          Text(
            l10n.deliveryNotYetDelivered,
            style: theme.textTheme.bodyMedium?.copyWith(
              color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
            ),
          ),
      ],
    );
  }

  Widget _buildDisputeSection(BuildContext context, ThemeData theme) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.statusError.withValues(alpha: 0.05),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: AppTheme.statusError.withValues(alpha: 0.2)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.warning, color: AppTheme.statusError, size: 20),
              const SizedBox(width: 8),
              Text(
                AppLocalizations.of(context)!.deliveryStatusDisputed,
                style: theme.textTheme.titleSmall?.copyWith(
                  color: AppTheme.statusError,
                  fontWeight: FontWeight.bold,
                ),
              ),
            ],
          ),
          if (delivery.disputeReason != null) ...[
            const SizedBox(height: 8),
            Text(
              delivery.disputeReason!,
              style: theme.textTheme.bodyMedium,
            ),
          ],
        ],
      ),
    );
  }

  List<Widget> _buildActionButtons(BuildContext context, ThemeData theme) {
    final l10n = AppLocalizations.of(context)!;
    final widgets = <Widget>[];
    final isFarmer = currentUserId == delivery.farmerId;
    final isTrucker = currentUserId == delivery.truckerId;
    final isBuyer = currentUserId == delivery.buyerId;
    final isParticipant = isFarmer || isTrucker || isBuyer;

    // Farmer + pending → Show QR Code
    if (isFarmer && delivery.status == DeliveryStatus.pending) {
      widgets.add(
        SizedBox(
          width: double.infinity,
          child: FilledButton.icon(
            onPressed: () {
              final deviceKeyService = DeviceSigningKeyService();
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (context) => QrGenerateScreen(
                    delivery: delivery,
                    qrService: QrVerificationService(
                      deviceKeyService: deviceKeyService,
                    ),
                    repository: context.read<DeliveryBloc>().repository,
                    userId: currentUserId,
                  ),
                ),
              );
            },
            icon: const Icon(Icons.qr_code),
            label: Text(l10n.deliveryShowQr),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Trucker + pending → Scan QR (Pickup)
    if (isTrucker && delivery.status == DeliveryStatus.pending) {
      widgets.add(
        SizedBox(
          width: double.infinity,
          child: FilledButton.icon(
            onPressed: () {
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (context) => QrScanScreen(
                    delivery: delivery,
                    verificationType: VerificationType.pickup,
                    qrService: QrVerificationService(
                      deviceKeyService: DeviceSigningKeyService(),
                    ),
                    gpsService: GpsCaptureService(),
                    photoManager: PhotoManager(),
                  ),
                ),
              );
            },
            icon: const Icon(Icons.qr_code_scanner),
            label: Text(l10n.deliveryConfirmPickup),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Trucker + pickedUp → Mark In Transit
    if (isTrucker && delivery.status == DeliveryStatus.pickedUp) {
      widgets.add(
        BlocBuilder<DeliveryBloc, DeliveryState>(
          builder: (context, state) {
            final isLoading = state is DeliveryLoading;
            return SizedBox(
              width: double.infinity,
              child: FilledButton.icon(
                onPressed: isLoading
                    ? null
                    : () => _showMarkInTransitConfirmation(context),
                icon: isLoading
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          valueColor:
                              AlwaysStoppedAnimation<Color>(Colors.white),
                        ),
                      )
                    : const Icon(Icons.route),
                label: Text(l10n.deliveryMarkInTransit),
                style: FilledButton.styleFrom(
                  minimumSize: const Size.fromHeight(48),
                ),
              ),
            );
          },
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Buyer + pickedUp/inTransit → Scan QR (Delivery). The trucker's separate
    // "mark in transit" step is optional, so the buyer can confirm the handoff
    // directly after pickup as well (server allows pickedUp -> delivered).
    if (isBuyer &&
        (delivery.status == DeliveryStatus.pickedUp ||
            delivery.status == DeliveryStatus.inTransit)) {
      widgets.add(
        SizedBox(
          width: double.infinity,
          child: FilledButton.icon(
            onPressed: () {
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (context) => QrScanScreen(
                    delivery: delivery,
                    verificationType: VerificationType.delivery,
                    qrService: QrVerificationService(
                      deviceKeyService: DeviceSigningKeyService(),
                    ),
                    gpsService: GpsCaptureService(),
                    photoManager: PhotoManager(),
                  ),
                ),
              );
            },
            icon: const Icon(Icons.qr_code_scanner),
            label: Text(l10n.deliveryConfirmDelivery),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Buyer + self-pickup + pending → Scan the farmer's QR to confirm collection.
    // Self-pickup has no trucker/transit leg: this single scan takes the delivery
    // pending → delivered at the farm. The scan is GATED until escrow is funded so
    // the farmer is actually paid on confirmation (see _SelfPickupScanButton).
    if (isBuyer &&
        delivery.deliveryType == DeliveryConstants.selfPickupDeliveryType &&
        delivery.status == DeliveryStatus.pending) {
      widgets.add(
        _SelfPickupScanButton(
          delivery: delivery,
          currentUserId: currentUserId,
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Farmer + delivered → Raise Dispute (within 48h)
    if (isFarmer && delivery.status == DeliveryStatus.delivered) {
      _addDisputeButton(context, widgets);
    }

    // Buyer + delivered → Raise Dispute (within 48h)
    if (isBuyer && delivery.status == DeliveryStatus.delivered) {
      _addDisputeButton(context, widgets);
    }

    // Buyer + pending → Initiate escrow payment
    if (isBuyer && delivery.status == DeliveryStatus.pending) {
      widgets.add(
        _EscrowPayButton(
          delivery: delivery,
          currentUserId: currentUserId,
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Cancel button (pending + participant). After pickup no party can cancel
    // (ESC-08); a problem then goes through a dispute.
    if (isParticipant && delivery.status == DeliveryStatus.pending) {
      widgets.add(
        BlocBuilder<DeliveryBloc, DeliveryState>(
          builder: (context, state) {
            final isLoading = state is DeliveryLoading;
            return SizedBox(
              width: double.infinity,
              child: OutlinedButton.icon(
                onPressed:
                    isLoading ? null : () => _showCancelConfirmation(context),
                style: OutlinedButton.styleFrom(
                  foregroundColor: AppTheme.statusError,
                  side: const BorderSide(color: AppTheme.statusError),
                  minimumSize: const Size.fromHeight(48),
                ),
                icon: const Icon(Icons.cancel_outlined),
                label: Text(l10n.deliveryCancel),
              ),
            );
          },
        ),
      );
    }

    // Disputed → read-only
    if (delivery.status == DeliveryStatus.disputed) {
      widgets.add(
        Container(
          width: double.infinity,
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: AppTheme.statusError.withValues(alpha: 0.05),
            borderRadius: BorderRadius.circular(8),
          ),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              const Icon(Icons.info_outline, color: AppTheme.statusError),
              const SizedBox(width: 8),
              Text(
                AppLocalizations.of(context)!.deliveryStatusDisputed,
                style: const TextStyle(
                  color: AppTheme.statusError,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ],
          ),
        ),
      );
    }

    return widgets;
  }

  void _addDisputeButton(BuildContext context, List<Widget> widgets) {
    final canDispute = delivery.confirmedAt != null &&
        DateTime.now().toUtc().difference(delivery.confirmedAt!) <
            DeliveryConstants.disputeWindow;

    if (canDispute) {
      widgets.add(
        SizedBox(
          width: double.infinity,
          child: ElevatedButton.icon(
            onPressed: () {
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (context) => DisputeScreen(
                    delivery: delivery,
                    userId: currentUserId,
                  ),
                ),
              );
            },
            style: ElevatedButton.styleFrom(
              backgroundColor: AppTheme.statusError,
              foregroundColor: Colors.white,
              minimumSize: const Size.fromHeight(48),
            ),
            icon: const Icon(Icons.warning),
            label: Text(AppLocalizations.of(context)!.deliveryRaiseDispute),
          ),
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }
  }

  void _showCancelConfirmation(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        return AlertDialog(
          title: Text(l10n.deliveryCancelConfirmTitle),
          content: Text(l10n.deliveryCancelConfirmMessage),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(l10n.cancel),
            ),
            TextButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                context.read<DeliveryBloc>().add(
                      CancelDelivery(
                        deliveryId: delivery.deliveryId,
                        userId: currentUserId,
                        tripId: delivery.tripId,
                      ),
                    );
              },
              style:
                  TextButton.styleFrom(foregroundColor: AppTheme.statusError),
              child: Text(l10n.deliveryCancel),
            ),
          ],
        );
      },
    );
  }

  void _showMarkInTransitConfirmation(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        return AlertDialog(
          title: Text(l10n.deliveryMarkInTransit),
          content: Text(l10n.deliveryInTransitConfirmMessage),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(l10n.cancel),
            ),
            FilledButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                context.read<DeliveryBloc>().add(
                      MarkInTransit(
                        deliveryId: delivery.deliveryId,
                        truckerId: currentUserId,
                      ),
                    );
              },
              child: Text(l10n.deliveryMarkInTransit),
            ),
          ],
        );
      },
    );
  }
}

/// Status timeline showing delivery progress.
class _StatusTimelineWidget extends StatelessWidget {
  final DeliveryStatus status;

  const _StatusTimelineWidget({required this.status});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    final steps = [
      (DeliveryStatus.pending, l10n.deliveryStatusPending, Icons.inventory_2),
      (
        DeliveryStatus.pickedUp,
        l10n.deliveryStatusPickedUp,
        Icons.local_shipping
      ),
      (DeliveryStatus.inTransit, l10n.deliveryStatusInTransit, Icons.route),
      (
        DeliveryStatus.delivered,
        l10n.deliveryStatusDelivered,
        Icons.check_circle
      ),
    ];

    final currentIndex = switch (status) {
      DeliveryStatus.pending => 0,
      DeliveryStatus.pickedUp => 1,
      DeliveryStatus.inTransit => 2,
      DeliveryStatus.delivered => 3,
      DeliveryStatus.disputed => 3,
      DeliveryStatus.cancelled => -1,
    };

    return Row(
      children: [
        for (var i = 0; i < steps.length; i++) ...[
          if (i > 0)
            Expanded(
              child: Container(
                height: 2,
                color: i <= currentIndex
                    ? theme.colorScheme.primary
                    : theme.colorScheme.outline.withValues(alpha: 0.3),
              ),
            ),
          _TimelineStep(
            icon: steps[i].$3,
            label: steps[i].$2,
            isReached: i <= currentIndex,
            isCurrent: i == currentIndex,
            theme: theme,
          ),
        ],
      ],
    );
  }
}

class _TimelineStep extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool isReached;
  final bool isCurrent;
  final ThemeData theme;

  const _TimelineStep({
    required this.icon,
    required this.label,
    required this.isReached,
    required this.isCurrent,
    required this.theme,
  });

  @override
  Widget build(BuildContext context) {
    final color = isReached
        ? theme.colorScheme.primary
        : theme.colorScheme.outline.withValues(alpha: 0.3);

    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 36,
          height: 36,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: isReached ? theme.colorScheme.primary : Colors.transparent,
            border: Border.all(color: color, width: isCurrent ? 3 : 2),
          ),
          child: Icon(
            icon,
            size: 18,
            color: isReached ? theme.colorScheme.onPrimary : color,
          ),
        ),
        const SizedBox(height: 4),
        Text(
          label,
          style: theme.textTheme.labelSmall?.copyWith(
            color: isReached
                ? theme.colorScheme.primary
                : theme.colorScheme.onSurface.withValues(alpha: 0.5),
            fontWeight: isCurrent ? FontWeight.bold : FontWeight.normal,
          ),
        ),
      ],
    );
  }
}

class _ParticipantRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String name;

  const _ParticipantRow({
    required this.icon,
    required this.label,
    required this.name,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Row(
      children: [
        Icon(
          icon,
          size: 20,
          color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
        ),
        const SizedBox(width: 12),
        Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              label,
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
              ),
            ),
            Text(
              name,
              style: theme.textTheme.bodyLarge?.copyWith(
                fontWeight: FontWeight.w500,
              ),
            ),
          ],
        ),
      ],
    );
  }
}

/// Loads the escrow amount from the delivery's order and shows a Pay button.
///
/// The server binds the escrow to the order (escrow-protocol.md v1.2.0): the
/// amount must equal the order's `orderTotal`, so that is what is offered.
/// Shows nothing if no order can be resolved locally.
class _EscrowPayButton extends StatefulWidget {
  final Delivery delivery;
  final String currentUserId;

  const _EscrowPayButton({
    required this.delivery,
    required this.currentUserId,
  });

  @override
  State<_EscrowPayButton> createState() => _EscrowPayButtonState();
}

class _EscrowPayButtonState extends State<_EscrowPayButton> {
  double? _amount;
  bool _loading = true;
  bool _alreadyPaid = false;

  @override
  void initState() {
    super.initState();
    _loadAmount();
  }

  Future<void> _loadAmount() async {
    try {
      // Money already held? Both legs count: a delivery escrow initiated here
      // earlier, or an order-funded escrow (the buyer paid at order time — a
      // second payment would bounce off the server's ESCROW_024 guard anyway).
      final paid = await _moneyAlreadyHeld();
      final order = await _linkedOrder();
      if (mounted) {
        setState(() {
          _alreadyPaid = paid;
          _amount = order?.orderTotal;
          _loading = false;
        });
      }
    } on Object catch (_) {
      // On web IsarService.instance.db is null, so the dynamic query throws
      // NoSuchMethodError (an Error, not an Exception). Catch broadly so the
      // spinner clears and the button is simply hidden (no order found).
      if (mounted) setState(() => _loading = false);
    }
  }

  /// The order this delivery fulfils. Prefer the delivery's own orderId (kept
  /// locally since SIM-14); rows pulled before that carry none, so fall back to
  /// the delivery's trip (a matched order shares the delivery's tripId).
  /// Matching on listing+buyer alone could pick a DIFFERENT order of the same
  /// buyer on the same listing.
  Future<Order?> _linkedOrder() async {
    final orderId = widget.delivery.orderId;
    if (orderId != null && orderId.isNotEmpty) {
      final byId = await IsarService.instance.db.orders
          .filter()
          .orderIdEqualTo(orderId)
          .findFirst();
      if (byId != null) return byId;
    }
    return IsarService.instance.db.orders
        .filter()
        .listingIdEqualTo(widget.delivery.listingId)
        .buyerIdEqualTo(widget.currentUserId)
        .tripIdEqualTo(widget.delivery.tripId)
        .findFirst();
  }

  Future<bool> _moneyAlreadyHeld() async {
    // NOT 'initiated': a stale abandoned initiation would hide the button
    // forever (this screen never refreshes history), and the server's
    // ESCROW_009 guard already blocks a genuine double-initiation.
    final payment = await IsarService.instance.db.payments
        .filter()
        .deliveryIdEqualTo(widget.delivery.deliveryId)
        .findFirst();
    final paymentStatus = payment?.status.name;
    if (paymentStatus == 'escrowed' ||
        paymentStatus == 'released' ||
        paymentStatus == 'completed' ||
        paymentStatus == 'disputed') {
      return true;
    }

    // When no trip-matched order exists, showing the button is safe (the
    // server's ESCROW_024 guard rejects a true double-charge).
    final order = await _linkedOrder();
    final orderStatus = order?.status.name;
    return orderStatus == 'funded' ||
        orderStatus == 'accepted' ||
        orderStatus == 'pickupConfirmed' ||
        orderStatus == 'inTransit' ||
        orderStatus == 'delivered';
  }

  @override
  Widget build(BuildContext context) {
    if (_loading) {
      return const SizedBox(
        height: 48,
        child: Center(child: CircularProgressIndicator()),
      );
    }
    // Money already in escrow — never offer a second payment path.
    if (_alreadyPaid) return const SizedBox.shrink();
    if (_amount == null) return const SizedBox.shrink();

    final l10n = AppLocalizations.of(context)!;
    return SizedBox(
      width: double.infinity,
      child: FilledButton.icon(
        onPressed: () => Navigator.push(
          context,
          MaterialPageRoute<void>(
            builder: (_) => InitiatePaymentScreen(
              deliveryId: widget.delivery.deliveryId,
              buyerId: widget.currentUserId,
              farmerId: widget.delivery.farmerId,
              farmerName: widget.delivery.farmerName,
              amount: _amount!,
            ),
          ),
        ),
        icon: const Icon(Icons.payment),
        label: Text(l10n.escrowPayForDelivery),
        style: FilledButton.styleFrom(
          minimumSize: const Size.fromHeight(48),
          backgroundColor: AppTheme.onStatusSuccessContainer,
        ),
      ),
    );
  }
}

/// Buyer's self-pickup "Scan farmer's QR" button, GATED on the escrow being funded.
///
/// Confirming self-pickup releases money, so the scan is disabled until this delivery's
/// escrow is `escrowed` locally — steering the buyer to pay first. Uses the same
/// dynamic (web-safe) local Isar lookup as [_EscrowPayButton]; if the local record can't
/// be read (e.g. web, where there is no QR scan flow anyway) it falls back to enabled so
/// the server stays the authority (a scan with no funded escrow is a safe no-op payout-wise).
class _SelfPickupScanButton extends StatefulWidget {
  final Delivery delivery;
  final String currentUserId;

  const _SelfPickupScanButton({
    required this.delivery,
    required this.currentUserId,
  });

  @override
  State<_SelfPickupScanButton> createState() => _SelfPickupScanButtonState();
}

class _SelfPickupScanButtonState extends State<_SelfPickupScanButton> {
  bool _loading = true;
  bool _escrowFunded = false;

  @override
  void initState() {
    super.initState();
    _loadEscrowStatus();
  }

  Future<void> _loadEscrowStatus() async {
    try {
      final payment = await IsarService.instance.db.payments
          .filter()
          .deliveryIdEqualTo(widget.delivery.deliveryId)
          .findFirst();
      // Read the enum name dynamically to avoid importing the Isar schema into
      // shared UI (keeps the web build clean). `escrowed`+ mean funds are held.
      final statusName = payment?.status.name;
      var funded = statusName == 'escrowed' ||
          statusName == 'released' ||
          statusName == 'completed';
      // Order-funded escrow: the buyer already paid when the order was funded,
      // so the local Payment row may not exist yet (it appears after a history
      // refresh). Prefer the delivery's own orderId (kept locally since SIM-14);
      // for older rows without one, ANY escrow-held order on this listing counts
      // as paid (self-pickup orders have no trip to match on). Both wrong
      // directions are safe: the server QR gate fail-closes an unpaid scan, and
      // a wrongly disabled button only asks the buyer to pay first.
      if (!funded) {
        var orders = <Order>[];
        final orderId = widget.delivery.orderId;
        if (orderId != null && orderId.isNotEmpty) {
          orders = await IsarService.instance.db.orders
              .filter()
              .orderIdEqualTo(orderId)
              .findAll();
        }
        if (orders.isEmpty) {
          orders = await IsarService.instance.db.orders
              .filter()
              .listingIdEqualTo(widget.delivery.listingId)
              .buyerIdEqualTo(widget.currentUserId)
              .findAll();
        }
        for (final order in orders) {
          final orderStatus = order.status.name;
          if (orderStatus == 'funded' ||
              orderStatus == 'accepted' ||
              orderStatus == 'pickupConfirmed' ||
              orderStatus == 'inTransit') {
            funded = true;
            break;
          }
        }
      }
      if (mounted) {
        setState(() {
          _escrowFunded = funded;
          _loading = false;
        });
      }
    } on Object catch (_) {
      // Web (db is null) or no local record — fall back to enabled; the server
      // remains authoritative and a scan without a funded escrow is a safe no-op.
      if (mounted) {
        setState(() {
          _escrowFunded = true;
          _loading = false;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    if (_loading) {
      return const SizedBox(
        height: 48,
        child: Center(child: CircularProgressIndicator()),
      );
    }

    if (!_escrowFunded) {
      // Not yet funded: guide the buyer to pay first (button disabled + hint).
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: double.infinity,
            child: FilledButton.icon(
              onPressed: null,
              icon: const Icon(Icons.qr_code_scanner),
              label: Text(l10n.deliverySelfPickupConfirm),
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
            ),
          ),
          const SizedBox(height: 4),
          Text(
            l10n.deliverySelfPickupPayFirst,
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ],
      );
    }

    return SizedBox(
      width: double.infinity,
      child: FilledButton.icon(
        onPressed: () {
          Navigator.push(
            context,
            MaterialPageRoute<void>(
              builder: (context) => QrScanScreen(
                delivery: widget.delivery,
                verificationType: VerificationType.delivery,
                qrService: QrVerificationService(
                  deviceKeyService: DeviceSigningKeyService(),
                ),
                gpsService: GpsCaptureService(),
                photoManager: PhotoManager(),
              ),
            ),
          );
        },
        icon: const Icon(Icons.qr_code_scanner),
        label: Text(l10n.deliverySelfPickupConfirm),
        style: FilledButton.styleFrom(
          minimumSize: const Size.fromHeight(48),
        ),
      ),
    );
  }
}

class _InfoRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;

  const _InfoRow({
    required this.icon,
    required this.label,
    required this.value,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Row(
      children: [
        Icon(
          icon,
          size: 20,
          color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
        ),
        const SizedBox(width: 12),
        Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              label,
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
              ),
            ),
            Text(
              value,
              style: theme.textTheme.bodyLarge?.copyWith(
                fontWeight: FontWeight.w500,
              ),
            ),
          ],
        ),
      ],
    );
  }
}
