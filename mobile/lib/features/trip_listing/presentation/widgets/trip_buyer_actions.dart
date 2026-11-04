import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_departure.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// B-2: the buyer's action on someone else's trip, pinned at the foot of
/// the trip detail: Find crops on this route opens the market at the place
/// the truck starts from with this truck chosen, so opening a listing and
/// tapping Place Order + Transport books it. Only for a buyer (the trip
/// detail decides); a farmer or another trucker gets no action.
///
/// A truck that cannot take an order (no longer open, its day over, full)
/// says why instead of opening the market. Works offline on the cached
/// listings.
class TripBuyerActions extends StatelessWidget {
  final Trip trip;

  const TripBuyerActions({required this.trip, super.key});

  void _findCrops(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final problem = tripBookingProblem(trip, now: DateTime.now());
    if (problem != null) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(tripBookingProblemMessage(problem, l10n))),
      );
      return;
    }
    // A bloc for this page only: its crop chips and searches must not change
    // the Market tab's list behind it.
    final repository = context.read<CropListingBloc>().repository;
    MarketplaceSync? sync;
    try {
      sync = context.read<MarketplaceSync?>();
    } on ProviderNotFoundException {
      sync = null;
    }
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => BlocProvider(
          create: (_) =>
              CropListingBloc(repository: repository, marketplaceSync: sync),
          child: BrowseListingsScreen(originTrip: trip),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return FwBottomActions(
      children: [
        FilledButton.icon(
          key: const Key('trip_detail_find_crops'),
          onPressed: () => _findCrops(context),
          icon: const Icon(Icons.eco_outlined),
          label: Text(l10n.tripFindCrops),
          style: FilledButton.styleFrom(
            minimumSize: const Size.fromHeight(56),
            textStyle: const TextStyle(
              fontFamily: AppTheme.fontFamily,
              fontFamilyFallback: AppTheme.ethiopicFontFallback,
              fontSize: 16,
              fontWeight: FontWeight.w800,
            ),
          ),
        ),
        FwActionNote(l10n.tripFindCropsNote(trip.fromWoreda)),
      ],
    );
  }
}
