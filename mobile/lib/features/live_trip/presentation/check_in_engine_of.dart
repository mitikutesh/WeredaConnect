import 'package:flutter/widgets.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';

/// The app's check-in engine (LT-08): both entry points provide it as
/// `Provider<CheckInEngine?>`. Null when there is none above [context] (a
/// bare widget-test harness), so a screen can drop its live trip parts.
CheckInEngine? checkInEngineOf(BuildContext context) {
  try {
    return Provider.of<CheckInEngine?>(context, listen: false);
  } on ProviderNotFoundException {
    return null;
  }
}
