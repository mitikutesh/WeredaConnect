import 'package:url_launcher/url_launcher.dart';

// Stub off the web; the browser reload only on web. Keeps dart:js_interop
// out of the mobile build.
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
  /// one is installed, else in the browser. The listing is not live yet
  /// (FUP-242), so the banner also says where else to get the app.
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

/// Web: reloads the page with the latest deployed app ([reloadLatestApp]);
/// false when it could not, and then nothing changed. Off the web it does
/// nothing and returns false.
Future<bool> reloadLatestWebApp() => impl.reloadLatestWebAppImpl();

/// What the browser offers [reloadLatestApp]. The web build implements it
/// with `package:web`; tests fake it.
abstract interface class AppReloadHost {
  /// What the browser says about the network (`navigator.onLine`).
  bool get online;

  /// Asks the service worker that controls the page, if there is one, for a
  /// newer app, and waits until a newer worker is active. True when there is
  /// no worker, nothing newer, or the newer one is active; false when the
  /// check failed (offline) or the newer worker did not finish in time.
  Future<bool> updateServiceWorker();

  /// Fetches `main.dart.js` past the browser cache; true on a 2xx answer.
  Future<bool> refetchAppCode();

  /// Reloads the page.
  void reload();
}

/// Reloads the page with the latest deployed web app, in two steps, because
/// the app's code can be held in two places:
///
/// - A Flutter caching service worker (the default of the Flutter that CI
///   builds the web app with) answers `main.dart.js` from its own cache,
///   whatever the page asks. Only a newer worker brings the new code: its
///   install downloads the app past the browser cache. So the page asks its
///   worker to update and waits until the newer one is active.
/// - The browser cache keeps `main.dart.js` for a year (`immutable` in
///   `deploy-webapp.yml`, and its name never changes). Fetching it with
///   `cache: 'reload'` stores the fresh copy for the reload to run.
///
/// Offline, or when either step fails, nothing is reloaded and nothing cached
/// is dropped, so the app keeps running and still starts offline: false.
Future<bool> reloadLatestApp(AppReloadHost host) async {
  if (!host.online) return false;
  if (!await host.updateServiceWorker()) return false;
  if (!await host.refetchAppCode()) return false;
  host.reload();
  return true;
}
