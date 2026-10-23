import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/utils/contact_in_text.dart';

/// The ASCII digits of [ascii] written in another digit block.
String _inBlock(String ascii, int zero) => String.fromCharCodes(
      ascii.codeUnits.map(
        (c) => c >= 0x30 && c <= 0x39 ? zero + (c - 0x30) : c,
      ),
    );

/// Digits 1-9 written as Ethiopic digits (Ethiopic has no zero).
String _ethiopic(String ascii) =>
    String.fromCharCodes(ascii.codeUnits.map((c) => 0x1369 + (c - 0x31)));

/// Each digit as a keycap emoji: the digit, variation selector 16, combining
/// keycap.
String _keycaps(String ascii) {
  final keycap = String.fromCharCodes([0xFE0F, 0x20E3]);
  return ascii.split('').map((d) => '$d$keycap').join();
}

String _joinedBy(int separator, List<String> parts) =>
    parts.join(String.fromCharCode(separator));

/// FUP-49: the phone-number detector for listing text. The same table runs on
/// the server (server/tests/Validation/ContactInTextTests.cs); keep the two in
/// step so the form refuses exactly what the server refuses.
void main() {
  final phoneNumbers = <String>[
    // English
    'Call 0911234567',
    'call me on 0911 23 45 67',
    '0911-234-567',
    '0911.23.45.67',
    '0911 - 23 - 45 - 67',
    '+251 911 234 567',
    '+251-91-123-4567',
    '(+251) 911234567',
    '+251 (0) 911 234 567',
    '251911234567',
    '00251911234567',
    '00 251 911 234 567',
    'Safaricom 0712345678',
    '+251 712 345 678',
    'Call 911 234 567',
    '0 9 1 1 2 3 4 5 6 7',
    'price 45 0911234567',
    'tel:0911234567',
    'o911234567 call me',
    'Harvested 2026-10-23 call 0911234567',
    '23.10.2026 0911234567',
    'Call\n0911\n234567',
    '0911-12-10-15',
    '0911 12 2019',
    // Amharic
    'ለበለጠ መረጃ 0911234567 ይደውሉ',
    'ስልክ 0911 23 45 67',
    'ስልክ ቁጥር ${_joinedBy(0x1361, ['0911', '23', '45', '67'])}',
    // Afan Oromo
    'Bilbilaa 0911234567 irratti bilbilaa',
    'Lakk. bilbilaa: +251 911 234 567',
    'Odeeffannoo dabalataaf 0712 34 56 78 bilbilaa',
    // Other digit forms and separators
    _inBlock('0911234567', 0x0660),
    _inBlock('0911234567', 0x06F0),
    _inBlock('0911234567', 0xFF10),
    _ethiopic('911234567'),
    _joinedBy(0x2013, ['0911', '23', '45', '67']),
    _joinedBy(0x2212, ['0911', '234', '567']),
    _joinedBy(0x00A0, ['0911', '234567']),
    // Invisible format characters are dropped and do not count toward the gap
    _joinedBy(0x200B, ['0911', '223344']),
    _joinedBy(0x200F, ['0911', '223344']),
    _joinedBy(0x00AD, ['0911', '223344']),
    _joinedBy(0x2060, ['0911', '223344']),
    _joinedBy(0x2064, ['0911', '223344']),
    _joinedBy(0xFEFF, ['0911', '223344']),
    '0${String.fromCharCode(0x200B)}911${String.fromCharCode(0x200D)}234567',
    '0911 ${String.fromCharCode(0x200B) * 3} 223344',
    // Other Unicode spaces separate groups
    _joinedBy(0x2000, ['0911', '223344']),
    _joinedBy(0x2002, ['0911', '223344']),
    _joinedBy(0x200A, ['0911', '223344']),
    _joinedBy(0x205F, ['0911', '223344']),
    _joinedBy(0x3000, ['0911', '223344']),
    _joinedBy(0x1680, ['0911', '223344']),
    // Keycap emoji digits
    'Call ${_keycaps('0911223344')}',
  ];

  final notPhoneNumbers = <String>[
    // English: prices, quantities, dates, weights, vehicles, places
    '500 kg',
    '2026-10-23',
    '1,200 ETB',
    'Harvested 2026-10-23, 500 quintals at 1,200 ETB per quintal',
    'Harvested 09-10-2026 50 quintals',
    'Harvested 09.10.2026 50 quintals',
    'Harvested 09-10-18 5000 kg',
    'Available 01-09-2017-30-09-2017',
    'Available 01.09.2017.30.09.2017',
    'Harvested 09 10 2017 50 quintals',
    '2026-10-23 500 quintals',
    'Price 7500 birr, 950 kg available',
    'Whole lot 750 000 - 800 000 birr',
    'Isuzu FSR 2019 model, 70 quintal capacity',
    'Isuzu NPR 7.5 ton 2015',
    'Mitsubishi FH 9.5 ton 2012',
    '3-AA-12345',
    'ET 3-12345 AA',
    'Kebele 07, Zone 3',
    '1 200 000 birr',
    'Moisture 12.5%, purity 99.9%',
    'Lot 12345678',
    'Account 1000123456789',
    'Departure 09:30, arrival 17:45',
    'Weights 20 30 40 50 kg',
    'Grade 1 white teff, 98 percent clean, 100 kg sacks',
    'Debre Birhan',
    '',
    '   ',
    // Amharic
    '50 ኩንታል ነጭ ጤፍ፣ በኪሎ 120 ብር',
    'ጥቅምት 12 2018 የተሰበሰበ',
    'አይሱዙ 2015 ሞዴል 70 ኩንታል',
    'ቀበሌ 07 ዞን 3',
    '09-10-2018 50 ኩንታል',
    'ተሰብስቧል 09 10 2017 50 ኩንታል',
    // Afan Oromo
    'Xaafii adii kuntaala 50, kiilootti qarshii 120',
    "Fe'umsa kuntaala 70, bara 2015",
    'Ganda 07, Godina 3',
    'Guyyaa 09.10.2018 50 kuntaala',
    'Bale 09 07 2017 12 kuntaala',
  ];

  group('containsPhoneNumber finds a number', () {
    for (final text in phoneNumbers) {
      test(text, () => expect(containsPhoneNumber(text), isTrue));
    }
  });

  group('containsPhoneNumber leaves prices, quantities, dates and places', () {
    for (final text in notPhoneNumbers) {
      test('"$text"', () => expect(containsPhoneNumber(text), isFalse));
    }
    test('null', () => expect(containsPhoneNumber(null), isFalse));
  });
}
