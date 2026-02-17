# 🔍 UX Audit - Critical Issues & Fixes

## Date: 2026-02-15
## Status: BLOCKING - Fix Before Production

---

## Issue #1: No Pre-Authentication Language Selection ⚠️ CRITICAL

### Problem
Users cannot change language without logging in first. The app goes directly from splash screen to login screen with no way to select preferred language.

### Impact
- **High**: Ethiopian users speaking Amharic or Afan Oromo see English by default
- **Cultural**: Forces users to navigate authentication in unfamiliar language
- **Trust**: Creates barrier to entry for rural, low-literacy users

### Root Cause
```dart
// main.dart → AuthGate → Login Screen (no landing page)
```

### Solution
✅ **Implemented in `docs/ux/onboarding-redesign.md`**
- Add Welcome/Landing screen with prominent language selector
- Show before authentication
- Allow language change without account creation

---

## Issue #2: Profile Screen Shows "Not Logged In" When User IS Logged In 🐛 CRITICAL

### Problem
The profile screen (bottom nav, 3rd tab) displays:
- ❌ "Not logged in" text
- ❌ "Login" button that does nothing
- ❌ Generic avatar icon

**BUT** the user IS authenticated because:
- ✅ They can access MainNavigation (auth-protected)
- ✅ Sidebar shows "Logout" button
- ✅ They can create listings (auth-required)

### Evidence
```dart
// profile_screen.dart:27-28
Text(l10n.notLoggedIn),  // WRONG: User IS logged in!

// profile_screen.dart:41-48
FilledButton.icon(
  onPressed: () {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(l10n.comingSoon)),  // Does nothing
    ),
  },
  icon: const Icon(Icons.login),
  label: Text(l10n.login),  // MISLEADING: User already logged in
),
```

### User Impact
- **Confusion**: "Am I logged in or not?"
- **Trust**: Inconsistent UI signals (logout in sidebar, login in profile)
- **Lost functionality**: Users don't know they're authenticated

### Root Cause
ProfileScreen is a **static placeholder** that doesn't check AuthBloc state. It was built as a stub and never completed.

---

## Issue #3: Redundant Logout Options (Minor UX Issue)

### Problem
Logout functionality appears in:
1. **Sidebar drawer** (main_navigation.dart:206-214)
2. Would appear in profile screen if properly implemented

### Impact
- Medium: Not broken, just redundant
- Could be feature (multiple ways to access)
- Better: Consolidate logout to profile screen only

---

## Proposed Fixes

### Fix for Issue #1: Landing Page
**Status**: ✅ Designed (see `onboarding-redesign.md`)
**Implementation**: 2-3 days
**Priority**: P0 (blocking MVP)

**Changes**:
1. Create `WelcomeScreen` with language selector
2. Update `AuthGate` to route: Splash → Welcome → Auth
3. Add first-launch detection
4. Allow guest mode exploration

---

### Fix for Issue #2: Profile Screen Authentication Awareness

**Status**: 🔨 Ready to implement
**Implementation**: 2 hours
**Priority**: P0 (critical bug)

#### Solution A: Simple Fix (Recommended for MVP)
Show actual user info when authenticated:

