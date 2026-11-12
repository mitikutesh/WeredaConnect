import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/account/domain/profile_edit.dart';

/// SIM-56: what a profile edit sends, and the server's rules the form checks
/// first (user-entity.md 2.2.0).
void main() {
  const current = ProfileValues(
    displayName: 'Abebe',
    woreda: 'Adama',
    buyerType: 'company',
    companyName: 'Adama Traders',
  );

  group('ProfileChanges.between', () {
    test('keeps only the fields that changed, trimmed', () {
      final changes = ProfileChanges.between(
        current: current,
        displayName: '  Abebe ',
        woreda: ' Meki ',
        companyName: 'Adama Traders',
      );

      expect(changes, const ProfileChanges(woreda: 'Meki'));
      expect(changes.toSyncData(), {'woreda': 'Meki'});
    });

    test('nothing changed is empty and sends nothing', () {
      final changes = ProfileChanges.between(
        current: current,
        displayName: 'Abebe',
        woreda: 'Adama',
      );

      expect(changes.isEmpty, isTrue);
      expect(changes.toSyncData(), isEmpty);
    });

    test('the company name is only compared when the form has it', () {
      final changes = ProfileChanges.between(
        current: current,
        displayName: 'Abebe K.',
        woreda: 'Adama',
      );

      expect(changes.toSyncData(), {'displayName': 'Abebe K.'});
    });
  });

  group('ProfileValues', () {
    test('apply keeps the buyer type and takes the changes', () {
      final saved = current.apply(const ProfileChanges(companyName: 'Meki'));

      expect(saved.companyName, 'Meki');
      expect(saved.buyerType, 'company');
      expect(saved.displayName, 'Abebe');
    });

    test('isCompanyBuyer only for a company buyer', () {
      expect(current.isCompanyBuyer, isTrue);
      expect(
        const ProfileValues(displayName: 'A', woreda: 'B', buyerType: 'private')
            .isCompanyBuyer,
        isFalse,
      );
      expect(
        const ProfileValues(displayName: 'A', woreda: 'B').isCompanyBuyer,
        isFalse,
      );
    });

    test('fromServerUser reads the user object of the caller only', () {
      final user = {
        'userId': 'buyer-1',
        'displayName': 'Almaz',
        'woreda': 'Meki',
        'buyerType': 'company',
        'companyName': 'Meki Traders',
        'role': 'buyer',
      };

      expect(
        ProfileValues.fromServerUser(user, userId: 'buyer-1'),
        const ProfileValues(
          displayName: 'Almaz',
          woreda: 'Meki',
          buyerType: 'company',
          companyName: 'Meki Traders',
        ),
      );
      expect(ProfileValues.fromServerUser(user, userId: 'buyer-2'), isNull);
      expect(ProfileValues.fromServerUser('x', userId: 'buyer-1'), isNull);
    });

    test('fromServerUser falls back to the Fayda name like listings do', () {
      final values = ProfileValues.fromServerUser(
        {'userId': 'u', 'fullName': 'Abebe Kebede Tola', 'woreda': 'Adama'},
        userId: 'u',
      );

      expect(values!.displayName, 'Abebe Kebede Tola');
    });
  });

  group('profileFieldError', () {
    test('a fine value passes', () {
      expect(profileFieldError('Abebe K.', isName: true), isNull);
      expect(profileFieldError('Meki'), isNull);
    });

    test('empty, over 100 characters, a phone number', () {
      expect(profileFieldError('   '), ProfileFieldError.required);
      expect(profileFieldError(null), ProfileFieldError.required);
      expect(profileFieldError('a' * 101), ProfileFieldError.tooLong);
      expect(profileFieldError('${'a' * 98}  '), isNull);
      expect(
        profileFieldError('Abebe 0911 22 33 44', isName: true),
        ProfileFieldError.contactInText,
      );
      expect(
        profileFieldError('Adama +251 911 223344'),
        ProfileFieldError.contactInText,
      );
    });

    test("the deleted account's name is refused for the name only", () {
      expect(
        profileFieldError(' deleted USER ', isName: true),
        ProfileFieldError.nameUnavailable,
      );
      expect(profileFieldError('Deleted user'), isNull);
    });
  });

  group('contactInTextFields', () {
    test('reads the fields of an AUTH_027 refusal', () {
      expect(
        contactInTextFields(
          'AUTH_027: Remove the phone number from displayName, woreda. '
          'Contact opens after payment is held in escrow.',
        ),
        ['displayName', 'woreda'],
      );
    });

    test('anything else names no field', () {
      expect(contactInTextFields(null), isEmpty);
      expect(
        contactInTextFields(
          'USER_PROFILE_VALIDATION: woreda must not be empty.',
        ),
        isEmpty,
      );
    });
  });
}
