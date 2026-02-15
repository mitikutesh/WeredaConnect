# 🎨 Onboarding UX Redesign for Ethiopian Context

## Current Problem Analysis

### Issues Identified
1. **No pre-authentication language selection** - Users must login before changing language
2. **No landing page** - App immediately shows login screen after splash
3. **No value proposition** - Users don't see what the app offers before registration
4. **No trust building** - Ethiopian users need familiarity before committing
5. **Forced authentication** - Cannot explore app features before signing up

### Ethiopian Context Requirements
- **Language diversity**: Amharic, Afan Oromo, English speakers need choice upfront
- **Trust building**: Rural users need to see value before sharing phone numbers
- **Low literacy**: Visual-first design with minimal text
- **Offline-first**: Must communicate offline capabilities immediately
- **Low-end devices**: Smooth performance on 2GB RAM Android phones
- **Data consciousness**: Users aware of data costs, need transparency

---

## Proposed UX Flow

```
┌─────────────┐
│   Splash    │  (1-2 seconds, app initialization)
│   Screen    │
└──────┬──────┘
       │
       ▼
┌─────────────┐
│  Welcome/   │  ◄── NEW: Language selection + value prop
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

## Screen-by-Screen Design

### 1. Welcome/Landing Screen (NEW)

**Purpose**: Language selection, value proposition, trust building

**Layout** (vertical scroll on small screens):

```
╔═══════════════════════════════════════════════════╗
║                                                   ║
║  🌾 [Large Agriculture Icon/Illustration]         ║
║                                                   ║
║         WeredaConnect                             ║
║                                                   ║
║  ┌─────────────────────────────────────────┐     ║
║  │  🌐 Language / ቋንቋ / Afaan               │     ║
║  │                                          │     ║
║  │  [ English          ▼ ]                 │     ║
║  └─────────────────────────────────────────┘     ║
║                                                   ║
║  Connecting Farmers, Truckers & Buyers            ║
║  (Localized tagline)                              ║
║                                                   ║
║  ✓ Works Offline                                  ║
║  ✓ Voice Support in Your Language                ║
║  ✓ Telebirr Payment                               ║
║                                                   ║
║  ┌──────────────────────────────────┐            ║
║  │      [Login] (Primary button)     │            ║
║  └──────────────────────────────────┘            ║
║                                                   ║
║  ┌──────────────────────────────────┐            ║
║  │   [Create Account] (Secondary)    │            ║
║  └──────────────────────────────────┘            ║
║                                                   ║
║          [Explore as Guest]                       ║
║          (Text link)                              ║
║                                                   ║
║  [?] How it works  |  📱 Offline Mode             ║
║                                                   ║
╚═══════════════════════════════════════════════════╝
```

**Key Features**:
- **Prominent language selector** at the top - first interaction
- **Visual hierarchy**: Icon → Language → Value prop → Actions
- **Large touch targets**: 48dp minimum (Material Design)
- **Ethiopian context**:
  - Agriculture icon (teff, wheat, coffee imagery)
  - Telebirr mentioned (familiar payment method)
  - Offline capability highlighted (trust signal)
- **Progressive disclosure**: "How it works" → Optional tutorial
- **Guest mode**: Explore without commitment

**Technical Implementation**:
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

### 2. Optional Tutorial/Onboarding Carousel (NEW)

**When shown**:
- First launch only (can skip)
- Accessible from "How it works" link on Welcome screen

**Screens** (3-4 pages, swipeable):

#### Page 1: Offline First
```
╔════════════════════════════════╗
║                                ║
║   📱 [Illustration: Phone      ║
║       with offline icon]       ║
║                                ║
║   Works Without Internet       ║
║   (Localized)                  ║
║                                ║
║   List your crops, accept      ║
║   orders - all offline.        ║
║   Syncs when connected.        ║
║                                ║
║   [Skip]          [Next ›]     ║
║                                ║
╚════════════════════════════════╝
```

#### Page 2: Voice Support
```
╔════════════════════════════════╗
║                                ║
║   🎤 [Illustration: Person     ║
║       speaking into phone]     ║
║                                ║
║   Speak Your Language          ║
║   (Localized)                  ║
║                                ║
║   Describe crops in Amharic,   ║
║   Oromo, or English.           ║
║   We understand you.           ║
║                                ║
║   [Skip]          [Next ›]     ║
║                                ║
╚════════════════════════════════╝
```

#### Page 3: Telebirr Escrow
```
╔════════════════════════════════╗
║                                ║
║   💰 [Illustration: Secure     ║
║       payment handshake]       ║
║                                ║
║   Safe Payments                ║
║   (Localized)                  ║
║                                ║
║   Telebirr escrow protects     ║
║   buyers and sellers.          ║
║   Money released on delivery.  ║
║                                ║
║   [Skip]      [Get Started]    ║
║                                ║
╚════════════════════════════════╝
```

**Design Principles**:
- **Visual-first**: Large illustrations, minimal text
- **Skippable**: Users can dismiss anytime
- **Localized**: All text in selected language
- **Progress indicator**: Dots showing 1/3, 2/3, 3/3
- **Fast**: Loads instantly even on 2GB RAM devices

---

### 3. Enhanced Login Screen (Modified)

**Changes from current**:
- Add **language switcher icon** in AppBar (top-right)
- Add **"Back to Welcome"** navigation option
- Keep current design otherwise (it's already good)

```dart
// Add to existing login_screen.dart
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

