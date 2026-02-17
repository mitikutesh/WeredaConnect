# Technical Architecture Deep Dive: Ethiopian Farming Logistics App

## Executive Summary

This document provides a comprehensive technical architecture for a mobile application connecting Ethiopian smallholder farmers, transport providers, and urban wholesalers. The architecture prioritizes **offline-first operation**, **voice-first interaction**, and **extreme resource optimization** for low-end devices on intermittent 3G networks.

**Core Stack**: Flutter (mobile) + AWS Lambda with .NET 10 Native AOT (backend) + DynamoDB (primary datastore) + SQLite/Drift (local-first database)

---

## 1. Flutter Frontend Architecture

### 1.1 Why Flutter

Flutter is the ideal choice for this use case due to several compelling factors:

| Requirement | Flutter Advantage |
|---|---|
| **Offline-first** | Mature SQLite/Drift ecosystem with typed queries and background sync |
| **Low-end devices** | Skia/Impeller rendering engine runs smoothly on 1-2GB RAM devices |
| **Small APK** | Base Flutter APK ~6MB; achievable under 10MB with optimization |
| **Cross-platform** | Single codebase for Android (primary) and iOS (future) |
| **Custom UI** | Full widget control for voice-first, low-literacy interfaces |
| **3G optimization** | Built-in HTTP client with retry/timeout, efficient binary protocols |

**Alternatives considered and rejected:**
- **React Native**: Larger APK size (~15-20MB), worse offline story, JavaScript bridge overhead on low-end devices
- **Native Android (Kotlin)**: Best performance but no cross-platform path, slower development velocity
- **KMP (Kotlin Multiplatform)**: Still maturing for UI, smaller ecosystem for African market-specific packages

### 1.2 Offline-First Architecture with Drift + SQLite

The app treats the **local SQLite database as the single source of truth**. Network connectivity is an enhancement, not a requirement.

```
┌─────────────────────────────────────────────────────┐
│                    FLUTTER APP                       │
│                                                      │
│  ┌──────────┐    ┌──────────────┐    ┌────────────┐ │
│  │    UI     │───▶│  Riverpod    │───▶│   Drift    │ │
│  │  Layer    │    │  Providers   │    │  Database   │ │
│  └──────────┘    └──────────────┘    └─────┬──────┘ │
│                                            │        │
│                  ┌──────────────┐    ┌──────┴──────┐ │
│                  │  Sync Engine │◀──▶│   SQLite    │ │
│                  │  (Background)│    │   (Local)   │ │
│                  └──────┬───────┘    └─────────────┘ │
│                         │                            │
└─────────────────────────┼────────────────────────────┘
                          │ (when online)
                    ┌─────▼─────┐
                    │  REST API  │
                    │  (Lambda)  │
                    └───────────┘
```

**Drift database design principles:**
- **Compile-time SQL safety**: All queries are validated at compile time
- **Stream-based reactivity**: UI automatically updates when local data changes
- **Migration support**: Schema versioning for seamless app updates
- **WAL mode**: Write-Ahead Logging for concurrent read/write performance

**Key local tables:**
```
farmers (id, name, phone, location, crops, created_at, synced_at)
listings (id, farmer_id, crop_type, quantity, price, photos, voice_note_path, status, created_at, synced_at)
orders (id, listing_id, buyer_id, quantity, price, payment_status, created_at, synced_at)
transport_requests (id, order_id, pickup, destination, weight, status, created_at, synced_at)
sync_queue (id, table_name, record_id, operation, payload, priority, retry_count, created_at)
price_cache (id, crop_type, region, price, source, fetched_at)
```

### 1.3 Voice-to-Text Integration

Voice is **not optional** — with 40% illiteracy and limited typing ability in Ethiopic script on small screens, voice is the primary input method.

**Architecture: Hybrid On-Device + Cloud**

```
┌──────────────┐     ┌───────────────────┐     ┌──────────────────┐
│  Microphone  │────▶│  On-Device VAD    │────▶│  Cloud STT API   │
│  Input       │     │  (Voice Activity  │     │  (when online)   │
│              │     │   Detection)      │     │                  │
└──────────────┘     └───────────────────┘     └────────┬─────────┘
                                                        │
                     ┌───────────────────┐              │
                     │  Offline Queue    │◀─────────────┘
                     │  (store audio for │     ┌────────▼─────────┐
                     │   later processing│     │  Text Output     │
                     │   when offline)   │     │  (structured     │
                     └───────────────────┘     │   form fields)   │
                                               └──────────────────┘
```

**STT provider strategy (phased):**

| Phase | Language | Provider | Expected WER | Notes |
|---|---|---|---|---|
| MVP | Amharic | ElevenLabs API | ~3.1% | Best commercial accuracy |
| Phase 2 | Oromo | Addis AI API | ~38% (improving) | Full Oromo support since June 2025 |
| Phase 3 | Tigrinya | Custom fine-tuned Whisper | ~31% | Limited commercial options |
| Future | All | On-device Whisper | TBD | For full offline STT |

**Flutter implementation:**
- Use `speech_to_text` package for on-device recognition (limited Ethiopian language support)
- Primary path: Record audio → send to ElevenLabs/Addis AI API → receive text
- Offline fallback: Queue audio recordings in local storage, process when connectivity returns
- Voice Activity Detection (VAD): On-device to minimize unnecessary API calls
- Audio format: Opus codec at 16kHz mono — optimal quality-to-size ratio for 3G

**Voice UX patterns:**
- Large microphone button as primary CTA on key screens
- Visual waveform feedback during recording
- Confirmation via Text-to-Speech playback (Amharic TTS available via Google/Narakeet)
- Structured voice commands: "Sell 5 quintals of teff at 8000 birr"

### 1.4 State Management: Riverpod 3.0

**Recommendation: Riverpod 3.0** over BLoC for this project.

