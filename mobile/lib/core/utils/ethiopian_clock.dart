import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// LT-03: which clock the person reads times on (Settings > Clock). Stored by
/// [name], so do not rename the values.
enum ClockPreference {
  /// The clock of the app language: Ethiopian for am and om, international
  /// for en. The default until the person picks one.
  auto,

  /// The Ethiopian clock, always with the part of the day.
  ethiopian,

  /// The 24-hour clock.
  international;

  /// The stored [name] read back; anything unknown is [auto].
  static ClockPreference fromName(String? name) => ClockPreference.values
      .firstWhere((p) => p.name == name, orElse: () => ClockPreference.auto);
}

/// The part of the day the Ethiopian clock names with every time.
enum DayPart { night, morning, day, evening }

/// The Ethiopian clock (specs/features/live-trip.md section 9). The day
/// starts at 6:00 in the morning, so 13:20 is 7:20 in the day. Input is the
/// Ethiopia wall-clock hour and minute (UTC+3, 24-hour).
///
/// The words for the parts of the day come from the app's l10n
/// (`clockNight`, `clockMorning`, `clockDay`, `clockEvening`), each a pattern
/// around the time: Amharic puts the part before it ("ከቀኑ 7:20"), Afaan
/// Oromo after it ("7:20 waaree booda"). The expected outputs are in
/// `specs/contracts/fixtures/ethiopian-clock-cases.json`, shared with the
/// server's `PushText`.
abstract final class EthiopianClock {
  /// The Ethiopian hour, 1 to 12, of a 24-hour [hour]: (h - 6) mod 12, with 0
  /// shown as 12.
  static int hourOf(int hour) {
    final h = (hour - 6) % 12;
    return h == 0 ? 12 : h;
  }

  /// The part of the day of a 24-hour [hour]: 0 to 5 night, 6 to 11 morning,
  /// 12 to 17 day, 18 to 23 evening.
  static DayPart partOf(int hour) => switch (hour) {
        < 6 => DayPart.night,
        < 12 => DayPart.morning,
        < 18 => DayPart.day,
        _ => DayPart.evening,
      };

  /// [hour]:[minute] on the Ethiopian clock with its part of the day, never a
  /// bare hour: "ከቀኑ 7:20", "7:20 waaree booda".
  static String format(int hour, int minute, AppLocalizations l10n) =>
      _withPart(partOf(hour), _digits(hour, minute), l10n);

  /// A range on the Ethiopian clock. The part of the day is said once when
  /// both ends share it ("ከቀኑ 7:20-8:00") and at each end when it changes
  /// ("ከቀኑ 11:30-ከምሽቱ 12:30").
  static String formatRange(
    int fromHour,
    int fromMinute,
    int toHour,
    int toMinute,
    AppLocalizations l10n,
  ) {
    final part = partOf(fromHour);
    if (part == partOf(toHour)) {
      return _withPart(
        part,
        '${_digits(fromHour, fromMinute)}-${_digits(toHour, toMinute)}',
        l10n,
      );
    }
    return '${format(fromHour, fromMinute, l10n)}-'
        '${format(toHour, toMinute, l10n)}';
  }

  /// [hour]:[minute] on the 24-hour clock: "13:20", "02:00".
  static String international(int hour, int minute) =>
      '${_two(hour)}:${_two(minute)}';

  /// A range on the 24-hour clock: "13:20-14:00".
  static String internationalRange(
    int fromHour,
    int fromMinute,
    int toHour,
    int toMinute,
  ) =>
      '${international(fromHour, fromMinute)}-${international(toHour, toMinute)}';

  /// Whether a language reads the Ethiopian clock when the setting is Auto.
  static bool isDefaultFor(String languageCode) =>
      languageCode == 'am' || languageCode == 'om';

  static String _digits(int hour, int minute) =>
      '${hourOf(hour)}:${_two(minute)}';

  static String _two(int value) => value.toString().padLeft(2, '0');

  static String _withPart(DayPart part, String time, AppLocalizations l10n) =>
      switch (part) {
        DayPart.night => l10n.clockNight(time),
        DayPart.morning => l10n.clockMorning(time),
        DayPart.day => l10n.clockDay(time),
        DayPart.evening => l10n.clockEvening(time),
      };
}

/// How times are shown to one viewer: the app language and the clock they
/// read. Every user-visible clock time goes through this, so no screen
/// formats an hour itself. Times are always Ethiopia time (UTC+3, see
/// [instantInEthiopia]) whatever the device timezone; dates stay Gregorian.
@immutable
class ClockFormat {
  const ClockFormat._(this._l10n, {required this.ethiopian});

  /// The clock for [l10n]'s language and the person's [preference].
  factory ClockFormat.resolve(
    AppLocalizations l10n,
    ClockPreference preference,
  ) =>
      ClockFormat._(
        l10n,
        ethiopian: switch (preference) {
          ClockPreference.ethiopian => true,
          ClockPreference.international => false,
          ClockPreference.auto => EthiopianClock.isDefaultFor(l10n.localeName),
        },
      );