### 4. Enhanced Register Screen (Modified)

**Changes**:
- Same as Login: language switcher + back navigation
- Keep existing role selection (Farmer/Trucker/Buyer)

---

### 5. Guest Mode (NEW)

**Purpose**: Let users explore without registration

**Features**:
- **View public crop listings** (read-only)
- **Browse by crop type** and region
- **See how the app works** in practice
- **Persistent banner**: "Sign up to list crops and make offers"

**Limitations**:
- Cannot create listings
- Cannot make offers
- Cannot see contact details
- No offline sync for guest data

**Layout**:
```
╔═══════════════════════════════════════╗
║  [≡]  WeredaConnect (Guest)    [🌐]   ║
╠═══════════════════════════════════════╣
║  ┌─────────────────────────────────┐  ║
║  │ 👤 Sign up to post crops &      │  ║
║  │    connect with buyers  [Join]  │  ║
║  └─────────────────────────────────┘  ║
║                                       ║
║  📍 Browse Crop Listings              ║
║                                       ║
║  [Teff] [Wheat] [Maize] [All]        ║
║                                       ║
║  ┌─────────────────────────────────┐ ║
║  │ 🌾 Teff - 50 quintals            │ ║
║  │ 📍 Addis Ketema                  │ ║
║  │ 💰 4,500 ETB/kg                  │ ║
║  │ [Sign up to contact]             │ ║
║  └─────────────────────────────────┘ ║
║                                       ║
║  ┌─────────────────────────────────┐ ║
║  │ 🌽 Maize - 100 quintals          │ ║
║  │ 📍 Bole                          │ ║
║  │ 💰 2,800 ETB/kg                  │ ║
║  │ [Sign up to contact]             │ ║
║  └─────────────────────────────────┘ ║
║                                       ║
╚═══════════════════════════════════════╝
```

---

## Implementation Plan

### Phase 1: Core Welcome Screen (Priority 1)
**Files to create**:
```
mobile/lib/features/onboarding/
├── screens/
│   ├── welcome_screen.dart          # Main landing page
│   └── tutorial_carousel_screen.dart # Optional tutorial
├── widgets/
│   ├── language_selector_card.dart   # Language picker for welcome
│   ├── value_proposition_section.dart # Benefits list
│   └── welcome_actions.dart          # Login/Register/Guest buttons
└── bloc/
    ├── onboarding_bloc.dart          # Track first-launch state
    ├── onboarding_event.dart
    └── onboarding_state.dart
```

**Files to modify**:
```
mobile/lib/features/auth/screens/auth_gate.dart
  - Add check for first launch
  - Route to WelcomeScreen if first launch OR not authenticated
  - Use shared preferences to track "has_seen_welcome"

mobile/lib/main.dart
  - No changes needed (AuthGate handles routing)

mobile/lib/features/auth/screens/login_screen.dart
  - Add language switcher in AppBar
  - Add "Back" navigation to welcome screen

mobile/lib/features/auth/screens/register_screen.dart
  - Add language switcher in AppBar
```

