import 'package:flutter/material.dart';

/// Responsive breakpoint system for WeredaConnect web/tablet/mobile.
///
/// - Mobile: < 600px (phone, bottom nav)
/// - Tablet: 600–1024px (collapsed rail nav)
/// - Desktop: > 1024px (full sidebar nav, split layouts)
class Breakpoints {
  Breakpoints._();

  static const double mobile = 600;
  static const double tablet = 1024;

  /// Sidebar width when expanded (desktop).
  static const double sidebarExpanded = 260;

  /// Sidebar width when collapsed (tablet rail).
  static const double sidebarCollapsed = 72;

  /// Maximum content width for readability.
  static const double maxContentWidth = 1200;

  /// Returns the current window class based on width.
  static WindowClass of(BuildContext context) {
    final width = MediaQuery.sizeOf(context).width;
    if (width >= tablet) return WindowClass.desktop;
    if (width >= mobile) return WindowClass.tablet;
    return WindowClass.mobile;
  }

  static bool isMobile(BuildContext context) =>
      of(context) == WindowClass.mobile;

  static bool isTablet(BuildContext context) =>
      of(context) == WindowClass.tablet;

  static bool isDesktop(BuildContext context) =>
      of(context) == WindowClass.desktop;

  static bool isWide(BuildContext context) => of(context) != WindowClass.mobile;
}

enum WindowClass { mobile, tablet, desktop }
