import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/account/data/repositories/account_deletion_repository.dart';
import 'package:wereda_connect/features/account/presentation/open_deletion_blocker.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockMarketplaceSync extends Mock implements MarketplaceSync {}

Delivery _delivery(String id, {String truckerId = 'trucker-1'}) => Delivery()
  ..deliveryId = id
  ..tripId = 'trip-1'
  ..listingId = 'listing-1'
  ..farmerId = 'farmer-1'
  ..farmerName = 'Abebe Kebede'
  ..truckerId = truckerId
  ..truckerName = 'Tadesse Worku'
  ..buyerId = 'buyer-1'
  ..buyerName = 'Mekonnen Tadesse'
  ..qrToken = 'token'
  ..status = DeliveryStatus.pickedUp
  ..createdAt = DateTime(2026, 11, 20)
  ..updatedAt = DateTime(2026, 11, 20)
  ..version = 1;

void main() {
  group('deletionBlockerTarget', () {
    ({DeletionBlockerKind kind, String id})? target(String type, String id) =>
        deletionBlockerTarget(AccountDeletionBlocker(type: type, id: id));

    test('an order, a dispute and a payment review open their order', () {
      for (final type in ['order', 'dispute', 'payment_review']) {
        expect(
          target(type, 'ord-1'),
          (kind: DeletionBlockerKind.order, id: 'ord-1'),
          reason: type,
        );
      }
    });

    test("an order's escrow opens the order; an older escrow opens nothing",
        () {
      expect(
        target('escrow', 'escrow-order-ord-1'),
        (kind: DeletionBlockerKind.order, id: 'ord-1'),
      );
      expect(target('escrow', 'esc-1'), isNull);
      expect(target('escrow', 'escrow-order-'), isNull);
    });

    test('a delivery opens the delivery', () {
      expect(
        target('delivery', 'del-77'),
        (kind: DeletionBlockerKind.delivery, id: 'del-77'),
      );
    });

    test('an unknown type or an id that is not a plain token opens nothing',
        () {
      expect(target('loan', 'x1'), isNull);
      expect(target('order', ''), isNull);
      for (final id in ['../escrow/history', 'a/b', 'ord 1', 'x' * 65]) {
        expect(target('order', id), isNull, reason: id);
        expect(target('delivery', id), isNull, reason: id);
      }
    });
  });

  group('openDeletionBlocker', () {
    late MockAuthBloc auth;
    late MockDeliveryBloc deliveries;
    late MockDeliveryRepository repository;
    late List<String> openedOrders;

    setUp(() {
      auth = MockAuthBloc();
      when(() => auth.state).thenReturn(
        const AuthAuthenticated(
          userId: 'trucker-1',
          tokens: AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i'),
          role: UserRole.trucker,
        ),
      );
      when(() => auth.stream).thenAnswer((_) => const Stream.empty());
      repository = MockDeliveryRepository();
      deliveries = MockDeliveryBloc();
      when(() => deliveries.repository).thenReturn(repository);
      when(() => deliveries.state).thenReturn(const DeliveryInitial());
      when(() => deliveries.stream).thenAnswer((_) => const Stream.empty());
      openedOrders = [];
    });

    Future<void> pumpAndOpen(
      WidgetTester tester,
      AccountDeletionBlocker blocker, {
      MarketplaceSync? sync,
    }) async {
      await tester.pumpWidget(
        MultiProvider(
          providers: [
            BlocProvider<AuthBloc>.value(value: auth),
            BlocProvider<DeliveryBloc>.value(value: deliveries),
            Provider<MarketplaceSync?>.value(value: sync),
          ],
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: Scaffold(
              body: Builder(
                builder: (context) => TextButton(
                  onPressed: () => openDeletionBlocker(
                    context,
                    blocker,
                    openOrder: (_, orderId) async => openedOrders.add(orderId),
                  ),
                  child: const Text('open'),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
    }

    testWidgets('an order blocker opens the order detail', (tester) async {
      await pumpAndOpen(
        tester,
        const AccountDeletionBlocker(type: 'escrow', id: 'escrow-order-ord-9'),
      );

      expect(openedOrders, ['ord-9']);
      expect(find.byType(DeliveryDetailScreen), findsNothing);
    });

    testWidgets('a delivery blocker opens the delivery detail', (tester) async {
      when(
        () => repository.getDeliveryById('del-77', userId: 'trucker-1'),
      ).thenAnswer((_) async => _delivery('del-77'));

      await pumpAndOpen(
        tester,
        const AccountDeletionBlocker(type: 'delivery', id: 'del-77'),
      );

      final screen = tester.widget<DeliveryDetailScreen>(
        find.byType(DeliveryDetailScreen),
      );
      expect(screen.delivery.deliveryId, 'del-77');
      expect(screen.currentUserId, 'trucker-1');
      expect(openedOrders, isEmpty);
    });

    testWidgets('a delivery the phone lacks is pulled first', (tester) async {
      final sync = MockMarketplaceSync();
      var pulled = false;
      when(sync.pullDeliveries).thenAnswer((_) async => pulled = true);
      when(
        () => repository.getDeliveryById('del-77', userId: 'trucker-1'),
      ).thenAnswer((_) async => pulled ? _delivery('del-77') : null);

      await pumpAndOpen(
        tester,
        const AccountDeletionBlocker(type: 'delivery', id: 'del-77'),
        sync: sync,
      );

      verify(sync.pullDeliveries).called(1);
      expect(find.byType(DeliveryDetailScreen), findsOneWidget);
    });

    testWidgets('a delivery still missing says it cannot be found',
        (tester) async {
      when(
        () => repository.getDeliveryById('del-77', userId: 'trucker-1'),
      ).thenAnswer((_) async => null);

      await pumpAndOpen(
        tester,
        const AccountDeletionBlocker(type: 'delivery', id: 'del-77'),
      );

      expect(find.byType(DeliveryDetailScreen), findsNothing);
      expect(find.text('Delivery not found'), findsOneWidget);
    });
  });
}
