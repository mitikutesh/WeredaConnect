import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/screens/settings_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

/// LT-10: the live trip map draws GeoNames places, so the About dialog
/// carries the credit the CC BY 4.0 licence asks for, in every language.
void main() {
  setUp(() => SharedPreferences.setMockInitialValues({}));

  for (final code in ['en', 'am', 'om']) {
    testWidgets('$code: About shows the GeoNames credit', (tester) async {
      tester.view.physicalSize = const Size(420, 900);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final language = LanguageProvider();
      await language.setLocale(Locale(code));
      await tester.pumpWidget(
        MultiProvider(
          providers: [
            ChangeNotifierProvider.value(value: language),
            ChangeNotifierProvider.value(value: SettingsProvider()),
          ],
          child: MaterialApp(
            locale: Locale(code),
            localizationsDelegates: const [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              GlobalWidgetsLocalizations.delegate,
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: AppLocalizations.supportedLocales,
            home: const SettingsScreen(),
          ),
        ),
      );
      await tester.pumpAndSettle();
      final l10n = lookupAppLocalizations(Locale(code));

      await tester.tap(find.text(l10n.about));
      await tester.pumpAndSettle();

      final credit = tester.widget<Text>(
        find.byKey(const Key('about_map_data_credit')),
      );
      expect(credit.data, l10n.aboutMapDataCredit(PlaceRepository.attribution));
      expect(credit.data, endsWith('© GeoNames (CC BY 4.0)'));
    });
  }
}
