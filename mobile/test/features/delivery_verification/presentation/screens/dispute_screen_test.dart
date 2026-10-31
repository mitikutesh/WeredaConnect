import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/dispute_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

class FakeDeliveryState extends Fake implements DeliveryState {}

Delivery _delivery({DateTime? confirmedAt}) {
  return Delivery()
    ..deliveryId = 'delivery-001-abcd-efgh'
    ..tripId = 'trip-001'
    ..listingId = 'listing-001'
    ..farmerId = 'farmer-001'
    ..farmerName = 'Abebe Kebede'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Tadesse Worku'
    ..buyerId = 'buyer-001'
    ..buyerName = 'Mekonnen Tadesse'
    ..qrToken = 'test-token'
    ..status = DeliveryStatus.delivered
    ..confirmedAt = confirmedAt
    ..createdAt = DateTime(2026, 2, 20)
    ..updatedAt = DateTime(2026, 2, 20)
    ..version = 1;
}

void main() {
  late MockDeliveryBloc bloc;

  setUpAll(() {
    registerFallbackValue(FakeDeliveryEvent());
    registerFallbackValue(FakeDeliveryState());
  });

  setUp(() {
    bloc = MockDeliveryBloc();
    when(() => bloc.state).thenReturn(const DeliveryInitial());
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget build(Delivery delivery) {
    return BlocProvider<DeliveryBloc>.value(
      value: bloc,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: DisputeScreen(delivery: delivery, userId: 'buyer-001'),
      ),
    );
  }

  group('DisputeScreen', () {
    testWidgets('names the delivery and the time left while the window is open',
        (tester) async {
      await tester.pumpWidget(
        build(
          _delivery(
            confirmedAt:
                DateTime.now().toUtc().subtract(const Duration(hours: 1)),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Raise Dispute'), findsOneWidget);
      expect(find.textContaining('Delivery #delivery'), findsOneWidget);
      expect(find.byIcon(Icons.timer), findsOneWidget);
      expect(find.textContaining('Time remaining'), findsOneWidget);
      expect(find.text('REASON FOR DISPUTE'), findsOneWidget);
      final submit = find.widgetWithText(FilledButton, 'Submit Dispute');
      expect(submit, findsOneWidget);
      expect(tester.widget<FilledButton>(submit).onPressed, isNotNull);
    });

    testWidgets('an empty reason is refused', (tester) async {
      await tester.pumpWidget(
        build(
          _delivery(
            confirmedAt:
                DateTime.now().toUtc().subtract(const Duration(hours: 1)),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.widgetWithText(FilledButton, 'Submit Dispute'));
      await tester.pumpAndSettle();

      expect(find.text('Please enter a reason'), findsOneWidget);
      expect(find.byType(AlertDialog), findsNothing);
    });

    testWidgets('an expired window disables the form and Submit',
        (tester) async {
      await tester.pumpWidget(
        build(
          _delivery(
            confirmedAt:
                DateTime.now().toUtc().subtract(const Duration(hours: 49)),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.timer_off), findsOneWidget);
      expect(find.text('Dispute window expired'), findsOneWidget);
      final submit = find.widgetWithText(FilledButton, 'Submit Dispute');
      expect(tester.widget<FilledButton>(submit).onPressed, isNull);
    });
  });
}
