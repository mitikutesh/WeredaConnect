import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';

/// The list screens' shared Fieldway pieces: the Market's pill chips, the
/// My Crops load bar and the "person · place" eyebrow.
void main() {
  Widget wrap(Widget child) => MaterialApp(home: Scaffold(body: child));

  group('FwFilterPillRow', () {
    testWidgets('shows every option and reports the tapped value',
        (tester) async {
      String? picked;
      await tester.pumpWidget(
        wrap(
          FwFilterPillRow<String?>(
            options: const [
              FwFilterOption(value: null, label: 'All'),
              FwFilterOption(value: 'open', label: 'Open'),
              FwFilterOption(value: 'done', label: 'Done'),
            ],
            selected: null,
            onSelected: (value) => picked = value,
          ),
        ),
      );

      expect(find.byType(FwFilterPill), findsNWidgets(3));
      await tester.tap(find.text('Done'));
      expect(picked, 'done');
    });

    testWidgets('the selected pill is filled field green, the rest white',
        (tester) async {
      await tester.pumpWidget(
        wrap(
          FwFilterPillRow<String>(
            options: const [
              FwFilterOption(value: 'a', label: 'A'),
              FwFilterOption(value: 'b', label: 'B'),
            ],
            selected: 'a',
            onSelected: (_) {},
          ),
        ),
      );

      Material materialOf(String label) => tester.widget<Material>(
            find.descendant(
              of: find.widgetWithText(FwFilterPill, label),
              matching: find.byType(Material),
            ),
          );
      expect(materialOf('A').color, AppTheme.fieldGreen);
      expect(materialOf('B').color, AppTheme.surfaceWhite);
    });
  });

  group('FwLoadBar', () {
    testWidgets('fills the booked share and shows the caption', (tester) async {
      await tester.pumpWidget(
        wrap(
          const FwLoadBar(fraction: 0.6, caption: '30 of 50 quintals booked'),
        ),
      );

      expect(find.text('30 of 50 quintals booked'), findsOneWidget);
      final fill = tester.widget<FractionallySizedBox>(
        find.byType(FractionallySizedBox),
      );
      expect(fill.widthFactor, 0.6);
    });

    testWidgets('clamps an overfull share and draws nothing for none',
        (tester) async {
      await tester.pumpWidget(
        wrap(const FwLoadBar(fraction: 1.4, caption: 'full')),
      );
      expect(
        tester
            .widget<FractionallySizedBox>(find.byType(FractionallySizedBox))
            .widthFactor,
        1.0,
      );

      await tester.pumpWidget(
        wrap(const FwLoadBar(fraction: 0, caption: 'empty')),
      );
      expect(find.byType(FractionallySizedBox), findsNothing);

      await tester.pumpWidget(
        wrap(const FwLoadBar(fraction: double.nan, caption: 'unknown')),
      );
      expect(find.byType(FractionallySizedBox), findsNothing);
    });
  });

  group('fwPersonPlaceEyebrow', () {
    const tokens =
        AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i');

    test('name and place, joined like the boards', () {
      const state = AuthAuthenticated(
        userId: 'u1',
        tokens: tokens,
        role: UserRole.farmer,
        userName: 'Almaz Bekele',
        woreda: 'Meki',
      );
      expect(fwPersonPlaceEyebrow(state, 'Farmer'), 'Almaz Bekele · Meki');
    });

    test('the name alone when the session has no place', () {
      const state = AuthAuthenticated(
        userId: 'u1',
        tokens: tokens,
        role: UserRole.trucker,
        userName: 'Dawit Tesfaye',
      );
      expect(fwPersonPlaceEyebrow(state, 'Trucker'), 'Dawit Tesfaye');
    });

    test('the fallback without a name or a session', () {
      const noName = AuthAuthenticated(
        userId: 'u1',
        tokens: tokens,
        role: UserRole.buyer,
        userName: '  ',
      );
      expect(
        fwPersonPlaceEyebrow(noName, 'Track your orders'),
        'Track your orders',
      );
      expect(fwPersonPlaceEyebrow(const AuthInitial(), 'Fallback'), 'Fallback');
    });
  });
}
