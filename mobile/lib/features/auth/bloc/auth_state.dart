import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';

/// Base class for authentication states.
abstract class AuthState extends Equatable {
  const AuthState();

  @override
  List<Object?> get props => [];
}

/// Initial state (before checking authentication status).
class AuthInitial extends AuthState {
  const AuthInitial();
}

/// Loading state (during authentication operations).
class AuthLoading extends AuthState {
  const AuthLoading();
}

/// Authenticated state (user is logged in).
class AuthAuthenticated extends AuthState {
  final String userId;
  final AuthTokens tokens;
  final bool isOfflineAuth;
  final UserRole role;

  const AuthAuthenticated({
    required this.userId,
    required this.tokens,
    this.isOfflineAuth = false,
    this.role = UserRole.farmer,
  });

  @override
  List<Object?> get props => [userId, tokens, isOfflineAuth, role];
}

/// Unauthenticated state (user is not logged in).
class AuthUnauthenticated extends AuthState {
  const AuthUnauthenticated();
}

/// OTP verification required state (after registration).
class AuthOtpVerificationRequired extends AuthState {
  final String phoneNumber;
  final String userId;

  const AuthOtpVerificationRequired({
    required this.phoneNumber,
    required this.userId,
  });

  @override
  List<Object?> get props => [phoneNumber, userId];
}

/// Error state (authentication operation failed).
class AuthError extends AuthState {
  final String errorCode;
  final String errorMessage;
  final bool canRetry;

  const AuthError({
    required this.errorCode,
    required this.errorMessage,
    this.canRetry = true,
  });

  @override
  List<Object?> get props => [errorCode, errorMessage, canRetry];
}
