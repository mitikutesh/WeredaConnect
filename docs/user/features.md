# Features

## Complete Feature List

This document provides a comprehensive overview of all WeredaConnect features.

## Audience
- End users (farmers, truckers, buyers/wholesalers)
- Product managers
- Stakeholders

---

## Core Features

### 1. Crop Listing

**List your crops for sale and reach buyers and truckers directly.**

#### Capabilities
- Create listings with crop type, quantity (quintals), price, and quality grade
- Voice-to-text listing creation in Amharic, Afan Oromo, and English
- Categorize by crop type (Teff, Wheat, Maize, Sorghum, Coffee, Sesame, etc.)
- Set pickup location with map pin or auto-detected GPS coordinates
- Add up to 5 photos of crop quality and storage conditions
- Attach audio notes describing crop details for low-literacy communication
- Edit listings until a transport match is confirmed
- Delete your own listings
- Track listing status (Active, Matched, In Transit, Delivered, Closed)

#### Offline Support
- Create listings offline with voice-to-text
- Photos and audio queued for upload when online
- Automatic sync when connectivity restored

---

### 2. Marketplace Discovery

**Browse and search crop listings across regions.**

#### Capabilities
- View all active crop listings in your area and beyond
- Filter by crop type (Teff, Wheat, Coffee, etc.)
- Filter by price range and quantity
- Sort by: Recent, Nearby, Lowest Price, Largest Quantity
- Search listings by keyword
- View listing details (photos, audio notes, location, seller profile)
- View listings on map with cluster markers by region
- Contact seller directly via in-app messaging or audio note

#### Offline Support
- View previously loaded listings offline
- Cached photos and audio available offline
- Search within locally cached listings offline

---

### 3. Transport Matching

**Connect farmers with truckers for crop delivery.**

#### Capabilities
- Truckers browse available loads by route, crop type, and pickup date
- Farmers request transport for their listed crops
- View trucker profiles (vehicle type, capacity, ratings, verified status)
- Route optimization suggestions based on multiple pickup/dropoff points
- Accept or decline transport requests
- View estimated distance, travel time, and transport cost
- Schedule pickup windows
- Track active transport assignments

#### Offline Support
- Browse cached transport requests offline
- Accept/decline requests offline (syncs later)
- Route information cached for active assignments

---

### 4. Escrow Payments

**Secure transactions through Telebirr escrow integration.**

#### Capabilities
- Buyer locks payment into Telebirr escrow when transport is confirmed
- Funds held securely until delivery is verified
- Automatic release on successful delivery verification (QR + GPS + photo)
- Dispute resolution flow for damaged or missing goods
- Transaction history with full audit trail
- Payment notifications at each stage (locked, in transit, released)
- Support for partial payments on large orders

#### Offline Support
- View transaction history offline
- Payment status cached locally
- Escrow initiation queued if offline (executed on sync)

---

### 5. Delivery Verification

**Validate deliveries with multi-factor evidence.**

#### Capabilities
- QR delivery token generated at trip creation
- Buyer scans QR code at delivery point to confirm receipt
- GPS tracking records pickup and dropoff coordinates
- Photo evidence required at pickup (loaded truck) and delivery (unloaded goods)
- Timestamp verification ensures delivery within expected window
- Audio notes for crop inspection comments at delivery
- Automatic escrow release triggered on successful verification
- Dispute flag if verification conditions are not met

#### Offline Support
- QR codes generated and stored locally
- QR scanning works offline
- GPS coordinates captured offline
- Photos and evidence queued for upload when online

---

### 6. Identity Verification

**Secure onboarding with Fayda ID integration.**

#### Capabilities
- Fayda ID biometric verification for farmer, trucker, and buyer onboarding
- Identity required for all financial transactions (Telebirr escrow)
- Verified badge displayed on user profiles
- One-time verification process during registration
- Trucker vehicle registration and license verification
- Cooperative membership verification (where applicable)

#### Offline Support
- Verification status cached locally after initial onboarding
- Verified identity persists offline
- New verification requires internet connection

---

### 7. Offline-First Architecture

**Work seamlessly with or without internet.**

#### Capabilities
- Local database (Isar) stores all data on-device
- Automatic background sync when connectivity detected
- Sync queue for pending operations (listings, matches, evidence uploads)
- Conflict resolution (server wins for financial data, client merges for listings)
- Sync status indicator showing pending/synced items
- Manual sync trigger
- View sync history and pending queue
- Resumable sync survives app crashes and phone restarts

