import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// LT-03: the Dart Ethiopian clock against the cases shared with the
/// server's PushText (specs/contracts/fixtures/ethiopian-clock-cases.json).
/// Tests run with cwd `mobile/`.
const _casesPath = '../specs/contracts/fixtures/ethiopian-clock-cases.json';

Map<String, dynamic> _loadCases() =>
    jsonDecode(File(_casesPath).readAsStringSync()) as Map<String, dynamic>;

(int, int) _hm(String time) {
  final parts = time.split(':');
  return (int.parse(parts[0]), int.parse(parts[1]));
}

void main() {
  final fixture = _loadCases();
  final cases = (fixture['cases'] as List).cast<Map<String, dynamic>>();
  final ranges = (fixture['ranges'] as List).cast<Map<String, dynamic>>();
  final am = lookupAppLocalizations(const Locale('am'));
  final om = lookupAppLocalizations(const Locale('om'));
  final en = lookupAppLocalizations(const Locale('en'));

  test('the fixture is present and not empty', () {
    expect(cases, isNotEmpty);
    expect(ranges, isNotEmpty);
  });

  group('EthiopianClock against the shared cases', () {
    for (final c in cases) {
      final time = c['time'] as String;
      test('$time is "${c['am']}", "${c['om']}" and "${c['en']}"', () {
        final (h, m) = _hm(time);
        expect(EthiopianClock.format(h, m, am), c['am']);
        expect(EthiopianClock.format(h, m, om), c['om']);
        expect(EthiopianClock.international(h, m), c['en']);
      });
    }

    for (final r in ranges) {
      final from = r['from'] as String;
      final to = r['to'] as String;
      test('range $from-$to', () {
        final (fh, fm) = _hm(from);
        final (th, tm) = _hm(to);
        expect(EthiopianClock.formatRange(fh, fm, th, tm, am), r['am']);
        expect(EthiopianClock.formatRange(fh, fm, th, tm, om), r['om']);
        expect(EthiopianClock.internationalRange(fh, fm, th, tm), r['en']);
      });
    }

    test('the l10n words for the parts of the day match the fixture', () {
      final parts = fixture['parts'] as Map<String, dynamic>;
      final amParts = (parts['am'] as Map).cast<String, String>();
      final omParts = (parts['om'] as Map).cast<String, String>();
      // Amharic puts the part before the time, Afaan Oromo after it.
      expect(am.clockNight('T'), '${amParts['night']} T');
      expect(am.clockMorning('T'), '${amParts['morning']} T');
      expect(am.clockDay('T'), '${amParts['day']} T');
      expect(am.clockEvening('T'), '${amParts['evening']} T');
      expect(om.clockNight('T'), 'T ${omParts['night']}');
      expect(om.clockMorning('T'), 'T ${omParts['morning']}');
      expect(om.clockDay('T'), 'T ${omParts['day']}');
      expect(om.clockEvening('T'), 'T ${omParts['evening']}');
    });
  });

  group('EthiopianClock rule', () {
    test('every hour of the day maps to 1-12 with a part, never bare', () {
      for (var h = 0; h < 24; h++) {
        final eh = EthiopianClock.hourOf(h);
        expect(eh, inInclusiveRange(1, 12));
        expect(EthiopianClock.format(h, 0, en), contains('$eh:00 '));
      }
    });

    test('English words follow the same rule', () {
      expect(EthiopianClock.format(13, 20, en), '7:20 in the afternoon');
      expect(EthiopianClock.format(6, 0, en), '12:00 in the morning');
    });
  });

  group('ClockPreference', () {
    test('reads its stored name back; anything else is auto', () {
      for (final p in ClockPreference.values) {
        expect(ClockPreference.fromName(p.name), p);
      }
      expect(ClockPreference.fromName(null), ClockPreference.auto);
      expect(ClockPreference.fromName('sundial'), ClockPreference.auto);
    });
  });

  group('ClockFormat', () {
    // 10:20 UTC is 13:20 in Ethiopia (UTC+3).
    final instant = DateTime.utc(2026, 10, 28, 10, 20);

    test('Auto follows the language', () {
      expect(ClockFormat.resolve(am, ClockPreference.auto).ethiopian, isTrue);
      expect(ClockFormat.resolve(om, ClockPreference.auto).ethiopian, isTrue);
      expect(ClockFormat.resolve(en, ClockPreference.auto).ethiopian, isFalse);
    });

    test('an explicit choice wins over the language', () {
      expect(
        ClockFormat.resolve(am, ClockPreference.international).time(instant),
        '13:20',
      );
      expect(
        ClockFormat.resolve(en, ClockPreference.ethiopian).time(instant),
        '7:20 in the afternoon',
      );
    });

    test('times are Ethiopia time, whatever the device timezone', () {
      final clock = ClockFormat.resolve(am, ClockPreference.auto);
      expect(clock.time(instant), 'ከቀኑ 7:20');
      expect(clock.time(instant.toLocal()), 'ከቀኑ 7:20');
      expect(
        ClockFormat.resolve(en, ClockPreference.auto).time(instant),
        '13:20',
      );
    });

    test('ranges', () {
      final to = instant.add(const Duration(minutes: 40));
      expect(
        ClockFormat.resolve(om, ClockPreference.auto).range(instant, to),
        '7:20-8:00 waaree booda',
      );
      expect(
        ClockFormat.resolve(en, ClockPreference.auto).range(instant, to),
        '13:20-14:00',
      );
    });

    test('an arrival window carries the 24-hour range on the Ethiopian clock',
        () {
      final to = instant.add(const Duration(minutes: 40));
      final amWindow =
          ClockFormat.resolve(am, ClockPreference.auto).window(instant, to);
      expect(amWindow.toString(), 'ከቀኑ 7:20-8:00 (13:20-14:00)');
      final enWindow =
          ClockFormat.resolve(en, ClockPreference.auto).window(instant, to);
      expect(enWindow.toString(), '13:20-14:00');
      expect(enWindow.time24, isNull);
    });

    test('dates stay Gregorian, only the time changes', () {
      expect(
        ClockFormat.resolve(am, ClockPreference.auto).dateTime(instant),
        '${DateFormat.yMMMd().format(DateTime.utc(2026, 10, 28))} ከቀኑ 7:20',
      );
      expect(
        ClockFormat.resolve(en, ClockPreference.auto)
            .dateTime(instant, date: DateFormat.MMMd()),
        'Oct 28 13:20',
      );
    });

    test('the 24-hour companion shows only on the Ethiopian clock', () {
      final am24 = ClockFormat.resolve(am, ClockPreference.auto);
      final en24 = ClockFormat.resolve(en, ClockPreference.auto);
      expect(am24.time24(instant), '13:20');
      expect(en24.time24(instant), isNull);
    });

    test('a deadline in a sentence keeps the text around the time', () {
      final label = ClockFormat.resolve(am, ClockPreference.auto).deadline(
        instant,
        date: DateFormat.MMMd(),
        sentence: am.commitmentAnswerBy,
      );
      final whole = am.commitmentAnswerBy('Oct 28 ከቀኑ 7:20');
      expect(label.time, 'Oct 28 ከቀኑ 7:20');
      expect(label.time24, '13:20');
      expect('${label.before}${label.time}${label.after}', whole);
      expect(
        label.toString(),
        whole.replaceFirst('ከቀኑ 7:20', 'ከቀኑ 7:20 (13:20)'),
      );
    });

    test('an English deadline has no companion and reads as before', () {
      final label = ClockFormat.resolve(en, ClockPreference.auto).deadline(
        instant,
        sentence: en.otpExpiresAt,
      );
      expect(label.time24, isNull);
      expect(label.toString(), en.otpExpiresAt('13:20'));
    });

    test('a sentence that lost its slot still shows the time', () {
      final label = ClockFormat.resolve(am, ClockPreference.auto).deadline(
        instant,
        sentence: (_) => 'no slot',
      );
      expect(label.toString(), 'no slot');
    });
  });

  group('ClockLabel', () {
    test('prefixed puts text in front and keeps the rest', () {
      const label = ClockLabel(
        before: 'Due ',
        time: 'ከቀኑ 7:20',
        time24: '13:20',
      );
      final out = label.prefixed('Teff · 500 kg · ');
      expect(out.toString(), 'Teff · 500 kg · Due ከቀኑ 7:20 (13:20)');
      expect(out.time24, '13:20');
    });

    test('the span puts the companion in the small style', () {
      const label = ClockLabel(time: '7:20 ganama', time24: '13:20');
      const small = TextStyle(fontSize: 9);
      final span = label.toSpan(smallStyle: small);
      final children = span.children!.cast<TextSpan>();
      expect(children.last.text, ' (13:20)');
      expect(children.last.style, small);
      expect(span.toPlainText(), '7:20 ganama (13:20)');
    });

    test('plain text has no companion', () {
      expect(const ClockLabel.text('Waiting').toString(), 'Waiting');
    });
  });
}
