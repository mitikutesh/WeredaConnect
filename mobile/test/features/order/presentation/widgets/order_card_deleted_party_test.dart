import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

Order _order() => Order()
  ..orderId = 'o1'
  ..listingId = 'l1'
  ..buyerId = 'b1'
  ..farmerId = 'f1'
  ..buyerName = deletedUserSentinel
  ..farmerName = deletedUserSentinel
  ..cropType = 'wheat'
  ..quantity = 100
  ..pricePerUnit = 50
  ..totalPrice = 5000
  ..status = OrderStatus.settled
  ..createdAt = DateTime(2026)
  ..version = 1;

/// ACC-03: the server's "Deleted user" name shows in the user's language.
void main() {
  for (final code in ['en', 'am', 'om']) {
    for (final isFarmerView in [true, false]) {
      testWidgets('$code, ${isFarmerView ? 'farmer' : 'buyer'} view',
          (tester) async {
        await tester.pumpWidget(
          MaterialApp(
            locale: Locale(code),
            localizationsDelegates: const [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              GlobalWidgetsLocalizations.delegate,
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(
              body: OrderCard(order: _order(), isFarmerView: isFarmerView),
            ),
          ),
        );
        await tester.pumpAndSettle();
        final l10n = lookupAppLocalizations(Locale(code));
        final role = isFarmerView ? l10n.buyer : l10n.farmer;

        expect(find.text('$role: ${l10n.deletedUser}'), findsOneWidget);
        if (code != 'en') {
          expect(find.textContaining(deletedUserSentinel), findsNothing);
        }
      });
    }
  }
}
