import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';

import '../../../../helpers/wait_for.dart';

class _MockRepository extends Mock implements BaseBuyerIntentRepository {}

class _MockMatching extends Mock implements BaseMatchingService {}

BuyerIntent _intent(String id) => BuyerIntent()
  ..intentId = id
  ..buyerId = 'buyer-1'
  ..cropType = 'teff'
  ..quantityNeeded = 1
  ..maxPricePerKg = 1
  ..deliveryWoreda = 'Adama'
  ..status = BuyerIntentStatus.active
  ..createdAt = DateTime.utc(2026, 11)
  ..updatedAt = DateTime.utc(2026, 11)
  ..version = 1;

List<String> _ids(BuyerIntentState state) =>
    (state as ActiveIntentsLoaded).intents.map((i) => i.intentId).toList();

/// SIM-56: My Requests shows what the device has and reads the server's copy
/// back once per buyer while the app runs, and on every pull to refresh.
void main() {
  late _MockRepository repo;
  late _MockMatching matching;
  late BuyerIntentBloc bloc;
  late List<BuyerIntentState> states;
  late StreamSubscription<BuyerIntentState> sub;
  late List<BuyerIntent> onDevice;

  setUpAll(() => registerFallbackValue(_MockRepository()));

  setUp(() {
    repo = _MockRepository();
    matching = _MockMatching();
    onDevice = [];
    when(() => repo.getActiveIntents('buyer-1'))
        .thenAnswer((_) async => List.of(onDevice));
    when(() => matching.refreshMatchCounts(any(), any()))
        .thenAnswer((_) async {});
    bloc = BuyerIntentBloc(repository: repo, matchingService: matching);
    states = [];
    sub = bloc.stream.listen(states.add);
  });

  tearDown(() async {
    await sub.cancel();
    await bloc.close();
  });

  test('nothing on the device: the list waits for the server copy', () async {
    when(() => repo.refreshFromServer('buyer-1')).thenAnswer((_) async {
      onDevice.add(_intent('from-server'));
      return true;
    });

    bloc.add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 2);

    expect(states.first, isA<BuyerIntentLoading>());
    expect(_ids(states[1]), ['from-server']);
    expect(states, hasLength(2));
  });

  test('requests on the device show first, the server copy follows', () async {
    onDevice.add(_intent('local'));
    final read = Completer<bool>();
    when(() => repo.refreshFromServer('buyer-1'))
        .thenAnswer((_) => read.future);

    bloc.add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 2);
    expect(_ids(states[1]), ['local']);

    onDevice.add(_intent('from-server'));
    read.complete(true);
    await waitForStates(states, 3);
    expect(_ids(states[2]), ['local', 'from-server']);
  });

  test('nothing new on the server: the list is shown once', () async {
    onDevice.add(_intent('local'));
    when(() => repo.refreshFromServer('buyer-1'))
        .thenAnswer((_) async => false);

    bloc.add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 2);
    await waitFor(() async {
      try {
        verify(() => repo.refreshFromServer('buyer-1')).called(1);
        return true;
      } on Object {
        return false;
      }
    });

    expect(states, hasLength(2));
  });

  test('offline: the device list stands, and the next load tries again',
      () async {
    onDevice.add(_intent('local'));
    var calls = 0;
    when(() => repo.refreshFromServer('buyer-1')).thenAnswer((_) async {
      calls++;
      throw Exception('offline');
    });

    bloc.add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 2);
    await waitFor(() => calls == 1);
    expect(_ids(states[1]), ['local']);
    expect(states.whereType<BuyerIntentError>(), isEmpty);

    bloc.add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 4);
    await waitFor(() => calls == 2);
    expect(calls, 2);
  });

  test('read once per buyer: a later load uses the device list only', () async {
    when(() => repo.refreshFromServer('buyer-1'))
        .thenAnswer((_) async => false);

    bloc.add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 2);
    bloc.add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 4);

    verify(() => repo.refreshFromServer('buyer-1')).called(1);
  });

  test('pull to refresh reads the server copy again, before the counts',
      () async {
    when(() => repo.refreshFromServer('buyer-1'))
        .thenAnswer((_) async => false);
    bloc.add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 2);

    // The server added a request the refresh lists (a new list, so a new
    // state: an equal one would not be emitted again).
    onDevice.add(_intent('from-server'));
    bloc.add(const RefreshAllMatches(buyerId: 'buyer-1'));
    await waitForStates(states, 3);

    verifyInOrder([
      () => repo.refreshFromServer('buyer-1'),
      () => repo.refreshFromServer('buyer-1'),
      () => matching.refreshMatchCounts('buyer-1', repo),
    ]);
    expect(_ids(states[2]), ['from-server']);
  });

  test('pull to refresh offline still recounts and shows the list', () async {
    onDevice.add(_intent('local'));
    when(() => repo.refreshFromServer('buyer-1'))
        .thenThrow(Exception('offline'));

    bloc.add(const RefreshAllMatches(buyerId: 'buyer-1'));
    await waitForStates(states, 1);

    expect(_ids(states.single), ['local']);
    verify(() => matching.refreshMatchCounts('buyer-1', repo)).called(1);
  });

  test(
      'two loads at once on first run share one read-back and both end '
      'with the requests', () async {
    final read = Completer<bool>();
    when(() => repo.refreshFromServer('buyer-1'))
        .thenAnswer((_) => read.future);

    bloc
      ..add(const LoadActiveIntents(buyerId: 'buyer-1'))
      ..add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 1);
    onDevice.add(_intent('from-server'));
    read.complete(true);
    // Both loads re-read after the one read-back; an equal second list is
    // not emitted again, so the last state is the one that counts.
    await waitFor(() => states.last is ActiveIntentsLoaded);

    verify(() => repo.refreshFromServer('buyer-1')).called(1);
    expect(states.first, isA<BuyerIntentLoading>());
    expect(states.whereType<ActiveIntentsLoaded>(), isNotEmpty);
    for (final loaded in states.whereType<ActiveIntentsLoaded>()) {
      expect(_ids(loaded), ['from-server']);
    }
  });

  test(
      'a read-back that fails after the rows landed (another load wrote '
      'them first) still lists them', () async {
    when(() => repo.refreshFromServer('buyer-1')).thenAnswer((_) async {
      // The other load's write landed; this one's lost on the unique index.
      onDevice.add(_intent('from-server'));
      throw Exception('Unique index violated.');
    });

    bloc.add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 2);

    expect(_ids(states[1]), ['from-server']);
  });

  test('a read-back that added nothing still shows a list the device changed',
      () async {
    onDevice.add(_intent('local'));
    when(() => repo.refreshFromServer('buyer-1')).thenAnswer((_) async {
      // Another load (or the sync) put a request on the device meanwhile.
      onDevice.add(_intent('meanwhile'));
      return false;
    });

    bloc.add(const LoadActiveIntents(buyerId: 'buyer-1'));
    await waitForStates(states, 3);

    expect(_ids(states[1]), ['local']);
    expect(_ids(states[2]), ['local', 'meanwhile']);
  });
}
