import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/database/schemas/user.dart';

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

/// Event to register a new user.
class AuthRegisterRequested extends AuthEvent {
  final String phoneNumber;
  final String password;
  final String name;
  final UserRole role;
  final String woreda;

  const AuthRegisterRequested({
    required this.phoneNumber,
    required this.password,
    required this.name,
    required this.role,
    required this.woreda,
  });

  @override
  List<Object?> get props => [phoneNumber, password, name, role, woreda];
}

/// Event to verify OTP after registration.
class AuthVerifyOtpRequested extends AuthEvent {
  final String phoneNumber;
  final String otp;

  const AuthVerifyOtpRequested({
    required this.phoneNumber,
    required this.otp,
  });

  @override
  List<Object?> get props => [phoneNumber, otp];
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

/// Event to logout user.
class AuthLogoutRequested extends AuthEvent {
  final bool clearCache;

  const AuthLogoutRequested({
    this.clearCache = true,
  });

  @override
  List<Object?> get props => [clearCache];
}
