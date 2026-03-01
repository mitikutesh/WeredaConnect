import 'package:flutter/material.dart';

import 'package:wereda_connect/features/auth/screens/registration/registration_flow.dart';

/// Registration screen — redirects to multi-step RegistrationFlow.
///
/// Preserved as a thin wrapper for backward compatibility with
/// navigation references from login_screen.dart and welcome_screen.dart.
class RegisterScreen extends StatelessWidget {
  const RegisterScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return const RegistrationFlow();
  }
}
