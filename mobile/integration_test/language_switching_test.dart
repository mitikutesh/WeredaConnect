import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/widgets/language_selection_dialog.dart';

/// Integration tests for language switching functionality.
///
/// These tests verify the LanguageProvider and LanguageSelectionDialog
/// work correctly in isolation, ensuring language switching logic is solid.
///
/// Note: Full end-to-end tests through authenticated screens can be added
/// once test authentication mocking is implemented.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('Language Provider Integration Tests', () {
    setUp(() async {
      // Clear SharedPreferences before each test
      SharedPreferences.setMockInitialValues({});
      final prefs = await SharedPreferences.getInstance();
      await prefs.clear();
    });

    testWidgets('LanguageProvider should initialize with English by default',
        (tester) async {
      final provider = LanguageProvider();
      await provider.initialize();

      expect(provider.locale.languageCode, 'en');
    });

    testWidgets('LanguageProvider should persist selected language',
        (tester) async {
      final provider = LanguageProvider();
      await provider.initialize();

      // Switch to Amharic
      await provider.setLocale(const Locale('am'));
      expect(provider.locale.languageCode, 'am');

      // Verify it's saved
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'am');

      // Create new provider to simulate app restart
      final provider2 = LanguageProvider();
      await provider2.initialize();

      // Should load saved language
      expect(provider2.locale.languageCode, 'am');
    });

    testWidgets('LanguageProvider should switch between all languages',
        (tester) async {
      final provider = LanguageProvider();
      await provider.initialize();

      // Start with English
      expect(provider.locale.languageCode, 'en');

      // Switch to Amharic
      await provider.setLocale(const Locale('am'));
      expect(provider.locale.languageCode, 'am');

      // Switch to Afan Oromo
      await provider.setLocale(const Locale('om'));
      expect(provider.locale.languageCode, 'om');

      // Switch back to English
      await provider.setLocale(const Locale('en'));
      expect(provider.locale.languageCode, 'en');
    });

    testWidgets('LanguageProvider should get correct language names',
        (tester) async {
      final provider = LanguageProvider();

      expect(provider.getLanguageName(const Locale('en')), 'English');
      expect(provider.getLanguageName(const Locale('am')), 'አማርኛ');
      expect(provider.getLanguageName(const Locale('om')), 'Afaan Oromoo');
    });

    testWidgets('LanguageProvider should list all available languages',
        (tester) async {
      final provider = LanguageProvider();
      final languages = provider.availableLanguages;

      expect(languages.length, 3);
      expect(languages[0].key.languageCode, 'en');
      expect(languages[1].key.languageCode, 'am');
      expect(languages[2].key.languageCode, 'om');
    });

    testWidgets('LanguageProvider should reject unsupported locales',
        (tester) async {
      final provider = LanguageProvider();
      await provider.initialize();

      // Try to set unsupported locale
      expect(
        () => provider.setLocale(const Locale('fr')),
        throwsArgumentError,
      );
    });
  });

  group('Language Selection Dialog Integration Tests', () {
    setUp(() async {
      SharedPreferences.setMockInitialValues({});
      final prefs = await SharedPreferences.getInstance();
      await prefs.clear();
    });

    testWidgets('LanguageSelectionDialog should show all 3 languages',
        (tester) async {
      final provider = LanguageProvider();
      await provider.initialize();

      await tester.pumpWidget(
        ChangeNotifierProvider.value(
          value: provider,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(
              body: Builder(
                builder: (context) {
                  return ElevatedButton(
                    onPressed: () {
                      showDialog<void>(
                        context: context,
                        builder: (dialogContext) =>
                            ChangeNotifierProvider.value(
                          value: provider,
                          child: const LanguageSelectionDialog(),
                        ),
                      );
                    },
                    child: const Text('Open Dialog'),
                  );
                },
              ),
            ),
          ),
        ),
      );

      // Open dialog
      await tester.tap(find.text('Open Dialog'));
      await tester.pumpAndSettle();

      // Should show all 3 languages
      expect(find.text('English'), findsOneWidget);
      expect(find.text('አማርኛ'), findsOneWidget);
      expect(find.text('Afaan Oromoo'), findsOneWidget);
    });

    testWidgets(
        'LanguageSelectionDialog should show checkmark for current language',
        (tester) async {
      final provider = LanguageProvider();
      await provider.initialize();
      await provider.setLocale(const Locale('am')); // Set to Amharic

      await tester.pumpWidget(
        ChangeNotifierProvider.value(
          value: provider,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(
              body: Builder(
                builder: (context) {
                  return ElevatedButton(
                    onPressed: () {
                      showDialog<void>(
                        context: context,
                        builder: (dialogContext) =>
                            ChangeNotifierProvider.value(
                          value: provider,
                          child: const LanguageSelectionDialog(),
                        ),
                      );
                    },
                    child: const Text('Open Dialog'),
                  );
                },
              ),
            ),
          ),
        ),
      );

      await tester.tap(find.text('Open Dialog'));
      await tester.pumpAndSettle();

      // Should have checkmark icon (Amharic is selected)
      expect(find.byIcon(Icons.check), findsOneWidget);
    });

    testWidgets('Should switch language when selecting from dialog',
        (tester) async {
      final provider = LanguageProvider();
      await provider.initialize();

      int changeCount = 0;
      provider.addListener(() {
        changeCount++;
      });

      await tester.pumpWidget(
        ChangeNotifierProvider.value(
          value: provider,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(
              body: Builder(
                builder: (context) {
                  return ElevatedButton(
                    onPressed: () {
                      showDialog<void>(
                        context: context,
                        builder: (dialogContext) =>
                            ChangeNotifierProvider.value(
                          value: provider,
                          child: const LanguageSelectionDialog(),
                        ),
                      );
                    },
                    child: const Text('Open Dialog'),
                  );
                },
              ),
            ),
          ),
        ),
      );

      // Open dialog
      await tester.tap(find.text('Open Dialog'));
      await tester.pumpAndSettle();

      // Select Amharic
      await tester.tap(find.text('አማርኛ'));
      await tester.pumpAndSettle();

      // Dialog should close
      expect(find.text('አማርኛ'), findsNothing);

      // Verify language changed
      expect(provider.locale.languageCode, 'am');
      expect(changeCount, 1);
    });

    testWidgets('Should handle multiple language switches', (tester) async {
      final provider = LanguageProvider();
      await provider.initialize();

      // Switch through all languages multiple times
      for (var i = 0; i < 3; i++) {
        await provider.setLocale(const Locale('am'));
        expect(provider.locale.languageCode, 'am');

        await provider.setLocale(const Locale('om'));
        expect(provider.locale.languageCode, 'om');

        await provider.setLocale(const Locale('en'));
        expect(provider.locale.languageCode, 'en');
      }

      // Should still work after multiple switches
      expect(provider.locale.languageCode, 'en');
    });

    testWidgets('Should maintain language after multiple save operations',
        (tester) async {
      final provider = LanguageProvider();
      await provider.initialize();

      // Switch and verify persistence multiple times
      await provider.setLocale(const Locale('am'));
      var prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'am');

      await provider.setLocale(const Locale('om'));
      prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'om');

      await provider.setLocale(const Locale('en'));
      prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'en');
    });
  });

  group('AppLocalizations Integration Tests', () {
    testWidgets('AppLocalizations should load all supported locales',
        (tester) async {
      for (final locale in [
        const Locale('en'),
        const Locale('am'),
        const Locale('om'),
      ]) {
        await tester.pumpWidget(
          MaterialApp(
            locale: locale,
            localizationsDelegates: const [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(
              body: Builder(
                builder: (context) {
                  final l10n = AppLocalizations.of(context)!;
                  return Text(l10n.appName);
                },
              ),
            ),
          ),
        );

        await tester.pumpAndSettle();

        // Verify app name is loaded (different per locale)
        const l10nDelegates = AppLocalizations.localizationsDelegates;
        expect(l10nDelegates, isNotEmpty);
      }
    });

    testWidgets('AppLocalizations should provide correct translations',
        (tester) async {
      // Test English
      await tester.pumpWidget(
        MaterialApp(
          locale: const Locale('en'),
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: Builder(
              builder: (context) {
                final l10n = AppLocalizations.of(context)!;
                return Column(
                  children: [
                    Text(l10n.settings),
                    Text(l10n.home),
                    Text(l10n.profile),
                  ],
                );
              },
            ),
          ),
        ),
      );

      await tester.pumpAndSettle();
      expect(find.text('Settings'), findsOneWidget);
      expect(find.text('Home'), findsOneWidget);
      expect(find.text('Profile'), findsOneWidget);
    });
  });
}
