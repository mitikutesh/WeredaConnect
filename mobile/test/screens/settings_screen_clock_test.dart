import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/screens/settings_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';

/// LT-03: Settings > Clock (Auto, Ethiopian, International). Auto follows
/// the language; a pick applies at once and is saved.
void main() {
  late SettingsProvider settings;
  late LanguageProvider language;

  setUp(() async {
    SharedPreferences.setMockInitialValues({});
    settings = SettingsProvider();
    language = LanguageProvider();
  });

  Future<AppLocalizations> pump(WidgetTester tester, String code) async {
    tester.view.physicalSize = const Size(420, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await language.setLocale(Locale(code));
    await tester.pumpWidget(
      MultiProvider(
        providers: [
          ChangeNotifierProvider.value(value: language),
          ChangeNotifierProvider.value(value: settings),
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
    return lookupAppLocalizations(Locale(code));
  }

  Text subtitle(WidgetTester tester) {
    final tile = tester.widget<ListTile>(
      find.byKey(const Key('settings_clock_tile')),
    );
    return tile.subtitle! as Text;
  }

  testWidgets('English: Auto shows the 24-hour clock', (tester) async {
    final l10n = await pump(tester, 'en');
    expect(find.text(l10n.clockSetting), findsOneWidget);
    expect(subtitle(tester).data, '${l10n.clockAuto} · 13:20');
  });

  testWidgets('Amharic: Auto shows the Ethiopian clock', (tester) async {
    final l10n = await pump(tester, 'am');
    expect(find.text(l10n.clockSetting), findsOneWidget);
    expect(subtitle(tester).data, '${l10n.clockAuto} · ከቀኑ 7:20');
  });

  testWidgets('the dialog lists the three clocks with an example',
      (tester) async {
    final l10n = await pump(tester, 'om');
    await tester.tap(find.byKey(const Key('settings_clock_tile')));
    await tester.pumpAndSettle();

    expect(find.byType(AlertDialog), findsOneWidget);
    for (final p in ClockPreference.values) {
      expect(find.byKey(Key('clock_option_${p.name}')), findsOneWidget);
    }
    expect(find.text(l10n.clockAutoHint), findsOneWidget);
    expect(find.text('7:20 waaree booda'), findsWidgets);
    expect(find.text('13:20'), findsOneWidget);
  });

  testWidgets('English: picking Ethiopian applies at once and is saved',
      (tester) async {
    final l10n = await pump(tester, 'en');
    await tester.tap(find.byKey(const Key('settings_clock_tile')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('clock_option_ethiopian')));
    await tester.pumpAndSettle();

    expect(find.byType(AlertDialog), findsNothing);
    expect(settings.clockPreference, ClockPreference.ethiopian);
    final prefs = await SharedPreferences.getInstance();
    expect(prefs.getString('clock_preference'), 'ethiopian');
    expect(
      subtitle(tester).data,
      '${l10n.clockEthiopian} · 7:20 in the afternoon',
    );
  });

  testWidgets('Amharic: picking International shows the 24-hour clock',
      (tester) async {
    final l10n = await pump(tester, 'am');
    await tester.tap(find.byKey(const Key('settings_clock_tile')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('clock_option_international')));
    await tester.pumpAndSettle();

    expect(settings.clockPreference, ClockPreference.international);
    expect(subtitle(tester).data, '${l10n.clockInternational} · 13:20');
  });

  testWidgets('Cancel keeps the clock', (tester) async {
    final l10n = await pump(tester, 'en');
    await tester.tap(find.byKey(const Key('settings_clock_tile')));
    await tester.pumpAndSettle();
    await tester.tap(find.text(l10n.cancel));
    await tester.pumpAndSettle();

    expect(find.byType(AlertDialog), findsNothing);
    expect(settings.clockPreference, ClockPreference.auto);
  });
}