**Rationale:**
- **Less boilerplate**: Critical for a small team moving fast on MVP
- **Compile-time safety**: Catches dependency errors before runtime
- **Built-in offline persistence**: First-class support for local state persistence
- **Provider-based architecture**: Natural fit for offline-first where data flows from local DB
- **Code generation**: Reduces manual wiring, fewer bugs

**Architecture pattern:**
```dart
// Repository pattern with Riverpod
@riverpod
class ListingsRepository extends _$ListingsRepository {
  @override
  Future<List<Listing>> build() async {
    // Always read from local DB first
    final db = ref.read(driftDatabaseProvider);
    return db.listingsDao.getAllListings();
  }

  Future<void> createListing(ListingForm form) async {
    final db = ref.read(driftDatabaseProvider);
    // 1. Write to local DB immediately
    final listing = await db.listingsDao.insert(form);
    // 2. Queue for sync
    await db.syncQueueDao.enqueue('listings', listing.id, 'INSERT');
    // 3. Trigger background sync if online
    ref.read(syncEngineProvider).triggerSync();
  }
}
```

### 1.5 APK Size Optimization (Target: <10MB)

A fresh Flutter app is ~6MB. Our budget allows ~4MB for app logic, assets, and dependencies.

**Optimization strategy:**

| Technique | Savings | Implementation |
|---|---|---|
| **ABI splitting** | ~30-40% | `flutter build apk --split-per-abi` — ship only arm64-v8a and armeabi-v7a |
| **Tree shaking** | ~15-25% | Automatic in release builds; audit unused imports |
| **ProGuard/R8** | ~10-15% | Enable in `android/app/build.gradle`: `minifyEnabled true` |
| **SVG over PNG** | ~50% per icon | Use `flutter_svg` for all icons and illustrations |
| **WebP images** | ~30% over JPEG | Convert all raster images to WebP format |
| **Deferred loading** | Variable | Load voice/map features only when needed |
| **Font subsetting** | ~80% per font | Include only required Ethiopic glyphs |
| **Dependency audit** | Variable | Remove unused packages; prefer lightweight alternatives |

**APK size budget:**
```
Flutter engine:           ~4.0 MB
App code (compiled):      ~1.5 MB
Ethiopic fonts (subset):  ~0.5 MB
Icons & SVG assets:       ~0.3 MB
SQLite/Drift:             ~0.8 MB
Other dependencies:       ~1.5 MB
──────────────────────────────────
Estimated total:          ~8.6 MB ✓ (under 10MB target)
```

**Critical rules:**
- NO embedded ML models in APK (use cloud APIs for voice)
- NO Google Maps SDK (use OpenStreetMap/Leaflet — saves ~5MB)
- Lazy-load all images from network/local cache
- Use `--analyze-size` flag to monitor size in CI

### 1.6 Low-End Device Optimization

**Target devices:** Samsung Galaxy A03, Tecno Spark, Itel — typically 1-2GB RAM, quad-core, Android 7.1+ (API 25+)

**Performance targets:**
- App startup: <2 seconds cold start
- Memory usage: <80MB active, <30MB background
- Scrolling: 60fps on target devices
- Database queries: <50ms for common operations

**Key strategies:**
1. **Memory management**: `const` constructors everywhere; dispose all controllers/streams; use `ListView.builder` (never `ListView` with children) for all lists
2. **Image handling**: Resize to max 800px before display; cache with size limits; use `ResizeImage` provider; lazy-load below the fold
3. **Render optimization**: Minimize widget rebuilds with `const` and selective `watch()`; avoid opacity animations on low-end (use visibility toggles); limit simultaneous animations to 1-2
4. **Background work**: Use `Isolate` for JSON parsing and data processing; sync operations run in background isolate; audio compression done off main thread
5. **Testing**: Profile on Pixel 3a AVD (2GB RAM) as baseline; test on actual Tecno/Itel devices before release

### 1.7 3G Network Optimization

**Assumptions:** 200-500 Kbps typical, 1000ms+ latency, frequent drops

| Technique | Implementation |
|---|---|
| **Delta sync** | Only sync changed records since last sync timestamp; reduces bandwidth by 80%+ |
| **Image compression** | Client-side compression to WebP <100KB before upload; progressive loading |
| **Request batching** | Batch multiple API calls into single request; sync all pending changes in one call |
| **Response compression** | gzip/brotli on all API responses; binary protocols for data-heavy endpoints |
| **Retry with backoff** | Exponential backoff with jitter; max 3 retries; queue failed requests |
| **Priority queuing** | Payment confirmations sync first, then orders, then listings, then analytics |
| **Connectivity-aware UI** | Show sync status badge; queue operations silently; never block UI on network |

### 1.8 Multi-Language Support

**Supported languages (launch):** Amharic (am), Oromo (om), Tigrinya (ti)

**Implementation:**
- Flutter `intl` package with ARB files for all UI strings
- Right-to-left support not needed (Ethiopic scripts are LTR)
- Language detection: Set during onboarding; changeable in settings
- Ethiopic font: Use Noto Sans Ethiopic (subset to required glyphs, ~200KB)
- Numbers: Support both Ethiopic numerals (፩፪፫) and Arabic numerals (123) — user preference
- Voice prompts: TTS in selected language for confirmations and navigation cues

---

## 2. AWS Lambda + .NET 10 Native AOT Backend

### 2.1 Why .NET 10 Native AOT on Lambda

| Metric | JIT (.NET 10) | Native AOT (.NET 10) | Improvement |
|---|---|---|---|
| **Cold start** | ~800ms | ~90ms | **~86% faster** |
| **Memory footprint** | ~120MB | ~40MB | **~66% less** |
| **Binary size** | N/A (runtime) | ~15-25MB | Self-contained |
| **Cost** | Higher (longer billed ms) | Lower (shorter billed ms) | **~88% cheaper cold starts** |

