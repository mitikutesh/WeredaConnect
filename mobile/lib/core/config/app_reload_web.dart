import 'dart:async';
import 'dart:js_interop';
import 'dart:js_interop_unsafe';

import 'package:web/web.dart' as web;
import 'package:wereda_connect/core/config/app_update.dart';

/// Web: [reloadLatestApp] with the browser's own APIs.
Future<bool> reloadLatestWebAppImpl() => reloadLatestApp(_BrowserReloadHost());

class _BrowserReloadHost implements AppReloadHost {
  /// How long the service worker may take to fetch its script.
  static const _updateTimeout = Duration(seconds: 30);

  /// How long a newer worker may take to install (it downloads the app's
  /// code, several megabytes on 3G) and activate.
  static const _activateTimeout = Duration(minutes: 3);

  @override
  bool get online => web.window.navigator.onLine;

  @override
  Future<bool> updateServiceWorker() async {
    try {
      final navigator = web.window.navigator;
      // Absent off https (and in some private windows): no worker to update.
      if (!navigator.has('serviceWorker')) return true;
      final registration =
          await navigator.serviceWorker.getRegistration().toDart;
      if (registration == null) return true;
      // Rejects when the worker's script cannot be fetched (offline).
      await registration.update().toDart.timeout(_updateTimeout);
      final next = registration.installing ?? registration.waiting;
      if (next == null) return true;
      final deadline = DateTime.now().add(_activateTimeout);
      while (next.state != 'activated') {
        if (next.state == 'redundant' || DateTime.now().isAfter(deadline)) {
          return false;
        }
        await Future<void>.delayed(const Duration(milliseconds: 250));
      }
      return true;
    } on Object {
      return false;
    }
  }

  @override
  Future<bool> refetchAppCode() async {
    try {
      // Relative to the page's base href, as the Flutter loader reads it.
      final response = await web.window
          .fetch('main.dart.js'.toJS, web.RequestInit(cache: 'reload'))
          .toDart;
      return response.ok;
    } on Object {
      return false;
    }
  }

  @override
  void reload() => web.window.location.reload();
}
