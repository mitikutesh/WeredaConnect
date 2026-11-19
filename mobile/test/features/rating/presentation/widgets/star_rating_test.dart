import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/rating/presentation/widgets/star_rating.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// BUG-L16: the stars of the rating picker were five unnamed buttons.
void main() {
  Widget host(Widget child, {Locale locale = const Locale('en')}) =>
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: locale,
        home: Scaffold(body: Center(child: child)),
      );

  testWidgets('each star of the picker is named and sets its value',
      (tester) async {
    int? picked;
    await tester.pumpWidget(
      host(StarRating(rating: 2, onRatingChanged: (v) => picked = v)),
    );

    for (var star = 1; star <= 5; star++) {
      expect(find.byTooltip('$star of 5 stars'), findsOneWidget);
    }
    await tester.tap(find.byTooltip('4 of 5 stars'));
    expect(picked, 4);
  });

  testWidgets('the names are in Amharic for an Amharic user', (tester) async {
    await tester.pumpWidget(
      host(
        StarRating(rating: 0, onRatingChanged: (_) {}),
        locale: const Locale('am'),
      ),
    );
    final am = lookupAppLocalizations(const Locale('am'));

    expect(find.byTooltip(am.ratingStarsOf(3, 5)), findsOneWidget);
    expect(find.byTooltip('3 of 5 stars'), findsNothing);
  });

  testWidgets('a read-only rating has no buttons to name', (tester) async {
    await tester.pumpWidget(host(const StarRating(rating: 3.5)));

    expect(find.byType(IconButton), findsNothing);
    expect(find.byType(Tooltip), findsNothing);
    expect(find.byIcon(Icons.star_half), findsOneWidget);
  });

  testWidgets('without the app strings the picker still works', (tester) async {
    int? picked;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: StarRating(rating: 0, onRatingChanged: (v) => picked = v),
        ),
      ),
    );

    await tester.tap(find.byType(IconButton).at(2));
    expect(tester.takeException(), isNull);
    expect(picked, 3);
  });
}
