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

    // BUG-L8 reworded both from "synced" to "updated".
    test('om syncedAgo is spelled in Latin letters', () {
      final om = lookupAppLocalizations(const Locale('om'));
      expect(om.syncedAgo('5 min'), '5 min dura haaromfame');
    });

    test('am syncedAgo is spelled in Ethiopic letters', () {
      final am = lookupAppLocalizations(const Locale('am'));
      expect(am.syncedAgo('5 min'), 'ከ5 min በፊት ታድሷል');
    });
  });

  // The persona simulation of 2026-11-19: one word, one spelling.
  group('one wording per term', () {
    final am = _arbValues('am');
    final om = _arbValues('om');
    final en = _arbValues('en');

    List<String> keysWith(Map<String, String> values, RegExp pattern) => [
          for (final entry in values.entries)
            if (pattern.hasMatch(entry.value)) entry.key,
        ];

    test('BUG-L1: Amharic Cancel is not the word for Delete', () {
      expect(am['cancel'], isNot(am['delete']));
      expect(am['cancel'], 'ይቅር');
      // A destructive button never reads like the button that backs out.
      for (final key in ['delete', 'deleteAccountButton', 'cancelOrder']) {
        expect(am[key], isNot(contains(am['cancel']!)), reason: key);
      }
    });

    test('BUG-L10: escrow has one spelling per language', () {
      expect(keysWith(am, RegExp('ኤስክሮ|እስክሮ')), isEmpty);
      expect(am['escrow'], 'ኢስክሮ');
      expect(
        keysWith(
          om,
          RegExp(r'escrow|eesciroo|\beskiroo', caseSensitive: false),
        ),
        isEmpty,
      );
      expect(om['escrow'], 'Eeskiroo');
    });

    test('BUG-L11: one quintal spelling and Birrii in Afaan Oromoo', () {
      expect(
        keysWith(
          om,
          RegExp('kuintaal|kuuntaal|kuntaalli', caseSensitive: false),
        ),
        isEmpty,
      );
      expect(
        keysWith(om, RegExp(r'\bETB\b|qarshii', caseSensitive: false)),
        isEmpty,
      );
      // The unit comes before the number, as in "Birrii 500".
      expect(keysWith(om, RegExp(r'[}0-9] (Birrii|kuntaala)\b')), isEmpty);
    });

    test('BUG-L11: birr in Ethiopic after the amount in Amharic', () {
      expect(keysWith(am, RegExp(r'\bETB\b')), isEmpty);
      expect(keysWith(am, RegExp(r'ብር (\{|[0-9])')), isEmpty);
    });

    test('BUG-L12: the short price unit names the quintal', () {
      expect(om['detailEtbPerQuintalShort'], 'Birrii/kuntaala');
      expect(om['detailEtbPerQuintalShort'], isNot(endsWith('/k')));
    });

    test('BUG-L9, BUG-25: no server, offline or online jargon left', () {
      expect(keysWith(am, RegExp('አገልጋ|ሰርቨ')), isEmpty);
      expect(
        keysWith(
          om,
          RegExp('sarvar|server|offline|oflaayin', caseSensitive: false),
        ),
        isEmpty,
      );
    });

    test('BUG-L14: the payout line speaks of money that arrives', () {
      expect(am['escrowPayoutExpectedOn'], contains('ገንዘብዎ'));
      expect(om['escrowPayoutExpectedOn'], contains('Maallaqni keessan'));
      for (final values in [am, om]) {
        expect(values['escrowPayoutExpectedOn'], contains('{date}'));
      }
    });

    test('BUG-L8: sync status says sent or waiting, never sync', () {
      const keys = [
        'syncPending',
        'syncInProgress',
        'syncComplete',
        'syncFailed',
        'syncNow',
        'synced',
        'syncFailureCount',
        'syncPendingCount',
        'offline',
        'offlineModeIndicator',
      ];
      for (final key in keys) {
        expect(
          en[key]!.toLowerCase(),
          isNot(anyOf(contains('sync'), contains('offline'))),
          reason: 'en $key',
        );
        expect(am[key], isNot(contains('ማመሳሰል')), reason: 'am $key');
        expect(am[key], isNot(contains('ከመስመር ውጭ')), reason: 'am $key');
        expect(
          om[key]!.toLowerCase(),
          isNot(contains('walsims')),
          reason: 'om $key',
        );
      }
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
