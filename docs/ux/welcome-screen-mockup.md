# Welcome Screen - Detailed Mockup

## Visual Layout (Mobile - 360x640dp base)

```
┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓
┃ Status Bar (System)                    ┃ 24dp
┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛
┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓
┃                                         ┃
┃            [Scroll Container]           ┃
┃                                         ┃
┃  ┌───────────────────────────────────┐ ┃
┃  │                                   │ ┃ 32dp padding
┃  │         🌾🚛🏪                     │ ┃
┃  │    [Agriculture Illustration]     │ ┃ 120dp
┃  │    (Farmer, Truck, Market)        │ ┃
┃  │                                   │ ┃
┃  └───────────────────────────────────┘ ┃
┃                                         ┃
┃         WeredaConnect                   ┃ 24sp, bold
┃                                         ┃ 16dp spacing
┃  ┌───────────────────────────────────┐ ┃
┃  │ 🌐 Language / ቋንቋ / Afaan         │ ┃
┃  │                                   │ ┃ 56dp height
┃  │  English                      ▼  │ ┃ (large touch)
┃  │                                   │ ┃
┃  └───────────────────────────────────┘ ┃
┃                                         ┃ 24dp spacing
┃  ┌───────────────────────────────────┐ ┃
┃  │  Connecting Ethiopian Farmers,    │ ┃
┃  │  Truckers, and Buyers              │ ┃ 16sp, centered
┃  └───────────────────────────────────┘ ┃
┃                                         ┃ 32dp spacing
┃  ┌───────────────────────────────────┐ ┃
┃  │ ✓ Works Offline                   │ ┃
┃  │ ✓ Voice Support in Your Language  │ ┃ 14sp, list
┃  │ ✓ Telebirr Payment                │ ┃
┃  │ ✓ Connect with Local Markets      │ ┃
┃  └───────────────────────────────────┘ ┃
┃                                         ┃ 32dp spacing
┃  ┌───────────────────────────────────┐ ┃
┃  │                                   │ ┃
┃  │          [LOGIN]                  │ ┃ 48dp height
┃  │      (Primary Button)             │ ┃ Green fill
┃  │                                   │ ┃
┃  └───────────────────────────────────┘ ┃
┃                                         ┃ 12dp spacing
┃  ┌───────────────────────────────────┐ ┃
┃  │                                   │ ┃
┃  │      [CREATE ACCOUNT]             │ ┃ 48dp height
┃  │     (Secondary Button)            │ ┃ Green outline
┃  │                                   │ ┃
┃  └───────────────────────────────────┘ ┃
┃                                         ┃ 24dp spacing
┃        [Explore as Guest]               ┃ 16sp, link
┃                                         ┃ 16dp spacing
┃  ┌─────────────────┬─────────────────┐ ┃
┃  │ 📘 How it works │ 📱 Offline Mode │ ┃ 14sp, chips
┃  └─────────────────┴─────────────────┘ ┃
┃                                         ┃ 24dp bottom
┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛
```

## Component Breakdown

### 1. Hero Illustration (Top)
```dart
Container(
  height: 120,
  child: SvgPicture.asset(
    'assets/illustrations/agriculture_hero.svg',
    fit: BoxFit.contain,
  ),
)
```
**Design Notes**:
- Simple, flat illustration style
- Ethiopian context: traditional farmer clothing, local crops
- Colors: Green (crops), Brown (earth), Blue (sky)
- SVG optimized to <50KB

### 2. Language Selector
```dart
Card(
  elevation: 2,
  child: InkWell(
    onTap: () => _showLanguageBottomSheet(context),
    child: Padding(
      padding: EdgeInsets.symmetric(horizontal: 16, vertical: 16),
      child: Row(
        children: [
          Icon(Icons.language, size: 24),
          SizedBox(width: 12),
          Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Language / ቋንቋ / Afaan',
                style: TextStyle(fontSize: 12, color: Colors.grey[600])),
              SizedBox(height: 4),
              Text(selectedLanguageName,  // "English", "አማርኛ", etc.
                style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
            ],
          ),
          Spacer(),
          Icon(Icons.arrow_drop_down),
        ],
      ),
    ),
  ),
)
```

**Interaction**:
- Tapping opens bottom sheet with language options
- Shows language in native script (አማርኛ, Afaan Oromoo, English)
- Radio buttons with large touch targets
- Instant UI update on selection

