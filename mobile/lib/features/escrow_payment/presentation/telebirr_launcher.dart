import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Whether [uri] is Telebirr sending the buyer back to the app
/// (`weredaconnect://payment/complete`, the server's return URL).
bool isTelebirrReturnLink(Uri uri) =>
    uri.scheme == 'weredaconnect' && uri.host == 'payment';

/// Screens that refresh on their own when Telebirr sends the buyer back.
/// While one is open, the app-wide `PaymentReturnListener` leaves the link
/// to it.
class TelebirrReturnWatchers {
  TelebirrReturnWatchers._();

  static int _count = 0;

  /// True while a screen watches for the return link.
  static bool get any => _count > 0;

  static void add() => _count++;

  static void remove() {
    if (_count > 0) _count--;
  }
}

/// Opens a URL outside the app; true when something opened it.
typedef ExternalUrlLauncher = Future<bool> Function(Uri uri);

/// The default [ExternalUrlLauncher]: the Telebirr app or a browser.
Future<bool> launchExternally(Uri uri) =>
    launchUrl(uri, mode: LaunchMode.externalApplication);

/// Open a Telebirr H5 checkout [url] in the Telebirr app or a browser and
/// tell the buyer whether it opened. Used by the escrow status screen and
/// the order checkout (ESC-44). Returns whether it opened.
Future<bool> openTelebirrCheckout(
  BuildContext context,
  String? url, {
  ExternalUrlLauncher launcher = launchExternally,
}) async {
  if (url == null || url.isEmpty) return false;
  final messenger = ScaffoldMessenger.of(context);
  final l10n = AppLocalizations.of(context)!;
  final uri = Uri.tryParse(url);
  var launched = false;
  if (uri != null) {
    try {
      launched = await launcher(uri);
    } on Object {
      launched = false;
    }
  }
  if (!context.mounted) return launched;
  messenger.showSnackBar(
    SnackBar(
      content: Text(
        launched ? l10n.escrowPaymentOpening : l10n.escrowPaymentOpenFailed,
      ),
      backgroundColor: launched ? null : AppTheme.statusError,
    ),
  );
  return launched;
}