**Key advantages for this project:**
- **Cost-critical**: Startup budget; every ms of Lambda billing matters
- **User experience**: 90ms cold starts mean snappy API responses even on first hit
- **Scaling**: Handles burst traffic (market days) without provisioned concurrency costs
- **Ecosystem**: Mature .NET AWS SDK, strong DynamoDB support, excellent JSON source generators

**AOT-specific considerations:**
- Use `System.Text.Json` with source generators (no reflection)
- Avoid libraries that rely on runtime code generation
- Test with `PublishAot` in CI pipeline
- Monitor binary size — trim unused assemblies

### 2.2 API Architecture

**Recommendation: Minimal API pattern** (not controllers) — perfect fit for Lambda + AOT.

```
┌──────────────────────────────────────────────────────────────┐
│                    API GATEWAY (REST)                         │
│                                                              │
│  /api/v1/auth/*          → Auth Lambda                       │
│  /api/v1/listings/*      → Listings Lambda                   │
│  /api/v1/orders/*        → Orders Lambda                     │
│  /api/v1/transport/*     → Transport Lambda                  │
│  /api/v1/payments/*      → Payments Lambda                   │
│  /api/v1/sync            → Sync Lambda (bulk operations)     │
│  /api/v1/prices/*        → Market Prices Lambda              │
│  /api/v1/voice/*         → Voice Processing Lambda           │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

**Function-per-domain pattern** (not function-per-route):
- Each domain (listings, orders, etc.) is a single Lambda with internal routing
- Reduces cold start variety (fewer distinct functions to warm)
- Shared domain logic within each function
- Separate Lambda for sync endpoint (handles bulk operations, longer timeout)

**Key API design principles:**
- **Versioned API**: `/api/v1/` prefix for all routes
- **Batch sync endpoint**: `POST /api/v1/sync` accepts array of operations, returns array of results
- **Idempotency keys**: All write operations require client-generated idempotency key (UUID)
- **ETag caching**: Use ETags for GET responses; client sends `If-None-Match` header
- **Compression**: All responses gzip compressed; accept brotli
- **Pagination**: Cursor-based pagination (not offset) for efficient DynamoDB queries

### 2.3 Data Layer: DynamoDB

**Recommendation: DynamoDB** over Aurora Serverless for primary datastore.

**Rationale:**
| Factor | DynamoDB | Aurora Serverless v2 |
|---|---|---|
| **Minimum cost** | $0 (free tier: 25GB, 25 WCU, 25 RCU) | ~$43/month minimum |
| **Scaling** | Instant, per-request | ACU-based, slight lag |
| **Lambda integration** | Native, zero-config | VPC required, cold start impact |
| **Offline sync** | Natural fit (key-value operations) | Requires more complex sync logic |
| **Operational overhead** | Zero (fully managed) | Near-zero but more config |
| **Data model fit** | Excellent for this use case | Over-engineered for MVP |

**DynamoDB table design (single-table pattern):**

```
Table: FarmingApp
──────────────────────────────────────────────────────
PK                    SK                    Data
──────────────────────────────────────────────────────
USER#<phone>          PROFILE               {name, location, role, lang...}
USER#<phone>          LISTING#<id>           {crop, qty, price, status...}
USER#<phone>          ORDER#<id>             {listing_id, buyer, amount...}
LISTING#<id>          METADATA               {farmer_id, crop, region...}
LISTING#<id>          BID#<id>               {buyer_id, amount, status...}
REGION#<code>         PRICE#<crop>#<date>    {avg_price, volume...}
TRANSPORT#<id>        METADATA               {order_id, driver, status...}
SYNC#<user_phone>     CHANGE#<timestamp>     {operations[], ack_id...}
──────────────────────────────────────────────────────

