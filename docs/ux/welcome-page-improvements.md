# Welcome & Auth — UI Improvements

**Date**: 2026-02-19
**Status**: Open
**Related files**:
- `mobile/lib/features/onboarding/screens/welcome_screen.dart`
- `mobile/lib/features/onboarding/widgets/welcome_actions.dart`
- `mobile/lib/features/onboarding/widgets/value_proposition_list.dart`
- `mobile/lib/features/auth/screens/auth_gate.dart`
- `mobile/lib/features/auth/screens/register_screen.dart`

---

## Current State

The welcome screen layout, language selector, login button, and register button all work. The remaining items below are stubbed or incomplete.

---

## Task 1: Implement "Explore as Guest" Flow

**Priority**: High
**File**: `welcome_actions.dart:84-87`

### What's broken
The button shows a "Coming Soon" snackbar instead of navigating anywhere.

```dart
// Current (stub)
onPressed: () {
  ScaffoldMessenger.of(context).showSnackBar(
    SnackBar(content: Text(l10n.comingSoon)),
  );
},
```

### What needs to happen
1. Add a guest/anonymous state variant to `AuthBloc` (e.g. `AuthGuest`) so the rest of the app knows the user is unauthenticated but browsing.
2. Update `AuthGate` (`auth_gate.dart`) to route `AuthGuest` → `MainNavigation` in a read-only mode.
3. Wire the button to dispatch a guest-mode event instead of showing a snackbar.
4. Define what guests can see (marketplace listings, read-only) vs what requires login (creating listings, ordering, chat).
5. Add a persistent banner or prompt inside the guest session encouraging sign-up.

### Acceptance criteria
- Tapping "Explore as Guest" navigates to the main app in read-only mode.
- Guest users can browse listings but cannot create, order, or chat.
- A clear sign-up prompt is visible during the guest session.
- Works fully offline (no network call needed to enter guest mode).

---

## Task 2: Use Localized String for "Explore as Guest" Label

**Priority**: Low (quick fix)
**File**: `welcome_actions.dart:89-90`

### What's wrong
The button text is hardcoded in English despite `l10n.exploreAsGuest` already existing in all three languages.

```dart
// Current
child: const Text('Explore as Guest', // TODO: Add to l10n

// Fix
child: Text(l10n.exploreAsGuest,
```

---

## Task 3: Localize Value Proposition Strings

**Priority**: Low (quick fix)
**File**: `value_proposition_list.dart:32-39`

### What's wrong
Two value proposition items are hardcoded in English:

| Line | Current (hardcoded) | Needed |
|------|-------------------|--------|
| 34   | `'Telebirr Payment'` | Add `l10n.telebirrPayment` key and translations |
| 39   | `'Connect with Local Markets'` | Add `l10n.connectWithLocalMarkets` key and translations |