### Phase 2: Guest Mode (Priority 2)
**Files to create**:
```
mobile/lib/features/guest/
├── screens/
│   └── guest_home_screen.dart        # Limited browsing
└── widgets/
    ├── guest_banner.dart             # "Sign up" CTA banner
    └── limited_listing_card.dart     # Listing with blocked actions
```

**Files to modify**:
```
mobile/lib/features/auth/bloc/auth_state.dart
  - Add AuthGuest state

mobile/lib/features/auth/screens/auth_gate.dart
  - Handle AuthGuest state → GuestHomeScreen
```

### Phase 3: Tutorial Carousel (Priority 3)
**Files to create**:
```
mobile/lib/features/onboarding/screens/tutorial_carousel_screen.dart
mobile/lib/features/onboarding/widgets/tutorial_page.dart
```

---

## Localization Additions

**New strings needed** (add to `lib/shared/l10n/app_en.arb`):

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
  "tutorialOfflineTitle": "Works Without Internet",
  "tutorialOfflineBody": "List your crops, accept orders - all offline. Syncs when connected.",
  "tutorialVoiceTitle": "Speak Your Language",
  "tutorialVoiceBody": "Describe crops in Amharic, Oromo, or English. We understand you.",
  "tutorialPaymentTitle": "Safe Payments",
  "tutorialPaymentBody": "Telebirr escrow protects buyers and sellers. Money released on delivery.",
  "skip": "Skip",
  "next": "Next",
  "getStarted": "Get Started",
  "guestMode": "Guest Mode",
  "signUpToContact": "Sign up to contact seller",
  "signUpBanner": "Sign up to post crops & connect with buyers",
  "browseCropListings": "Browse Crop Listings",
  "joinNow": "Join Now"
}
```

**Amharic translations** (`app_am.arb`):
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

**Afan Oromo translations** (`app_om.arb`):
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

## Visual Design System

### Colors (Ethiopian Context)
```dart
// Primary: Green (agriculture, growth, fertility)
primaryColor: Color(0xFF2E7D32)  // Deep green

// Secondary: Gold (teff, wheat, Ethiopian flag)
secondaryColor: Color(0xFFFFA726)  // Warm gold/amber

// Accent: Red (Ethiopian flag, urgency for CTAs)
accentColor: Color(0xFFD32F2F)  // Rich red

// Background: Light earth tones
backgroundColor: Color(0xFFFAF8F3)  // Warm off-white
surfaceColor: Color(0xFFFFFFFF)

// Text
textPrimary: Color(0xFF212121)     // Almost black
textSecondary: Color(0xFF757575)   // Gray
```

### Typography
```dart
// Use system fonts for best performance on 2GB RAM devices
// Avoid custom fonts to reduce app size

