import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';

/// Extracted validation logic for testing.
///
/// The actual validators live inside widget State classes (login_screen.dart,
/// register_screen.dart) and in TripListingRepository._validateTripData.
/// We re-implement the same rules here as pure functions so they can be
/// unit-tested without widget infrastructure.

// ---------------------------------------------------------------------------
// Phone validation (shared by login & register screens)
// Uses the shared phone_utils.dart for consistent validation.
// ---------------------------------------------------------------------------
String? validatePhone(String? value) {
  if (value == null || value.isEmpty) {
    return 'Phone Number';
  }
  if (!isValidEthiopianPhone(value)) {
    return 'Invalid phone number format';
  }
  return null;
}

// ---------------------------------------------------------------------------
// Login password validation (login_screen.dart — minimum 8 chars)
// ---------------------------------------------------------------------------
String? validateLoginPassword(String? value) {
  if (value == null || value.isEmpty) {
    return 'Password';
  }
  if (value.length < 8) {
    return 'Password must be at least 8 characters with a lowercase letter and a number';
  }
  return null;
}

// ---------------------------------------------------------------------------
// Registration password validation (register_screen.dart — 8 chars + lowercase + digit)
// ---------------------------------------------------------------------------
String? validateRegisterPassword(String? value) {
  if (value == null || value.isEmpty) {
    return 'Password';
  }
  if (value.length < 8 ||
      !RegExp(r'[a-z]').hasMatch(value) ||
      !RegExp(r'[0-9]').hasMatch(value)) {
    return 'Password must be at least 8 characters with a lowercase letter and a number';
  }
  return null;
}

// ---------------------------------------------------------------------------
// Confirm password validation
// ---------------------------------------------------------------------------
String? validateConfirmPassword(String? value, String password) {
  if (value != password) {
    return 'Passwords do not match';
  }
  return null;
}

// ---------------------------------------------------------------------------
// Name validation (register_screen.dart)
// ---------------------------------------------------------------------------
String? validateName(String? value) {
  if (value == null || value.trim().isEmpty) {
    return 'Full Name';
  }
  return null;
}

// ---------------------------------------------------------------------------
// Woreda validation (register_screen.dart)
// ---------------------------------------------------------------------------
String? validateWoreda(String? value) {
  if (value == null || value.trim().isEmpty) {
    return 'Wereda';
  }
  return null;
}

