import 'package:url_launcher/url_launcher.dart';

// Stub off the web; the cache-skipping reload only on web. Keeps
// dart:js_interop out of the mobile build.
import 'package:wereda_connect/core/config/app_reload_stub.dart'
    if (dart.library.html) 'package:wereda_connect/core/config/app_reload_web.dart'
    as impl;

/// Where this app is updated from (FUP-75).
class AppStore {
  AppStore._();

  /// The Android application id (`applicationId` in
  /// `android/app/build.gradle`).
  static const String androidPackageId = 'et.wereda.connect';

  /// The app's Play Store page. Android opens it in the Play Store app when
  /// one is installed, else in the browser.
  static final Uri playStorePage = Uri.https(
    'play.google.com',
    '/store/apps/details',
    {'id': androidPackageId},
  );
}

/// Opens [AppStore.playStorePage] outside the app; true when something
/// opened it.
Future<bool> openAppStorePage() async {
  try {
    return await launchUrl(
      AppStore.playStorePage,
      mode: LaunchMode.externalApplication,
    );
  } on Object {
    return false;
  }
}

/// Web: reloads the page with the latest deployed app, past the browser's
/// cached copy of the app's code. Off the web it does nothing and returns
/// false.
Future<bool> reloadLatestWebApp() => impl.reloadLatestWebAppImpl();
