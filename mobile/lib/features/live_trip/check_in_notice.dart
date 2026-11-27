import 'package:flutter/widgets.dart';

import 'package:wereda_connect/features/live_trip/domain/location_source.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// The Android notification shown while the trucker shares the trip's
/// location, in the app's language (LT-08). Read when the foreground service
/// starts, outside any widget.
BackgroundNotice checkInNoticeFor(Locale locale) {
  final l10n = lookupAppLocalizations(locale);
  return (
    title: l10n.liveTripNotificationTitle,
    text: l10n.liveTripNotificationText,
    channel: l10n.liveTripNotificationChannel,
  );
}