GSI-1 (Inverted Index): SK → PK  (for querying by entity type)
GSI-2 (Regional):      region → crop_type#price  (for market prices)
GSI-3 (Status):        status → created_at  (for active listings/orders)
```

**DynamoDB Streams** for:
- Triggering SMS notifications on order status changes
- Updating market price aggregations
- Feeding analytics pipeline

**Capacity planning (Year 1, 50K-100K users):**
- On-demand capacity mode (auto-scaling, no provisioning needed)
- Estimated: ~5M reads/month, ~1M writes/month
- Cost: ~$5-15/month (well within free tier initially)

### 2.4 Authentication: AWS Cognito with Phone OTP

**Architecture: Passwordless phone-number authentication**

```
┌──────────┐    ┌───────────┐    ┌──────────┐    ┌─────────────┐
│  Flutter  │───▶│  Cognito  │───▶│  Lambda  │───▶│  SNS / SMS  │
│  App      │    │  User Pool│    │  Trigger │    │  Gateway    │
│           │◀───│           │◀───│  (OTP)   │    │             │
└──────────┘    └───────────┘    └──────────┘    └─────────────┘
```

**Flow:**
1. User enters phone number (+251...)
2. Cognito triggers "Create Auth Challenge" Lambda
3. Lambda generates 6-digit OTP, sends via AfroMessage SMS gateway (cheaper than SNS for Ethiopia)
4. User enters OTP
5. Cognito triggers "Verify Auth Challenge" Lambda
6. On success, Cognito issues JWT tokens (access + refresh)

**Why Cognito + custom SMS (not SNS directly):**
- SNS SMS delivery to Ethiopia is unreliable and expensive (~$0.04/SMS)
- AfroMessage has direct Ethio Telecom SMPP integration (~$0.005/SMS)
- Custom Lambda trigger allows using local SMS provider
- Cognito still manages tokens, sessions, and user pool

**Token management:**
- Access token: 1 hour expiry
- Refresh token: 30 days
- Offline: Cache last valid token locally; re-authenticate on next sync

### 2.5 Payment Integration

**Primary: Telebirr** (51.5M users) | **Secondary: M-Birr/CBE Birr** via WeBirr gateway

```
┌──────────┐    ┌──────────────┐    ┌──────────────┐    ┌───────────┐
│  Flutter  │───▶│  Payment     │───▶│  WeBirr API  │───▶│  Telebirr │
│  App      │    │  Lambda      │    │  Gateway     │    │  M-Birr   │
│           │◀───│              │◀───│              │◀───│  CBE Birr │
└──────────┘    └──────────────┘    └──────────────┘    └───────────┘
```

**Integration strategy:**
- **WeBirr as unified gateway**: Single integration covers Telebirr, CBE Birr, Awash Birr, and major banks
- **WeBirr provides Dart SDK** — direct Flutter integration possible
- **Webhook-based confirmation**: Payment Lambda receives webhook from WeBirr on completion
- **Escrow pattern**: Funds held until delivery confirmed (reduces fraud risk)
- **Offline payment queuing**: Create payment intent locally; process when online
- **Fallback**: USSD-based payment for users without smartphone (Telebirr *127# menu)

**Transaction flow:**
1. Buyer confirms order in app → Payment intent created locally
2. On sync → Payment Lambda creates WeBirr payment request
3. Buyer receives Telebirr push notification / USSD prompt
4. Buyer approves payment on Telebirr
5. WeBirr webhook → Payment Lambda → Update order status → DynamoDB Stream → SMS notification to seller

### 2.6 SMS/USSD Gateway Integration

**SMS Provider: AfroMessage** (primary) + Negarit (backup)

**AfroMessage advantages:**
- Direct SMPP integration with Ethio Telecom
- API-first platform built for developers
- Supports both MT (Mobile Terminated — outbound) and MO (Mobile Originated — inbound)
- Competitive pricing for bulk SMS
- Dashboard + API endpoints for system integrators

**SMS use cases:**
| Type | Trigger | Content |
|---|---|---|
| OTP authentication | Login attempt | "Your code: 123456" |
| Price alerts | Daily/weekly | "Teff price in Addis: 8,200 Birr/quintal" |
| Order notifications | Order status change | "New order: 10q teff from Buyer X" |
| Payment confirmation | Payment complete | "Payment of 82,000 Birr received" |
| Transport updates | Status change | "Your shipment picked up, ETA 3hrs" |

**USSD fallback architecture:**
- Partner with Ethio Telecom for shortcode (e.g., *847#)
- USSD application hosted on Ethio Telecom infrastructure
- Menu-driven interface: Check prices → List produce → View orders → Check payments
- Bridges gap for 85% of users who don't have smartphones

### 2.7 Voice-to-Text Service Architecture

**Backend voice processing pipeline:**

```
┌──────────┐    ┌──────────────┐    ┌──────────────┐    ┌───────────┐
│  Audio   │───▶│  S3 Upload   │───▶│  Voice       │───▶│  STT API  │
│  (Opus)  │    │  (presigned  │    │  Processing  │    │  Provider │
│  ~50KB   │    │   URL)       │    │  Lambda      │    │           │
└──────────┘    └──────────────┘    └──────────────┘    └─────┬─────┘
                                                              │
                                    ┌──────────────┐    ┌─────▼─────┐
                                    │  NLU Lambda  │◀───│  Raw Text │
                                    │  (extract    │    │           │
                                    │   intent +   │    └───────────┘
                                    │   entities)  │
                                    └──────┬───────┘
                                           │
                                    ┌──────▼───────┐
                                    │  Structured  │
                                    │  Data        │
                                    │  {crop, qty, │
                                    │   price}     │
                                    └──────────────┘
