import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';

import '../../../../helpers/wait_for.dart';

class _MockOrderRepository extends Mock implements BaseOrderRepository {}

/// LT-12: the order bloc carries the drop-off landmark to the repository, on
/// create and on the buyer's change, and explains a refusal with its own
/// text (web throws the server's code from /sync; mobile the local guard).
void main() {
  late _MockOrderRepository repo;

  setUp(() {
    repo = _MockOrderRepository();
  });

  Future<List<OrderState>> run(OrderEvent event) async {
    final bloc = OrderBloc(repository: repo);
    final states = <OrderState>[];
    final sub = bloc.stream.listen(states.add);
    bloc.add(event);
    await waitForStates(states, 2);
    await Future<void>.delayed(Duration.zero);
    await sub.cancel();
    await bloc.close();
    return states;
  }

  const change = UpdateDropoffNote(
    orderId: 'o-1',
    buyerId: 'buyer-1',
    dropoffNote: 'Kality, gate 2',
  );

  test('a saved change ends in DropoffNoteSaved', () async {
    when(() => repo.updateDropoffNote(any(), any(), any()))
        .thenAnswer((_) async {});

    final states = await run(change);

    expect(states.first, isA<OrderLoading>());
    expect(states.last, isA<DropoffNoteSaved>());
    verify(() => repo.updateDropoffNote('o-1', 'buyer-1', 'Kality, gate 2'))
        .called(1);
  });

  for (final (name, error, code) in [
    (
      'after pickup',
      const SyncRejectedException(
        'ORDER_DROPOFF_NOTE_LOCKED: The drop-off note can only be changed '
        'before pickup.',
      ),
      AppErrorCode.dropoffNoteLocked,
    ),
    (
      'a phone number',
      const SyncRejectedException(
        'ORDER_CONTACT_IN_TEXT: Remove the phone number from dropoffNote. '
        'Contact opens after payment is held in escrow.',
      ),
      AppErrorCode.contactInText,
    ),
    (
      'the local guard',
      const OrderActionNotAllowedException('not the buyer'),
      AppErrorCode.orderRefused,
    ),
  ]) {
    test('a refusal ($name) ends in its own error, never success', () async {
      when(() => repo.updateDropoffNote(any(), any(), any()))
          .thenAnswer((_) async => throw error);

      final states = await run(change);

      expect(states.last, isA<OrderError>());
      expect((states.last as OrderError).code, code);
    });
  }

  test('an order create passes the note to the repository', () async {
    final created = Order()
      ..orderId = 'o-1'
      ..buyerId = 'buyer-1'
      ..status = OrderStatus.created;
    when(
      () => repo.createOrder(
        listingId: any(named: 'listingId'),
        buyerId: any(named: 'buyerId'),
        farmerId: any(named: 'farmerId'),
        buyerName: any(named: 'buyerName'),
        farmerName: any(named: 'farmerName'),
        cropType: any(named: 'cropType'),
        quantity: any(named: 'quantity'),
        pricePerUnit: any(named: 'pricePerUnit'),
        tripId: any(named: 'tripId'),
        truckerId: any(named: 'truckerId'),
        truckerName: any(named: 'truckerName'),
        transportCost: any(named: 'transportCost'),
        commissionRate: any(named: 'commissionRate'),
        dropoffNote: any(named: 'dropoffNote'),
      ),
    ).thenAnswer((_) async => created);

    final states = await run(
      const CreateOrder(
        listingId: 'crop-1',
        buyerId: 'buyer-1',
        farmerId: 'farmer-1',
        buyerName: 'Buyer',
        farmerName: 'Farmer',
        cropType: 'teff',
        quantity: 500,
        pricePerUnit: 60,
        tripId: 'trip-1',
        truckerId: 'trucker-1',
        truckerName: 'Trucker',
        transportCost: 600,
        dropoffNote: 'Merkato, near Anwar Mosque',
      ),
    );

    expect(states.last, isA<OrderCreated>());
    final note = verify(
      () => repo.createOrder(
        listingId: any(named: 'listingId'),
        buyerId: any(named: 'buyerId'),
        farmerId: any(named: 'farmerId'),
        buyerName: any(named: 'buyerName'),
        farmerName: any(named: 'farmerName'),
        cropType: any(named: 'cropType'),
        quantity: any(named: 'quantity'),
        pricePerUnit: any(named: 'pricePerUnit'),
        tripId: any(named: 'tripId'),
        truckerId: any(named: 'truckerId'),
        truckerName: any(named: 'truckerName'),
        transportCost: any(named: 'transportCost'),
        commissionRate: any(named: 'commissionRate'),
        dropoffNote: captureAny(named: 'dropoffNote'),
      ),
    ).captured.single;
    expect(note, 'Merkato, near Anwar Mosque');
  });
}
