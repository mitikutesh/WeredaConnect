# Onboarding System Architecture

## Version
1.0.0 | 2026-02-21

## Status
Implemented (R1)

---

## 1. Overview

The onboarding feature is a lightweight, offline-capable welcome screen that appears on first app launch. It frontloads language selection before authentication, respecting Ethiopian linguistic diversity and low-literacy user needs.

**Key Principle**: Language choice happens BEFORE authentication — rural users must see their language immediately.

---

## 2. Component Overview

```
┌──────────────────────────────────────────────────────────┐
│                     MOBILE (Flutter)                      │
│                                                           │
│  ┌────────────────┐  ┌─────────────────┐                │
│  │  OnboardingBloc │  │ LanguageProvider │                │
│  │  (first-launch  │  │ (locale state   │                │
│  │   detection)    │  │  + persistence) │                │
│  └───────┬────────┘  └────────┬────────┘                │
│          │                     │                          │
│  ┌───────┴─────────────────────┴────────┐                │
│  │           AuthGate (Router)           │                │
│  │                                       │                │
│  │  IF FirstLaunch → WelcomeScreen       │                │
│  │  IF ReturningUser → LoginScreen       │                │
│  │  IF Authenticated → MainNavigation    │                │
│  └───────────────────────────────────────┘                │
│                                                           │
│  ┌───────────────────────────────────────┐                │
│  │         WelcomeScreen                  │                │
│  │  ┌──────────────────────────────────┐ │                │
│  │  │ LanguageSelectorCard             │ │                │
│  │  │ (opens LanguageSelectionDialog)  │ │                │
│  │  ├──────────────────────────────────┤ │                │
│  │  │ ValuePropositionList             │ │                │
│  │  │ (4 icon-based feature cards)     │ │                │
│  │  ├──────────────────────────────────┤ │                │
│  │  │ WelcomeActions                   │ │                │
│  │  │ (Login / Sign Up / Guest)        │ │                │
│  │  └──────────────────────────────────┘ │                │
│  └───────────────────────────────────────┘                │
│                                                           │
│  Storage: SharedPreferences (has_seen_welcome flag)       │
└──────────────────────────────────────────────────────────┘
```

---

## 3. Onboarding Flow

```
[App Start]
    │
    ▼
[AuthGate.initState() → CheckFirstLaunch event]
    │
    ▼
[OnboardingBloc reads SharedPreferences]
    │
    ├── has_seen_welcome = false/null → emit FirstLaunch
    │       │
    │       ▼
    │   [WelcomeScreen shown]
    │       │
    │       ├── User selects language → LanguageProvider persists
    │       ├── User taps Login → MarkWelcomeSeen → LoginScreen
    │       └── User taps Sign Up → MarkWelcomeSeen → RegisterScreen
    │
    └── has_seen_welcome = true → emit ReturningUser
            │
            ▼
        [Skip to LoginScreen]
```

---

## 4. State Management (BLoC)

### Events
| Event | Trigger |
|-------|---------|
| `CheckFirstLaunch` | AuthGate.initState() |
| `MarkWelcomeSeen` | User taps Login or Sign Up on WelcomeScreen |

### States
| State | Meaning |
|-------|---------|
| `OnboardingInitial` | Checking SharedPreferences |
| `FirstLaunch` | First time — show WelcomeScreen |
| `ReturningUser` | Already seen — skip to LoginScreen |

### Persistence
- **Key**: `has_seen_welcome` (boolean)
- **Storage**: SharedPreferences (unencrypted — not sensitive)
- **Set when**: User taps Login/Sign Up, OR on successful authentication

---

## 5. Language Selection

### Supported Languages

| Code | Name | Native |
|------|------|--------|
| `en` | English | English |
| `am` | Amharic | አማርኛ |
| `om` | Afan Oromo | Afaan Oromoo |

### LanguageProvider (ChangeNotifier)

```
Initialize:
  1. Restore saved language from SharedPreferences
  2. If none, check device locale against supported locales
  3. Fall back to English

setLocale(Locale):
  1. Validate locale is supported
  2. Update internal _locale
  3. Persist to SharedPreferences ('selected_language')
  4. notifyListeners() → MaterialApp rebuilds with new locale
```

### Selection Flow
1. User taps `LanguageSelectorCard` (shows current language)
2. `LanguageSelectionDialog` opens (radio buttons for 3 languages)
3. User selects → `languageProvider.setLocale()` called
4. App locale changes immediately (no restart needed)
5. Snackbar confirms selection in the new language

**Key**: Language persists across app restarts and is restored before MaterialApp builds, preventing English flash on cold start.

---

