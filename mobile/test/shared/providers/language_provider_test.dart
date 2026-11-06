import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() {
    SharedPreferences.setMockInitialValues({});
  });

  group('LanguageProvider.onLanguageChanged (SIM-37)', () {
    test('a real change invokes the hook with the new language code', () async {
      final provider = LanguageProvider();
      final codes = <String>[];
      provider.onLanguageChanged = (code) async => codes.add(code);

      await provider.setLocale(const Locale('om'));

      expect(provider.locale, const Locale('om'));
      expect(codes, ['om']);
    });

    test('selecting the current language does not invoke the hook', () async {
      final provider = LanguageProvider(); // defaults to 'en'
      var called = false;
      provider.onLanguageChanged = (_) async => called = true;

      await provider.setLocale(const Locale('en'));

      expect(called, isFalse);
    });

    test('a failing hook does not break language switching', () async {
      final provider = LanguageProvider();
      provider.onLanguageChanged = (_) async => throw Exception('network down');

      // Must not throw.
      await provider.setLocale(const Locale('am'));

      expect(provider.locale, const Locale('am'));
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'am');
    });

    test('no hook wired is fine', () async {
      final provider = LanguageProvider();

      await provider.setLocale(const Locale('om'));

      expect(provider.locale, const Locale('om'));
    });
  });

  group('LanguageProvider.applyProfileLanguage (SIM-37 read-back)', () {
    test(
        'no local pick: the profile language shows and is stored, '
        'not sent back', () async {
      final provider = LanguageProvider();
      await provider.initialize();
      final sent = <String>[];
      provider.onLanguageChanged = (code) async => sent.add(code);
      var notified = 0;
      provider.addListener(() => notified++);

      final applied = await provider.applyProfileLanguage('am');

      expect(applied, isTrue);
      expect(provider.locale, const Locale('am'));
      expect(notified, 1);
      expect(sent, isEmpty, reason: 'the value came from the server');
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'am');
      expect(prefs.getString('selected_language_source'), 'profile');
    });

    test('the next start opens in the stored profile language', () async {
      await LanguageProvider().applyProfileLanguage('om');

      final restarted = LanguageProvider();
      await restarted.initialize();

      expect(restarted.locale, const Locale('om'));
    });

    test('a language the user picked on this device wins', () async {
      final provider = LanguageProvider();
      await provider.setLocale(const Locale('om'));

      final applied = await provider.applyProfileLanguage('am');

      expect(applied, isFalse);
      expect(provider.locale, const Locale('om'));
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'om');
      expect(prefs.getString('selected_language_source'), 'user');
    });

    test('a value stored by an older build counts as the user\'s pick',
        () async {
      // Before the read-back, only setLocale wrote the language (on a real
      // change), and no source was stored.
      SharedPreferences.setMockInitialValues({'selected_language': 'om'});
      final provider = LanguageProvider();
      await provider.initialize();

      final applied = await provider.applyProfileLanguage('am');

      expect(applied, isFalse);
      expect(provider.locale, const Locale('om'));
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'om');
      expect(prefs.getString('selected_language_source'), isNull);
    });

    test('picking the language already shown counts as a pick', () async {
      final provider = LanguageProvider(); // shows 'en'
      var sent = false;
      provider.onLanguageChanged = (_) async => sent = true;

      await provider.setLocale(const Locale('en'));
      final applied = await provider.applyProfileLanguage('am');

      expect(sent, isFalse, reason: 'nothing changed, nothing to send');
      expect(applied, isFalse);
      expect(provider.locale, const Locale('en'));
    });

    test('a later sign-in replaces a language taken from a profile', () async {
      // Another account on a shared device, or a pick made on another device.
      final provider = LanguageProvider();
      await provider.applyProfileLanguage('am');

      final applied = await provider.applyProfileLanguage('om');

      expect(applied, isTrue);
      expect(provider.locale, const Locale('om'));
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'om');
    });

    test('a pick after a profile language makes it the user\'s', () async {
      final provider = LanguageProvider();
      await provider.applyProfileLanguage('am');
      await provider.setLocale(const Locale('en'));

      final applied = await provider.applyProfileLanguage('am');

      expect(applied, isFalse);
      expect(provider.locale, const Locale('en'));
    });

    test('a missing or unsupported profile language changes nothing', () async {
      final provider = LanguageProvider(); // shows 'en'
      var notified = 0;
      provider.addListener(() => notified++);

      for (final code in [null, '', 'fr', 'english', 'a m']) {
        expect(await provider.applyProfileLanguage(code), isFalse);
      }

      expect(provider.locale, const Locale('en'));
      expect(notified, 0);
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), isNull);
      expect(prefs.getString('selected_language_source'), isNull);
    });

    test('the profile code is matched without case or spaces', () async {
      final provider = LanguageProvider();

      expect(await provider.applyProfileLanguage(' AM '), isTrue);

      expect(provider.locale, const Locale('am'));
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'am');
    });
  });
}