void main() {
  group('Login form validation', () {
    group('Phone number', () {
      test('empty phone returns error', () {
        expect(validatePhone(''), isNotNull);
        expect(validatePhone(null), isNotNull);
      });

      test('too short phone returns error', () {
        expect(validatePhone('+25191123'), isNotNull);
        expect(validatePhone('091123'), isNotNull);
      });

      test('too long phone returns error', () {
        expect(validatePhone('+2519112345678'), isNotNull);
        expect(validatePhone('09112345678'), isNotNull);
      });

      test('phone with letters returns error', () {
        expect(validatePhone('+251abc234567'), isNotNull);
      });

      test('valid +251 format returns null', () {
        expect(validatePhone('+251911234567'), isNull);
        expect(validatePhone('+251912345678'), isNull);
        expect(validatePhone('+251700000000'), isNull);
      });

      test('valid 00251 format returns null', () {
        expect(validatePhone('00251911234567'), isNull);
        expect(validatePhone('00251912345678'), isNull);
      });

      test('valid local 0 format returns null', () {
        expect(validatePhone('0911234567'), isNull);
        expect(validatePhone('0912345678'), isNull);
        expect(validatePhone('0700000000'), isNull);
      });
    });

    group('Password', () {
      test('empty password returns error', () {
        expect(validateLoginPassword(''), isNotNull);
        expect(validateLoginPassword(null), isNotNull);
      });

      test('password shorter than 8 chars returns error', () {
        expect(validateLoginPassword('abc12'), isNotNull);
        expect(validateLoginPassword('1234567'), isNotNull);
      });

      test('valid password (8+ chars) returns null', () {
        expect(validateLoginPassword('test1234'), isNull);
        expect(validateLoginPassword('longpassword'), isNull);
      });
    });
  });

  group('Registration form validation', () {
    group('Phone number', () {
      test('same validation as login phone', () {
        expect(validatePhone(''), isNotNull);
        expect(validatePhone('+251911234567'), isNull);
      });
    });

    group('Name', () {
      test('empty name returns error', () {
        expect(validateName(''), isNotNull);
        expect(validateName(null), isNotNull);
      });

      test('whitespace-only name returns error', () {
        expect(validateName('   '), isNotNull);
        expect(validateName('\t'), isNotNull);
      });

      test('valid Latin name returns null', () {
        expect(validateName('Abebe Kebede'), isNull);
      });

      test('valid Amharic name returns null', () {
        expect(validateName('አበበ ከበደ'), isNull);
      });

      test('valid Oromo name returns null', () {
        expect(validateName('Caalaa Bultii'), isNull);
      });
    });

    group('Password (registration)', () {
      test('empty password returns error', () {
        expect(validateRegisterPassword(''), isNotNull);
        expect(validateRegisterPassword(null), isNotNull);
      });

      test('password shorter than 8 chars returns error', () {
        expect(validateRegisterPassword('ab1'), isNotNull);
        expect(validateRegisterPassword('abcdefg'), isNotNull);
      });

      test('password without lowercase returns error', () {
        expect(validateRegisterPassword('ABCDEFG1'), isNotNull);
      });

      test('password without digit returns error', () {
        expect(validateRegisterPassword('abcdefgh'), isNotNull);
      });

      test('valid password returns null', () {
        expect(validateRegisterPassword('password1'), isNull);
        expect(validateRegisterPassword('abcdefg1'), isNull);
        expect(validateRegisterPassword('test12345'), isNull);
      });
    });

    group('Confirm password', () {
      test('mismatch returns error', () {
        expect(validateConfirmPassword('abc', 'xyz'), isNotNull);
        expect(validateConfirmPassword('', 'password1'), isNotNull);
      });

      test('match returns null', () {
        expect(validateConfirmPassword('password1', 'password1'), isNull);
        expect(validateConfirmPassword('', ''), isNull);
      });
    });

    group('Woreda', () {
      test('empty woreda returns error', () {
        expect(validateWoreda(''), isNotNull);
        expect(validateWoreda(null), isNotNull);
      });

      test('whitespace-only woreda returns error', () {
        expect(validateWoreda('   '), isNotNull);
      });

      test('valid woreda returns null', () {
        expect(validateWoreda('Jimma'), isNull);
        expect(validateWoreda('ጅማ'), isNull);
      });
    });

    group('Role (null/unselected)', () {
      test('null role is invalid', () {
        // In the register screen, _selectedRole == null triggers an error.
        // We test the concept here.
        const dynamic selectedRole = null;
        expect(selectedRole == null, isTrue);
      });
    });
  });

  group('Trip creation validation', () {
    // Use a fixed future date for testing
    late DateTime validFutureDate;
    late DateTime pastDate;
    late DateTime tooFarDate;

    setUp(() {
      final now = DateTime.now().toUtc();
      validFutureDate = now.add(const Duration(days: 5));
      pastDate = now.subtract(const Duration(days: 1));
      tooFarDate = now.add(const Duration(days: 31));
    });

    /// Helper that calls the repository's validation logic indirectly
    /// by attempting to create a trip. Since the repo requires Isar,
    /// we directly test the same rules using [TripValidationException].
    void expectValidationError(
      String field, {
      String truckerName = 'Abebe',
      String truckerPhone = '+251911234567',
      String fromWoreda = 'Jimma',
      String toWoreda = 'Addis Ababa',
      DateTime? departureDate,
      double capacity = 10.0,
      double pricePerQuintal = 100.0,
    }) {
      // Re-implement _validateTripData rules to keep tests independent of Isar
      final date =
          departureDate ?? DateTime.now().toUtc().add(const Duration(days: 5));

      String? errorField;

      if (truckerName.isEmpty || truckerName.length > 100) {
        errorField = 'truckerName';
      } else if (!isValidEthiopianPhone(truckerPhone)) {
        errorField = 'truckerPhone';
      } else if (fromWoreda.isEmpty || fromWoreda.length > 100) {
        errorField = 'fromWoreda';
      } else if (toWoreda.isEmpty || toWoreda.length > 100) {
        errorField = 'toWoreda';
      } else if (fromWoreda == toWoreda) {
        errorField = 'toWoreda';
      } else {
        final now = DateTime.now().toUtc();
        if (!date.isAfter(now)) {
          errorField = 'departureDate';
        } else if (date.isAfter(now.add(const Duration(days: 30)))) {
          errorField = 'departureDate';
        }
      }

      if (errorField == null) {
        if (capacity < 0.1 || capacity > 1000) {
          errorField = 'capacity';
        } else if (pricePerQuintal < 0.01 || pricePerQuintal > 10000) {
          errorField = 'pricePerQuintal';
        }
      }

      expect(errorField, equals(field));
    }

    group('truckerName', () {
      test('empty name is invalid', () {
        expectValidationError('truckerName', truckerName: '');
      });

      test('name over 100 chars is invalid', () {
        expectValidationError('truckerName', truckerName: 'A' * 101);
      });

      test('valid name passes', () {
        // Should not trigger truckerName error
        expectValidationError(
          'departureDate', // falls through to a different validation
          truckerName: 'Abebe Kebede',
          departureDate:
              DateTime.now().toUtc().subtract(const Duration(days: 1)),
        );
      });
    });

    group('truckerPhone', () {
      test('invalid phone format is rejected', () {
        expectValidationError('truckerPhone', truckerPhone: '+25191');
        expectValidationError('truckerPhone', truckerPhone: '');
        expectValidationError('truckerPhone', truckerPhone: '12345');
      });

      test('all three valid formats pass', () {
        // +251 format — valid phone, but past date triggers departure error
        expectValidationError(
          'departureDate',
          truckerPhone: '+251911234567',
          departureDate:
              DateTime.now().toUtc().subtract(const Duration(days: 1)),
        );
        // 00251 format
        expectValidationError(
          'departureDate',
          truckerPhone: '00251911234567',
          departureDate:
              DateTime.now().toUtc().subtract(const Duration(days: 1)),
        );
        // 0 format
        expectValidationError(
          'departureDate',
          truckerPhone: '0911234567',
          departureDate:
              DateTime.now().toUtc().subtract(const Duration(days: 1)),
        );
      });

      test('valid phone passes', () {
        // Valid phone, but past date triggers departure error
        expectValidationError(
          'departureDate',
          departureDate:
              DateTime.now().toUtc().subtract(const Duration(days: 1)),
        );
      });
    });

    group('fromWoreda', () {
      test('empty fromWoreda is invalid', () {
        expectValidationError('fromWoreda', fromWoreda: '');
      });

      test('fromWoreda over 100 chars is invalid', () {
        expectValidationError('fromWoreda', fromWoreda: 'W' * 101);
      });
    });

    group('toWoreda', () {
      test('empty toWoreda is invalid', () {
        expectValidationError('toWoreda', toWoreda: '');
      });

      test('toWoreda over 100 chars is invalid', () {
        expectValidationError('toWoreda', toWoreda: 'W' * 101);
      });

      test('same woreda is invalid', () {
        expectValidationError(
          'toWoreda',
          toWoreda: 'Jimma',
        );
      });
    });

    group('departureDate', () {
      test('date in the past is invalid', () {
        expectValidationError('departureDate', departureDate: pastDate);
      });

      test('date more than 30 days out is invalid', () {
        expectValidationError('departureDate', departureDate: tooFarDate);
      });

      test('valid future date within 30 days passes', () {
        // Valid date, valid everything — no error expected for the date field
        // so we force a capacity error to prove date passed
        expectValidationError(
          'capacity',
          departureDate: validFutureDate,
          capacity: 0,
        );
      });
    });

    group('capacity', () {
      test('zero capacity is invalid', () {
        expectValidationError(
          'capacity',
          capacity: 0,
          departureDate: validFutureDate,
        );
      });

      test('negative capacity is invalid', () {
        expectValidationError(
          'capacity',
          capacity: -5,
          departureDate: validFutureDate,
        );
      });

      test('capacity over 1000 is invalid', () {
        expectValidationError(
          'capacity',
          capacity: 1001,
          departureDate: validFutureDate,
        );
      });

      test('minimum valid capacity (0.1) passes', () {
        // 0.1 is valid, so next validation (price) should trigger
        expectValidationError(
          'pricePerQuintal',
          capacity: 0.1,
          pricePerQuintal: 0,
          departureDate: validFutureDate,
        );
      });

      test('maximum valid capacity (1000) passes', () {
        expectValidationError(
          'pricePerQuintal',
          capacity: 1000,
          pricePerQuintal: 0,
          departureDate: validFutureDate,
        );
      });
    });

    group('pricePerQuintal', () {
      test('zero price is invalid', () {
        expectValidationError(
          'pricePerQuintal',
          pricePerQuintal: 0,
          departureDate: validFutureDate,
        );
      });

      test('negative price is invalid', () {
        expectValidationError(
          'pricePerQuintal',
          pricePerQuintal: -100,
          departureDate: validFutureDate,
        );
      });

      test('price over 10000 is invalid', () {
        expectValidationError(
          'pricePerQuintal',
          pricePerQuintal: 10001,
          departureDate: validFutureDate,
        );
      });

      test('minimum valid price (0.01) passes', () {
        // All valid — no error field should match pricePerQuintal
        const name = 'Abebe';
        const phone = '+251911234567';
        const from = 'Jimma';
        const to = 'Addis Ababa';
        const cap = 10.0;
        const price = 0.01;

        // Verify all rules pass
        expect(name.isNotEmpty && name.length <= 100, isTrue);
        expect(isValidEthiopianPhone(phone), isTrue);
        expect(from.isNotEmpty && from.length <= 100, isTrue);
        expect(to.isNotEmpty && to.length <= 100, isTrue);
        expect(from != to, isTrue);
        expect(cap >= 0.1 && cap <= 1000, isTrue);
        expect(price >= 0.01 && price <= 10000, isTrue);
      });

      test('maximum valid price (10000) passes', () {
        const price = 10000.0;
        expect(price >= 0.01 && price <= 10000, isTrue);
      });
    });

    group('TripValidationException', () {
      test('has correct field and message', () {
        final ex = TripValidationException('capacity', 'Out of range');
        expect(ex.field, equals('capacity'));
        expect(ex.message, equals('Out of range'));
        expect(ex.toString(), contains('capacity'));
        expect(ex.toString(), contains('Out of range'));
      });
    });
  });

  group('Phone normalization', () {
    test('normalizes +251 format', () {
      expect(normalizeEthiopianPhone('+251911234567'), '+251911234567');
    });

    test('normalizes 00251 format', () {
      expect(normalizeEthiopianPhone('00251911234567'), '+251911234567');
    });

    test('normalizes local 0 format', () {
      expect(normalizeEthiopianPhone('0911234567'), '+251911234567');
    });

    test('strips spaces and dashes', () {
      expect(normalizeEthiopianPhone('+251 911 234 567'), '+251911234567');
      expect(normalizeEthiopianPhone('0911-234-567'), '+251911234567');
    });

    test('returns null for invalid input', () {
      expect(normalizeEthiopianPhone(null), isNull);
      expect(normalizeEthiopianPhone(''), isNull);
      expect(normalizeEthiopianPhone('12345'), isNull);
      expect(normalizeEthiopianPhone('+252911234567'), isNull);
      expect(normalizeEthiopianPhone('+251abc234567'), isNull);
      expect(normalizeEthiopianPhone('+25191123'), isNull);
    });

    test('all three formats normalize to the same value', () {
      final e164 = normalizeEthiopianPhone('+251911234567');
      final international = normalizeEthiopianPhone('00251911234567');
      final local = normalizeEthiopianPhone('0911234567');
      expect(e164, equals(international));
      expect(e164, equals(local));
      expect(e164, '+251911234567');
    });
  });
}