```dart
// profile_screen.dart - FIXED VERSION
class ProfileScreen extends StatelessWidget {
  const ProfileScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocBuilder<AuthBloc, AuthState>(
      builder: (context, authState) {
        // User is authenticated - show profile
        if (authState is AuthAuthenticated) {
          final user = authState.user;

          return _buildAuthenticatedProfile(context, user, l10n);
        }

        // User not authenticated - show login prompt
        // (This should never happen since MainNavigation requires auth,
        // but handle it gracefully)
        return _buildUnauthenticatedPrompt(context, l10n);
      },
    );
  }

  Widget _buildAuthenticatedProfile(
    BuildContext context,
    User user,
    AppLocalizations l10n,
  ) {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: Column(
        children: [
          // User avatar
          const SizedBox(height: 32),
          CircleAvatar(
            radius: 60,
            backgroundColor: Theme.of(context).colorScheme.primaryContainer,
            child: Text(
              user.fullName[0].toUpperCase(),  // First letter of name
              style: TextStyle(
                fontSize: 48,
                fontWeight: FontWeight.bold,
                color: Theme.of(context).colorScheme.onPrimaryContainer,
              ),
            ),
          ),

          const SizedBox(height: 24),

          // User name
          Text(
            user.fullName,
            style: Theme.of(context).textTheme.headlineSmall?.copyWith(
              fontWeight: FontWeight.bold,
            ),
          ),

          const SizedBox(height: 8),

          // Phone number
          Text(
            user.phoneNumber,
            style: Theme.of(context).textTheme.bodyLarge?.copyWith(
              color: Theme.of(context).colorScheme.onSurface.withOpacity(0.6),
            ),
          ),

          const SizedBox(height: 8),

          // Role chip
          Chip(
            avatar: Icon(
              _getRoleIcon(user.role),
              size: 18,
            ),
            label: Text(_getRoleLabel(user.role, l10n)),
            backgroundColor: Theme.of(context).colorScheme.secondaryContainer,
          ),

          const SizedBox(height: 48),

          // Profile actions
          _ProfileActionCard(
            icon: Icons.person_outline,
            title: l10n.editProfile,
            subtitle: 'Update your name and details',
            onTap: () {
              ScaffoldMessenger.of(context).showSnackBar(
                SnackBar(content: Text(l10n.comingSoon)),
              );
            },
          ),

          const SizedBox(height: 12),

          _ProfileActionCard(
            icon: Icons.agriculture_outlined,
            title: 'My Listings',
            subtitle: '${user.listingsCount ?? 0} active listings',
            onTap: () {
              // Navigate to user's listings
            },
          ),

          const SizedBox(height: 12),

          _ProfileActionCard(
            icon: Icons.receipt_long_outlined,
            title: 'My Orders',
            subtitle: 'View your order history',
            onTap: () {
              ScaffoldMessenger.of(context).showSnackBar(
                SnackBar(content: Text(l10n.comingSoon)),
              );
            },
          ),

          const SizedBox(height: 12),

          _ProfileActionCard(
            icon: Icons.notifications_outlined,
            title: 'Notifications',
            subtitle: 'Manage your alerts',
            onTap: () {
              ScaffoldMessenger.of(context).showSnackBar(
                SnackBar(content: Text(l10n.comingSoon)),
              );
            },
          ),

          const SizedBox(height: 32),

          // Logout button (moved from drawer)
          OutlinedButton.icon(
            onPressed: () => _showLogoutConfirmation(context, l10n),
            icon: const Icon(Icons.logout, color: Colors.red),
            label: Text(
              l10n.logout,
              style: const TextStyle(color: Colors.red),
            ),
            style: OutlinedButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
              side: const BorderSide(color: Colors.red, width: 1.5),
            ),
          ),

          const SizedBox(height: 32),
        ],
      ),
    );
  }

  Widget _buildUnauthenticatedPrompt(
    BuildContext context,
    AppLocalizations l10n,
  ) {
    // This shouldn't happen, but handle gracefully
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(
            Icons.person_off_outlined,
            size: 80,
            color: Theme.of(context).colorScheme.onSurface.withOpacity(0.3),
          ),
          const SizedBox(height: 24),
          Text(
            l10n.notLoggedIn,
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 16),
          Text(
            'Please restart the app',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
              color: Theme.of(context).colorScheme.onSurface.withOpacity(0.6),
            ),
          ),
        ],
      ),
    );
  }

  IconData _getRoleIcon(String role) {
    switch (role.toLowerCase()) {
      case 'farmer':
        return Icons.agriculture;
      case 'trucker':
        return Icons.local_shipping;
      case 'buyer':
        return Icons.shopping_bag;
      default:
        return Icons.person;
    }
  }

  String _getRoleLabel(String role, AppLocalizations l10n) {
    switch (role.toLowerCase()) {
      case 'farmer':
        return l10n.farmer;
      case 'trucker':
        return l10n.trucker;
      case 'buyer':
        return l10n.buyer;
      default:
        return role;
    }
  }

  void _showLogoutConfirmation(BuildContext context, AppLocalizations l10n) {
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        return AlertDialog(
          title: Text(l10n.logout),
          content: Text(l10n.logoutConfirmation),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(l10n.cancel),
            ),
            TextButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                context.read<AuthBloc>().add(
                  const AuthLogoutRequested(clearCache: false),
                );
              },
              style: TextButton.styleFrom(foregroundColor: Colors.red),
              child: Text(l10n.logout),
            ),
          ],
        );
      },
    );
  }
}

// Helper widget for profile action cards
class _ProfileActionCard extends StatelessWidget {
  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;

  const _ProfileActionCard({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Card(
      elevation: 0,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(
          color: Theme.of(context).colorScheme.outline.withOpacity(0.5),
        ),
      ),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Icon(icon, size: 28),
              const SizedBox(width: 16),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      style: Theme.of(context).textTheme.titleMedium,
                    ),
                    const SizedBox(height: 4),
                    Text(
                      subtitle,
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: Theme.of(context).colorScheme.onSurface.withOpacity(0.6),
                      ),
                    ),
                  ],
                ),
              ),
              const Icon(Icons.chevron_right),
            ],
          ),
        ),
      ),
    );
  }
}
```

#### Required Changes Summary

