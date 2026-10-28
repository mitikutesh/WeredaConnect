import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/rating/presentation/widgets/rate_counterparty_dialog.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Buyer-only section (delivered, disbursing or settled orders) to rate the
/// farmer and, when the order was transported, the trucker. Tracks which
/// counterparties have already been rated (local check) and hides them once
/// done. Shown on the order detail and on the live trip's delivered summary
/// (LT-11), so it takes the order's fields rather than the order: the
/// delivery carries the same ids and names.
class RateCounterpartiesSection extends StatefulWidget {
  final String orderId;
  final String buyerId;
  final String farmerId;
  final String farmerName;
  final String? truckerId;
  final String? truckerName;

  const RateCounterpartiesSection({
    required this.orderId,
    required this.buyerId,
    required this.farmerId,
    required this.farmerName,
    this.truckerId,
    this.truckerName,
    super.key,
  });

  @override
  State<RateCounterpartiesSection> createState() =>
      _RateCounterpartiesSectionState();
}

class _RateCounterpartiesSectionState extends State<RateCounterpartiesSection> {
  bool _farmerRated = false;
  bool _truckerRated = false;
  bool _loading = true;

  bool get _hasTrucker =>
      widget.truckerId != null && widget.truckerId!.isNotEmpty;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final repo = context.read<RatingBloc>().repository;
    final farmerRated = widget.farmerId.isEmpty ||
        await repo.hasRated(
          orderId: widget.orderId,
          rateeId: widget.farmerId,
        );
    var truckerRated = true;
    if (_hasTrucker) {
      truckerRated = await repo.hasRated(
        orderId: widget.orderId,
        rateeId: widget.truckerId!,
      );
    }
    if (!mounted) return;
    setState(() {
      _farmerRated = farmerRated;
      _truckerRated = truckerRated;
      _loading = false;
    });
  }

  Future<void> _rate(String rateeId, String role, String name) async {
    final result = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => BlocProvider.value(
        value: context.read<RatingBloc>(),
        child: RateCounterpartyDialog(
          orderId: widget.orderId,
          raterId: widget.buyerId,
          rateeId: rateeId,
          rateeRole: role,
          rateeName: name,
        ),
      ),
    );
    if (result == true) await _load();
  }

  @override
  Widget build(BuildContext context) {
    if (_loading) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    // Everything already rated → confirmation line.
    if (_farmerRated && (!_hasTrucker || _truckerRated)) {
      return Row(
        children: [
          const Icon(
            Icons.check_circle,
            color: AppTheme.statusSuccess,
            size: 18,
          ),
          const SizedBox(width: 8),
          Text(l10n.alreadyRated, style: theme.textTheme.bodyMedium),
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // FUP-105: no Rate button for a party who deleted their account; the
        // server refuses the rating and the name reads as the sentinel.
        if (!_farmerRated &&
            widget.farmerId.isNotEmpty &&
            widget.farmerName != deletedUserSentinel) ...[
          OutlinedButton.icon(
            onPressed: () => _rate(
              widget.farmerId,
              'farmer',
              partyDisplayName(widget.farmerName, l10n),
            ),
            icon: const Icon(Icons.star_outline),
            label: Text(l10n.rateFarmer),
            style: OutlinedButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
          const SizedBox(height: 12),
        ],
        if (_hasTrucker &&
            !_truckerRated &&
            widget.truckerName != deletedUserSentinel) ...[
          OutlinedButton.icon(
            onPressed: () => _rate(
              widget.truckerId!,
              'trucker',
              partyDisplayName(widget.truckerName, l10n),
            ),
            icon: const Icon(Icons.star_outline),
            label: Text(l10n.rateTrucker),
            style: OutlinedButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
          const SizedBox(height: 12),
        ],
      ],
    );
  }
}
