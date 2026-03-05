import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/models/user.dart';

/// Base class for authentication events.
abstract class AuthEvent extends Equatable {
  const AuthEvent();

  @override
  List<Object?> get props => [];
}

/// Event to check if user is already authenticated.
class AuthCheckRequested extends AuthEvent {
  const AuthCheckRequested();
}

/// Event to register a new user, optionally with Fayda verification.
///
/// Fayda verification is optional during registration (two-tier model):
/// - Without Fayda: user can trade up to 10,000 ETB
/// - With Fayda: user can trade unlimited amounts
class AuthRegisterRequested extends AuthEvent {
  final String phoneNumber;
  final String password;
  final String name;
  final UserRole role;
  final String woreda;
  final String preferredLanguage;
  final String? faydaVerificationToken;

  // Optional fields from multi-step registration flow
  final String? drivingLicensePath;
  final String? buyerType;
  final String? companyName;

  const AuthRegisterRequested({
    required this.phoneNumber,
    required this.password,
    required this.name,
    required this.role,
    required this.woreda,
    this.faydaVerificationToken,
    this.preferredLanguage = 'am',
    this.drivingLicensePath,
    this.buyerType,
    this.companyName,
  });

  @override
  List<Object?> get props => [
        phoneNumber,
        password,
        name,
        role,
        woreda,
        preferredLanguage,
        faydaVerificationToken,
        drivingLicensePath,
        buyerType,
        companyName,
      ];
}

/// Event to login (auto-detects online/offline).
class AuthLoginRequested extends AuthEvent {
  final String phoneNumber;
  final String password;

  const AuthLoginRequested({
    required this.phoneNumber,
    required this.password,
  });

  @override
  List<Object?> get props => [phoneNumber, password];
}

/// Event to login online (forces online authentication).
class AuthLoginOnlineRequested extends AuthEvent {
  final String phoneNumber;
  final String password;

  const AuthLoginOnlineRequested({
    required this.phoneNumber,
    required this.password,
  });

  @override
  List<Object?> get props => [phoneNumber, password];
}

/// Event to login offline (uses cached credentials).
class AuthLoginOfflineRequested extends AuthEvent {
  final String phoneNumber;
  final String password;

  const AuthLoginOfflineRequested({
    required this.phoneNumber,
    required this.password,
  });

  @override
  List<Object?> get props => [phoneNumber, password];
}

/// Event to refresh authentication tokens.
class AuthRefreshTokenRequested extends AuthEvent {
  final String refreshToken;

  const AuthRefreshTokenRequested({
    required this.refreshToken,
  });

  @override
  List<Object?> get props => [refreshToken];
}

/// Event to cancel an in-flight auth operation (e.g. user navigated back).
class AuthCancelRequested extends AuthEvent {
  const AuthCancelRequested();
}

/// Event to logout user.
class AuthLogoutRequested extends AuthEvent {
  final bool clearCache;

  const AuthLogoutRequested({
    this.clearCache = true,
  });

  @override
  List<Object?> get props => [clearCache];
}