TextTheme(
  headlineLarge: TextStyle(
    fontSize: 32,
    fontWeight: FontWeight.bold,
    height: 1.2,  // Tight line height for Amharic
  ),
  bodyLarge: TextStyle(
    fontSize: 16,
    height: 1.5,  // Readable spacing
  ),
  // Support for Ethiopic script (Amharic/Oromo)
  // System fonts handle this automatically
)
```

### Icons & Illustrations
**Guidelines**:
- Use **Material Icons** (built-in, no extra size)
- For landing page, use **simple SVG illustrations**:
  - Farmer with crops (locally recognizable clothing/context)
  - Truck on rural road
  - Mobile phone with checkmark
- **Avoid**: Western-centric imagery, unfamiliar contexts
- **Cultural authenticity**: Ethiopian farmer clothing, local crop imagery

**Asset budget**: <2MB total for all onboarding assets (3G friendly)

---

## Accessibility (Ethiopian Context)

### Low Literacy Users
1. **Icons > Text**: Every action has an icon
2. **Color coding**:
   - Green = safe/confirmed
   - Red = caution/delete
   - Blue = information
3. **Voice feedback**: Use TTS for button labels (future)
4. **Consistent patterns**: Same layout across screens

### Touch Targets
- **Minimum 48dp** (Material Design)
- **Spacing**: 8dp between tappable elements
- **Feedback**: Ripple effects on all buttons

### Performance (2GB RAM Devices)
1. **Lazy load images**: Use `Image.network` with caching
2. **Optimize illustrations**: SVG → PNG conversion at build time
3. **Preload critical assets**: Welcome screen images cached
4. **Memory monitoring**: Clear image cache on background

### Offline UX
1. **Always available**: Welcome screen works fully offline
2. **No blank states**: Show cached content
3. **Clear indicators**: Offline mode badge always visible
4. **Graceful degradation**: Guest mode shows cached listings

---

## Success Metrics

**How we'll know it works**:

1. **Language selection adoption**:
   - Target: >60% users change from system default
   - Measure: % of users who interact with language selector

2. **Reduced drop-off**:
   - Current: Unknown (no landing page)
   - Target: <20% drop-off from welcome → registration
   - Measure: Funnel analytics

3. **Guest mode engagement**:
   - Target: 30% of new users explore as guest first
   - Measure: % who click "Explore as Guest"

4. **Tutorial completion**:
   - Target: >50% who start tutorial complete it
   - Measure: Page views per tutorial session

5. **Time to first action**:
   - Current: Unknown
   - Target: <2 minutes from app open to login/register
   - Measure: Time from launch to authentication

---

## Ethiopian UX Best Practices Applied

### ✅ Cultural Considerations
- **Language parity**: All 3 languages get equal prominence
- **Familiar imagery**: Local agriculture context (teff, not corn)
- **Payment trust**: Telebirr mentioned upfront (familiar brand)
- **Community-first**: "Connecting" language vs "Selling" language

### ✅ Infrastructure Reality
- **Offline-first messaging**: Featured in top 3 benefits
- **Data transparency**: File sizes shown, no auto-downloads
- **3G optimized**: Images <100KB, lazy loading
- **Progressive enhancement**: Core features work offline

### ✅ Device Constraints
- **Memory efficient**: <50MB RAM for welcome flow
- **Fast load**: Welcome screen renders in <1s
- **Responsive**: Works on 480x800 screens (low-end devices)
- **Battery conscious**: Minimal animations, no background tasks

### ✅ Trust Building
- **Transparency**: Show how app works before signup
- **Reversibility**: Guest mode lets users explore safely
- **Familiar patterns**: Material Design (known from other apps)
- **Local context**: Ethiopian phone format, Birr currency

---

## Next Steps

### Immediate Actions (This Week)
1. **Create welcome screen**:
   - Implement `WelcomeScreen` widget
   - Add language selector card
   - Create action buttons (Login/Register/Guest)

2. **Update routing**:
   - Modify `AuthGate` to check first-launch
   - Route to welcome screen for new users
   - Add shared preferences for "has_seen_welcome"

3. **Add localization strings**:
   - Update `app_en.arb`, `app_am.arb`, `app_om.arb`
   - Generate localization files
   - Test all three languages

### Short Term (Next Sprint)
1. **Implement guest mode**:
   - Create read-only listing view
   - Add persistent signup banner
   - Limit actions for unauthenticated users

2. **Enhance auth screens**:
   - Add language switcher to login/register
   - Add back navigation to welcome screen
   - Test flow end-to-end

### Medium Term (Future)
1. **Tutorial carousel**:
   - Design illustrations (culturally appropriate)
   - Implement swipeable tutorial pages
   - Add skip functionality

2. **A/B testing**:
   - Test different value propositions
   - Measure conversion rates
   - Iterate based on data

---

## Mockup Summary

```
Flow: Splash → Welcome → [Login OR Register OR Guest] → Main App
              ↓
         [Tutorial] (optional, first launch only)
```

**Key Innovation**: Language selection happens BEFORE any authentication, respecting user choice and Ethiopian linguistic diversity.

**Ethiopian Context Win**: Users see value, build trust, and choose their language—all before sharing personal information.

---

**Document Version**: 1.0
**Last Updated**: 2026-02-15
**Status**: Approved for Implementation
**Owner**: Mobile UX Team