  final AppLocalizations _l10n;

  /// True on the Ethiopian clock, false on the 24-hour clock.
  final bool ethiopian;

  /// The time of day of [instant] in Ethiopia: "ከቀኑ 7:20",
  /// "7:20 waaree booda" or "13:20".
  String time(DateTime instant) {
    final t = instantInEthiopia(instant);
    return ethiopian
        ? EthiopianClock.format(t.hour, t.minute, _l10n)
        : EthiopianClock.international(t.hour, t.minute);
  }

  /// A window from [from] to [to] in Ethiopia: "ከቀኑ 7:20-8:00" or
  /// "13:20-14:00".
  String range(DateTime from, DateTime to) {
    final a = instantInEthiopia(from);
    final b = instantInEthiopia(to);
    return ethiopian
        ? EthiopianClock.formatRange(a.hour, a.minute, b.hour, b.minute, _l10n)
        : EthiopianClock.internationalRange(a.hour, a.minute, b.hour, b.minute);
  }

  /// An arrival window: [range], and on the Ethiopian clock the 24-hour range
  /// for the small text beside it ("ከቀኑ 7:20-8:00" with "13:20-14:00").
  ClockLabel window(DateTime from, DateTime to) {
    final a = instantInEthiopia(from);
    final b = instantInEthiopia(to);
    final small = EthiopianClock.internationalRange(
      a.hour,
      a.minute,
      b.hour,
      b.minute,
    );
    return ClockLabel(time: range(from, to), time24: ethiopian ? small : null);
  }

  /// [date]'s day (Gregorian, `yMMMd` by default) and the time of [instant]
  /// in Ethiopia: "Oct 23, 2026 ከሌሊቱ 8:30", "Oct 23, 2026 02:30".
  String dateTime(DateTime instant, {DateFormat? date}) =>
      '${(date ?? DateFormat.yMMMd()).format(instantInEthiopia(instant))} '
      '${time(instant)}';

  /// The 24-hour time shown small beside a deadline on the Ethiopian clock,
  /// so both readings agree; null on the 24-hour clock.
  String? time24(DateTime instant) {
    if (!ethiopian) return null;
    final t = instantInEthiopia(instant);
    return EthiopianClock.international(t.hour, t.minute);
  }

  /// A deadline at [instant], with [date]'s day in front when given (null:
  /// the time alone), placed in [sentence] (an l10n message taking the time,
  /// such as `commitmentAnswerBy`) when given. On the Ethiopian clock it also
  /// carries [time24] for the small 24-hour text.
  ClockLabel deadline(
    DateTime instant, {
    DateFormat? date,
    String Function(String time)? sentence,
  }) {
    final when = date == null ? time(instant) : dateTime(instant, date: date);
    final small = time24(instant);
    if (sentence == null) return ClockLabel(time: when, time24: small);
    final whole = sentence(_slot);
    final at = whole.indexOf(_slot);
    if (at < 0) return ClockLabel.text(sentence(when));
    return ClockLabel(
      before: whole.substring(0, at),
      time: when,
      time24: small,
      after: whole.substring(at + _slot.length),
    );
  }

  /// Marks where the time goes in a sentence (U+FFFC, never in a message).
  static const _slot = '\u{FFFC}';
}

/// Text with at most one clock time in it, split so a screen can show the
/// 24-hour companion ([time24]) small right after the time.
@immutable
class ClockLabel {
  const ClockLabel({
    required this.time,
    this.before = '',
    this.time24,
    this.after = '',
  });

  /// Text with no time in it.
  const ClockLabel.text(String text)
      : before = text,
        time = '',
        time24 = null,
        after = '';

  final String before;
  final String time;

  /// The 24-hour time beside [time], or null when there is none to show.
  final String? time24;
  final String after;

  /// The same label with [prefix] in front.
  ClockLabel prefixed(String prefix) => ClockLabel(
        before: '$prefix$before',
        time: time,
        time24: time24,
        after: after,
      );

  /// The label as spans, [time24] in parentheses in [smallStyle].
  TextSpan toSpan({TextStyle? style, TextStyle? smallStyle}) => TextSpan(
        style: style,
        children: [
          if (before.isNotEmpty) TextSpan(text: before),
          if (time.isNotEmpty) TextSpan(text: time),
          if (time24 != null) TextSpan(text: ' ($time24)', style: smallStyle),
          if (after.isNotEmpty) TextSpan(text: after),
        ],
      );

  /// The whole label as one string, the 24-hour time in parentheses.
  @override
  String toString() =>
      '$before$time${time24 == null ? '' : ' ($time24)'}$after';

  @override
  bool operator ==(Object other) =>
      other is ClockLabel &&
      other.before == before &&
      other.time == time &&
      other.time24 == time24 &&
      other.after == after;

  @override
  int get hashCode => Object.hash(before, time, time24, after);
}
