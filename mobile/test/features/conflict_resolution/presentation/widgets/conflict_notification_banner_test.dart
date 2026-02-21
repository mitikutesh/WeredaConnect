import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/conflict_resolution/presentation/widgets/conflict_notification_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

Widget _buildWidget(Widget child) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: Scaffold(body: child),
  );
}

void main() {
  group('ConflictNotificationBanner', () {
    testWidgets('renders single conflict message when count is 1',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          ConflictNotificationBanner(
            entityType: 'crop_listing',
            conflictCount: 1,
            onViewChanges: () {},
            onDismiss: () {},
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Single conflict message: "Your crop_listing was updated on another device"
      expect(find.textContaining('crop_listing'), findsOneWidget);
      expect(find.textContaining('updated on another device'), findsOneWidget);

      // Should NOT show count-based message
      expect(find.textContaining('items were updated'), findsNothing);
    });

    testWidgets('renders multiple conflict message when count > 1',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          ConflictNotificationBanner(
            entityType: 'crop_listing',
            conflictCount: 5,
            onViewChanges: () {},
            onDismiss: () {},
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Multiple conflict message: "5 items were updated on another device"
      expect(find.textContaining('5'), findsOneWidget);
      expect(find.textContaining('items were updated'), findsOneWidget);
    });

    testWidgets('"View Changes" button calls onViewChanges callback',
        (WidgetTester tester) async {
      var viewChangesCalled = false;

      await tester.pumpWidget(
        _buildWidget(
          ConflictNotificationBanner(
            entityType: 'crop_listing',
            conflictCount: 1,
            onViewChanges: () => viewChangesCalled = true,
            onDismiss: () {},
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Find and tap "View Changes" button
      expect(find.text('View Changes'), findsOneWidget);
      await tester.tap(find.text('View Changes'));
      await tester.pump();

      expect(viewChangesCalled, isTrue);
    });

    testWidgets('has amber background and sync icon',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          ConflictNotificationBanner(
            entityType: 'crop_listing',
            conflictCount: 1,
            onViewChanges: () {},
            onDismiss: () {},
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Sync icon is present
      expect(find.byIcon(Icons.sync), findsOneWidget);

      // MaterialBanner has amber background
      final banner = tester.widget<MaterialBanner>(find.byType(MaterialBanner));
      expect(banner.backgroundColor, equals(Colors.amber.shade50));
    });

    testWidgets('text has correct style (fontSize 16, maxLines 2)',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          ConflictNotificationBanner(
            entityType: 'crop_listing',
            conflictCount: 1,
            onViewChanges: () {},
            onDismiss: () {},
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Find the content Text widget inside the MaterialBanner
      final banner = tester.widget<MaterialBanner>(find.byType(MaterialBanner));
      final contentText = banner.content as Text;
      expect(contentText.style?.fontSize, equals(16));
      expect(contentText.maxLines, equals(2));
      expect(contentText.overflow, equals(TextOverflow.ellipsis));
    });

    testWidgets('renders as TextButton for View Changes action',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          ConflictNotificationBanner(
            entityType: 'delivery',
            conflictCount: 3,
            onViewChanges: () {},
            onDismiss: () {},
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Action is a TextButton
      expect(find.widgetWithText(TextButton, 'View Changes'), findsOneWidget);
    });
  });
}
