# WeredaConnect - Project Background

## 1. The Idea

**WeredaConnect** - The Rural Trade & Logistics Aggregator

While Ethiopia has high mobile penetration, rural farmers often lose up to **40% of their crops** due to a lack of transport and market access.

**The Problem:** Smallholder farmers rely on "middlemen" who take high commissions, and truck drivers often travel with empty loads after a delivery.

**The App:** A "logistics marketplace" that connects rural farmers directly with transport providers and urban wholesalers.

**Unique Feature: Voice-to-Text in Local Languages** (Amharic, Afaan Oromo, English). Many rural users have limited literacy; allowing them to record a voice note saying, *"I have 50 quintals of Teff in Ada'a ready for pickup,"* and having the app categorize it into a searchable map for truckers would be a game-changer.

**Monetization:** Small transaction fee or a subscription for high-volume traders.

**Supported Languages:** Amharic, Afan Oromo, English (3 languages at launch).

---

## 2. Project Overview

**WeredaConnect** is an offline-first agritech logistics platform designed to streamline the transport, trade, and verification of crops between rural farmers, local cooperatives, and buyers across Ethiopia (2026 context).

The system leverages:
- **Mobile Android devices** (primarily low-end Tecno and Itel phones)
- **Serverless backend** (AWS Lambda + DynamoDB + S3)
- **National ID integration** (Fayda ID for identity verification)
- **Digital payment platforms** (Telebirr escrow)
- **Micro-insurance** (automatic coverage via AICE APIs)

**Primary Goal:** Enable safe, traceable, and efficient crop movement while respecting Ethiopian rural realities, offline connectivity, and low-bandwidth constraints.

---

## 3. Ethiopian Context - Critical Reality Checks

### 3.1 Connectivity
- Rural areas have **unstable 2G/3G and intermittent 4G**
- Power outages are common; devices can die mid-operation
- Offline-first design is **non-negotiable**

### 3.2 Devices
- Farmers and drivers use **budget Android phones** ($40-$60)
- Memory and CPU are limited; heavy UI frameworks will break
- Background tasks can be killed; sync must be resumable

### 3.3 Language and Localization
- Supported languages (launch): **Amharic**, **Afan Oromo**, **English**
- All UI text, audio instructions, and forms must support all three natively
- Voice-to-Text must work in Amharic and Afan Oromo for low-literacy users
- Ethiopic characters (Ge'ez script) must render without glitches

### 3.4 Trust and Compliance
- Fayda ID is **mandatory for all financial transactions** since Jan 2026
- Telebirr acts as the **escrow agent**; funds are released only on verified delivery
- Identity and transaction verification is critical to prevent fraud
- Any system must reflect **local legal and cultural practices**

---

## 4. Core Functional Requirements

### 4.1 Mobile App (Primary User)
- **Offline-first crop listing**: Farmers list crop details without network
- **Voice-to-Text crop listing**: Record voice notes in Amharic/Oromo, auto-categorize into searchable listings with map location for truckers
- **Background Sync**: SyncJob queue processes data once network is available
- **Audio Notes**: Voice recordings (.ogg/Opus @ 16-32 kbps) for crop inspection or communication
- **Payment Escrow**: Interact with Telebirr API to lock and release funds
- **Insurance Integration**: Trigger micro-insurance coverage automatically per trip
- **Fayda ID Authentication**: Biometric verification for driver/farmer onboarding

### 4.2 Backend
- **Serverless Functions**: AWS Lambda (.NET 10 Native AOT)
- **DynamoDB Single-Table Design**: Fast, scalable, free-tier-friendly
- **S3 Storage**: Secure hosting of audio files and image evidence
- **Business Rules**:
  - Idempotent escrow release
  - QR delivery token validation
  - Retry logic for offline sync

### 4.3 Admin / Dashboard (Phase 2)
- Web-based dashboard for cooperative admins, local authorities, and QA
- Read-only dashboards: crop flow, deliveries, disputes
- Optional admin override: release escrow, validate trips

---

## 5. Architectural Constraints

1. **Offline-first & Local DB**
   - UI must be responsive without network
   - Writes go first to local DB (Isar)
   - Sync queue must handle retries, conflicts, and partial uploads

2. **Low-End Device Performance**
   - Flutter UI optimized for $50 Androids at 60fps
   - Audio/video compression mandatory
   - Minimize background memory footprint

3. **Serverless Scalability**
   - Lambda cold start <150ms
   - ARM64 architecture for cost efficiency
   - Infrastructure as Code (AWS CDK) for deployment reproducibility

4. **Security**
   - Fayda ID verification for all users
   - Escrow flows must prevent double-spending
   - GPS + photo evidence for delivery validation
   - Micro-insurance triggers automatically on trip start

---

## 6. Key Agents & Their Responsibilities

| Agent | Role |
|-------|------|
| **PM / Domain Agent** | Protect Ethiopian reality, validate specs, prevent Silicon Valley assumptions |
| **Flutter Offline Agent** | Mobile UX, Isar schema, offline-first, background sync, audio compression |
| **Backend / .NET AOT Agent** | Lambda handlers, DynamoDB logic, Telebirr escrow, Fayda authentication stubs |
| **Cloud & Infra Agent** | AWS CDK stacks, IAM roles, CI/CD, monitoring, $0 cost assurance |
| **QA / Threat Modeling Agent** | Fraud detection, offline abuse, double payments, security holes, resilience scenarios |

---

## 7. AI-Friendly Architectural Guidance

- Treat **mobile app** as **primary source of truth** offline
- Treat **backend** as authoritative for **financial and identity operations**
- Sync pattern: Local DB -> SyncJob Queue -> Backend Lambda -> S3 / DynamoDB
- Microservices are **serverless Lambdas**, stateless, idempotent
- Audio and images are **compressed & hashed** before upload
- All flows must account for **low connectivity, low device memory, and offline abuse**

---

## 8. Architectural Goals for AI

- Generate **architecture diagrams** showing:
  - Offline-first mobile + Isar DB
  - SyncJob workflow
  - Lambda backend & S3/DynamoDB interactions
  - Telebirr escrow flow
  - Fayda ID auth flow
  - Optional micro-insurance API integration
- Produce **sequence diagrams** for:
  - Crop listing from farmer -> sync -> buyer -> payment release
  - Trip incident (theft/accident) -> claim -> insurance payout

---

## 9. Non-Functional Requirements (AI Must Respect)

- **Offline-first**: Must function with intermittent 2G/3G connectivity
- **Performance**: Mobile UI >= 60fps on budget Android
- **Security & Compliance**: Must validate identity, escrow, and delivery
- **Resilience**: Must survive app crashes, phone restarts, network interruptions
- **Cost-efficiency**: Serverless infra designed for $0 initial burn using AWS free-tier

---

## 10. References for AI Agents

- Ethiopian National Digital Payment Strategy 2026-2030
- Fayda ID integration documentation (eKYC)
- Telebirr Escrow API spec (2026)
- AICE micro-insurance API
- Rural network & device realities (Oromia, Amhara, SNNPR regions)

---

**Last Updated**: 2026-02-09
**Version**: 1.0.0
**Status**: Canonical product definition
