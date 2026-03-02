import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/features/onboarding/bloc/onboarding_event.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_state.dart';

/// BLoC for managing onboarding flow state.
///
/// Tracks whether the user has seen the welcome screen using shared preferences.
/// This allows us to show the welcome screen only on first launch or when
/// the user is unauthenticated.
class OnboardingBloc extends Bloc<OnboardingEvent, OnboardingState> {
  static const String _hasSeenWelcomeKey = 'has_seen_welcome';

  OnboardingBloc() : super(const OnboardingInitial()) {
    on<CheckFirstLaunch>(_onCheckFirstLaunch);
    on<MarkWelcomeSeen>(_onMarkWelcomeSeen);
    on<DismissSplash>(_onDismissSplash);
    on<ShowWelcomeScreen>(_onShowWelcomeScreen);
  }

  Future<void> _onCheckFirstLaunch(
    CheckFirstLaunch event,
    Emitter<OnboardingState> emit,
  ) async {
    final prefs = await SharedPreferences.getInstance();
    final hasSeenWelcome = prefs.getBool(_hasSeenWelcomeKey) ?? false;

    if (hasSeenWelcome) {
      emit(const ReturningUser());
    } else {
      emit(const FirstLaunch());
    }
  }

  Future<void> _onMarkWelcomeSeen(
    MarkWelcomeSeen event,
    Emitter<OnboardingState> emit,
  ) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_hasSeenWelcomeKey, true);
    emit(const ReturningUser());
  }

  /// Dismiss the branded splash landing and show the welcome screen.
  void _onDismissSplash(
    DismissSplash event,
    Emitter<OnboardingState> emit,
  ) {
    emit(const FirstLaunch(splashDismissed: true));
  }

  /// Show the welcome screen without clearing the persisted "has seen" flag.
  /// Used when user taps back from LoginScreen to revisit WelcomeScreen.
  void _onShowWelcomeScreen(
    ShowWelcomeScreen event,
    Emitter<OnboardingState> emit,
  ) {
    emit(const FirstLaunch());
  }
}
