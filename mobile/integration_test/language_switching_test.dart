import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/main.dart' as app;
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Integration tests for language switching functionality.
///
/// Tests verify:
/// - Language can be changed from Settings
/// - UI updates immediately after language change
/// - Language preference persists across app restarts
/// - All supported languages work correctly
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('Language Switching Integration Tests', () {
    setUp(() async {
      // Clear SharedPreferences before each test
      SharedPreferences.setMockInitialValues({});
      final prefs = await SharedPreferences.getInstance();
      await prefs.clear();
    });

    testWidgets('should start with English by default', (tester) async {
      await app.main();
      await tester.pumpAndSettle();

      // App should start in English
      expect(find.text('Settings'), findsOneWidget);
      expect(find.text('ቅንብሮች'), findsNothing); // Amharic
      expect(find.text('Qindaa\'ina'), findsNothing); // Afan Oromo
    });

    testWidgets('should switch from English to Amharic', (tester) async {
      await app.main();
      await tester.pumpAndSettle();

      // Verify starting in English
      expect(find.text('Settings'), findsOneWidget);

      // Navigate to Settings
      await tester.tap(find.byIcon(Icons.settings));
      await tester.pumpAndSettle();

      // Tap Language option
      final languageTile = find.ancestor(
        of: find.text('Language'),
        matching: find.byType(ListTile),
      );
      await tester.tap(languageTile);
      await tester.pumpAndSettle();

      // Dialog should appear with language options
      expect(find.text('English'), findsOneWidget);
      expect(find.text('አማርኛ'), findsOneWidget);
      expect(find.text('Afaan Oromoo'), findsOneWidget);

      // Select Amharic
      await tester.tap(find.text('አማርኛ'));
      await tester.pumpAndSettle();

      // UI should now be in Amharic
      expect(find.text('ቅንብሮች'), findsOneWidget); // Settings in Amharic
      expect(find.text('ቋንቋ'), findsOneWidget); // Language in Amharic
      expect(find.text('Settings'), findsNothing); // English should be gone
    });

    testWidgets('should switch from Amharic to Afan Oromo', (tester) async {
      await app.main();
      await tester.pumpAndSettle();

      // Switch to Amharic first
      await tester.tap(find.byIcon(Icons.settings));
      await tester.pumpAndSettle();

      final languageTile = find.ancestor(
        of: find.text('Language'),
        matching: find.byType(ListTile),
      );
      await tester.tap(languageTile);
      await tester.pumpAndSettle();

      await tester.tap(find.text('አማርኛ'));
      await tester.pumpAndSettle();

      // Verify in Amharic
      expect(find.text('ቅንብሮች'), findsOneWidget);

      // Now switch to Afan Oromo
      final languageTileAmharic = find.ancestor(
        of: find.text('ቋንቋ'), // Language in Amharic
        matching: find.byType(ListTile),
      );
      await tester.tap(languageTileAmharic);
      await tester.pumpAndSettle();

      await tester.tap(find.text('Afaan Oromoo'));
      await tester.pumpAndSettle();

      // UI should now be in Afan Oromo
      expect(find.text('Qindaa\'ina'), findsOneWidget); // Settings in Afan Oromo
      expect(find.text('Afaan'), findsOneWidget); // Language in Afan Oromo
      expect(find.text('ቅንብሮች'), findsNothing); // Amharic should be gone
    });

    testWidgets('should switch from Afan Oromo back to English',
        (tester) async {
      await app.main();
      await tester.pumpAndSettle();

      // Switch to Afan Oromo first
      await tester.tap(find.byIcon(Icons.settings));
      await tester.pumpAndSettle();

      var languageTile = find.ancestor(
        of: find.text('Language'),
        matching: find.byType(ListTile),
      );
      await tester.tap(languageTile);
      await tester.pumpAndSettle();

      await tester.tap(find.text('Afaan Oromoo'));
      await tester.pumpAndSettle();

      // Verify in Afan Oromo
      expect(find.text('Qindaa\'ina'), findsOneWidget);

      // Switch back to English
      languageTile = find.ancestor(
        of: find.text('Afaan'), // Language in Afan Oromo
        matching: find.byType(ListTile),
      );
      await tester.tap(languageTile);
      await tester.pumpAndSettle();

      await tester.tap(find.text('English'));
      await tester.pumpAndSettle();

      // UI should be back in English
      expect(find.text('Settings'), findsOneWidget);
      expect(find.text('Language'), findsOneWidget);
      expect(find.text('Qindaa\'ina'), findsNothing); // Afan Oromo should be gone
    });

    testWidgets('should persist language choice across app restarts',
        (tester) async {
      // Start app and switch to Amharic
      await app.main();
      await tester.pumpAndSettle();

      await tester.tap(find.byIcon(Icons.settings));
      await tester.pumpAndSettle();

      final languageTile = find.ancestor(
        of: find.text('Language'),
        matching: find.byType(ListTile),
      );
      await tester.tap(languageTile);
      await tester.pumpAndSettle();

      await tester.tap(find.text('አማርኛ'));
      await tester.pumpAndSettle();

      // Verify in Amharic
      expect(find.text('ቅንብሮች'), findsOneWidget);

      // Simulate app restart by pumping a new app instance
      // (In a real scenario, we'd restart the app completely)
      await tester.pumpWidget(Container()); // Clear existing widget tree
      await tester.pumpAndSettle();

      await app.main();
      await tester.pumpAndSettle(const Duration(seconds: 2));

      // App should remember Amharic was selected
      // Note: This test may need adjustment based on actual restart behavior
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'am');
    });

    testWidgets('should show current language with checkmark in dialog',
        (tester) async {
      await app.main();
      await tester.pumpAndSettle();

      // Navigate to language dialog
      await tester.tap(find.byIcon(Icons.settings));
      await tester.pumpAndSettle();

      final languageTile = find.ancestor(
        of: find.text('Language'),
        matching: find.byType(ListTile),
      );
      await tester.tap(languageTile);
      await tester.pumpAndSettle();

      // English should have a checkmark (current language)
      final englishTile = find.ancestor(
        of: find.text('English'),
        matching: find.byType(RadioListTile<Locale>),
      );
      expect(englishTile, findsOneWidget);

      // Verify radio button is selected for English
      final radioListTile = tester.widget<RadioListTile<Locale>>(englishTile);
      expect(radioListTile.value.languageCode, 'en');
      expect(radioListTile.groupValue?.languageCode, 'en');
    });

    testWidgets('should update navigation labels after language change',
        (tester) async {
      await app.main();
      await tester.pumpAndSettle();

      // Check English navigation labels
      expect(find.text('Home'), findsOneWidget);
      expect(find.text('Listings'), findsOneWidget);
      expect(find.text('Profile'), findsOneWidget);

      // Switch to Amharic
      await tester.tap(find.byIcon(Icons.settings));
      await tester.pumpAndSettle();

      final languageTile = find.ancestor(
        of: find.text('Language'),
        matching: find.byType(ListTile),
      );
      await tester.tap(languageTile);
      await tester.pumpAndSettle();

      await tester.tap(find.text('አማርኛ'));
      await tester.pumpAndSettle();

      // Go back to home to check navigation
      await tester.tap(find.byIcon(Icons.arrow_back));
      await tester.pumpAndSettle();

      // Check Amharic navigation labels
      expect(find.text('መነሻ'), findsOneWidget); // Home
      expect(find.text('ዝርዝሮች'), findsOneWidget); // Listings
      expect(find.text('መገለጫ'), findsOneWidget); // Profile

      // English labels should be gone
      expect(find.text('Home'), findsNothing);
      expect(find.text('Listings'), findsNothing);
      expect(find.text('Profile'), findsNothing);
    });

    testWidgets('should handle rapid language switching without crashes',
        (tester) async {
      await app.main();
      await tester.pumpAndSettle();

      // Rapidly switch between languages
      for (var i = 0; i < 3; i++) {
        // Go to settings
        await tester.tap(find.byIcon(Icons.settings));
        await tester.pumpAndSettle();

        // Open language dialog
        final languageTile = find.ancestor(
          of: find.textContaining(RegExp(r'Language|ቋንቋ|Afaan')),
          matching: find.byType(ListTile),
        );
        await tester.tap(languageTile.first);
        await tester.pumpAndSettle();

        // Switch to different language each iteration
        switch (i % 3) {
          case 0:
            await tester.tap(find.text('አማርኛ'));
            break;
          case 1:
            await tester.tap(find.text('Afaan Oromoo'));
            break;
          case 2:
            await tester.tap(find.text('English'));
            break;
        }
        await tester.pumpAndSettle();

        // Go back to home
        await tester.tap(find.byIcon(Icons.arrow_back));
        await tester.pumpAndSettle();
      }

      // App should still be running without crashes
      expect(find.byType(MaterialApp), findsOneWidget);
    });

    testWidgets('should show confirmation snackbar after language change',
        (tester) async {
      await app.main();
      await tester.pumpAndSettle();

      // Navigate to language dialog
      await tester.tap(find.byIcon(Icons.settings));
      await tester.pumpAndSettle();

      final languageTile = find.ancestor(
        of: find.text('Language'),
        matching: find.byType(ListTile),
      );
      await tester.tap(languageTile);
      await tester.pumpAndSettle();

      // Switch to Amharic
      await tester.tap(find.text('አማርኛ'));
      await tester.pumpAndSettle();

      // Snackbar should appear with confirmation
      // (Looking for text that contains language confirmation)
      expect(find.byType(SnackBar), findsOneWidget);
    });
  });
}
