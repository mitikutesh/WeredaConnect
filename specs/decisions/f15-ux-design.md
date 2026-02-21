# F-15 Conflict Resolution UX Design

**Status**: Approved
**Date**: 2026-02-21
**Author**: UX Design Agent
**Feature**: Offline Sync Conflict Resolution UI

---

## 1. Overview

When a user edits data offline and syncs, the server may have a newer version. The server always wins (last-write-wins strategy). This UI explains what happened and shows what changed, in a way that low-literacy Ethiopian rural users can understand.

**Key principle**: Conflicts are rare. Most users will never see this screen. When they do, the UI must be calming, not alarming.

---

## 2. Conflict Notification Banner

A non-intrusive Material Banner that slides in at the top of the current screen when a sync completes with auto-resolved conflicts. It does NOT interrupt the user's workflow.

### Behavior
- Appears after sync completes, only if conflicts were auto-resolved
- Stays visible for 8 seconds, then auto-dismisses
- Tappable "View Changes" action to see details
- Dismissible via swipe or close button
- If dismissed (tapped close or auto-dismissed), the conflict is marked as "notified" and not re-shown
- Maximum one banner at a time; if multiple conflicts, show count (e.g., "2 items were updated")

### Visual Spec
- Background: `Colors.amber.shade50` (warm, non-alarming)
- Leading icon: `Icons.sync` (24dp, amber.shade700)
- Text: 16sp, body style, max 2 lines
- Action button: "View Changes" text button, primary color
- Close button: `Icons.close` (24dp), top-right
- Min height: 56dp
- Horizontal padding: 16dp

### Copy (English)
- Single conflict: "Your [entity] was updated on another device"
- Multiple conflicts: "[N] items were updated on another device"
- Action: "View Changes"

### ASCII Mockup

```
+--------------------------------------------------+
| [x]                                              |
|  (sync)  Your crop listing was updated           |
|          on another device.                      |
|                              [ View Changes ]    |
+--------------------------------------------------+
```

With count:

```
+--------------------------------------------------+
| [x]                                              |
|  (sync)  2 items were updated                    |
|          on another device.                      |
|                              [ View Changes ]    |
+--------------------------------------------------+
```

### Narrow Screen (360px)

```
+------------------------------------+
| [x]                                |
|  (sync)  Your crop listing was     |
|          updated on another        |
|          device.                   |
|                   [ View Changes ] |
+------------------------------------+
```

---

## 3. ConflictResolutionScreen

A full-screen page (pushed via Navigator, not a modal) that shows all pending/recent conflicts. Accessible from the notification banner or from Settings > Sync History.

### Screen Structure

```
AppBar: "Updates from Sync"
Body:
  - If single conflict: show detail directly
  - If multiple conflicts: show scrollable list of conflict cards
  - If no conflicts: empty state
Footer:
  - "Got It" primary button (dismisses all and pops screen)
```

### Conflict Detail Card

Each conflict is shown as a Card with:
1. **Header row**: Entity icon + entity name + timestamp
2. **Diff section**: Two-column or stacked comparison (local vs server)
3. **Result badge**: "Server version applied" in green

### Visual Spec - Card
- Card elevation: 1
- Border radius: 12dp
- Padding: 16dp
- Margin between cards: 12dp

### Visual Spec - Header Row
- Entity icon: 24dp (crop = `Icons.grass`, trip = `Icons.local_shipping`, delivery = `Icons.inventory_2`)
- Entity name: 16sp, `titleMedium`, bold
- Timestamp: 14sp, `bodySmall`, secondary color
- Spacing: icon 12dp gap text

### Visual Spec - Diff Section
- Stacked layout (not side-by-side, too narrow on 360px screens)
- **"Your version" row**: amber.shade50 background, amber.shade700 left border (4dp)
  - Label: "Your version" in 12sp caps, amber.shade800
  - Content: field name + old value, 14sp body
- **"Current version" row**: green.shade50 background, green.shade700 left border (4dp)
  - Label: "Current version" in 12sp caps, green.shade800
  - Content: field name + new value, 14sp body
  - Checkmark icon (16dp) to indicate this is the applied version
- Each changed field is listed as a separate row pair
- Unchanged fields are NOT shown (only show what changed)
- Vertical spacing between field pairs: 8dp

### Visual Spec - Result Badge
- Container: green.shade50 background, green.shade700 border, rounded 8dp
- Icon: `Icons.check_circle` 16dp, green.shade700
- Text: "Server version applied" in 12sp, green.shade800
- Height: 32dp
- Full width, centered content

### ASCII Mockup - Single Conflict

