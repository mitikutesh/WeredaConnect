import 'package:equatable/equatable.dart';

/// Base class for onboarding states.
abstract class OnboardingState extends Equatable {
  const OnboardingState();

  @override
  List<Object?> get props => [];
}

/// Initial state before checking first launch status.
class OnboardingInitial extends OnboardingState {
  const OnboardingInitial();
}

/// State indicating this is the user's first launch (should show welcome screen).
class FirstLaunch extends OnboardingState {
  const FirstLaunch();
}

/// State indicating the user is returning (has seen welcome screen before).
class ReturningUser extends OnboardingState {
  const ReturningUser();
}
