import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/escrow_payment/domain/escrow_hold.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/initiate_payment_screen.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/presentation/open_live_trip.dart';
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
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/photo_evidence_widget.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Full delivery details (a sibling of the Buyer · Order detail board): the
/// delivery named in the header with its status pill, the timeline card,
/// who is involved, the pickup and the drop-off evidence, and the role's
/// next step pinned at the foot.
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
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);

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
        body: SafeArea(
          bottom: false,
          child: Column(
            children: [
              const OfflineBanner(),
              Expanded(
                child: SingleChildScrollView(
                  child: FwContentWidth(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        _DeliveryHeader(
                          delivery: delivery,
                          currentUserId: currentUserId,
                        ),
                        Padding(
                          padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              // LT-11: the buyer and the farmer follow the
                              // truck from pickup on, and see the trip summary
                              // after delivery. Never the trucker (their own
                              // trip mode is LT-09).
                              if (liveTripViewerOf(delivery, currentUserId) !=
                                      null &&
                                  hasLiveTrip(delivery) &&
                                  !isWaitingForPickup(delivery)) ...[
                                OutlinedButton.icon(
                                  key: const Key('delivery_live_trip'),
                                  onPressed: () => openLiveTrip(
                                    context,
                                    delivery,
                                    currentUserId,
                                  ),
                                  icon: const Icon(Icons.map_outlined),
                                  label: Text(l10n.liveTripTitle),
                                  style: _secondaryStyle,
                                ),
                                const SizedBox(height: 12),
                              ],

                              _DeliveryTimeline(status: delivery.status),
                              const SizedBox(height: 12),

                              _PartiesCard(delivery: delivery),
                              const SizedBox(height: 16),

                              // The evidence of each leg: when, where and the
                              // photo, or that the leg has not happened yet.
                              FwSectionLabel(
                                l10n.deliveryPickupSection,
                                padding: const EdgeInsets.only(bottom: 8),
                              ),
                              _StageCard(
                                at: delivery.pickedUpAt,
                                gps: delivery.gpsPickup,
                                photoRef: delivery.pickupPhotoRef,
                                photoType: PhotoType.pickup,
                                deliveryId: delivery.deliveryId,
                                notYet: l10n.deliveryNotYetPickedUp,
                                clock: clock,
                              ),
                              const SizedBox(height: 16),

                              FwSectionLabel(
                                l10n.deliveryDeliverySection,
                                padding: const EdgeInsets.only(bottom: 8),
                              ),
                              _StageCard(
                                at: delivery.confirmedAt,
                                gps: delivery.gpsDropoff,
                                photoRef: delivery.dropoffPhotoRef,
                                photoType: PhotoType.dropoff,
                                deliveryId: delivery.deliveryId,
                                notYet: l10n.deliveryNotYetDelivered,
                                clock: clock,
                              ),

                              if (delivery.status ==
                                  DeliveryStatus.disputed) ...[
                                const SizedBox(height: 16),
                                _DisputeCard(reason: delivery.disputeReason),
                              ],
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
        bottomNavigationBar: _buildBottomActions(context),
      ),
    );
  }

  static final ButtonStyle _primaryStyle = FilledButton.styleFrom(
    minimumSize: const Size.fromHeight(56),
    textStyle: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800),
  );

  static final ButtonStyle _secondaryStyle = OutlinedButton.styleFrom(
    minimumSize: const Size.fromHeight(52),
    textStyle: const TextStyle(fontSize: 15, fontWeight: FontWeight.w700),
  );

  /// The role's next step on this delivery, pinned at the foot of the page
  /// (Show QR, Confirm Pickup, Mark In Transit, Confirm Delivery, Pay,
  /// Raise Dispute), with Cancel Delivery under it while that is allowed.
  /// Null when the viewer has nothing to do here.
  Widget? _buildBottomActions(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final actions = <Widget>[];
    final isFarmer = currentUserId == delivery.farmerId;
    final isTrucker = currentUserId == delivery.truckerId;
    final isBuyer = currentUserId == delivery.buyerId;
    final isParticipant = isFarmer || isTrucker || isBuyer;

    // Farmer + pending → Show QR Code
    if (isFarmer && delivery.status == DeliveryStatus.pending) {
      actions.add(
        FilledButton.icon(
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
          style: _primaryStyle,
        ),
      );
    }

    // Trucker + pending → Scan QR (Pickup)
    if (isTrucker && delivery.status == DeliveryStatus.pending) {
      actions.add(
        FilledButton.icon(
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
          style: _primaryStyle,
        ),
      );
    }

    // Trucker + pickedUp → Mark In Transit
    if (isTrucker && delivery.status == DeliveryStatus.pickedUp) {
      actions.add(
        BlocBuilder<DeliveryBloc, DeliveryState>(
          builder: (context, state) {
            final isLoading = state is DeliveryLoading;
            return FilledButton.icon(
              onPressed: isLoading
                  ? null
                  : () => _showMarkInTransitConfirmation(context),
              icon:
                  isLoading ? const _ButtonSpinner() : const Icon(Icons.route),
              label: Text(l10n.deliveryMarkInTransit),
              style: _primaryStyle,
            );
          },
        ),
      );
    }

    // Buyer + pickedUp/inTransit → Scan QR (Delivery). The trucker's separate
    // "mark in transit" step is optional, so the buyer can confirm the handoff
    // directly after pickup as well (server allows pickedUp -> delivered).
    if (isBuyer &&
        (delivery.status == DeliveryStatus.pickedUp ||
            delivery.status == DeliveryStatus.inTransit)) {
      actions.add(
        FilledButton.icon(
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
          style: _primaryStyle,
        ),
      );
    }

    // Buyer + self-pickup + pending → Scan the farmer's QR to confirm collection.
    // Self-pickup has no trucker/transit leg: this single scan takes the delivery
    // pending → delivered at the farm. The scan is GATED until escrow is funded so
    // the farmer is actually paid on confirmation (see _SelfPickupScanButton).
    if (isBuyer &&
        delivery.deliveryType == DeliveryConstants.selfPickupDeliveryType &&
        delivery.status == DeliveryStatus.pending) {
      actions.add(
        _SelfPickupScanButton(
          delivery: delivery,
          currentUserId: currentUserId,
        ),
      );
    }

    // Farmer or buyer + delivered → Raise Dispute (within 48h)
    if ((isFarmer || isBuyer) && delivery.status == DeliveryStatus.delivered) {
      _addDisputeButton(context, actions);
    }

    // Buyer + pending → Initiate escrow payment
    if (isBuyer && delivery.status == DeliveryStatus.pending) {
      actions.add(
        _EscrowPayButton(
          delivery: delivery,
          currentUserId: currentUserId,
        ),
      );
    }

    // Cancel (pending + participant). After pickup no party can cancel
    // (ESC-08); a problem then goes through a dispute.
    Widget? cancel;
    if (isParticipant && delivery.status == DeliveryStatus.pending) {
      cancel = BlocBuilder<DeliveryBloc, DeliveryState>(
        builder: (context, state) {
          final isLoading = state is DeliveryLoading;
          return TextButton.icon(
            onPressed:
                isLoading ? null : () => _showCancelConfirmation(context),
            style: TextButton.styleFrom(
              foregroundColor: AppTheme.statusError,
              minimumSize: const Size.fromHeight(44),
              textStyle: const TextStyle(
                fontSize: 14,
                fontWeight: FontWeight.w700,
              ),
            ),
            icon: const Icon(Icons.cancel_outlined, size: 18),
            label: Text(l10n.deliveryCancel),
          );
        },
      );
    }

    if (actions.isEmpty && cancel == null) return null;
    return FwBottomActions(
      children: [
        for (var i = 0; i < actions.length; i++) ...[
          if (i > 0) const SizedBox(height: 8),
          actions[i],
        ],
        if (cancel != null) ...[
          if (actions.isNotEmpty) const SizedBox(height: 4),
          cancel,
        ],
      ],
    );
  }

  void _addDisputeButton(BuildContext context, List<Widget> widgets) {
    final canDispute = delivery.confirmedAt != null &&
        DateTime.now().toUtc().difference(delivery.confirmedAt!) <
            DeliveryConstants.disputeWindow;

    if (canDispute) {
      widgets.add(
        FilledButton.icon(
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
          style: _primaryStyle.copyWith(
            backgroundColor: const WidgetStatePropertyAll(AppTheme.statusError),
            foregroundColor: const WidgetStatePropertyAll(Colors.white),
          ),
          icon: const Icon(Icons.warning),
          label: Text(AppLocalizations.of(context)!.deliveryRaiseDispute),
        ),
      );
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

/// The 20 px spinner inside a busy filled button.
class _ButtonSpinner extends StatelessWidget {
  const _ButtonSpinner();

  @override
  Widget build(BuildContext context) {
    return const SizedBox(
      height: 20,
      width: 20,
      child: CircularProgressIndicator(
        strokeWidth: 2,
        valueColor: AlwaysStoppedAnimation<Color>(Colors.white),
      ),
    );
  }
}

/// The page header: Back, what the delivery carries ("Teff · 1,500 kg", or
/// its number for rows without a crop), the parties the viewer is not and
/// the planned pickup day under it, and the status pill.
class _DeliveryHeader extends StatelessWidget {
  final Delivery delivery;
  final String currentUserId;

  const _DeliveryHeader({
    required this.delivery,
    required this.currentUserId,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final status = delivery.status;
    final isFarmer = currentUserId == delivery.farmerId;
    final isBuyer = currentUserId == delivery.buyerId;
    final selfPickup =
        delivery.deliveryType == DeliveryConstants.selfPickupDeliveryType ||
            delivery.truckerId.isEmpty;

    final cropKey = delivery.cropType;
    final crop = cropKey == null || cropKey.isEmpty
        ? null
        : CropType.labelFor(cropKey, l10n);
    final quantity = delivery.quantity;
    final id = delivery.deliveryId;
    final title = crop == null
        ? l10n.deliveryIdLabel(id.length > 8 ? id.substring(0, 8) : id)
        : quantity == null
            ? crop
            : '$crop · ${l10n.quantityKg(NumberFormat('#,##0.##').format(quantity))}';

    final farmer = partyDisplayName(delivery.farmerName, l10n);
    final place = delivery.pickupPlace;
    final farmerLine =
        place == null || place.isEmpty ? farmer : '$farmer, $place';
    final trucker = partyDisplayName(delivery.truckerName, l10n);
    final buyer = partyDisplayName(delivery.buyerName, l10n);
    final parties = isBuyer
        ? l10n.liveTripFrom(farmerLine)
        : isFarmer
            ? (selfPickup ? buyer : '$buyer · $trucker')
            : '$farmerLine → $buyer';
    final planned = delivery.pickupPlannedAt;
    final subtitle = status == DeliveryStatus.pending && planned != null
        ? '$parties · ${l10n.delivPickupOn(_shortDay(planned))}'
        : parties;

    return FwDetailHeader(
      title: title,
      subtitle: subtitle,
      trailing: StatusChip(
        label: deliveryStatusLabel(status, l10n),
        kind: _statusKind(status),
      ),
    );
  }

  /// "Thu Oct 30": the weekday, then the short date, on the Ethiopian day.
  static String _shortDay(DateTime at) {
    final day = dayInEthiopia(at);
    return '${DateFormat.E().format(day)} ${DateFormat.MMMd().format(day)}';
  }

  static StatusKind _statusKind(DeliveryStatus status) => switch (status) {
        DeliveryStatus.pending => StatusKind.neutral,
        DeliveryStatus.pickedUp ||
        DeliveryStatus.inTransit =>
          StatusKind.transit,
        DeliveryStatus.delivered => StatusKind.success,
        DeliveryStatus.disputed => StatusKind.error,
        DeliveryStatus.cancelled => StatusKind.neutral,
      };
}

const TextStyle _cardTitle = TextStyle(
  fontSize: 15,
  fontWeight: FontWeight.w800,
  color: AppTheme.soilDark,
);

/// The delivery's four steps as the boards draw a timeline: short bars,
/// done in green, the current one in gold, the rest grey. A cancelled
/// delivery has no current step.
class _DeliveryTimeline extends StatelessWidget {
  final DeliveryStatus status;

  const _DeliveryTimeline({required this.status});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final steps = [
      l10n.deliveryStatusPending,
      l10n.deliveryStatusPickedUp,
      l10n.deliveryStatusInTransit,
      l10n.deliveryStatusDelivered,
    ];

    final currentIndex = switch (status) {
      DeliveryStatus.pending => 0,
      DeliveryStatus.pickedUp => 1,
      DeliveryStatus.inTransit => 2,
      DeliveryStatus.delivered => 3,
      DeliveryStatus.disputed => 3,
      DeliveryStatus.cancelled => -1,
    };

    return FwCard(
      key: const Key('delivery_timeline'),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            header: true,
            child: Text(l10n.restDeliveryTimeline, style: _cardTitle),
          ),
          const SizedBox(height: 10),
          Row(
            children: [
              for (var i = 0; i < steps.length; i++) ...[
                if (i > 0) const SizedBox(width: 6),
                Expanded(
                  child: _StepBar(
                    label: steps[i],
                    done: i < currentIndex,
                    current: i == currentIndex,
                  ),
                ),
              ],
            ],
          ),
        ],
      ),
    );
  }
}

/// One step of the timeline: a 4 px bar over its name.
class _StepBar extends StatelessWidget {
  final String label;
  final bool done;
  final bool current;

  const _StepBar({
    required this.label,
    required this.done,
    required this.current,
  });

  @override
  Widget build(BuildContext context) {
    final bar = done
        ? AppTheme.fieldGreen
        : current
            ? AppTheme.accentSoft
            : AppTheme.whisperBorder;
    final ink = current
        ? AppTheme.money
        : done
            ? AppTheme.soilDark
            : AppTheme.mutedSteel;
    return Semantics(
      selected: current,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Container(
            height: 4,
            decoration: BoxDecoration(
              color: bar,
              borderRadius: BorderRadius.circular(999),
            ),
          ),
          const SizedBox(height: 6),
          Text(
            label,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(
              fontSize: 11,
              fontWeight: done || current ? FontWeight.w700 : FontWeight.w600,
              color: ink,
            ),
          ),
        ],
      ),
    );
  }
}

