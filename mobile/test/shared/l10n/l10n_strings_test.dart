import 'dart:convert';
import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_wire.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Punctuation that every locale shares: the ellipsis and the em dash that
/// already appear in a few common strings.
const _sharedPunctuation = {0x2026, 0x2014};

/// Reads the translatable values of one arb file (metadata keys skipped).
Map<String, String> _arbValues(String locale) {
  final raw = File('lib/shared/l10n/app_$locale.arb').readAsStringSync();
  final decoded = jsonDecode(raw) as Map<String, dynamic>;
  return {
    for (final entry in decoded.entries)
      if (!entry.key.startsWith('@') && entry.value is String)
        entry.key: entry.value as String,
  };
}

/// Lists every `key: U+XXXX` whose code point [allowed] rejects.
List<String> _offenders(
  Map<String, String> values,
  bool Function(int codePoint) allowed,
) {
  final out = <String>[];
  for (final entry in values.entries) {
    for (final rune in entry.value.runes) {
      if (!allowed(rune)) {
        final hex = rune.toRadixString(16).toUpperCase().padLeft(4, '0');
        out.add('${entry.key}: U+$hex');
      }
    }
  }
  return out;
}

bool _isAscii(int c) => c < 0x80;

bool _isEthiopic(int c) =>
    (c >= 0x1200 && c <= 0x139F) ||
    (c >= 0x2D80 && c <= 0x2DDF) ||
    (c >= 0xAB00 && c <= 0xAB2F);

final _asciiLetter = RegExp('[A-Za-z]');

void main() {
  group('arb script hygiene (SIM-58)', () {
    test('app_am.arb values use only Ethiopic, ASCII and allowed punctuation',
        () {
      final offenders = _offenders(
        _arbValues('am'),
        (c) => _isAscii(c) || _isEthiopic(c) || _sharedPunctuation.contains(c),
      );
      expect(offenders, isEmpty, reason: 'foreign script in am: $offenders');
    });

    test('app_om.arb values use only Latin', () {
      // U+02BC is the Qubee hudhaa (glottal stop apostrophe).
      final offenders = _offenders(
        _arbValues('om'),
        (c) => _isAscii(c) || c == 0x02BC || _sharedPunctuation.contains(c),
      );
      expect(offenders, isEmpty, reason: 'foreign script in om: $offenders');
    });

    test('app_en.arb values are ASCII plus allowed punctuation', () {
      final offenders = _offenders(
        _arbValues('en'),
        (c) => _isAscii(c) || _sharedPunctuation.contains(c),
      );
      expect(offenders, isEmpty, reason: 'non-ASCII in en: $offenders');
    });

    test('all three arb files have the same keys', () {
      final en = _arbValues('en').keys.toSet();
      final am = _arbValues('am').keys.toSet();
      final om = _arbValues('om').keys.toSet();
      expect(am.difference(en), isEmpty, reason: 'am keys missing from en');
      expect(en.difference(am), isEmpty, reason: 'en keys missing from am');
      expect(om.difference(en), isEmpty, reason: 'om keys missing from en');
      expect(en.difference(om), isEmpty, reason: 'en keys missing from om');
    });

    test('am tapToListCrops uses the Ethiopic le, not a Thai character', () {
      final am = lookupAppLocalizations(const Locale('am'));
      expect(am.tapToListCrops, contains('ሰብልዎን'));
    });

    test('om syncedAgo is spelled in Latin letters', () {
      final om = lookupAppLocalizations(const Locale('om'));
      expect(om.syncedAgo('5 min'), '5 min dura walsimame');
    });

    test('am syncedAgo matches the synced badge wording', () {
      final am = lookupAppLocalizations(const Locale('am'));
      expect(am.syncedAgo('5 min'), 'ከ5 min በፊት ተመሳስሏል');
      expect(am.syncedAgo('5 min'), endsWith(am.synced));
    });
  });

  group('status labels (SIM-58)', () {
    final locales = {
      for (final code in ['en', 'am', 'om'])
        code: lookupAppLocalizations(Locale(code)),
    };

    for (final MapEntry(key: code, value: l10n) in locales.entries) {
      test('$code: every order status has a localized label', () {
        for (final s in OrderStatus.values) {
          final label = orderStatusLabel(s, l10n);
          expect(label, isNotEmpty, reason: '$code ${s.name}');
          _expectLocalized(code, label, [s.name, orderStatusToWire(s.name)]);
        }
      });

      test('$code: every trip status has a localized label', () {
        for (final s in TripStatus.values) {
          final label = tripStatusLabel(s, l10n);
          expect(label, isNotEmpty, reason: '$code ${s.name}');
          _expectLocalized(code, label, [s.name]);
        }
      });

      test('$code: every delivery status has a localized label', () {
        for (final s in DeliveryStatus.values) {
          final label = deliveryStatusLabel(s, l10n);
          expect(label, isNotEmpty, reason: '$code ${s.name}');
          _expectLocalized(code, label, [s.name]);
        }
      });
    }

    test('en labels are the human-readable form, not the enum name', () {
      final en = locales['en']!;
      expect(
        orderStatusLabel(OrderStatus.paymentSubmitted, en),
        'Payment Submitted',
      );
      expect(tripStatusLabel(TripStatus.inTransit, en), 'In Transit');
      expect(deliveryStatusLabel(DeliveryStatus.pickedUp, en), 'Picked Up');
    });
  });
}

/// am labels carry no ASCII letters; om labels never equal a raw token.
void _expectLocalized(String code, String label, List<String> rawTokens) {
  if (code == 'am') {
    expect(
      _asciiLetter.hasMatch(label),
      isFalse,
      reason: 'am label "$label" has ASCII letters',
    );
  } else if (code == 'om') {
    for (final token in rawTokens) {
      expect(
        label.toLowerCase(),
        isNot(token.toLowerCase()),
        reason: 'om label "$label" is the raw token "$token"',
      );
    }
  }
}
