import 'package:flutter/widgets.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

/// The signed-in person's role, null before sign-in. Rebuilds [context]
/// only when the role changes (sign-in, sign-out, another account), so the
/// app root can hand it to `AppTheme.light(role:)` and the whole app takes
/// that role's colours.
UserRole? watchSignedInRole(BuildContext context) {
  return context.select<AuthBloc, UserRole?>((bloc) {
    final state = bloc.state;
    return state is AuthAuthenticated ? state.role : null;
  });
}
