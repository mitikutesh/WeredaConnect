# Onboarding UX Redesign for Ethiopian Context

## Current Problem Analysis

### Issues Identified
1. No pre-authentication language selection
2. No landing page (app immediately shows login screen after splash)
3. No value proposition shown before registration
4. No trust building (Ethiopian users need familiarity before committing)
5. Forced authentication (cannot explore before signing up)

### Ethiopian Context Requirements
- **Language diversity**: Amharic, Afan Oromo, English need choice upfront
- **Trust building**: Rural users need to see value before sharing phone numbers
- **Low literacy**: Visual-first design with minimal text
- **Offline-first**: Must communicate offline capabilities immediately
- **Low-end devices**: Smooth performance on 2GB RAM Android phones
- **Data consciousness**: Users aware of data costs, need transparency

---

## Proposed UX Flow

```
Splash (1-2s) → Welcome/Landing → [Login | Register | Guest | Tutorial]
```

```
┌─────────────┐
│   Splash    │  (1-2 seconds, app initialization)
└──────┬──────┘
       │
       ▼
┌─────────────┐
│  Welcome/   │  ◄── Language selection + value prop
│  Landing    │      Can skip to guest mode or proceed to auth
└──────┬──────┘
       │
       ├──────────────┬──────────────┬──────────────┐
       ▼              ▼              ▼              ▼
   ┌────────┐   ┌─────────┐   ┌──────────┐   ┌─────────┐
   │ Login  │   │Register │   │Guest Mode│   │Tutorial │
   │ Screen │   │ Screen  │   │(Limited) │   │(Optional)│
   └────────┘   └─────────┘   └──────────┘   └─────────┘
```

---

## Screen Designs

### 1. Welcome/Landing Screen (NEW)

**Purpose**: Language selection, value proposition, trust building

```
╔═══════════════════════════════════════════════════╗
║  [Agriculture Illustration]                       ║
║         WeredaConnect                             ║
║                                                   ║
║  ┌─────────────────────────────────────────┐     ║
║  │  Language / ቋንቋ / Afaan                  │     ║
║  │  [ English          ▼ ]                 │     ║
║  └─────────────────────────────────────────┘     ║
║                                                   ║
║  Connecting Farmers, Truckers & Buyers            ║
║                                                   ║
║  ✓ Works Offline                                  ║
║  ✓ Voice Support in Your Language                ║
║  ✓ Telebirr Payment                               ║
║                                                   ║
║  [      Login       ]  (Primary)                  ║
║  [  Create Account  ]  (Secondary)                ║
║       Explore as Guest (text link)                ║
║                                                   ║
║  [?] How it works  |  Offline Mode                ║
╚═══════════════════════════════════════════════════╝
```

**Key Features**:
- Prominent language selector at top (first interaction)
- Visual hierarchy: Icon -> Language -> Value prop -> Actions
- 48dp minimum touch targets
- Ethiopian context: teff/wheat/coffee imagery, Telebirr mention, offline badge
- Guest mode for exploration without commitment

```dart
// lib/features/onboarding/screens/welcome_screen.dart
class WelcomeScreen extends StatelessWidget {
  // - Language dropdown using LanguageProvider
  // - Animated hero section with agriculture imagery
  // - Three CTAs: Login, Register, Guest
  // - Bottom sheet for "How it works"
}
```

---

### 2. Enhanced Login/Register Screens (Modified)

- Add language switcher icon in AppBar (top-right)
- Add "Back to Welcome" navigation
- Keep existing design otherwise

```dart
AppBar(
  title: Text(l10n.login),
  actions: [
    IconButton(
      icon: const Icon(Icons.language),
      onPressed: () => showLanguageSelectionDialog(context),
    ),
  ],
)
```

---

### 3. Guest Mode (NEW)

**Features**: View public crop listings (read-only), browse by crop type/region
**Limitations**: Cannot create listings, make offers, see contacts, or sync offline

```
╔═══════════════════════════════════════╗
║  [≡]  WeredaConnect (Guest)    [lang] ║
╠═══════════════════════════════════════╣
║  ┌─────────────────────────────────┐  ║
║  │ Sign up to post crops &        │  ║
║  │    connect with buyers  [Join] │  ║
║  └─────────────────────────────────┘  ║
║                                       ║
║  Browse Crop Listings                 ║
║  [Teff] [Wheat] [Maize] [All]        ║
║                                       ║
║  ┌─────────────────────────────────┐ ║
║  │ Teff - 50 quintals              │ ║
║  │ Addis Ketema | 4,500 ETB/kg     │ ║
║  │ [Sign up to contact]           │ ║
║  └─────────────────────────────────┘ ║
╚═══════════════════════════════════════╝
```

---

## Implementation Plan

