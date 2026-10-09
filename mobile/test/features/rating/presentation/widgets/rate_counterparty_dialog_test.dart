import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/rating/presentation/widgets/rate_counterparty_dialog.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockRatingRepository extends Mock implements BaseRatingRepository {}

/// ESC-12 (f): a rating that already exists on the server (web conflict)
/// closes the dialog as rated instead of saying "updated elsewhere".
void main() {
  late _MockRatingRepository repo;
  final l10n = lookupAppLocalizations(const Locale('en'));

  setUp(() {
    repo = _MockRatingRepository();
  });

  void createThrows(Object error) {
    when(
      () => repo.createRating(
        orderId: any(named: 'orderId'),
        raterId: any(named: 'raterId'),
        rateeId: any(named: 'rateeId'),
        rateeRole: any(named: 'rateeRole'),
        stars: any(named: 'stars'),
        comment: any(named: 'comment'),
      ),
    ).thenAnswer((_) async => throw error);
  }

  /// Opens the dialog, submits, and returns what the dialog popped with.
  Future<bool?> submit(WidgetTester tester) async {
    bool? popped;
    var closed = false;
    final bloc = RatingBloc(repository: repo);
    addTearDown(bloc.close);
    await tester.pumpWidget(
      BlocProvider<RatingBloc>.value(
        value: bloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: Builder(
              builder: (context) => TextButton(
                onPressed: () async {
                  popped = await showDialog<bool>(
                    context: context,
                    builder: (_) => BlocProvider<RatingBloc>.value(
                      value: bloc,
                      child: const RateCounterpartyDialog(
                        orderId: 'order-1',
                        raterId: 'buyer-1',
                        rateeId: 'farmer-1',
                        rateeRole: 'farmer',
                        rateeName: 'Farmer',
                      ),
                    ),
                  );
                  closed = true;
                },
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    await tester.tap(find.text(l10n.submitRating));
    await tester.pumpAndSettle();
    return closed ? popped : null;
  }

  testWidgets('an existing rating closes the dialog as rated', (tester) async {
    createThrows(const AlreadyRatedException());

    final popped = await submit(tester);

    expect(popped, isTrue);
    expect(find.byType(RateCounterpartyDialog), findsNothing);
    expect(find.text(l10n.alreadyRated), findsOneWidget);
    expect(find.text(l10n.errorConflict), findsNothing);
  });

  testWidgets('any other failure keeps the dialog open with the reason',
      (tester) async {
    createThrows(const SyncRejectedException('RATING_VALIDATION: no'));

    await submit(tester);

    expect(find.byType(RateCounterpartyDialog), findsOneWidget);
    expect(
      find.text(localizeErrorCode(AppErrorCode.server, l10n)),
      findsOneWidget,
    );
  });
}