/// Who is involved: the farmer, the trucker and the buyer, each with a
/// tinted role tile, as the board's contact card lists the parties. A
/// self-pickup has no trucker row.
class _PartiesCard extends StatelessWidget {
  final Delivery delivery;

  const _PartiesCard({required this.delivery});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final hasTrucker = delivery.truckerId.isNotEmpty &&
        delivery.deliveryType != DeliveryConstants.selfPickupDeliveryType;
    return FwCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            header: true,
            child: Text(l10n.deliveryDetails, style: _cardTitle),
          ),
          const SizedBox(height: 12),
          _PartyRow(
            icon: Icons.agriculture,
            background: AppTheme.fieldGreenTint,
            foreground: AppTheme.fieldGreen,
            label: l10n.deliveryFarmer,
            name: partyDisplayName(delivery.farmerName, l10n),
          ),
          if (hasTrucker) ...[
            const SizedBox(height: 10),
            _PartyRow(
              icon: Icons.local_shipping,
              background: AppTheme.actionCream,
              foreground: AppTheme.money,
              label: l10n.deliveryTrucker,
              name: partyDisplayName(delivery.truckerName, l10n),
            ),
          ],
          const SizedBox(height: 10),
          _PartyRow(
            icon: Icons.store,
            background: AppTheme.dropoffBlueTint,
            foreground: AppTheme.dropoffBlue,
            label: l10n.deliveryBuyer,
            name: partyDisplayName(delivery.buyerName, l10n),
          ),
        ],
      ),
    );
  }
}

