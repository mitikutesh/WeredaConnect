import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/screens/splash_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';

/// BUG-40: the first screen of a first launch was English only, with no way
/// to change the language before "Get Started". It now offers the three
/// languages, each named in its own script.
void main() {
  late LanguageProvider languages;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    languages = LanguageProvider();
  });

  Widget app({VoidCallback? onGetStarted}) =>
      ChangeNotifierProvider<LanguageProvider>.value(
        value: languages,
        child: Consumer<LanguageProvider>(
          builder: (context, provider, _) => MaterialApp(
            locale: provider.locale,
            localizationsDelegates: const [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              GlobalWidgetsLocalizations.delegate,
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: AppLocalizations.supportedLocales,
            home: SplashScreen(onGetStarted: onGetStarted ?? () {}),
          ),
        ),
      );

  testWidgets('offers the three languages, each in its own script',
      (tester) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();

    expect(find.text('English'), findsOneWidget);
    expect(find.text('አማርኛ'), findsOneWidget);
    expect(find.text('Afaan Oromoo'), findsOneWidget);
    // Big enough to tap.
    for (final code in ['en', 'am', 'om']) {
      final size = tester.getSize(find.byKey(Key('splash_language_$code')));
      expect(size.height, greaterThanOrEqualTo(48));
    }
  });

  testWidgets('a pick switches this screen at once and is remembered',
      (tester) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();
    expect(find.text('Get Started'), findsOneWidget);

    await tester.tap(find.byKey(const Key('splash_language_am')));
    await tester.pumpAndSettle();

    expect(languages.locale, const Locale('am'));
    expect(find.text('ጀምር'), findsOneWidget);
    expect(find.text('Get Started'), findsNothing);

    await tester.tap(find.byKey(const Key('splash_language_om')));
    await tester.pumpAndSettle();
    expect(find.text('Jalqabi'), findsOneWidget);

    // Kept as this device's language for the next start.
    final restored = LanguageProvider();
    await tester.runAsync(restored.initialize);
    expect(restored.locale, const Locale('om'));
  });

  testWidgets('Get Started still works after a pick', (tester) async {
    var started = 0;
    await tester.pumpWidget(app(onGetStarted: () => started++));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('splash_language_am')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('ጀምር'));
    await tester.pump();

    expect(started, 1);
  });

  testWidgets('without a language provider the screen still shows',
      (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: SplashScreen(onGetStarted: () {}),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Get Started'), findsOneWidget);
    expect(find.byKey(const Key('splash_language_en')), findsNothing);
  });
}
