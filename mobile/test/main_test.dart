import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/main.dart';

void main() {
  group('WeredaConnectApp', () {
    testWidgets('renders without crashing', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());
      
      // Verify the app renders
      expect(find.byType(WeredaConnectApp), findsOneWidget);
    });

    testWidgets('shows app title in app bar', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());
      await tester.pumpAndSettle();
      
      // Verify app title is displayed
      expect(find.text('WeredaConnect'), findsOneWidget);
    });

    testWidgets('shows welcome message', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());
      await tester.pumpAndSettle();
      
      // Verify welcome message is displayed
      expect(find.text('Welcome to WeredaConnect'), findsOneWidget);
    });

    testWidgets('shows offline ready indicator', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());
      await tester.pumpAndSettle();
      
      // Verify offline ready text is displayed
      expect(find.text('Offline Ready'), findsOneWidget);
    });

    testWidgets('shows agriculture icon', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());
      await tester.pumpAndSettle();
      
      // Verify agriculture icon is displayed
      expect(find.byIcon(Icons.agriculture), findsOneWidget);
    });
  });

  group('HomePage', () {
    testWidgets('renders correctly', (WidgetTester tester) async {
      await tester.pumpWidget(const WeredaConnectApp());
      await tester.pumpAndSettle();
      
      // Verify HomePage is rendered
      expect(find.byType(HomePage), findsOneWidget);
    });
  });

  group('Localization', () {
    testWidgets('supports English locale', (WidgetTester tester) async {
      await tester.pumpWidget(const WeredaConnectApp());
      await tester.pumpAndSettle();

      // Verify English text is displayed
      expect(find.text('Welcome to WeredaConnect'), findsOneWidget);
    });
  });
}

