import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/contact_actions_sheet.dart';

/// The contact sheet in the boards' language: who it is, the number on a
/// sand tile, Call as the black pill and Message as the outlined one.
void main() {
  Future<void> open(
    WidgetTester tester, {
    String? phone,
    VoidCallback? onMessage,
    Locale locale = const Locale('en'),
    Size size = const Size(390, 844),
    double textScale = 1,
  }) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    tester.platformDispatcher.textScaleFactorTestValue = textScale;
    addTearDown(tester.view.reset);
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        locale: locale,
        // The app's delegates: Material has no Afaan Oromoo of its own.
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          GlobalWidgetsLocalizations.delegate,
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () => showContactActionsSheet(
                context,
                name: 'Dawit Tesfaye',
                roleLabel: 'Trucker',
                phone: phone,
                onMessage: onMessage,
              ),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    );
    // The fallback delegates load asynchronously.
    await tester.pumpAndSettle();
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
  }

  testWidgets('name, initials and role; Call is black, Message outlined',
      (tester) async {
    var messaged = 0;
    await open(tester, phone: ' +251911000002 ', onMessage: () => messaged++);

    expect(find.text('Dawit Tesfaye'), findsOneWidget);
    expect(find.text('DT'), findsOneWidget);
    expect(find.text('Trucker'), findsOneWidget);
    expect(find.text('+251911000002'), findsOneWidget);

    final call = tester.widget<FilledButton>(
      find.ancestor(
        of: find.text('Call'),
        matching: find.bySubtype<FilledButton>(),
      ),
    );
    expect(
      call.style?.backgroundColor?.resolve(const {}),
      AppTheme.ink,
    );
    expect(call.style?.foregroundColor?.resolve(const {}), Colors.white);

    final message = find.ancestor(
      of: find.text('Send a message'),
      matching: find.bySubtype<OutlinedButton>(),
    );
    expect(message, findsOneWidget);
    await tester.tap(message);
    await tester.pumpAndSettle();
    expect(messaged, 1);
    expect(find.text('Dawit Tesfaye'), findsNothing);
  });

  testWidgets('no number: says so, no Call, Close still closes',
      (tester) async {
    await open(tester);

    expect(find.text('Phone number not available'), findsOneWidget);
    expect(find.text('Call'), findsNothing);
    expect(find.byIcon(Icons.copy), findsNothing);

    await tester.tap(find.text('Close'));
    await tester.pumpAndSettle();
    expect(find.text('Dawit Tesfaye'), findsNothing);
  });

  for (final code in ['am', 'om']) {
    testWidgets('$code, 320 px at 2x text: nothing overflows', (tester) async {
      await open(
        tester,
        phone: '+251911000002',
        onMessage: () {},
        locale: Locale(code),
        size: const Size(320, 640),
        textScale: 2,
      );
      expect(tester.takeException(), isNull);
    });
  }
}