### 3. Value Proposition List
```dart
Column(
  crossAxisAlignment: CrossAxisAlignment.start,
  children: [
    _ValuePropItem(
      icon: Icons.cloud_off,
      text: l10n.worksOffline,
      color: Colors.green,
    ),
    _ValuePropItem(
      icon: Icons.mic,
      text: l10n.voiceSupport,
      color: Colors.blue,
    ),
    _ValuePropItem(
      icon: Icons.payment,
      text: l10n.telebirrPayment,
      color: Colors.orange,
    ),
    _ValuePropItem(
      icon: Icons.storefront,
      text: l10n.connectWithMarkets,
      color: Colors.purple,
    ),
  ],
)

class _ValuePropItem extends StatelessWidget {
  Widget build(BuildContext context) {
    return Padding(
      padding: EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          Icon(icon, color: color, size: 24),
          SizedBox(width: 12),
          Expanded(
            child: Text(text,
              style: TextStyle(fontSize: 14, height: 1.4)),
          ),
        ],
      ),
    );
  }
}
```

### 4. Primary Action Button
```dart
ElevatedButton(
  onPressed: () => Navigator.push(context,
    MaterialPageRoute(builder: (_) => LoginScreen())),
  style: ElevatedButton.styleFrom(
    backgroundColor: Color(0xFF2E7D32),  // Deep green
    foregroundColor: Colors.white,
    minimumSize: Size.fromHeight(48),
    padding: EdgeInsets.symmetric(vertical: 16),
    shape: RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(8),
    ),
    elevation: 2,
  ),
  child: Text(
    l10n.login,
    style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600),
  ),
)
```

### 5. Secondary Action Button
```dart
OutlinedButton(
  onPressed: () => Navigator.push(context,
    MaterialPageRoute(builder: (_) => RegisterScreen())),
  style: OutlinedButton.styleFrom(
    foregroundColor: Color(0xFF2E7D32),
    minimumSize: Size.fromHeight(48),
    padding: EdgeInsets.symmetric(vertical: 16),
    shape: RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(8),
    ),
    side: BorderSide(color: Color(0xFF2E7D32), width: 2),
  ),
  child: Text(
    l10n.createAccount,
    style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600),
  ),
)
```

### 6. Guest Mode Link
```dart
TextButton(
  onPressed: () {
    context.read<AuthBloc>().add(AuthGuestModeRequested());
    // Navigates to GuestHomeScreen via AuthGate
  },
  child: Text(
    l10n.exploreAsGuest,
    style: TextStyle(
      fontSize: 16,
      fontWeight: FontWeight.w500,
      decoration: TextDecoration.underline,
    ),
  ),
)
```

---

## Language Bottom Sheet

When user taps language selector:

```
┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓
┃                                         ┃
┃        Select Language / ቋንቋ ይምረጡ    ┃ 20sp, bold
┃                                         ┃ 24dp spacing
┃  ┌───────────────────────────────────┐ ┃
┃  │  ○  English                    ✓  │ ┃ 56dp height
┃  └───────────────────────────────────┘ ┃
┃                                         ┃
┃  ┌───────────────────────────────────┐ ┃
┃  │  ●  አማርኛ (Amharic)              │ ┃ Selected
┃  └───────────────────────────────────┘ ┃
┃                                         ┃
┃  ┌───────────────────────────────────┐ ┃
┃  │  ○  Afaan Oromoo                  │ ┃
┃  └───────────────────────────────────┘ ┃
┃                                         ┃ 32dp spacing
┃          [Close]                        ┃
┃                                         ┃
┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛
```

**Interaction**:
- Radio buttons for single selection
- Check mark on selected language
- Instant app language update
- Auto-closes after selection
- Confirmation snackbar in new language

---

## Responsive Behavior

### Small Screens (< 360dp width)
- Reduce illustration height to 80dp
- Font sizes: -2sp across the board
- Padding: 16dp instead of 24dp
- Stack info chips vertically

### Large Screens (> 600dp width, tablets)
- Max width: 480dp (centered)
- Increase illustration to 160dp
- Larger fonts for readability
- Side-by-side info chips

### Landscape Mode
- Reduce illustration to 60dp
- Horizontal scroll for value props
- Buttons side-by-side (Login | Register)

---

## Animation & Transitions

