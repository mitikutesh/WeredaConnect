import 'package:flutter/material.dart';

import 'package:wereda_connect/features/auth/screens/login_screen.dart';

/// First-launch screen.
///
/// The Join board "Welcome and login" is one screen: the wordmark and the
/// Offline Ready pill, the welcome title, the three role pills, then the
/// language switch and the login form on a white card, with Register under
/// it. [LoginScreen] draws that board, so the first launch shows the same
/// composition a returning user lands on. The language switch sits inside
/// the card, so a person picks their language before reading anything else.
class WelcomeScreen extends StatelessWidget {
  const WelcomeScreen({super.key});

  @override
  Widget build(BuildContext context) => const LoginScreen();
}