**Files to modify**:
1. `mobile/lib/screens/profile_screen.dart`
   - Replace entire file with authenticated version above
   - Add BlocBuilder to check AuthBloc state
   - Show user info when authenticated
   - Move logout button here from drawer

2. `mobile/lib/screens/main_navigation.dart` (optional)
   - Remove logout from drawer (now in profile)
   - Keep settings and about links

3. Add l10n strings:
```json
{
  "editProfile": "Edit Profile",
  "myListings": "My Listings",
  "myOrders": "My Orders",
  "notifications": "Notifications"
}
```

---

## Visual Comparison

### BEFORE (Current - BROKEN)
```
┌─────────────────────────────────────┐
│  [≡]  My Profile                    │
├─────────────────────────────────────┤
│                                     │
│          [Generic Avatar]           │
│                                     │
│         Not logged in               │  ← WRONG!
│         Coming Soon                 │
│                                     │
│        [LOGIN] Button               │  ← MISLEADING!
│                                     │
│                                     │
└─────────────────────────────────────┘

Sidebar:
  - Settings
  - About
  - [Logout]  ← CONTRADICTS profile screen
```

### AFTER (Fixed)
```
┌─────────────────────────────────────┐
│  [≡]  My Profile                    │
├─────────────────────────────────────┤
│                                     │
│          [Avatar with "A"]          │
│                                     │
│         Abebe Kebede                │
│       +251911234567                 │
│       [Farmer] chip                 │
│                                     │
│  ┌───────────────────────────────┐ │
│  │ 👤 Edit Profile               │ │
│  │    Update your details        │ │
│  └───────────────────────────────┘ │
│                                     │
│  ┌───────────────────────────────┐ │
│  │ 🌾 My Listings (3)            │ │
│  │    View your active listings  │ │
│  └───────────────────────────────┘ │
│                                     │
│  ┌───────────────────────────────┐ │
│  │ 📦 My Orders                  │ │
│  │    View your order history    │ │
│  └───────────────────────────────┘ │
│                                     │
│        [LOGOUT] Button              │  ← Clear action
│                                     │
└─────────────────────────────────────┘

Sidebar:
  - Settings  ← Keep
  - About     ← Keep
  (No logout - moved to profile)
```

---

## Additional UX Improvements to Consider

### 1. Consistent Iconography
**Current**: Mix of outlined and filled icons
**Recommendation**: Use outlined icons consistently, filled only for selected states

### 2. Empty States
**Current**: "Coming Soon" snackbars everywhere
**Recommendation**: Create proper empty state screens with helpful messages

### 3. Loading States
**Current**: Some screens show immediate "Coming Soon"
**Recommendation**: Add skeleton loaders for future data-loading screens

### 4. Offline Indicators
**Current**: Cloud icon in top bar
**Recommendation**: Add persistent offline mode chip when no connection

---

## Testing Checklist

Before marking as complete:

### Issue #1 (Landing Page)
- [ ] Language can be changed before login
- [ ] Welcome screen shows on first launch
- [ ] Guest mode allows exploration
- [ ] All three languages display correctly

### Issue #2 (Profile Screen)
- [ ] Authenticated users see their name
- [ ] Phone number displays correctly
- [ ] Role chip shows correct role
- [ ] Logout button works
- [ ] No "Not logged in" text when authenticated
- [ ] Profile actions are tappable (even if stub)

### Issue #3 (Consistency)
- [ ] Only one logout location (profile screen)
- [ ] Sidebar doesn't duplicate profile actions
- [ ] Icon style is consistent

---

## Implementation Priority

### Sprint 1 (This Week) - CRITICAL
1. ✅ **Fix Profile Screen** (2 hours)
   - Blocking: Users confused about auth state
   - High impact, low effort

2. ✅ **Create Welcome Screen** (2-3 days)
   - Blocking: Language selection required for launch
   - High impact, medium effort

### Sprint 2 (Next Week)
3. **Guest Mode** (2 days)
   - Nice to have: Allows exploration before signup
   - Medium impact, medium effort

4. **Tutorial Carousel** (1-2 days)
   - Nice to have: Helps onboarding
   - Low impact, low effort

---

## Success Metrics

**How we measure fix effectiveness**:

1. **User confusion reduction**:
   - Metric: Support tickets about "am I logged in?"
   - Target: 0 tickets after fix

2. **Language selection adoption**:
   - Metric: % users who change from default language
   - Target: >60% adoption

3. **Profile screen engagement**:
   - Metric: Time spent on profile screen
   - Target: >10 seconds (vs current ~2 seconds)

4. **Logout success rate**:
   - Metric: % users who successfully logout on first try
   - Target: >95% (vs current unknown, likely lower due to confusion)

---

**Document Status**: Ready for Implementation
**Owner**: Mobile Team
**Reviewer**: UX Designer + Product Owner
**Priority**: P0 (Blocking MVP Launch)
