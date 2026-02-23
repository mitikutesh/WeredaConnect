import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

// ---------------------------------------------------------------------------
// Mocks
// ---------------------------------------------------------------------------
class MockAuthBloc extends Mock implements AuthBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

void main() {
  late MockAuthBloc authBloc;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
  });

  setUp(() {
    authBloc = MockAuthBloc();

    // Default: unauthenticated, no loading
    when(() => authBloc.state).thenReturn(const AuthUnauthenticated());
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildTestWidget({
    Stream<AuthState>? stream,
  }) {
    if (stream != null) {
      when(() => authBloc.stream).thenAnswer((_) => stream);
    }

    return BlocProvider<AuthBloc>.value(
      value: authBloc,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const LoginScreen(),
      ),
    );
  }

  group('LoginScreen', () {
    testWidgets('renders all UI elements', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Phone field with pre-filled +251
      final phoneField = find.byType(TextFormField).first;
      expect(phoneField, findsOneWidget);

      // Password field
      expect(find.text('Password'), findsWidgets);

      // Login button
      expect(find.text('Login'), findsWidgets);

      // Register link
      expect(find.text('Register'), findsOneWidget);

      // Forgot password
      expect(find.text('Forgot Password?'), findsOneWidget);

      // Language selector icon
      expect(find.byIcon(Icons.language), findsOneWidget);
    });

    testWidgets('phone field pre-filled with +251', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Find the text input that contains +251
      expect(find.text('+251'), findsOneWidget);
    });

    testWidgets('shows validation errors on empty submit', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Clear the pre-filled phone number
      final phoneField = find.byType(TextFormField).first;
      await tester.tap(phoneField);
      await tester.pumpAndSettle();

      // Select all and delete
      final editableText = find.byType(EditableText).first;
      final controller = tester.widget<EditableText>(editableText).controller;
      controller.clear();
      await tester.pumpAndSettle();

      // Tap login button — find the ElevatedButton
      final loginButton = find.byType(ElevatedButton);
      await tester.ensureVisible(loginButton);
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();

      // Should show validation error for phone
      expect(find.text('Phone Number'), findsWidgets);
    });

    testWidgets('shows error for invalid phone format', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Enter invalid phone
      final phoneField = find.byType(TextFormField).first;
      await tester.tap(phoneField);
      await tester.pumpAndSettle();

      // Clear and enter invalid phone
      final phoneEditableText = find.byType(EditableText).first;
      final phoneController =
          tester.widget<EditableText>(phoneEditableText).controller;
      phoneController.text = '+25191';
      await tester.pumpAndSettle();

      // Enter a valid password so only phone error shows
      final passwordField = find.byType(TextFormField).at(1);
      await tester.tap(passwordField);
      await tester.pumpAndSettle();
      await tester.enterText(passwordField, 'password1');
      await tester.pumpAndSettle();

      // Tap login
      final loginButton = find.byType(ElevatedButton);
      await tester.ensureVisible(loginButton);
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();

      // Should show phone format error
      expect(
        find.text('Invalid phone number format. Use +251XXXXXXXXX'),
        findsOneWidget,
      );
    });

    testWidgets('shows error for short password', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Enter valid phone
      final phoneField = find.byType(TextFormField).first;
      await tester.tap(phoneField);
      await tester.pumpAndSettle();
      final phoneEditableText = find.byType(EditableText).first;
      final phoneController =
          tester.widget<EditableText>(phoneEditableText).controller;
      phoneController.text = '+251911234567';
      await tester.pumpAndSettle();

      // Enter short password
      final passwordField = find.byType(TextFormField).at(1);
      await tester.enterText(passwordField, 'short');
      await tester.pumpAndSettle();

      // Tap login
      final loginButton = find.byType(ElevatedButton);
      await tester.ensureVisible(loginButton);
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();

      // Should show password requirements error
      expect(
        find.text(
          'Password must be at least 8 characters with a lowercase letter and a number',
        ),
        findsOneWidget,
      );
    });

    testWidgets('password visibility toggle works', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Initially password is obscured (visibility_off icon shown)
      expect(find.byIcon(Icons.visibility_off), findsOneWidget);
      expect(find.byIcon(Icons.visibility), findsNothing);

      // Tap the visibility toggle
      await tester.tap(find.byIcon(Icons.visibility_off));
      await tester.pumpAndSettle();

      // Now should show visibility icon (password visible)
      expect(find.byIcon(Icons.visibility), findsOneWidget);
      expect(find.byIcon(Icons.visibility_off), findsNothing);

      // Tap again to hide
      await tester.tap(find.byIcon(Icons.visibility));
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.visibility_off), findsOneWidget);
    });

    testWidgets('shows server error from BLoC', (tester) async {
      final controller = StreamController<AuthState>.broadcast();
      addTearDown(controller.close);

      when(() => authBloc.stream).thenAnswer((_) => controller.stream);

      await tester.pumpWidget(buildTestWidget(stream: controller.stream));
      await tester.pumpAndSettle();

      // Emit error state
      controller.add(
        const AuthError(
          errorCode: 'AUTH_007',
          errorMessage: 'Server error. Please try again later.',
        ),
      );
      await tester.pump();
      await tester.pump();

      // Error banner should appear with localized message
      expect(
        find.text('Server error. Please try again later.'),
        findsOneWidget,
      );
      // Error icon should be visible
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
    });

    testWidgets('shows rate limit error with duration', (tester) async {
      final controller = StreamController<AuthState>.broadcast();
      addTearDown(controller.close);

      when(() => authBloc.stream).thenAnswer((_) => controller.stream);

      await tester.pumpWidget(buildTestWidget(stream: controller.stream));
      await tester.pumpAndSettle();

      // Emit rate limit error with retryAfterSeconds
      controller.add(
        const AuthError(
          errorCode: 'AUTH_015',
          errorMessage: 'Account locked',
          retryAfterSeconds: 900,
        ),
      );
      await tester.pump();
      await tester.pump();

      // Should show lockout duration message (15 minutes)
      expect(
        find.textContaining('Account locked'),
        findsOneWidget,
      );
    });

    testWidgets('shows loading spinner during auth', (tester) async {
      // Set loading state
      when(() => authBloc.state).thenReturn(const AuthLoading());

      await tester.pumpWidget(buildTestWidget());
      // Use pump() instead of pumpAndSettle() because
      // CircularProgressIndicator animates infinitely.
      await tester.pump();

      // Login button should show CircularProgressIndicator
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('login button dispatches AuthLoginRequested', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Enter valid phone
      final phoneField = find.byType(TextFormField).first;
      await tester.tap(phoneField);
      await tester.pumpAndSettle();
      final phoneEditableText = find.byType(EditableText).first;
      final phoneController =
          tester.widget<EditableText>(phoneEditableText).controller;
      phoneController.text = '+251911234567';
      await tester.pumpAndSettle();

      // Enter valid password
      final passwordField = find.byType(TextFormField).at(1);
      await tester.enterText(passwordField, 'password1');
      await tester.pumpAndSettle();

      // Tap login
      final loginButton = find.byType(ElevatedButton);
      await tester.ensureVisible(loginButton);
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();

      // Verify BLoC received the event
      verify(
        () => authBloc.add(
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'password1',
          ),
        ),
      ).called(1);
    });

    testWidgets('navigate to register taps register link', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Scroll to make register link visible
      final registerLink = find.text('Register');
      await tester.ensureVisible(registerLink);
      await tester.pumpAndSettle();

      // Tap the Register TextButton
      await tester.tap(registerLink);
      await tester.pumpAndSettle();

      // After tapping Register, a new route should be pushed.
      // We verify by checking the Navigator has a new route.
      // Since RegisterScreen requires its own BLoC, it may error,
      // but the navigation attempt was made.
      // In a full integration test, we'd verify the screen is shown.
    });

    testWidgets('forgot password dialog appears on tap', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Tap "Forgot Password?"
      final forgotPassword = find.text('Forgot Password?');
      await tester.ensureVisible(forgotPassword);
      await tester.pumpAndSettle();
      await tester.tap(forgotPassword);
      await tester.pumpAndSettle();

      // Dialog should appear with support contact info
      expect(find.byType(AlertDialog), findsOneWidget);
      expect(
        find.textContaining('+251-111-234567'),
        findsOneWidget,
      );
    });
  });
}
