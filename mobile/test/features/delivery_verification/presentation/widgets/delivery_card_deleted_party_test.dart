import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

Delivery _delivery() => Delivery()
  ..deliveryId = 'del-12345678'
  ..tripId = 't1'
  ..listingId = 'l1'
  ..farmerId = 'f1'
  ..farmerName = deletedUserSentinel
  ..truckerId = 'tr1'
  ..truckerName = 'Kebede'
  ..buyerId = 'b1'
  ..buyerName = 'Almaz'
  ..qrToken = 'qr'
  ..status = DeliveryStatus.delivered
  ..createdAt = DateTime(2026)
  ..updatedAt = DateTime(2026)
  ..version = 1;

/// ACC-03: a deleted party's name is the localized "Deleted user"; the
/// other names are unchanged.
void main() {
  testWidgets('am: the deleted farmer reads in Amharic', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        locale: const Locale('am'),
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: DeliveryCard(delivery: _delivery(), currentUserId: 'b1'),
        ),
      ),
    );
    await tester.pumpAndSettle();
    final am = lookupAppLocalizations(const Locale('am'));

    expect(find.text('${am.deletedUser} → Almaz'), findsOneWidget);
    expect(find.textContaining('Kebede'), findsOneWidget);
    expect(find.textContaining(deletedUserSentinel), findsNothing);
  });
}