#### Data Management
- Cache listings, photos, and audio for offline viewing
- Configurable cache size limit
- Clear cache to free device space
- Audio compressed to .ogg/Opus at 16-32 kbps to minimize storage

---

### 8. Localization

**Full support for Ethiopian languages.**

#### Supported Languages
- Amharic
- Afaan Oromoo (Afan Oromo)
- English

#### Localized Content
- UI text in all three languages
- Voice-to-text input in Amharic and Afan Oromo
- Error messages
- Notifications
- Help documentation
- Date/time formats (Ethiopian calendar support)
- Number formats
- Ge'ez script rendering without glitches

---

### 9. Maps & Location

**Visualize crop locations, transport routes, and delivery points.**

#### Capabilities
- View crop listings on map with cluster markers
- Filter map by crop type and availability
- Auto-detect current location via GPS
- Manually place crop pickup location on map
- View transport routes between pickup and delivery points
- Get directions to pickup/delivery locations
- Regional view showing crop density and active routes

#### Offline Support
- Cached map tiles for offline viewing
- GPS works offline for location capture
- Saved routes available offline
- Directions require internet

---

### 10. Media Management

**Handle crop photos, delivery evidence, and audio notes efficiently.**

#### Capabilities
- Take crop photos in-app with camera
- Select photos from gallery
- Crop and rotate photos
- Automatic compression to save data (configurable quality)
- Record audio notes for crop descriptions and inspection comments
- Audio compression (.ogg/Opus at 16-32 kbps)
- View full-screen photos with zoom/pan
- Play audio notes in-app
- Download media for offline access

#### Offline Support
- Photos and audio cached for offline viewing/playback
- Upload queued when offline with automatic retry
- Retry failed uploads on next sync
- Partial upload resumption supported

---

## Upcoming Features

### Phase 2 (Q2 2026)
- Cooperative management tools (group listings, shared accounts)
- Admin dashboard for cooperative leaders and local authorities
- SMS integration for feature phone users (listing alerts, delivery confirmations)
- Advanced search filters (harvest date, organic certification)
- In-app dispute resolution workflow

### Phase 3 (Q3 2026)
- Market price analytics and historical trends by crop and region
- Weather integration (rainfall forecasts, harvest timing alerts)
- Micro-insurance per trip via AICE APIs (automatic coverage on trip start)
- Trucker fleet management for multi-vehicle operators
- Buyer order placement and recurring purchase scheduling

### Phase 4 (Q4 2026)
- Credit scoring based on transaction history and delivery reliability
- Multi-region expansion beyond initial pilot weredas
- Integration with Agricultural Transformation Agency (ATA) data
- AI-powered crop price prediction and demand forecasting
- Voice-only mode for full app operation without reading

---

## User Roles

### Farmer vs Trucker vs Buyer

| Feature | Farmer | Trucker | Buyer |
|---------|--------|---------|-------|
| Create crop listings | Yes | -- | -- |
| Voice-to-text listing | Yes | -- | -- |
| Browse listings | Yes | Yes | Yes |
| Request transport | Yes | -- | Yes |
| Accept transport jobs | -- | Yes | -- |
| Initiate escrow payment | -- | -- | Yes |
| Scan delivery QR | -- | -- | Yes |
| Provide delivery evidence | -- | Yes | -- |
| View transaction history | Yes | Yes | Yes |
| Fayda ID verification | Required | Required | Required |
| Audio notes | Yes | Yes | Yes |
| Offline access | Full | Full | Full |

**Note**: All roles require Fayda ID verification for financial transactions. Basic browsing is available without verification.

---

## Technical Features

### For Developers

See **[Developer Documentation](../developer/)** for:
- API endpoints and contracts
- Data models and Isar schemas
- Sync protocols and conflict resolution
- Telebirr escrow integration
- Fayda ID authentication flow
- Testing guidelines

---

## Accessibility Features

### Current
- Large text support
- High contrast mode
- Voice-to-text input for low-literacy users
- Audio notes as alternative to text
- Simple, clear UI optimized for low-end devices
- Screen reader support (basic)

### Planned
- Full voice-only navigation mode
- Audio playback of listing details
- Simplified mode with icon-driven interface
- Haptic feedback for key actions

---

## Related Documentation

- **[User Guide](./user-guide.md)** - How to use WeredaConnect
- **[Troubleshooting](./troubleshooting.md)** - Common issues and solutions

---

**Last Updated**: 2026-02-09
**Version**: 1.0.0
**Product Owner**: Product Team