```
+--------------------------------------------------+
|  < Updates from Sync                             |
+--------------------------------------------------+
|                                                  |
|  +--------------------------------------------+ |
|  |  (grass) Crop Listing: Teff 50kg            | |
|  |  Updated Feb 21, 2026                       | |
|  |                                             | |
|  |  +----------------------------------------+| |
|  |  | YOUR VERSION                amber bg    || |
|  |  | Price: 45 Birr/kg                       || |
|  |  +----------------------------------------+| |
|  |  +----------------------------------------+| |
|  |  | CURRENT VERSION  (check)   green bg    || |
|  |  | Price: 48 Birr/kg                       || |
|  |  +----------------------------------------+| |
|  |                                             | |
|  |  +----------------------------------------+| |
|  |  | YOUR VERSION                amber bg    || |
|  |  | Quantity: 50 quintals                   || |
|  |  +----------------------------------------+| |
|  |  +----------------------------------------+| |
|  |  | CURRENT VERSION  (check)   green bg    || |
|  |  | Quantity: 45 quintals                   || |
|  |  +----------------------------------------+| |
|  |                                             | |
|  |  +---(check) Server version applied-------+| |
|  |                                             | |
|  +--------------------------------------------+ |
|                                                  |
|  +--------------------------------------------+ |
|  |                                             | |
|  |  [========= Got It (primary) ============] | |
|  |                                             | |
|  +--------------------------------------------+ |
|                                                  |
+--------------------------------------------------+
```

### ASCII Mockup - Multiple Conflicts

```
+--------------------------------------------------+
|  < Updates from Sync                             |
+--------------------------------------------------+
|                                                  |
|  2 items were updated on another device          |
|                                                  |
|  +--------------------------------------------+ |
|  |  (grass) Crop Listing: Teff 50kg            | |
|  |  Updated Feb 21, 2026                       | |
|  |                                             | |
|  |  YOUR VERSION              amber bg         | |
|  |  Price: 45 Birr/kg                          | |
|  |                                             | |
|  |  CURRENT VERSION (check)   green bg         | |
|  |  Price: 48 Birr/kg                          | |
|  |                                             | |
|  |  (check) Server version applied             | |
|  +--------------------------------------------+ |
|                                                  |
|  +--------------------------------------------+ |
|  |  (truck) Trip: Addis → Hawassa              | |
|  |  Updated Feb 21, 2026                       | |
|  |                                             | |
|  |  YOUR VERSION              amber bg         | |
|  |  Departure: Feb 22, 8:00 AM                 | |
|  |                                             | |
|  |  CURRENT VERSION (check)   green bg         | |
|  |  Departure: Feb 23, 7:00 AM                 | |
|  |                                             | |
|  |  (check) Server version applied             | |
|  +--------------------------------------------+ |
|                                                  |
|  +--------------------------------------------+ |
|  |                                             | |
|  |  [========= Got It (primary) ============] | |
|  |                                             | |
|  +--------------------------------------------+ |
|                                                  |
+--------------------------------------------------+
```

### ASCII Mockup - Empty State

```
+--------------------------------------------------+
|  < Updates from Sync                             |
+--------------------------------------------------+
|                                                  |
|                                                  |
|                                                  |
|              (check_circle, 80dp)                |
|                 green, large                     |
|                                                  |
|           Everything is up to date               |
|                                                  |
|       No changes from other devices.             |
|                                                  |
|                                                  |
|                                                  |
+--------------------------------------------------+
```

---

## 4. Interaction Flow

```
  [User edits offline]
        |
        v
  [App syncs when online]
        |
        v
  [Server detects conflict, applies server version]
        |
        v
  [Sync engine returns conflict metadata]
        |
        v
  [ConflictNotificationBanner appears]
        |
   +----+----+
   |         |
   v         v
 [Auto-    [User taps
  dismiss]  "View Changes"]
   |         |
   v         v
 [Mark as  [Push ConflictResolutionScreen]
  notified]  |
             v
           [User views diff cards]
             |
             v
           [User taps "Got It"]
             |
             v
           [Mark conflicts as dismissed, pop screen]
```

---

## 5. Edge Cases

### 5.1 Multiple Conflicts at Once
- Banner shows count: "2 items were updated on another device"
- ConflictResolutionScreen shows all conflicts as a scrollable list of cards
- "Got It" dismisses all at once
- Conflicts are sorted by timestamp, newest first

### 5.2 User Dismisses Without Viewing
- If user closes the banner (X button) or it auto-dismisses after 8 seconds:
  - Mark conflict as `notified` in local DB
  - Do NOT re-show the banner for the same conflict
  - User can still access conflict history via Settings > Sync History
- Rationale: Conflicts are auto-resolved (server wins). Pestering the user serves no purpose.

### 5.3 Conflict While on ConflictResolutionScreen
- If a new conflict arrives while the user is already viewing conflicts:
  - Add the new conflict card to the list with a subtle slide-in animation
  - Update the count in the subtitle text
  - Do NOT show a separate banner

### 5.4 No Pending Conflicts (Empty State)
- Show the empty state illustration with checkmark icon
- Text: "Everything is up to date"
- Subtitle: "No changes from other devices."
- Reachable from Settings > Sync History when no conflicts exist

