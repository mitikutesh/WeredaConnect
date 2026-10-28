import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';

/// LT-03: the Clock setting is stored in shared preferences next to the
/// language, so it survives a restart on mobile and web.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() => SharedPreferences.setMockInitialValues({}));

  test('the clock follows the language until the person picks one', () async {
    final settings = SettingsProvider();
    await settings.initialize();
    expect(settings.clockPreference, ClockPreference.auto);
  });

  test('a picked clock is saved and notifies listeners', () async {
    final settings = SettingsProvider();
    var notified = 0;
    settings.addListener(() => notified++);

    await settings.setClockPreference(ClockPreference.ethiopian);

    expect(settings.clockPreference, ClockPreference.ethiopian);
    expect(notified, 1);
    final prefs = await SharedPreferences.getInstance();
    expect(prefs.getString('clock_preference'), 'ethiopian');
  });

  test('picking the current clock changes nothing', () async {
    final settings = SettingsProvider();
    var notified = 0;
    settings.addListener(() => notified++);

    await settings.setClockPreference(ClockPreference.auto);

    expect(notified, 0);
    final prefs = await SharedPreferences.getInstance();
    expect(prefs.getString('clock_preference'), isNull);
  });

  test('the saved clock is restored on start', () async {
    SharedPreferences.setMockInitialValues({
      'clock_preference': 'international',
    });
    final settings = SettingsProvider();
    await settings.initialize();
    expect(settings.clockPreference, ClockPreference.international);
  });

  test('an unknown saved value falls back to auto', () async {
    SharedPreferences.setMockInitialValues({'clock_preference': 'sundial'});
    final settings = SettingsProvider();
    await settings.initialize();
    expect(settings.clockPreference, ClockPreference.auto);
  });
}