## 6. WelcomeScreen Layout

### Visual Hierarchy (Top to Bottom)
1. **Hero Illustration**: 120dp agriculture icon (green-themed)
2. **App Title**: "WeredaConnect" (centered, bold)
3. **Language Selector Card**: Prominent, shows current language with dropdown
4. **Tagline**: "Connecting Ethiopian farmers, truckers, and buyers" (localized)
5. **Value Propositions**: 4 icon-based cards:

| Icon | Label | Color | Feature |
|------|-------|-------|---------|
| cloud_off | Offline Ready | Green | Works without internet |
| mic | Voice Support | Blue | Local language audio |
| payment | Telebirr Payment | Orange | Mobile money integration |
| store | Connect with Markets | Purple | Marketplace access |

6. **Action Buttons** (stacked vertically):
   - Primary: "Login" (green elevated button, 48dp)
   - Secondary: "Sign Up" (outlined button, 48dp)
   - Tertiary: "Explore as Guest" (text link — coming soon)

7. **Info Chips** (bottom row):
   - "How it works" → tutorial placeholder
   - "Offline Mode" → reassurance message

---

## 7. Low-Literacy UX Patterns

| Pattern | Implementation |
|---------|---------------|
| Visual-first hierarchy | Large icons (120dp) before text |
| Large touch targets | All buttons 48dp minimum height |
| Color-coded features | Each value prop has distinct color |
| Icon + short label | No paragraphs, just icon + 2-3 words |
| Prominent language switch | Top of screen, easy to discover |
| Reassurance messaging | "Your data will sync when you're back online" |
| No jargon | Conversational tone, no technical terms |

---

## 8. Integration with Auth Flow

### AuthGate Decision Tree

```
AuthGate.build()
  │
  ├── AuthAuthenticated → MarkWelcomeSeen + MainNavigation
  ├── AuthOtpVerificationRequired → OtpVerificationScreen
  ├── AuthInitial / AuthLoading → Splash screen
  └── AuthUnauthenticated / AuthError:
       ├── FirstLaunch → WelcomeScreen
       ├── ReturningUser → LoginScreen
       └── OnboardingInitial → Splash screen
```

### Key Behaviors
- **Fresh install**: WelcomeScreen → language selection → Login/Register
- **Returning user**: Skip directly to LoginScreen
- **After logout**: LoginScreen (not WelcomeScreen — already "onboarded")
- **Successful auth**: Always marks welcome as seen (redundant but safe)

---

## 9. App Initialization Sequence

```
main() async {
  1. LanguageProvider.initialize()     // Restore saved language FIRST
  2. DataUsageTracker.initialize()     // Load persisted counters
  3. ApiClient.initialize()            // HTTP client + interceptors
  4. IsarService.initialize()          // Local database
  5. ConnectivityMonitor()             // Network monitoring
  6. SettingsProvider.initialize()      // Load app settings
  7. runApp(WeredaConnectApp)           // MaterialApp with locale
}
```

**Critical**: LanguageProvider initializes BEFORE MaterialApp so the user never sees English flash before their preferred language loads.

---

## 10. Design Decisions

| Decision | Rationale (Ethiopian Context) |
|----------|-------------------------------|
| Language BEFORE auth | Rural users speak Amharic/Oromo; forcing English first creates friction |
| Single welcome screen | Low-literacy users distracted by multi-screen carousels |
| Prominent language card | Amharic/Oromo speakers look for language switch immediately |
| Icon-based value props | Visual recognition over reading ability |
| "Offline Ready" emphasis | Farmers experience 3G dropouts regularly, need upfront reassurance |
| Telebirr call-out | Familiar mobile money — shows app integrates with their payment |
| SharedPreferences for flag | Not sensitive data, no encryption needed |
| MarkWelcomeSeen on button click | Respect user's decision to "proceed" even if auth fails later |

---

## 11. File Structure

```
mobile/lib/features/onboarding/
├── bloc/
│   ├── onboarding_bloc.dart
│   ├── onboarding_event.dart
│   └── onboarding_state.dart
├── screens/
│   └── welcome_screen.dart
└── widgets/
    ├── language_selector_card.dart
    ├── value_proposition_list.dart
    └── welcome_actions.dart

mobile/lib/shared/
├── providers/
│   └── language_provider.dart
└── widgets/
    └── language_selection_dialog.dart
```

---

## 12. Offline Capability

The entire onboarding flow works fully offline:
- Language selection persists to SharedPreferences (local)
- Welcome screen has no network calls
- All text is bundled in ARB localization files
- OnboardingBloc reads/writes SharedPreferences only

---

**Last Updated**: 2026-02-21