### 5.5 Conflict on Deleted Entity
- If the server deleted an entity the user edited locally:
  - Show in red-tinted card instead of green
  - Icon: `Icons.delete_outline`
  - Message: "This [entity] was removed on another device"
  - No diff comparison, just the notification
  - "Got It" dismisses as normal

### 5.6 Large Number of Field Changes
- If more than 4 fields changed, show the first 3 and a "Show all changes" expandable
- Keeps the card compact on small screens

---

## 6. Ethiopian Context Adaptations

### 6.1 Language & Tone
- Avoid technical terms: NO "conflict", "merge", "version", "sync error"
- Use plain language: "updated on another device", "changes", "your version"
- The tone is informational, not alarming: the system handled it, the user just needs to know

### 6.2 Terminology Mapping
| Technical Term | User-Facing Term (EN) | Notes |
|---|---|---|
| Conflict | "Updated on another device" | Avoids alarming language |
| Server version | "Current version" | Neutral, factual |
| Local version | "Your version" | Personal, relatable |
| Resolved | "Applied" | Action-oriented |
| Entity | Use specific name: "crop listing", "trip", "delivery" | Concrete, not abstract |
| Quintal | "quintals" | Local unit, keep as-is |
| Woreda | "woreda" | Local admin unit, keep as-is |

### 6.3 Visual Cues for Low-Literacy Users
- Color coding carries the primary meaning:
  - Amber = your version (warm, personal)
  - Green = current/applied version (positive, resolved)
- Icons reinforce entity type (crop, truck, package)
- Checkmark icon on "current version" row clearly shows what was applied
- Minimal text required to understand the screen

### 6.4 Touch Targets
- All interactive elements: minimum 48dp height
- "Got It" button: full-width, 48dp height, `FilledButton`
- "View Changes" in banner: 48dp touch target
- Banner close button: 48dp touch target
- Cards are NOT tappable (no nested navigation to reduce confusion)

### 6.5 Font Sizes
- Minimum body text: 14sp
- Card headers: 16sp
- Labels ("YOUR VERSION", "CURRENT VERSION"): 12sp uppercase
- Button text: 14sp
- AppBar title: default (titleLarge)

---

## 7. Accessibility

- All icons have semantic labels for screen readers
- Color is not the only differentiator (labels "Your version" / "Current version" are always shown)
- Sufficient contrast ratios on amber/green backgrounds (use shade50 bg with shade800 text)
- Support for system font scaling up to 1.3x
- Banner auto-dismiss can be disabled via system accessibility settings (reduce motion)

---

## 8. Widget Inventory

| Widget | Type | Description |
|---|---|---|
| `ConflictNotificationBanner` | `StatelessWidget` | MaterialBanner shown after sync with conflicts |
| `ConflictResolutionScreen` | `StatelessWidget` | Full page listing all conflict detail cards |
| `ConflictDetailCard` | `StatelessWidget` | Card showing entity header + field diffs + result badge |
| `ConflictFieldDiff` | `StatelessWidget` | Stacked amber/green row pair for one changed field |
| `ConflictEmptyState` | `StatelessWidget` | Checkmark illustration + "up to date" message |

---

## 9. L10n Keys Required

```
conflictBannerSingle: "Your {entityType} was updated on another device"
conflictBannerMultiple: "{count} items were updated on another device"
conflictBannerAction: "View Changes"
conflictScreenTitle: "Updates from Sync"
conflictYourVersion: "Your version"
conflictCurrentVersion: "Current version"
conflictServerApplied: "Server version applied"
conflictGotIt: "Got It"
conflictEmptyTitle: "Everything is up to date"
conflictEmptySubtitle: "No changes from other devices."
conflictEntityDeleted: "This {entityType} was removed on another device"
conflictShowAllChanges: "Show all changes"
```

---

## 10. Design Rationale

1. **Stacked diff (not side-by-side)**: 360px screens are too narrow for meaningful side-by-side comparison. Stacked layout gives each value full width.

2. **Amber + Green (not Red + Green)**: Red implies error or danger. Amber is warm and neutral — the user's version isn't "wrong", it's just been superseded. Green signals the positive outcome (resolved).

3. **"Got It" (not "OK" or "Accept")**: "Got It" is acknowledgment-oriented. "Accept" implies the user has a choice (they don't — server always wins). "OK" is too terse and may feel dismissive.

4. **No undo/revert button**: The sync strategy is server-wins. Offering a revert button would create false expectations and add complexity. If users need to change back, they can simply edit the entity again.

5. **Non-intrusive banner**: Conflicts are rare and auto-resolved. An alert dialog would be disruptive. A banner respects the user's current workflow.

6. **8-second auto-dismiss**: Long enough to read, short enough to not be annoying. Users who care will tap "View Changes"; those who don't won't be bothered.

7. **Full page (not bottom sheet)**: Bottom sheets can be accidentally dismissed. A full page is more deliberate and gives proper space for the diff content on small screens.