### Page Load (First Time)
```dart
@override
void initState() {
  super.initState();
  _fadeController = AnimationController(
    duration: Duration(milliseconds: 800),
    vsync: this,
  );
  _slideController = AnimationController(
    duration: Duration(milliseconds: 600),
    vsync: this,
  );

  // Stagger animations
  Future.delayed(Duration(milliseconds: 100), () {
    _fadeController.forward();
  });
  Future.delayed(Duration(milliseconds: 300), () {
    _slideController.forward();
  });
}
```

**Animation Sequence**:
1. Illustration fades in (0-800ms)
2. Title slides up (300-900ms)
3. Language selector slides up (400-1000ms)
4. Content fades in (500-1300ms)
5. Buttons slide up (600-1200ms)

**Performance**: All animations use `Transform` (GPU-accelerated, no repaints)

### Language Change Transition
```dart
AnimatedSwitcher(
  duration: Duration(milliseconds: 300),
  transitionBuilder: (child, animation) {
    return FadeTransition(
      opacity: animation,
      child: child,
    );
  },
  child: Text(
    l10n.taglineShort,
    key: ValueKey(currentLocale.languageCode),
  ),
)
```

---

## Offline Handling

**Welcome screen works 100% offline**:
- No network requests
- All assets bundled with app
- Language switching persisted locally
- Cached in app memory

**Offline indicators**:
```dart
// Top-right corner badge
if (!isOnline)
  Positioned(
    top: 8,
    right: 8,
    child: Chip(
      avatar: Icon(Icons.cloud_off, size: 16),
      label: Text(l10n.offlineMode, style: TextStyle(fontSize: 12)),
      backgroundColor: Colors.orange[100],
    ),
  ),
```

---

## Accessibility

### Screen Reader Support
```dart
Semantics(
  label: '${l10n.selectLanguagePrompt}. ${l10n.currentLanguage}',
  hint: 'Tap to change language',
  child: LanguageSelectorCard(...),
)

ElevatedButton(
  onPressed: _handleLogin,
  child: Semantics(
    label: '${l10n.login} button',
    hint: 'Navigate to login screen',
    child: Text(l10n.login),
  ),
)
```

### High Contrast Mode
```dart
Theme(
  data: ThemeData(
    // Adjust for high contrast
    brightness: Brightness.light,
    primaryColor: Colors.green[900],  // Darker green
    elevatedButtonTheme: ElevatedButtonThemeData(
      style: ElevatedButton.styleFrom(
        backgroundColor: Colors.green[900],
        foregroundColor: Colors.white,
        elevation: 4,  // More prominent shadow
      ),
    ),
  ),
)
```

### Reduced Motion
```dart
// Check system preference
final reducedMotion = MediaQuery.of(context).disableAnimations;

AnimatedSwitcher(
  duration: reducedMotion
    ? Duration.zero  // No animation
    : Duration(milliseconds: 300),
  ...
)
```

---

## Testing Checklist

### Visual Testing
- [ ] Renders correctly on 480x800 (low-end)
- [ ] Renders correctly on 1080x1920 (mid-range)
- [ ] Landscape mode works
- [ ] Dark mode compatible (future)
- [ ] High contrast readable

### Language Testing
- [ ] English displays correctly
- [ ] Amharic (Ethiopic script) renders
- [ ] Afan Oromo (Latin script) renders
- [ ] Language switch updates all text
- [ ] RTL languages work (future Arabic)

### Interaction Testing
- [ ] Language selector opens bottom sheet
- [ ] Login button navigates to login screen
- [ ] Register button navigates to register screen
- [ ] Guest link triggers guest mode
- [ ] Back button exits app (root screen)
- [ ] All buttons have ripple effect

### Performance Testing
- [ ] Loads in <1 second on 2GB RAM device
- [ ] No frame drops during animations
- [ ] Memory usage <50MB for welcome screen
- [ ] Works 100% offline

---

## File Structure

```
mobile/lib/features/onboarding/
├── screens/
│   └── welcome_screen.dart
├── widgets/
│   ├── agriculture_hero.dart
│   ├── language_selector_card.dart
│   ├── value_proposition_list.dart
│   ├── welcome_actions.dart
│   └── language_bottom_sheet.dart
└── bloc/
    ├── onboarding_bloc.dart
    ├── onboarding_event.dart
    └── onboarding_state.dart

mobile/assets/illustrations/
└── agriculture_hero.svg  (< 50KB)
```

---

**Status**: Ready for Implementation
**Estimated Effort**: 2-3 days
**Priority**: High (blocking better UX)
