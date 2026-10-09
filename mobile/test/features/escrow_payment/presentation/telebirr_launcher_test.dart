import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/escrow_payment/presentation/telebirr_launcher.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// The Telebirr launch shared by the escrow status screen and the order
/// checkout (ESC-44).
void main() {
  late List<Uri> launched;

  Future<BuildContext> pump(WidgetTester tester) async {
    late BuildContext captured;
    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: Scaffold(
          body: Builder(
            builder: (context) {
              captured = context;
              return const SizedBox.shrink();
            },
          ),
        ),
      ),
    );
    return captured;
  }

  setUp(() => launched = []);

  testWidgets('opens the page and says Telebirr is opening', (tester) async {
    final context = await pump(tester);

    final ok = await openTelebirrCheckout(
      context,
      'https://pay.example/checkout?prepay_id=P1',
      launcher: (uri) async {
        launched.add(uri);
        return true;
      },
    );
    await tester.pump();

    expect(ok, isTrue);
    expect(launched.single.queryParameters['prepay_id'], 'P1');
    expect(find.text('Opening Telebirr…'), findsOneWidget);
  });

  testWidgets('nothing opened it: says so', (tester) async {
    final context = await pump(tester);

    final ok = await openTelebirrCheckout(
      context,
      'https://pay.example/x',
      launcher: (_) async => false,
    );
    await tester.pump();

    expect(ok, isFalse);
    expect(
      find.textContaining('Could not open the payment page'),
      findsOneWidget,
    );
  });

  testWidgets('a launcher that throws counts as not opened', (tester) async {
    final context = await pump(tester);

    final ok = await openTelebirrCheckout(
      context,
      'https://pay.example/x',
      launcher: (_) async => throw StateError('no browser'),
    );
    await tester.pump();

    expect(ok, isFalse);
    expect(
      find.textContaining('Could not open the payment page'),
      findsOneWidget,
    );
  });

  testWidgets('no URL: nothing is launched', (tester) async {
    final context = await pump(tester);

    final ok = await openTelebirrCheckout(
      context,
      null,
      launcher: (uri) async {
        launched.add(uri);
        return true;
      },
    );

    expect(ok, isFalse);
    expect(launched, isEmpty);
  });

  test('isTelebirrReturnLink matches only the payment return', () {
    expect(
      isTelebirrReturnLink(Uri.parse('weredaconnect://payment/complete')),
      isTrue,
    );
    expect(
      isTelebirrReturnLink(Uri.parse('weredaconnect://fayda-callback?code=x')),
      isFalse,
    );
    expect(
      isTelebirrReturnLink(Uri.parse('https://payment.example/complete')),
      isFalse,
    );
  });
}
