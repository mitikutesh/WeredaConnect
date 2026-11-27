import 'package:flutter/material.dart';

import 'package:wereda_connect/features/buyer/presentation/screens/find_crops_screen.dart';

/// The buyer's Market tab (Fieldway Canvas: Market.dc.html): the Find Crops
/// screen, Browse over every farmer's listing and My Requests. The board has
/// no transport segment; every trucker's trips stay one tap away on Home
/// (Find trips, `/trips`) and from a crop's suggested transport.
///
/// The buyer-sees-all visibility rule is enforced by the embedded browse
/// screen's own role-aware queries; this tab only composes it.
class MarketScreen extends StatelessWidget {
  const MarketScreen({super.key});

  @override
  Widget build(BuildContext context) => const FindCropsScreen(embedded: true);
}