class _PartyRow extends StatelessWidget {
  final IconData icon;
  final Color background;
  final Color foreground;
  final String label;
  final String name;

  const _PartyRow({
    required this.icon,
    required this.background,
    required this.foreground,
    required this.label,
    required this.name,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Container(
          width: 36,
          height: 36,
          decoration: BoxDecoration(
            color: background,
            shape: BoxShape.circle,
          ),
          child: Icon(icon, size: 18, color: foreground),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                name,
                style: const TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.soilDark,
                ),
              ),
              Text(
                label,
                style: const TextStyle(
                  fontSize: 12,
                  color: AppTheme.mutedSteel,
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

/// One leg's evidence card: the time, the GPS fix (or that none was
/// captured) and the photo, or the muted line saying the leg is still to
/// come.
class _StageCard extends StatelessWidget {
  final DateTime? at;
  final LatLng? gps;
  final String? photoRef;
  final PhotoType photoType;
  final String deliveryId;
  final String notYet;
  final ClockFormat clock;

  const _StageCard({
    required this.at,
    required this.gps,
    required this.photoRef,
    required this.photoType,
    required this.deliveryId,
    required this.notYet,
    required this.clock,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final at = this.at;
    if (at == null) {
      return FwCard(
        child: Text(
          notYet,
          style: const TextStyle(
            fontSize: 13,
            fontWeight: FontWeight.w500,
            color: AppTheme.mutedSteel,
          ),
        ),
      );
    }
    final gps = this.gps;
    return FwCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _InfoRow(
            icon: Icons.access_time,
            label: l10n.deliveryTimeLabel,
            value: clock.dateTime(at),
          ),
          const SizedBox(height: 10),
          if (gps != null)
            _InfoRow(
              icon: Icons.location_on,
              label: l10n.deliveryGpsLabel,
              value:
                  '${gps.latitude.toStringAsFixed(4)}, ${gps.longitude.toStringAsFixed(4)}',
            )
          else
            _InfoRow(
              icon: Icons.location_off,
              label: l10n.deliveryGpsLabel,
              value: l10n.deliveryGpsNotCaptured,
            ),
          const SizedBox(height: 12),
          PhotoEvidenceWidget(
            photoRef: photoRef,
            photoType: photoType,
            deliveryId: deliveryId,
            canCapture: false,
          ),
        ],
      ),
    );
  }
}

/// The dispute on this delivery: the status and the reason given, on the
/// red tint.
class _DisputeCard extends StatelessWidget {
  final String? reason;

  const _DisputeCard({required this.reason});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final reason = this.reason;
    return FwNoteBox(
      icon: Icons.warning_amber_rounded,
      background: AppTheme.statusErrorContainer,
      foreground: AppTheme.onStatusErrorContainer,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(l10n.deliveryStatusDisputed),
          if (reason != null && reason.isNotEmpty) ...[
            const SizedBox(height: 4),
            Text(
              reason,
              style: const TextStyle(fontWeight: FontWeight.w500),
            ),
          ],
        ],
      ),
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
      // earlier, or an order-funded escrow (the buyer paid at order time; a
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
    // NOT 'initiated' (escrowAlreadyPaid): this screen never refreshes
    // history. A `held` escrow (FRD-06) counts as paid.
    final payment = await IsarService.instance.db.payments
        .filter()
        .deliveryIdEqualTo(widget.delivery.deliveryId)
        .findFirst();
    if (payment != null && escrowAlreadyPaid(payment.status)) return true;

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
    // Money already in escrow: never offer a second payment path.
    if (_alreadyPaid) return const SizedBox.shrink();
    if (_amount == null) return const SizedBox.shrink();

    final l10n = AppLocalizations.of(context)!;
    return FilledButton.icon(
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
      // The money button is soil dark, as on the Telebirr payment board.
      style: DeliveryDetailScreen._primaryStyle.copyWith(
        backgroundColor: const WidgetStatePropertyAll(AppTheme.soilDark),
        foregroundColor: const WidgetStatePropertyAll(Colors.white),
      ),
    );
  }
}

/// Buyer's self-pickup "Scan farmer's QR" button, GATED on the escrow being funded.
///
/// Confirming self-pickup releases money, so the scan is disabled until this delivery's
/// escrow is `escrowed` locally, steering the buyer to pay first. Uses the same
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
      // Web (db is null) or no local record: fall back to enabled; the server
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
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          FilledButton.icon(
            onPressed: null,
            icon: const Icon(Icons.qr_code_scanner),
            label: Text(l10n.deliverySelfPickupConfirm),
            style: DeliveryDetailScreen._primaryStyle,
          ),
          FwActionNote(l10n.deliverySelfPickupPayFirst),
        ],
      );
    }

    return FilledButton.icon(
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
      style: DeliveryDetailScreen._primaryStyle,
    );
  }
}

/// A fact of a leg: a small icon, the label over the value.
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
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(top: 2),
          child: Icon(icon, size: 18, color: AppTheme.mutedSteel),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                label,
                style: const TextStyle(
                  fontSize: 12,
                  color: AppTheme.mutedSteel,
                ),
              ),
              Text(
                value,
                style: const TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.soilDark,
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
