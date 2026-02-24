import 'package:equatable/equatable.dart';

/// Base class for onboarding events.
abstract class OnboardingEvent extends Equatable {
  const OnboardingEvent();

  @override
  List<Object?> get props => [];
}

/// Event to check if this is the user's first launch.
class CheckFirstLaunch extends OnboardingEvent {
  const CheckFirstLaunch();
}

/// Event to mark the welcome screen as seen.
class MarkWelcomeSeen extends OnboardingEvent {
  const MarkWelcomeSeen();
}

/// Event to navigate back to the welcome screen (without clearing persisted state).
class ShowWelcomeScreen extends OnboardingEvent {
  const ShowWelcomeScreen();
}
