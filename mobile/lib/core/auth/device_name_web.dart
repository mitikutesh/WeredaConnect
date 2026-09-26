import 'package:web/web.dart' as web;

import 'package:wereda_connect/core/auth/device_name_parser.dart';

/// Short browser description ("Chrome on Windows") parsed from the user agent.
/// Only the browser family and OS family are kept, never the full UA string.
String describeDevice() {
  String ua;
  try {
    ua = web.window.navigator.userAgent;
  } on Object {
    return 'Web browser';
  }
  return describeUserAgent(ua);
}