```

**Provider selection:**
- **ElevenLabs**: Best Amharic accuracy (3.1% WER), $0.10/minute, 99 languages
- **Addis AI**: Best for Oromo, local company, potential partnership opportunity
- **Google Cloud STT**: Wide language support but poor Amharic quality (>60% WER) — avoid for primary
- **Future**: Fine-tuned Whisper model for cost reduction and offline capability

**NLU (Natural Language Understanding) layer:**
- Simple intent extraction Lambda using pattern matching + Claude API for complex cases
- Extracts: crop type, quantity, unit, price, location from voice input
- Confirmation step: "You said: Sell 5 quintals of teff at 8000 Birr. Correct?"

### 2.8 File/Image Storage

```
┌──────────┐    ┌──────────────┐    ┌─────────────┐    ┌────────────┐
│  Flutter  │───▶│  Presigned   │───▶│     S3      │───▶│ CloudFront │
│  (upload) │    │  URL Lambda  │    │   Bucket    │    │   CDN      │
└──────────┘    └──────────────┘    └─────────────┘    └────────────┘
```

- **Client-side compression**: Images compressed to WebP <100KB before upload
- **Presigned URLs**: Direct S3 upload from client; no Lambda proxy needed (saves cost + bandwidth)
- **S3 lifecycle rules**: Move to Glacier after 90 days; delete after 1 year
- **CloudFront**: Edge caching for frequently accessed images (produce photos, profile pics)
- **Image variants**: Lambda@Edge generates thumbnails on first request

---

## 3. Offline-First Sync Architecture

### 3.1 Core Design Principles

1. **Local-first**: All reads come from local SQLite; never block UI on network
2. **Eventually consistent**: Data syncs when connectivity is available
3. **Conflict-aware**: Detect and resolve conflicts deterministically
4. **Bandwidth-efficient**: Delta sync + compression minimizes data transfer
5. **Priority-based**: Critical data (payments, orders) syncs before non-critical (analytics, profiles)

### 3.2 Sync Engine Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     SYNC ENGINE (Background Isolate)             │
│                                                                  │
│  ┌───────────────┐    ┌────────────────┐    ┌───────────────┐   │
│  │ Change        │───▶│  Conflict      │───▶│  Network      │   │
│  │ Tracker       │    │  Resolver      │    │  Manager      │   │
│  │ (monitors     │    │  (LWW + merge  │    │  (retry,      │   │
│  │  local DB     │    │   rules)       │    │   batch,      │   │
│  │  changes)     │    │                │    │   compress)   │   │
│  └───────────────┘    └────────────────┘    └───────┬───────┘   │
│                                                      │          │
│  ┌───────────────┐    ┌────────────────┐    ┌───────▼───────┐   │
│  │ Priority      │───▶│  Queue         │───▶│  HTTP Client  │   │
│  │ Classifier    │    │  Manager       │    │  (with retry) │   │
│  │ (P0-P3)       │    │  (FIFO per     │    │               │   │
│  │               │    │   priority)    │    │               │   │
│  └───────────────┘    └────────────────┘    └───────────────┘   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 3.3 Conflict Resolution Strategy

**Approach: Last-Write-Wins (LWW) with domain-specific merge rules**

CRDTs are powerful but add complexity inappropriate for MVP. LWW with smart merge rules covers 95%+ of real-world conflicts in this domain.

**Resolution rules by entity:**

| Entity | Conflict Rule | Rationale |
|---|---|---|
| **Listings** | LWW (farmer always wins) | Farmer owns their listing data |
| **Orders** | State machine (no backward transitions) | Orders can only progress forward |
| **Payments** | Server authority | Payment state is authoritative from payment gateway |
| **Prices** | Server authority | Market prices come from server aggregation |
| **Profiles** | LWW with field-level merge | Merge non-conflicting field changes |
| **Transport** | State machine | Status progresses: requested→accepted→picked_up→delivered |

**Conflict detection:**
- Each record has a `version` (incrementing integer) and `updated_at` timestamp
- Client sends `version` with every sync push
- Server compares versions; if server version > client version → conflict
- Conflict resolved per entity rules above
- Client receives resolved state in sync response

### 3.4 Delta Sync Protocol

**Sync request format:**
```json
{
  "device_id": "uuid",
  "last_sync_timestamp": "2026-02-17T10:00:00Z",
  "push": [
    {
      "table": "listings",
      "id": "uuid",
      "operation": "INSERT",
      "version": 1,
      "data": { "crop": "teff", "qty": 5, "price": 8000 },
      "idempotency_key": "uuid"
    }
  ],
  "pull_tables": ["listings", "orders", "prices"]
}
```

**Sync response format:**
```json
{
  "server_timestamp": "2026-02-17T10:00:05Z",
  "push_results": [
    { "id": "uuid", "status": "accepted", "server_version": 1 }
  ],
  "pull": {
    "listings": [
      { "id": "uuid", "data": {...}, "version": 3, "operation": "UPDATE" }
    ],
    "orders": [],
    "prices": [
      { "crop": "teff", "region": "addis", "price": 8200, "date": "2026-02-17" }
    ]
  },
  "has_more": false
}
```

**Bandwidth optimization:**
- Only changed fields in UPDATE operations (not full records)
- gzip compression on request/response bodies
- Binary encoding option for extreme bandwidth constraints (MessagePack)
- Pagination: Max 100 records per sync response; `has_more` flag for continuation

### 3.5 Queue-Based Transaction Handling

**Priority levels:**

| Priority | Data Type | Sync Frequency | Max Queue Time |
|---|---|---|---|
| **P0 (Critical)** | Payment confirmations, OTP requests | Immediate (real-time) | 0 — must be online |
| **P1 (High)** | Orders, transport requests | Next sync cycle | 15 minutes |
| **P2 (Normal)** | Listings, profile updates, bids | Background sync | 1 hour |
| **P3 (Low)** | Analytics, price cache updates, logs | Opportunistic | 24 hours |

**Queue processing:**
- WorkManager (Android) triggers sync every 15 minutes when connected
- Immediate sync triggered on: order creation, payment, connectivity restored
- Queue persisted in SQLite `sync_queue` table (survives app restart/crash)
- Failed items retried with exponential backoff: 1min → 5min → 15min → 1hr → manual

---

## 4. Infrastructure & DevOps

### 4.1 AWS Architecture

```
┌────────────────────────────────────────────────────────────────────────┐
│                           AWS CLOUD                                    │
│                                                                        │
│  ┌──────────┐    ┌──────────────┐    ┌──────────────────────────────┐  │
│  │ Route 53 │───▶│ CloudFront   │───▶│ API Gateway (REST)           │  │
│  │ (DNS)    │    │ (CDN + Edge) │    │                              │  │
│  └──────────┘    └──────────────┘    │  /auth    → Auth Lambda      │  │
│                                      │  /sync    → Sync Lambda      │  │
│                                      │  /listings→ Listings Lambda  │  │
│                                      │  /orders  → Orders Lambda    │  │
│                                      │  /payments→ Payments Lambda  │  │
│                                      │  /voice   → Voice Lambda     │  │
│                                      └──────────────┬───────────────┘  │
│                                                      │                 │
│  ┌─────────────────────────────────────────────────────┐               │
│  │                    DATA LAYER                        │               │
│  │                                                      │               │
│  │  ┌──────────┐  ┌──────────┐  ┌──────────────────┐  │               │
│  │  │ DynamoDB │  │    S3    │  │ Cognito User Pool│  │               │
│  │  │ (main   │  │ (images, │  │ (authentication) │  │               │
│  │  │  data)  │  │  audio)  │  │                  │  │               │
│  │  └────┬─────┘  └──────────┘  └──────────────────┘  │               │
│  │       │                                             │               │
│  │  ┌────▼─────┐  ┌──────────┐  ┌──────────────────┐  │               │
│  │  │ DynamoDB │  │   SQS    │  │  EventBridge     │  │               │
│  │  │ Streams  │  │ (async   │  │  (scheduled      │  │               │
│  │  │ (events) │  │  tasks)  │  │   tasks)         │  │               │
│  │  └──────────┘  └──────────┘  └──────────────────┘  │               │
│  │                                                      │               │
│  └──────────────────────────────────────────────────────┘               │
│                                                                        │
│  ┌─────────────────────────────────────────────────────┐               │
│  │               ASYNC PROCESSING                       │               │
│  │                                                      │               │
│  │  ┌──────────────┐  ┌──────────────┐                 │               │
│  │  │ SMS Lambda   │  │ Price Agg    │                 │               │
│  │  │ (AfroMessage)│  │ Lambda       │                 │               │
│  │  └──────────────┘  │ (scheduled)  │                 │               │
│  │                     └──────────────┘                 │               │
│  └──────────────────────────────────────────────────────┘               │
│                                                                        │
│  ┌─────────────────────────────────────────────────────┐               │
│  │               MONITORING                             │               │
│  │  CloudWatch Logs + Metrics | X-Ray Tracing          │               │
│  └─────────────────────────────────────────────────────┘               │
│                                                                        │
└────────────────────────────────────────────────────────────────────────┘
```

### 4.2 CI/CD Pipeline

```
┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐
│  GitHub   │───▶│  Build   │───▶│  Test    │───▶│  Deploy  │───▶│  Verify  │
│  Push     │    │          │    │          │    │          │    │          │
└──────────┘    └──────────┘    └──────────┘    └──────────┘    └──────────┘
```

**GitHub Actions workflows:**

**Backend (.NET 10 AOT):**
```yaml
# .github/workflows/backend.yml
- Trigger: push to main, PRs
- Build: dotnet publish -c Release -r linux-arm64 (AOT)
- Test: dotnet test (unit + integration with DynamoDB Local)
- Package: ZIP for Lambda deployment
- Deploy: AWS SAM deploy (dev → staging → prod)
- Verify: Smoke tests against deployed API
```

**Frontend (Flutter):**
```yaml
# .github/workflows/mobile.yml
- Trigger: push to main, PRs
- Build: flutter build apk --split-per-abi --release
- Test: flutter test (unit + widget tests)
- Size check: Fail if APK > 10MB
- Deploy (dev): Firebase App Distribution
- Deploy (prod): Google Play Internal Track → Staged rollout
```

**Environment strategy:**
| Environment | Purpose | AWS Account | Data |
|---|---|---|---|
| **dev** | Daily development | Shared dev account | Synthetic data |
| **staging** | Pre-release validation | Separate account | Anonymized prod data |
| **prod** | Production | Isolated account | Real data |

### 4.3 Monitoring & Observability

**CloudWatch:**
- Custom metrics: Sync success/failure rate, API latency p50/p95/p99, payment completion rate
- Alarms: Error rate >5%, latency p99 >3s, sync queue depth >10K
- Dashboards: Real-time operational health, user activity, sync health

**X-Ray tracing:**
- End-to-end trace: API Gateway → Lambda → DynamoDB → External APIs
- Trace sampling: 5% in production (cost optimization)
- Identify slow sync operations and payment bottlenecks

**Client-side telemetry:**
- Crash reporting: Firebase Crashlytics (free, Flutter-native)
- Analytics: Lightweight custom events to DynamoDB (no heavy SDK)
- Sync health: Track sync success rate, queue depth, conflict count per device

### 4.4 Cost Estimates (Year 1 — MVP with 50K-100K Users)

**Assumptions:**
- 100K registered users, 30K MAU, 10K DAU
- Average 5 API calls per active user per day
- 500 sync operations per day per active user (background)
- 1,000 SMS per day (OTP + notifications)
- 500 voice-to-text minutes per day
- 10GB image uploads per month

| Service | Monthly Cost | Annual Cost | Notes |
|---|---|---|---|
| **Lambda** (all functions) | $15-30 | $180-360 | ARM64, 256MB, AOT cold starts |
| **API Gateway** | $10-20 | $120-240 | ~5M requests/month |
| **DynamoDB** | $5-25 | $60-300 | On-demand; mostly within free tier initially |
| **S3 + CloudFront** | $5-15 | $60-180 | Images + audio files |
| **Cognito** | $0-25 | $0-300 | First 50K MAU free |
| **CloudWatch** | $10-20 | $120-240 | Logs + metrics + alarms |
| **X-Ray** | $5-10 | $60-120 | 5% sampling rate |
| **SMS (AfroMessage)** | $50-150 | $600-1,800 | ~30K SMS/month at $0.005/SMS |
| **Voice STT (ElevenLabs)** | $100-300 | $1,200-3,600 | ~15K minutes/month at ~$0.01-0.02/min |
| **Route 53** | $1-2 | $12-24 | DNS hosting |
| **Secrets Manager** | $2-5 | $24-60 | API keys + secrets |
| **──────────────** | **──────────** | **──────────** | |
| **TOTAL** | **$200-600** | **$2,400-7,200** | |

**Cost optimization strategies:**
- ARM64 Lambda functions (20% cheaper than x86)
- DynamoDB on-demand mode (no paying for idle capacity)
- S3 Intelligent Tiering for image storage
- Reserved capacity for DynamoDB once usage patterns are clear (Year 2)
- Aggressive CloudWatch log retention (7 days for debug, 30 days for errors)

---

## 5. Security Architecture

### 5.1 Data Encryption

**At rest:**
- DynamoDB: Server-side encryption (SSE) with AWS-managed keys (default, free)
- S3: SSE-S3 encryption for all objects
- SQLite (on device): SQLCipher encryption with user-derived key
- Audio files (on device): Encrypted at filesystem level

**In transit:**
- TLS 1.3 for all API communication (enforced at API Gateway)
- Certificate pinning in Flutter app (prevents MITM attacks)
- Presigned URLs for S3 uploads/downloads (time-limited, single-use)

### 5.2 Phone-Based Authentication Flow

```
┌──────────────────────────────────────────────────────────────┐
│                    AUTH FLOW                                   │
│                                                               │
│  1. User enters phone: +251912345678                         │
│  2. Client → Cognito: InitiateAuth (CUSTOM_AUTH)             │
│  3. Cognito → Lambda: CreateAuthChallenge                    │
│  4. Lambda: Generate 6-digit OTP, store in DynamoDB (TTL 5m) │
│  5. Lambda → AfroMessage: Send OTP via SMS                   │
│  6. User enters OTP in app                                   │
│  7. Client → Cognito: RespondToAuthChallenge                 │
│  8. Cognito → Lambda: VerifyAuthChallenge                    │
│  9. Lambda: Compare OTP, check expiry                        │
│  10. Cognito → Client: JWT tokens (access + refresh + id)    │
│                                                               │
│  Rate limiting: Max 3 OTP requests per phone per hour        │
│  Lockout: 5 failed attempts → 30 minute lockout             │
└──────────────────────────────────────────────────────────────┘
```

### 5.3 API Security

| Layer | Mechanism | Implementation |
|---|---|---|
| **Authentication** | JWT (Cognito) | All API calls require valid Bearer token |
| **Authorization** | Role-based (farmer/buyer/transporter/admin) | Custom Lambda authorizer checks role claims |
| **Rate limiting** | API Gateway throttling | 100 req/sec per user; 1000 req/sec global |
| **Input validation** | Request validation | API Gateway request models + Lambda validation |
| **SQL injection** | N/A | DynamoDB is NoSQL; parameterized queries for any SQL |
| **XSS** | Content-Type headers | API returns JSON only; no HTML rendering |
| **CORS** | API Gateway | Restrict to app domains only |
| **Secrets** | AWS Secrets Manager | All API keys, payment credentials stored securely |
| **WAF** | AWS WAF on API Gateway | Bot protection, geo-blocking, SQL injection rules |

### 5.4 Data Protection Compliance

**Ethiopian context:**
- Ethiopia enacted the Personal Data Protection Proclamation (No. 1321/2024) — modeled after GDPR
- Key requirements: Consent-based data collection, purpose limitation, data minimization, breach notification

**Implementation:**
- Consent: Explicit opt-in during registration (recorded with timestamp)
- Data minimization: Collect only necessary data (name, phone, location, crop data)
- Right to deletion: API endpoint to request account/data deletion
- Data residency: AWS Africa (Cape Town) region as nearest; consider data residency requirements
- Breach notification: Automated alerting via CloudWatch → SNS → admin notification
- Audit trail: All data access logged in CloudWatch; DynamoDB Streams for change tracking

---

## 6. Technical Risks & Mitigations

### 6.1 Internet Shutdowns

**Risk**: Ethiopia has experienced 7+ internet shutdowns since 2019, some lasting weeks.

**Mitigations:**
| Strategy | Implementation |
|---|---|
| **Full offline operation** | App works completely offline; all CRUD operations on local DB |
| **Long sync queues** | Queue can hold weeks of data; process when connectivity returns |
| **SMS fallback** | Critical notifications via SMS (works during internet shutdowns) |
| **USSD fallback** | Basic operations via USSD menu (phone network usually stays up) |
| **Data integrity** | Idempotent sync operations; no data loss from extended offline periods |
| **Local price caching** | Last known prices cached locally; age indicator shown to user |
| **Peer-to-peer** | Future: Bluetooth/WiFi Direct sync between nearby app users |

### 6.2 Low-End Device Performance

**Risk**: Target devices have 1-2GB RAM, slow processors, limited storage.

**Mitigations:**
| Strategy | Implementation |
|---|---|
| **Memory budget** | Strict 80MB active memory limit; profiling in CI |
| **Lazy loading** | Load screens/data on demand; never preload entire datasets |
| **Image limits** | Max 3 photos per listing; compressed to <100KB each |
| **Database limits** | Auto-purge synced data older than 30 days; keep last 500 listings |
| **Background limits** | Sync runs in isolate; minimal background memory footprint |
| **APK size** | Under 10MB; deferred features loaded as needed |
| **Testing** | CI runs on low-end device emulator profile |

### 6.3 Voice-to-Text Accuracy

**Risk**: Ethiopian languages have high WER (31-38%) in research; real-world conditions (noise, dialects) make it worse.

**Mitigations:**
| Strategy | Implementation |
|---|---|
| **Structured input** | Guide users with prompts: "Say the crop name" → "Say the quantity" |
| **Confirmation step** | Always show/speak back the recognized text for confirmation |
| **Noise reduction** | Client-side audio preprocessing before sending to STT |
| **Vocabulary priming** | Send expected vocabulary (crop names, numbers) with STT request |
| **Fallback to typing** | Always allow manual text/number entry as alternative |
| **Feedback loop** | Collect corrections to build training data for future model improvement |
| **Provider diversification** | Easy to swap STT provider; abstracted behind interface |

### 6.4 Telebirr API Reliability

**Risk**: Telebirr is a government monopoly service; API reliability/uptime is not well-documented.

**Mitigations:**
| Strategy | Implementation |
|---|---|
| **WeBirr abstraction** | Use WeBirr gateway instead of direct Telebirr integration |
| **Multi-provider** | WeBirr supports CBE Birr, Awash Birr as fallbacks |
| **Retry logic** | Payment retries with exponential backoff |
| **Manual confirmation** | If webhook fails, allow manual payment confirmation with screenshot |
| **Escrow safety** | Never release goods before payment confirmation |
| **Reconciliation** | Daily batch reconciliation job comparing app records with payment gateway |
| **USSD payments** | Fallback to manual Telebirr USSD transfer with reference code |

### 6.5 Regulatory & Political Risk

**Risk**: Regulatory changes, new licensing requirements, political instability affecting operations.

**Mitigations:**
- Start in stable regions (Addis Ababa, parts of Oromia, SNNPR)
- Maintain government relations; align with Digital Agriculture Roadmap
- Design for multi-country deployment from day 1 (configuration-driven country settings)
- Keep data portable; avoid vendor lock-in where possible
- Legal counsel for Ethiopian fintech regulations (NBE licensing for payment intermediation)

---

## 7. Development Roadmap

### Phase 1: MVP (Months 1-4)

**Sprint 1-2 (Weeks 1-4): Foundation**
- Flutter project setup with Riverpod + Drift
- .NET 10 AOT Lambda project with SAM template
- DynamoDB table design and deployment
- Cognito user pool with phone OTP
- CI/CD pipelines (GitHub Actions)
- Basic sync engine (push/pull without conflict resolution)

**Sprint 3-4 (Weeks 5-8): Core Features**
- Farmer profile creation (with voice input for Amharic)
- Listing creation (crop, quantity, price, photos)
- Buyer/wholesaler directory and search
- Market price display (manual data entry initially)
- Basic SMS notifications (AfroMessage integration)

**Sprint 5-6 (Weeks 9-12): Transactions**
- Order creation and management
- WeBirr/Telebirr payment integration
- Transaction history and tracking
- Enhanced sync with conflict resolution
- Offline-first data queue and retry

**Sprint 7-8 (Weeks 13-16): Polish & Pilot Prep**
- Multi-language UI (Amharic + Oromo)
- Low-end device optimization and testing
- APK size optimization (<10MB)
- Security hardening and penetration testing
- Pilot deployment preparation

### Phase 2: Growth (Months 5-8)
- Transport provider features (matching, tracking)
- Voice-to-text for Oromo (Addis AI integration)
- USSD fallback menu
- Advanced analytics dashboard
- Cooperative-level features

### Phase 3: Scale (Months 9-12)
- On-device voice processing (Whisper fine-tuned)
- Bluetooth peer-to-peer sync
- Credit/loan integration with MFIs
- Multi-region deployment
- East Africa expansion preparation

---

## 8. Architecture Decision Records (ADR)

### ADR-001: DynamoDB over Aurora Serverless
**Decision**: Use DynamoDB as primary datastore
**Rationale**: Zero minimum cost, instant scaling, natural fit for key-value sync patterns, no VPC requirement
**Trade-off**: Limited query flexibility; requires careful access pattern design upfront

### ADR-002: Riverpod over BLoC
**Decision**: Use Riverpod 3.0 for state management
**Rationale**: Less boilerplate, compile-time safety, built-in persistence, better fit for small team velocity
**Trade-off**: Smaller community than BLoC; less enterprise adoption history

### ADR-003: WeBirr over Direct Telebirr API
**Decision**: Integrate payments via WeBirr gateway
**Rationale**: Single integration covers all Ethiopian payment methods, Dart SDK available, reduces regulatory complexity
**Trade-off**: Additional fee layer; dependency on WeBirr availability

### ADR-004: AfroMessage over AWS SNS for SMS
**Decision**: Use AfroMessage for all SMS delivery in Ethiopia
**Rationale**: 8x cheaper than SNS for Ethiopian numbers, direct SMPP integration, better delivery rates
**Trade-off**: Single-country solution; need different provider for East Africa expansion

### ADR-005: ElevenLabs for STT over Google Cloud
**Decision**: Use ElevenLabs as primary Amharic STT provider
**Rationale**: 3.1% WER vs >60% WER for Google Cloud in Amharic; significantly better accuracy
**Trade-off**: Higher cost per minute; dependency on external API

### ADR-006: Native AOT over Managed Runtime
**Decision**: Compile all Lambda functions with .NET 10 Native AOT
**Rationale**: 86% faster cold starts, 88% lower cold start billing, better user experience
**Trade-off**: Longer build times; some libraries may not be AOT-compatible; need source generators for JSON

---

## 9. Key Technology References

- **Flutter offline-first patterns**: [Flutter Official Docs](https://docs.flutter.dev/app-architecture/design-patterns/offline-first), [GeekyAnts Blueprint](https://geekyants.com/blog/offline-first-flutter-implementation-blueprint-for-real-world-apps)
- **Drift + PowerSync**: [Dinko Marinac's Guide](https://dinkomarinac.dev/blog/building-local-first-flutter-apps-with-riverpod-drift-and-powersync/)
- **.NET 10 AOT on Lambda**: [AWS Official Blog](https://aws.amazon.com/blogs/compute/net-10-runtime-now-available-in-aws-lambda/), [Martin Costello's Deep Dive](https://blog.martincostello.com/native-aot-make-dotnet-lambda-go-brr/)
- **Telebirr Integration**: [Zoromia Guide](https://zoromia.com/how-to-integrate-telebirr-payment-in-2025-a-simple-guide/), [WeBirr Gateway](https://www.webirr.com/)
- **AfroMessage SMS**: [Shega Coverage](https://shega.co/news/afromessage-is-rising-as-ethiopias-sms-powerhouse)
- **Addis AI Voice**: [Addis AI](http://addisassistant.com/en/blogs/addis-ai-the-smart-voice-assistant-for-amharic-afan-oromo-how-it-works/)
- **ElevenLabs Amharic STT**: [ElevenLabs](https://elevenlabs.io/speech-to-text/amharic)
- **Ethio Telecom Developer Portal**: [Developer Docs](https://developer.ethiotelecom.et/docs/)
- **Conflict Resolution Patterns**: [DevelopersVoice](https://developersvoice.com/blog/mobile/offline-first-sync-patterns/)

---

*Document compiled: February 17, 2026*
*Research conducted by: Architecture Agent (Claude Opus 4.6)*
*Based on: Ethiopia Farming App Feasibility Report + extensive web research*
