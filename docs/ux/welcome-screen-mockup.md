# Welcome Screen - Detailed Mockup

## Visual Layout (Mobile - 360x640dp base)

```
┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓
┃ Status Bar (System)                    ┃ 24dp
┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛
┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓
┃            [Scroll Container]           ┃
┃                                         ┃
┃  ┌───────────────────────────────────┐ ┃
┃  │    [Agriculture Illustration]     │ ┃ 120dp
┃  │    (Farmer, Truck, Market)        │ ┃
┃  └───────────────────────────────────┘ ┃
┃                                         ┃
┃         WeredaConnect                   ┃ 24sp, bold
┃                                         ┃
┃  ┌───────────────────────────────────┐ ┃
┃  │ Language / ቋንቋ / Afaan            │ ┃ 56dp height
┃  │  English                      ▼  │ ┃ (large touch)
┃  └───────────────────────────────────┘ ┃
┃                                         ┃
┃  Connecting Ethiopian Farmers,          ┃ 16sp, centered
┃  Truckers, and Buyers                   ┃
┃                                         ┃
┃  ✓ Works Offline                        ┃
┃  ✓ Voice Support in Your Language       ┃ 14sp, list
┃  ✓ Telebirr Payment                     ┃
┃  ✓ Connect with Local Markets           ┃
┃                                         ┃
┃  ┌───────────────────────────────────┐ ┃
┃  │          [LOGIN]                  │ ┃ 48dp, green fill
┃  └───────────────────────────────────┘ ┃
┃  ┌───────────────────────────────────┐ ┃
┃  │      [CREATE ACCOUNT]             │ ┃ 48dp, green outline
┃  └───────────────────────────────────┘ ┃
┃                                         ┃
┃        [Explore as Guest]               ┃ 16sp, link
┃                                         ┃
┃  ┌─────────────────┬─────────────────┐ ┃
┃  │ How it works    │ Offline Mode    │ ┃ 14sp, chips
┃  └─────────────────┴─────────────────┘ ┃
┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛
```

## Core Dart Patterns

### Hero Illustration
```dart
Container(
  height: 120,
  child: SvgPicture.asset(
    'assets/illustrations/agriculture_hero.svg',
    fit: BoxFit.contain,
  ),
)
```
- SVG optimized to <50KB
- Ethiopian context: traditional farmer clothing, local crops

### Language Selector
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
              Text(selectedLanguageName,
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
- Bottom sheet with radio buttons (56dp height, large touch targets)
- Shows language in native script
- Instant UI update on selection

### Value Proposition List
```dart
class _ValuePropItem extends StatelessWidget {
  Widget build(BuildContext context) {
    return Padding(
      padding: EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          Icon(icon, color: color, size: 24),
          SizedBox(width: 12),
          Expanded(
            child: Text(text, style: TextStyle(fontSize: 14, height: 1.4)),
          ),
        ],
      ),
    );
  }
}
```

### Action Buttons
```dart
// Primary: Login
ElevatedButton(
  onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => LoginScreen())),
  style: ElevatedButton.styleFrom(
    backgroundColor: Color(0xFF2E7D32),
    foregroundColor: Colors.white,
    minimumSize: Size.fromHeight(48),
    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
  ),
  child: Text(l10n.login, style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
)

// Secondary: Create Account — same but OutlinedButton with green border
// Guest: TextButton with underline
```

---

## Offline Handling

Welcome screen works 100% offline:
- No network requests
- All assets bundled with app
- Language switching persisted locally

```dart
// Offline indicator (top-right corner)
if (!isOnline)
  Positioned(
    top: 8, right: 8,
    child: Chip(
      avatar: Icon(Icons.cloud_off, size: 16),
      label: Text(l10n.offlineMode, style: TextStyle(fontSize: 12)),
      backgroundColor: Colors.orange[100],
    ),
  ),
```

---

## Testing Checklist

- [ ] Renders on 480x800 (low-end) and 1080x1920 (mid-range)
- [ ] Amharic (Ethiopic script) and Afan Oromo (Latin) render correctly
- [ ] Language switch updates all text instantly
- [ ] Login/Register/Guest buttons navigate correctly
- [ ] Loads in <1s on 2GB RAM device
- [ ] No frame drops during animations
- [ ] Memory <50MB for welcome screen
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