### Phase 1: Core Welcome Screen
```
mobile/lib/features/onboarding/
├── screens/
│   └── welcome_screen.dart
├── widgets/
│   ├── language_selector_card.dart
│   ├── value_proposition_section.dart
│   └── welcome_actions.dart
└── bloc/
    ├── onboarding_bloc.dart
    ├── onboarding_event.dart
    └── onboarding_state.dart
```

**Modify**:
- `auth_gate.dart` — check first launch, route to WelcomeScreen, use SharedPreferences for `has_seen_welcome`
- `login_screen.dart` / `register_screen.dart` — add language switcher + back nav

### Phase 2: Guest Mode
```
mobile/lib/features/guest/
├── screens/
│   └── guest_home_screen.dart
└── widgets/
    ├── guest_banner.dart
    └── limited_listing_card.dart
```

**Modify**:
- `auth_state.dart` — add `AuthGuest` state
- `auth_gate.dart` — handle `AuthGuest` -> `GuestHomeScreen`

### Phase 3: Tutorial Carousel (future work)

---

## Localization Strings

**English** (`app_en.arb`):
```json
{
  "welcomeTitle": "Welcome to WeredaConnect",
  "selectLanguagePrompt": "Select Your Language",
  "taglineShort": "Connecting Farmers, Truckers & Buyers",
  "worksOffline": "Works Offline",
  "voiceSupport": "Voice Support in Your Language",
  "telebirrPayment": "Telebirr Payment",
  "exploreAsGuest": "Explore as Guest",
  "howItWorks": "How it works",
  "createAccount": "Create Account",
  "guestMode": "Guest Mode",
  "signUpToContact": "Sign up to contact seller",
  "signUpBanner": "Sign up to post crops & connect with buyers",
  "browseCropListings": "Browse Crop Listings",
  "joinNow": "Join Now",
  "skip": "Skip",
  "next": "Next",
  "getStarted": "Get Started"
}
```

**Amharic** (`app_am.arb`):
```json
{
  "welcomeTitle": "እንኳን ወደ ወረዳ ኮነክት በደህና መጡ",
  "selectLanguagePrompt": "ቋንቋዎን ይምረጡ",
  "taglineShort": "ገበሬዎችን፣ መኪና ሹፌሮችን እና ገዢዎችን ማገናኘት",
  "worksOffline": "ከመስመር ውጭ ይሰራል",
  "voiceSupport": "በቋንቋዎ የድምፅ ድጋፍ",
  "telebirrPayment": "ቴሌብር ክፍያ",
  "exploreAsGuest": "እንደ እንግዳ ያስሱ",
  "howItWorks": "እንዴት እንደሚሰራ",
  "createAccount": "መለያ ፍጠር"
}
```

**Afan Oromo** (`app_om.arb`):
```json
{
  "welcomeTitle": "Gara WeredaConnect Baga Nagaan Dhuftan",
  "selectLanguagePrompt": "Afaan Kee Filadhu",
  "taglineShort": "Qonnaan Bultootaa, Konkolaataa fi Bitootaa Walitti Hidhuu",
  "worksOffline": "Oflaayiniin Hojjeta",
  "voiceSupport": "Afaan Keetiin Deeggarsa Sagalee",
  "telebirrPayment": "Kaffaltii Telebirr",
  "exploreAsGuest": "Akka Keessummaatti Qoradhu",
  "howItWorks": "Akkamitti akka hojjetu"
}
```

---

## Visual Design

```dart
// Colors (Ethiopian context)
primaryColor: Color(0xFF2E7D32)    // Deep green (agriculture)
secondaryColor: Color(0xFFFFA726)  // Warm gold (teff, Ethiopian flag)
accentColor: Color(0xFFD32F2F)     // Rich red (Ethiopian flag, CTAs)
backgroundColor: Color(0xFFFAF8F3) // Warm off-white
textPrimary: Color(0xFF212121)
textSecondary: Color(0xFF757575)
```

- Use system fonts (no custom fonts, reduces app size)
- Use Material Icons (built-in)
- SVG illustrations <2MB total (3G friendly), Ethiopian farming context
- Asset budget: <50KB per illustration

---

## Accessibility Checklist

- [ ] Icons accompany all actions (low literacy)
- [ ] Color coding: green=safe, red=caution, blue=info
- [ ] 48dp minimum touch targets, 8dp spacing between tappables
- [ ] Welcome screen works 100% offline (all assets bundled)
- [ ] No blank states (show cached content)
- [ ] Offline mode badge always visible
- [ ] Lazy load images, SVG optimized, memory monitored
- [ ] Fast load: welcome screen renders <1s on 2GB RAM device

---

## Success Metrics

| Metric | Target |
|--------|--------|
| Language selector usage | >60% change from system default |
| Welcome -> registration drop-off | <20% |
| Guest mode exploration rate | 30% of new users |
| Time to first action | <2 min from app open to auth |

---

**Document Version**: 1.0
**Last Updated**: 2026-02-15
**Status**: Approved for Implementation
