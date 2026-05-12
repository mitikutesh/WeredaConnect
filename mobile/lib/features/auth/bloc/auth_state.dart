import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';

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
  final String userName;
  final String? phoneNumber;
  final String? woreda;

  const AuthAuthenticated({
    required this.userId,
    required this.tokens,
    required this.role,
    this.isOfflineAuth = false,
    this.userName = '',
    this.phoneNumber,
    this.woreda,
  });

  @override
  List<Object?> get props =>
      [userId, tokens, isOfflineAuth, role, userName, phoneNumber, woreda];
}

/// Unauthenticated state (user is not logged in).
class AuthUnauthenticated extends AuthState {
  const AuthUnauthenticated();
}

/// Error state (authentication operation failed).
class AuthError extends AuthState {
  final String errorCode;
  final String errorMessage;
  final bool canRetry;
  final int? retryAfterSeconds;

  const AuthError({
    required this.errorCode,
    required this.errorMessage,
    this.canRetry = true,
    this.retryAfterSeconds,
  });

  @override
  List<Object?> get props =>
      [errorCode, errorMessage, canRetry, retryAfterSeconds];
}

/// State when password reset code has been sent.
class AuthPasswordResetCodeSent extends AuthState {
  final String phoneNumber;

  const AuthPasswordResetCodeSent({required this.phoneNumber});

  @override
  List<Object?> get props => [phoneNumber];
}

/// State when password has been successfully reset.
class AuthPasswordResetSuccess extends AuthState {
  const AuthPasswordResetSuccess();
}
