import 'package:app_links/app_links.dart';

/// Mobile / Dart VM: the deep link arrives through app_links.
Stream<Uri> faydaCallbackLinksImpl() => AppLinks().uriLinkStream;

/// No web callback page off the web; the deep link is used.
String? webFaydaRedirectUriImpl() => null;
