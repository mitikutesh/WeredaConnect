import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
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
import 'package:wereda_connect/features/trip_listing/presentation/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

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
              backgroundColor: Colors.red,
            ),
          );
        }
      },
      child: Scaffold(
        appBar: AppBar(
          title: Text(
            l10n.deliveryIdLabel(delivery.deliveryId.substring(0, 8)),
          ),
          elevation: 0,
        ),
        body: SafeArea(
          child: Column(
            children: [
              const TripOfflineBanner(),
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
        color: Colors.red.withValues(alpha: 0.05),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: Colors.red.withValues(alpha: 0.2)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.warning, color: Colors.red, size: 20),
              const SizedBox(width: 8),
              Text(
                AppLocalizations.of(context)!.deliveryStatusDisputed,
                style: theme.textTheme.titleSmall?.copyWith(
                  color: Colors.red,
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

    // Buyer + inTransit → Scan QR (Delivery)
    if (isBuyer && delivery.status == DeliveryStatus.inTransit) {
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

    // Cancel button (pending/pickedUp + participant)
    if (isParticipant &&
        (delivery.status == DeliveryStatus.pending ||
            delivery.status == DeliveryStatus.pickedUp)) {
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
                  foregroundColor: Colors.red,
                  side: const BorderSide(color: Colors.red),
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
            color: Colors.red.withValues(alpha: 0.05),
            borderRadius: BorderRadius.circular(8),
          ),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              const Icon(Icons.info_outline, color: Colors.red),
              const SizedBox(width: 8),
              Text(
                AppLocalizations.of(context)!.deliveryStatusDisputed,
                style: const TextStyle(
                  color: Colors.red,
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
              backgroundColor: Colors.red,
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
              style: TextButton.styleFrom(foregroundColor: Colors.red),
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

/// Loads the escrow amount from the crop listing and shows a Pay button.
///
/// Uses the delivery's listingId to look up pricePerKg × quantity × 100kg.
/// Shows nothing if the listing cannot be found.
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

  @override
  void initState() {
    super.initState();
    _loadAmount();
  }

  Future<void> _loadAmount() async {
    try {
      final listing = await IsarService.instance.db.cropListings
          .where()
          .listingIdEqualTo(widget.delivery.listingId)
          .findFirst();
      if (mounted) {
        setState(() {
          _amount = listing != null
              ? listing.quantity * 100 * listing.pricePerKg
              : null;
          _loading = false;
        });
      }
    } on Exception {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_loading) {
      return const SizedBox(
        height: 48,
        child: Center(child: CircularProgressIndicator()),
      );
    }
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
          backgroundColor: Colors.green.shade700,
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
