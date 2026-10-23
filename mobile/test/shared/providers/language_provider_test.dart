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
}