### Steps
1. Add `telebirrPayment` and `connectWithLocalMarkets` to the ARB files for all three languages.
2. Run `flutter gen-l10n`.
3. Replace hardcoded strings with `l10n.telebirrPayment` and `l10n.connectWithLocalMarkets`.
4. Remove the `const` keyword from those `_ValuePropItem` constructors (they'll now depend on runtime `l10n`).

---

## Task 4: Replace Hero Placeholder with SVG Illustration

**Priority**: Medium
**File**: `welcome_screen.dart:43-47`

### What's there now
A plain `Icons.agriculture` Material icon at 120dp. The mockup (`welcome-screen-mockup.md`) specifies a custom SVG with Ethiopian farmer, truck, and market imagery.

### Steps
1. Design or source an SVG illustration (<50KB) depicting Ethiopian agricultural context.
2. Add `flutter_svg` dependency (if not already present).
3. Place asset at `mobile/assets/illustrations/agriculture_hero.svg`.
4. Replace the `Icon` widget with `SvgPicture.asset(...)`.

---

## Task 5: Implement "How It Works" Tutorial

**Priority**: Low
**File**: `welcome_screen.dart:123-131`

### What's there now
The "How it works" chip shows a hardcoded English snackbar:
```dart
'Tutorial carousel coming soon. You can explore the app by creating an account!'
```

### What it should do
- Open a bottom sheet or a short page-view carousel (3-4 steps) explaining the app flow: Post → Match → Deliver → Pay.
- Each step should have an illustration/icon, a title, and a one-line description.
- Fully localized in all three languages.
- Hardcoded snackbar text should also be replaced with a localized string in the interim if the carousel is deferred.

---

## Task 6: Add Password Strength Validation on Registration

**Priority**: High
**File**: `register_screen.dart:321-358`

### What's there now
Password validation only checks two things:
1. Field is not empty.
2. Minimum 6 characters.

No requirements for character types, no real-time feedback, and the confirm-password field doesn't even check for empty input.

```dart
// Current validator (register_screen.dart:332-339)
validator: (value) {
  if (value == null || value.isEmpty) {
    return l10n.password;
  }
  if (value.length < 6) {
    return l10n.passwordRequirements;
  }
  return null;
},
```

The same minimal validator is duplicated in `login_screen.dart:175-212`.

### What needs to happen

**A. Stronger validation rules**
Enforce at minimum:
- At least 8 characters (increase from 6).
- At least one uppercase letter.
- At least one digit.
- (Optional) at least one special character — consider that rural users on basic keyboards may struggle with special characters, so this could be a "stronger" suggestion rather than a hard block.

**B. Real-time password strength indicator**
Add a visual strength meter below the password field:
- Weak (red) — under 8 chars or missing a character type.
- Fair (orange) — meets minimum requirements.
- Strong (green) — 10+ chars with mixed types.

This helps low-literacy users understand what's expected without reading error messages.

**C. Fix confirm-password validator**
The current confirm-password validator (`register_screen.dart:371-376`) doesn't check for empty input:
```dart
// Current — allows submitting with empty confirm field if password is also empty
validator: (value) {
  if (value != _passwordController.text) {
    return l10n.passwordsDoNotMatch;
  }
  return null;
},
```
Add an empty check before the match check.

**D. Shared validation utility**
Extract password validation into a reusable function (e.g. `lib/shared/utils/validators.dart`) so login and registration don't duplicate logic.

**E. Update localization strings**
Update `passwordRequirements` in all three ARB files to reflect the new rules. Add new keys for individual error messages if using per-rule feedback:
- `passwordTooShort` — "Must be at least 8 characters"
- `passwordNeedsUppercase` — "Must include an uppercase letter"
- `passwordNeedsDigit` — "Must include a number"

### Acceptance criteria
- Registration rejects passwords shorter than 8 characters.
- Registration rejects passwords missing an uppercase letter or a digit.
- A visual strength indicator updates as the user types.
- Confirm-password field rejects empty input.
- Validation messages are localized in all three languages.
- Login screen uses the same shared validator.

---

## Task 7: Implement Data Usage Screen

**Priority**: Medium
**File**: `settings_screen.dart:37-46`

### What's there now
The "Data Usage" list tile in Settings shows a "Coming Soon" snackbar on tap. No screen, no tracking logic.

```dart
// settings_screen.dart:41-45
onTap: () {
  ScaffoldMessenger.of(context).showSnackBar(
    SnackBar(content: Text(l10n.comingSoon)),
  );
},
```

### What it should do
Navigate to a dedicated Data Usage screen that shows:

1. **Sync data breakdown** — how much data the app has consumed for syncing (listings, images, audio notes). Track bytes sent/received in the sync worker and persist totals in local storage.
2. **Per-category stats** — e.g. Images: 12 MB, Listings: 0.5 MB, Audio: 3 MB.
3. **Time period selector** — today / this week / this month / all time.
4. **Reset counter** button.
5. **Tip text** — localized advice like "Switch to Data Saver Mode to reduce usage" with a link to toggle it.

### Implementation notes
- Track data at the HTTP client layer (intercept request/response sizes).
- Store cumulative counters in Isar or SharedPreferences with daily granularity.
- All data is local — no server call needed to display this screen.
- Keep the UI simple: a few `ListTile`s or a bar chart. No heavy chart libraries.

### Acceptance criteria
- Tapping "Data Usage" navigates to a real screen (no snackbar).
- Screen shows total data consumed, broken down by category.
- Data persists across app restarts.
- Fully localized in all three languages.
- Works offline.

---

## Task 8: Implement Data Saver Mode Toggle

**Priority**: High
**File**: `settings_screen.dart:48-58`

### What's there now
A `SwitchListTile` that is hardcoded to `value: true` and shows "Coming Soon" on toggle. The switch doesn't actually change state or affect anything.

```dart
// settings_screen.dart:48-58
SwitchListTile(
  secondary: const Icon(Icons.save_alt),
  title: Text(l10n.dataSaverMode),
  subtitle: Text(l10n.dataSaverDescription),
  value: true,  // hardcoded, never changes
  onChanged: (value) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(l10n.comingSoon)),
    );
  },
),
```

### What it should do

**A. Persist the toggle**
Store the data saver preference in SharedPreferences (or the existing settings provider). Read it on app start.

**B. Define what "data saver" means**
When enabled:
- **Images**: Load thumbnails only; skip full-resolution images until user taps to expand.
- **Sync frequency**: Reduce background sync interval (e.g. from 15 min to 60 min).
- **Audio**: Compress audio notes at lower bitrate before syncing.
- **Prefetch**: Disable prefetching of listings the user hasn't viewed.

**C. Wire it through the app**
- Expose the setting via a provider or BLoC so widgets can read it.
- Image widgets check the flag and load thumbnail vs full size.
- Sync worker reads the flag and adjusts its interval.

**D. Update the switch UI**
- Bind `value` to the actual persisted setting.
- `onChanged` should toggle the setting, not show a snackbar.
- Show a confirmation snackbar after toggling: "Data Saver Mode enabled" / "disabled".

### Acceptance criteria
- Toggle persists across app restarts.
- When enabled, images load as thumbnails only.
- When enabled, sync interval increases.
- Switch reflects the actual saved state (not hardcoded `true`).
- Localized in all three languages.

---

## Task 9: Implement Audio Note Recording in Create Listing

**Priority**: Medium (deferred to Phase R6 per BACKLOG.md F-20, but UI should be honest)
**File**: `create_crop_listing_screen.dart:243-256`

### What's there now
A plain `OutlinedButton.icon` that shows "Coming Soon" on tap. No recording, no playback, no audio packages installed.

```dart
// create_crop_listing_screen.dart:244-256
OutlinedButton.icon(
  onPressed: () {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(l10n.comingSoon)),
    );
  },
  icon: const Icon(Icons.mic),
  label: Text(l10n.recordAudioNote),
  ...
),
```

### What exists already
- **Schema field**: `audioNoteRef` in `crop_listing.dart:44-47` (format `audio/{userId}/{listingId}.m4a`) — never populated.
- **BLoC parameter**: `CreateCropListing` event accepts `audioNoteRef` — always `null`.
- **Localization**: `l10n.recordAudioNote` exists in all three languages.
- **Design doc**: F-20 in `specs/features/BACKLOG.md:487-503` — full plan for voice-to-text with offline speech recognition research.

### What's missing
1. **Audio packages** — no `record`, `just_audio`, or `permission_handler` in `pubspec.yaml`.
2. **Recording service** — no `AudioRecorderService` to capture, compress (Opus 16-32kbps), and store audio files locally.
3. **Microphone permissions** — no Android/iOS permission handling.
4. **Recording UI** — the button should transform into a recording widget (waveform animation, stop/cancel, duration timer) instead of being a static button.
5. **Playback UI** — after recording, show a player with play/pause and delete to re-record.
6. **File storage** — save to `audio/{userId}/{listingId}.m4a` and populate `audioNoteRef` in the BLoC event.
7. **Sync** — upload audio file when online (server endpoint not yet defined).

### UI design direction (per F-20 plan)
The planned design is icon-heavy and low-literacy friendly:
- **Idle state**: Large mic button with localized label.
- **Recording state**: Pulsing red circle, live waveform or level meter, elapsed time, stop button.
- **Recorded state**: Play/pause bar with duration, delete button to re-record.
- All states must be obvious without reading text (visual + color cues).

### Phased approach
**Phase A (now)**: Improve the stub — replace the generic button with a disabled-looking UI that clearly communicates "coming soon" (e.g. greyed-out mic with a badge), rather than a live-looking button that disappoints.
**Phase B (R6)**: Full recording/playback implementation per F-20.

### Acceptance criteria (Phase A)
- Button visually communicates that the feature is not yet available (not a surprise snackbar).
- Tapping shows an informative dialog instead of a snackbar (e.g. "Voice notes coming soon — type your description above for now").

### Acceptance criteria (Phase B)
- User can record, play back, and delete audio notes.
- Audio is compressed and stored locally.
- `audioNoteRef` is populated in the listing.
- Works fully offline.
- Microphone permission requested gracefully.

---

## Task 10: Add "Other" Option for Crop Type Selection

**Priority**: High
**File**: `crop_type.dart` and `create_crop_listing_screen.dart:298-365`

### What's there now
The `CropType` enum has a fixed list of 9 crops:
- Teff, Wheat, Barley, Maize, Chickpea, Lentils, Sorghum, Coffee, Sesame

These are displayed in a `_CropTypeGrid` widget. Farmers can only select from this hardcoded list — there is no way to enter a crop that isn't listed.

### Problem
Ethiopia grows many more crops (e.g. enset/false banana, haricot beans, niger seed, flax, millet, chat/khat, sugarcane, potatoes, onions, tomatoes, peppers, avocado, mango, banana). A farmer growing any unlisted crop cannot create a listing at all.

### What needs to happen

**Option A — "Other" with free-text (recommended for now)**
1. Add an `other` value to the `CropType` enum with a generic icon (e.g. `Icons.more_horiz`).
2. When the user selects "Other", show a `TextFormField` below the grid for the custom crop name.
3. Store the custom name in a new `customCropName` field in the schema and BLoC event.
4. Display custom name wherever crop type is shown (listing cards, detail screen).
5. Add localization for "Other" in all three languages.

**Option B — Expanded enum + "Other" (better long-term)**
1. Expand the enum with the most common missing crops (enset, haricot bean, niger seed, millet, potato, onion, tomato).
2. Still include "Other" as a catch-all.
3. Add localization for every new crop.

**Option C — Server-driven crop list (future)**
Fetch the crop list from the server so it can be updated without an app release. Falls back to the hardcoded enum when offline.

### Acceptance criteria
- Farmers can select a crop not in the predefined list.
- "Other" option is visually distinct in the grid (e.g. `+` or `...` icon).
- Custom crop name is stored, synced, and displayed correctly.
- Localized in all three languages.

---

## Task 11: Add Town/City Autocomplete for Trip From/To Fields

**Priority**: High
**File**: `create_trip_screen.dart:209-255`

### What's there now
The "From Woreda" and "To Woreda" fields are plain `TextFormField`s with free-text input. The only validation is non-empty and origin != destination. Users must type location names manually with no suggestions.

```dart
// create_trip_screen.dart:210-211
TextFormField(
  controller: _fromWoredaController,
  decoration: InputDecoration(
    labelText: l10n.fromWoreda,
    ...
  ),
),
```

### Problem
- Users can misspell location names, making matching unreliable (e.g. "Adis Abeba" vs "Addis Ababa" vs "አዲስ አበባ").
- No standard set of locations — every trucker invents their own spelling.
- Farmers searching for trips can't find matches because names don't align.

### What needs to happen

1. **Create a local location dataset** — a list of Ethiopian towns/cities/woredas relevant to agricultural routes. Start with the major market towns and regional capitals (~50-100 entries). Each entry needs:
   - English name
   - Amharic name (ግዕዝ script)
   - Afan Oromo name
   - Region/zone (for grouping)

2. **Replace `TextFormField` with `Autocomplete` widget** — Flutter's built-in `Autocomplete` or a `SearchAnchor` that filters the local dataset as the user types. Match against all three language variants so typing in any language finds the location.

3. **Allow custom entry** — if the user's location isn't in the list, they can still type it freely (don't hard-block). Show a subtle hint like "Location not found — using custom name".

4. **Store a normalized location key** — save the canonical location ID (not just the display name) so matching is reliable across languages.

5. **Bundle the dataset locally** — no network call needed. Ship as a JSON or Dart constant. Keep it under 50KB.

### Acceptance criteria
- Typing in the From/To fields shows autocomplete suggestions from the local dataset.
- Suggestions match against English, Amharic, and Afan Oromo names.
- Users can still enter a custom location not in the list.
- Stored location is normalized for reliable trip-to-listing matching.
- Works fully offline.
- Dataset is bundled in the app (<50KB).

---

## Task 12: Implement Edit Profile Screen

**Priority**: High
**File**: `profile_screen.dart:115-125`

### What's there now
The "Edit" card shows a "Coming Soon" snackbar on tap.

```dart
// profile_screen.dart:119-124
onTap: () {
  / TODO: Navigate to edit profile screen
  ScaffoldMessenger.of(context).showSnackBar(
    SnackBar(content: Text(l10n.comingSoon)),
  );
},
```

### What it should do
Navigate to an Edit Profile screen where the user can update:
1. **Full name** — pre-filled from current auth state.
2. **Profile photo** — optional avatar (compressed, stored locally and synced).
3. **Role** — farmer / trucker / buyer (if role switching is allowed, otherwise read-only).
4. **Location / Woreda** — home area for better listing and trip matching.

### Implementation notes
- The screen should be a simple `Form` with pre-populated fields from `AuthAuthenticated.user`.
- On save, dispatch an event to `AuthBloc` to update user info locally and enqueue a server sync.
- Phone number should be read-only (it's the login identifier).

### Acceptance criteria
- Tapping "Edit" navigates to a real form screen.
- Fields are pre-filled with current user data.
- Changes persist locally and sync when online.
- Localized in all three languages.

---

## Task 13: Implement My Listings Screen

**Priority**: High
**File**: `profile_screen.dart:129-139`

### What's there now
The "Listings" card shows a "Coming Soon" snackbar on tap.

```dart
// profile_screen.dart:133-138
onTap: () {
  / TODO: Navigate to user's listings
  ScaffoldMessenger.of(context).showSnackBar(
    SnackBar(content: Text(l10n.comingSoon)),
  );
},
```

### What it should do
Navigate to a filtered view showing only the current user's crop listings, with the ability to:
1. **View** all listings the user has created (active, expired, sold).
2. **Edit** an existing listing (navigate to create screen pre-filled with listing data).
3. **Delete / deactivate** a listing (with confirmation dialog).
4. **Status indicators** — show whether each listing is active, pending sync, or expired.

### Implementation notes
- Reuse the existing `CropListingCard` widget and `BrowseListingsScreen` layout.
- Filter by `farmerId == authState.userId` using the existing Isar query.
- Add an `EditCropListing` event to the BLoC if it doesn't exist.
- Empty state: show a message like "You haven't created any listings yet" with a CTA button to create one.

### Acceptance criteria
- Tapping "Listings" shows a list of the user's own listings.
- User can edit and delete their listings.
- Empty state is handled gracefully.
- Works offline (reads from local Isar database).
- Localized in all three languages.

---

## Task 14: Implement My Orders Screen

**Priority**: Medium
**File**: `profile_screen.dart:143-153`

### What's there now
The "My Orders" card shows a "Coming Soon" snackbar on tap.

```dart
// profile_screen.dart:147-152
onTap: () {
  / TODO: Navigate to orders screen
  ScaffoldMessenger.of(context).showSnackBar(
    SnackBar(content: Text(l10n.comingSoon)),
  );
},
```

### What it should do
Navigate to an Orders screen showing the user's transaction history:
1. **Buyer view** — orders placed on crop listings (pending, in-transit, delivered, disputed).
2. **Farmer view** — incoming orders on the user's listings.
3. **Trucker view** — trips the user has been booked for.
4. **Order details** — tap to see full order info, delivery status, payment status.

### Implementation notes
- This depends on the order/transaction feature being built (tied to Telebirr escrow — F-13).
- For now, at minimum show a proper empty state screen instead of a snackbar: "No orders yet — browse listings to get started."
- When the transaction flow is ready, wire it up to display real order data from Isar.

### Acceptance criteria
- Tapping "My Orders" navigates to a real screen (no snackbar).
- Shows order history filtered by user role and ID.
- Empty state with helpful CTA if no orders exist.
- Works offline.
- Localized in all three languages.

---

## Task 15: Implement Notifications Screen

**Priority**: Medium
**File**: `profile_screen.dart:157-167`

### What's there now
The "Notifications" card shows a "Coming Soon" snackbar on tap.

```dart
// profile_screen.dart:161-166
onTap: () {
  / TODO: Navigate to notifications settings
  ScaffoldMessenger.of(context).showSnackBar(
    SnackBar(content: Text(l10n.comingSoon)),
  );
},
```

### What it should do
Navigate to a Notifications screen with two sections:

**A. Notification inbox** — list of notifications the user has received:
- New order on your listing
- Trip matched to your listing
- Delivery status updates
- Payment confirmations
- Each entry: icon, title, message, timestamp, read/unread indicator.

**B. Notification preferences** — toggles for what the user wants to be notified about:
- Order updates (on/off)
- New listing matches (on/off)
- Delivery updates (on/off)
- Persist preferences in SharedPreferences.

### Implementation notes
- Notifications are stored locally in Isar (synced from server push or pulled during sync).
- For now (pre-push-notification infrastructure), show locally generated notifications from sync events.
- Empty state: "No notifications yet — you'll see updates about your listings and orders here."

### Acceptance criteria
- Tapping "Notifications" navigates to a real screen (no snackbar).
- Shows a list of notifications with read/unread state.
- Notification preferences can be toggled and persist.
- Empty state is handled.
- Works offline.
- Localized in all three languages.

---

## Summary

| # | Task | Priority | Effort |
|---|------|----------|--------|
| 1 | Implement "Explore as Guest" flow | High | 1-2 days |
| 2 | Use `l10n.exploreAsGuest` for button label | Low | 5 min |
| 3 | Localize value proposition strings | Low | 15 min |
| 4 | Replace hero icon with SVG illustration | Medium | Depends on asset |
| 5 | Implement "How It Works" tutorial | Low | 0.5-1 day |
| 6 | Add password strength validation on registration | High | 0.5 day |
| 7 | Implement Data Usage screen | Medium | 1 day |
| 8 | Implement Data Saver Mode toggle | High | 0.5-1 day |
| 9 | Implement Audio Note recording in Create Listing | Medium | Phase A: 1hr, Phase B: 2-3 days |
| 10 | Add "Other" option for crop type selection | High | 0.5 day |
| 11 | Add town/city autocomplete for trip From/To | High | 1 day |
| 12 | Implement Edit Profile screen | High | 1 day |
| 13 | Implement My Listings screen | High | 1 day |
| 14 | Implement My Orders screen | Medium | 1-2 days |
| 15 | Implement Notifications screen | Medium | 1-2 days |
