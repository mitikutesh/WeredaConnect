import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/farmer_trip_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/live_trip_screen.dart';

/// Opens the live trip of [delivery] for [userId] (LT-11): the buyer's screen
/// or the farmer's. Does nothing for anyone else: the trucker has their own
/// trip mode and never gets the buyer's map.
Future<void> openLiveTrip(
  BuildContext context,
  Delivery delivery,
  String userId,
) async {
  final Widget screen;
  switch (liveTripViewerOf(delivery, userId)) {
    case LiveTripViewer.buyer:
      screen = LiveTripScreen(delivery: delivery, currentUserId: userId);
    case LiveTripViewer.farmer:
      screen = FarmerTripScreen(delivery: delivery, currentUserId: userId);
    case null:
      return;
  }
  await Navigator.of(context).push(
    MaterialPageRoute<void>(builder: (_) => screen),
  );
}

/// The live trip body of [delivery] for [userId], for a pane beside a list
/// (web, wide screens), or null when the viewer has none.
Widget? liveTripPane(Delivery delivery, String userId) =>
    switch (liveTripViewerOf(delivery, userId)) {
      LiveTripViewer.buyer => LiveTripView(
          key: ValueKey('live-${delivery.deliveryId}'),
          delivery: delivery,
          currentUserId: userId,
        ),
      LiveTripViewer.farmer => FarmerTripView(
          key: ValueKey('farmer-${delivery.deliveryId}'),
          delivery: delivery,
          currentUserId: userId,
        ),
      null => null,
    };
