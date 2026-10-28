import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';

/// LT-03: screens read times through [clockFormatOf] and show deadlines with
/// [ClockText], so they follow the language and the Clock setting.
void main() {
  // 10:20 UTC is 13:20 in Ethiopia.
  final instant = DateTime.utc(2026, 10, 28, 10, 20);

  setUp(() => SharedPreferences.setMockInitialValues({}));

  Widget app(String code, Widget child, {SettingsProvider? settings}) {
    final material = MaterialApp(
      locale: Locale(code),
      localizationsDelegates: const [
        AppLocalizations.delegate,
        FallbackMaterialLocalizationsDelegate(),
        GlobalWidgetsLocalizations.delegate,
        FallbackCupertinoLocalizationsDelegate(),
      ],
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(body: child),
    );
    return settings == null
        ? material
        : ChangeNotifierProvider.value(value: settings, child: material);
  }

  Widget timeText() => Builder(
        builder: (context) => Text(
          clockFormatOf(context).time(instant),
          key: const Key('time'),
        ),
      );

  String shown(WidgetTester tester) =>
      tester.widget<Text>(find.byKey(const Key('time'))).data!;

  testWidgets('without settings above, the clock follows the language',
      (tester) async {
    await tester.pumpWidget(app('om', timeText()));
    await tester.pumpAndSettle();
    expect(shown(tester), '7:20 waaree booda');
    await tester.pumpWidget(app('en', timeText()));
    await tester.pumpAndSettle();
    expect(shown(tester), '13:20');
  });

  testWidgets('a screen follows a change of the Clock setting', (tester) async {
    final settings = SettingsProvider();
    await tester.pumpWidget(app('am', timeText(), settings: settings));
    await tester.pumpAndSettle();
    expect(shown(tester), 'ከቀኑ 7:20');

    await settings.setClockPreference(ClockPreference.international);
    await tester.pump();
    expect(shown(tester), '13:20');

    await settings.setClockPreference(ClockPreference.ethiopian);
    await tester.pump();
    expect(shown(tester), 'ከቀኑ 7:20');
  });

  testWidgets('a deadline in Amharic carries the 24-hour time in small text',
      (tester) async {
    const style = TextStyle(fontSize: 20);
    await tester.pumpWidget(
      app(
        'am',
        Builder(
          builder: (context) => ClockText(
            clockFormatOf(context).deadline(
              instant,
              sentence: AppLocalizations.of(context)!.commitmentAnswerBy,
            ),
            style: style,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    final am = lookupAppLocalizations(const Locale('am'));
    final whole = am.commitmentAnswerBy('ከቀኑ 7:20');
    expect(
      find.text(whole.replaceFirst('ከቀኑ 7:20', 'ከቀኑ 7:20 (13:20)')),
      findsOneWidget,
    );
    final text = tester.widget<Text>(find.byType(Text));
    final companion = (text.textSpan! as TextSpan)
        .children!
        .cast<TextSpan>()
        .singleWhere((s) => s.text == ' (13:20)');
    expect(companion.style!.fontSize, 20 * ClockText.smallScale);
  });

  testWidgets('an English deadline is the 24-hour time with no companion',
      (tester) async {
    await tester.pumpWidget(
      app(
        'en',
        Builder(
          builder: (context) => ClockText(
            clockFormatOf(context).deadline(
              instant,
              sentence: AppLocalizations.of(context)!.otpExpiresAt,
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    final en = lookupAppLocalizations(const Locale('en'));
    expect(find.text(en.otpExpiresAt('13:20')), findsOneWidget);
  });
}
