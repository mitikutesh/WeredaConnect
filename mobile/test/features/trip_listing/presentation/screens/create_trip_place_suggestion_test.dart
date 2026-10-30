import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/route_preview_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockTripListingBloc extends Mock implements TripListingBloc {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

/// The semantics node that a browser click at [point] (physical pixels in
/// [node]'s parent space) reaches on Flutter web with semantics on. The web
/// engine renders every node as a DOM element and stacks a node's children by
/// hit-test order (z-index), above the node itself. Only elements with a tap
/// action catch the click.
SemanticsNode? _webHit(SemanticsNode node, Offset point) {
  var local = point;
  final transform = node.transform;
  if (transform != null) {
    final inverse = Matrix4.tryInvert(transform);
    if (inverse == null) return null;
    local = MatrixUtils.transformPoint(inverse, point);
  }
  final children =
      node.debugListChildrenInOrder(DebugSemanticsDumpOrder.inverseHitTest);
  for (final child in children.reversed) {
    final hit = _webHit(child, local);
    if (hit != null) return hit;
  }
  return node.getSemanticsData().hasAction(SemanticsAction.tap) &&
          node.rect.contains(local)
      ? node
      : null;
}

// SIM-59: on Create Trip step 1 the "To" suggestions open over the departure
// date field (and "From" suggestions over the "To" field). On web with
// semantics on, clicking a suggestion opened the date picker instead.
void main() {
  late MockAuthBloc authBloc;
  late MockTripListingBloc tripListingBloc;

  setUpAll(() {
    // The screen uses the shared repository; load the small fixture so the
    // suggestions are deterministic (this file runs in its own isolate).
    PlaceRepository.instance.loadFromStringForTest(
      File('test/fixtures/woredas_sample.json').readAsStringSync(),
    );
  });

  setUp(() {
    authBloc = MockAuthBloc();
    tripListingBloc = MockTripListingBloc();
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'trucker-001',
        tokens: _testTokens,
        role: UserRole.trucker,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => tripListingBloc.state).thenReturn(const TripListingInitial());
    when(() => tripListingBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildTestWidget() {
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<TripListingBloc>.value(value: tripListingBloc),
      ],
      child: const MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: CreateTripScreen(),
      ),
    );
  }

  final fromField = find.descendant(
    of: find.byKey(const Key('create_trip_from')),
    matching: find.byType(TextFormField),
  );
  final toField = find.descendant(
    of: find.byKey(const Key('create_trip_to')),
    matching: find.byType(TextFormField),
  );
  final dateField = find.byKey(const Key('create_trip_departure_date'));

  String textOf(WidgetTester tester, Finder field) =>
      tester.widget<TextFormField>(field).controller!.text;

  /// Focuses [field], types [query] and lets the selector's debounce fire.
  Future<void> typeQuery(
    WidgetTester tester,
    Finder field,
    String query,
  ) async {
    await tester.tap(field);
    await tester.enterText(field, query);
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pump();
  }

  /// The suggestion tile whose Amharic line is [amharic] (the English name
  /// can also appear as a region label, so it is not a unique finder).
  Finder suggestion(String amharic) => find
      .ancestor(of: find.text(amharic), matching: find.byType(InkWell))
      .first;

  SemanticsNode? webClickTarget(WidgetTester tester, Offset point) {
    final root = tester.getSemantics(find.byType(Scaffold)).owner!;
    return _webHit(
      root.rootSemanticsNode!,
      point * tester.view.devicePixelRatio,
    );
  }

  /// Clicks at [point] the way Flutter web does with semantics on: the
  /// browser focuses the element under the pointer if it is focusable (the
  /// engine forwards that as [SemanticsAction.focus]), then the engine sends
  /// [SemanticsAction.tap] to that same element's node.
  Future<void> webClick(WidgetTester tester, Offset point) async {
    final target = webClickTarget(tester, point);
    expect(target, isNotNull, reason: 'nothing clickable at $point');
    if (target!.getSemanticsData().hasAction(SemanticsAction.focus)) {
      target.owner!.performAction(target.id, SemanticsAction.focus);
      await tester.pump();
    }
    // A focus change can close the list and remove the clicked node.
    if (target.attached) {
      target.owner!.performAction(target.id, SemanticsAction.tap);
    }
    await tester.pumpAndSettle();
  }

  /// Center of the area where [top] covers [below].
  Offset overlapCenter(WidgetTester tester, Finder top, Finder below) {
    final overlap = tester.getRect(top).intersect(tester.getRect(below));
    expect(overlap.isEmpty, isFalse, reason: 'no overlap to test');
    return overlap.center;
  }

  testWidgets('route fields keep their top-to-bottom order', (tester) async {
    await tester.pumpWidget(buildTestWidget());
    await tester.pumpAndSettle();

    final from = tester.getRect(fromField);
    final to = tester.getRect(toField);
    final date = tester.getRect(dateField);
    expect(from.top, lessThan(to.top));
    expect(to.top, lessThan(date.top));
    expect(from.top, lessThan(150)); // starts at the top, not the bottom
  });

  testWidgets('a web click on a "To" suggestion over the date field selects it',
      (tester) async {
    await tester.pumpWidget(buildTestWidget());
    await tester.pumpAndSettle();

    await typeQuery(tester, toField, 'Ad');
    final addis = suggestion('አዲስ አበባ');
    final point = overlapCenter(tester, addis, dateField);
    expect(webClickTarget(tester, point)!.label, startsWith('Addis Ababa'));

    await webClick(tester, point);

    expect(textOf(tester, toField), 'Addis Ababa');
    expect(find.byType(DatePickerDialog), findsNothing);
    expect(find.text('Select departure date'), findsOneWidget);
    expect(find.text('አዲስ አበባ'), findsNothing); // list closed
  });

  testWidgets('a web click on a "From" suggestion over "To" selects it',
      (tester) async {
    await tester.pumpWidget(buildTestWidget());
    await tester.pumpAndSettle();

    await typeQuery(tester, fromField, 'Ad');
    final adama = suggestion('አዳማ');
    final point = overlapCenter(tester, adama, toField);
    expect(webClickTarget(tester, point)!.label, startsWith('Adama'));

    await webClick(tester, point);

    expect(textOf(tester, fromField), 'Adama');
    expect(textOf(tester, toField), isEmpty);
    final toEditable = tester.widget<EditableText>(
      find.descendant(of: toField, matching: find.byType(EditableText)),
    );
    expect(toEditable.focusNode.hasFocus, isFalse);
    expect(find.byType(DatePickerDialog), findsNothing);
  });

  testWidgets('a touch tap on a "To" suggestion selects it', (tester) async {
    await tester.pumpWidget(buildTestWidget());
    await tester.pumpAndSettle();

    await typeQuery(tester, toField, 'Ad');
    final point = overlapCenter(tester, suggestion('አዲስ አበባ'), dateField);
    await tester.tapAt(point);
    await tester.pumpAndSettle();

    expect(textOf(tester, toField), 'Addis Ababa');
    expect(find.byType(DatePickerDialog), findsNothing);
  });

  testWidgets(
      'screen reader activation selects a suggestion; tiles take no focus',
      (tester) async {
    await tester.pumpWidget(buildTestWidget());
    await tester.pumpAndSettle();

    await typeQuery(tester, toField, 'Ad');
    final node = tester.getSemantics(suggestion('አዳማ'));
    final data = node.getSemanticsData();
    expect(data.hasAction(SemanticsAction.tap), isTrue);
    expect(data.hasAction(SemanticsAction.focus), isFalse);
    expect(data.label, contains('Adama'));

    node.owner!.performAction(node.id, SemanticsAction.tap);
    await tester.pumpAndSettle();

    expect(textOf(tester, toField), 'Adama');
    expect(find.byType(DatePickerDialog), findsNothing);
  });

  // Trucker · Create trip board: once both ends are picked places with
  // coordinates, the page draws the route and says how far it is.
  testWidgets('picking both places shows the route preview', (tester) async {
    tester.view.physicalSize = const Size(500, 1800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(buildTestWidget());
    await tester.pumpAndSettle();
    expect(find.byType(RoutePreviewCard), findsNothing);

    await typeQuery(tester, fromField, 'Mek');
    await tester.tap(suggestion('መቂ'));
    await tester.pumpAndSettle();
    expect(find.byType(RoutePreviewCard), findsNothing);

    await typeQuery(tester, toField, 'Ad');
    await tester.tap(suggestion('አዲስ አበባ'));
    await tester.pumpAndSettle();

    expect(find.byType(RoutePreviewCard), findsOneWidget);
    expect(
      find.textContaining('km in a straight line, drawn on your phone'),
      findsOneWidget,
    );
    expect(
      find.bySemanticsLabel(RegExp('Route preview from .* to Addis Ababa')),
      findsOneWidget,
    );
  });
}
