# System Architecture

## Purpose
This document provides a high-level overview of WeredaConnect's system architecture, explaining the major components and how they work together to deliver an offline-first agritech logistics platform for Ethiopian rural communities.

## Audience
- Architects
- Senior developers
- Technical decision-makers
- New contributors seeking system understanding

## Prerequisites
- Basic understanding of mobile and serverless architectures
- Familiarity with offline-first concepts
- Read [Local Reality](../local_reality/README.md) for context

---

## Architecture Overview

WeredaConnect follows a **3-tier offline-first architecture**:

```
┌─────────────────────────────────────────────────────────────┐
│                     Mobile Client (Flutter)                  │
│  ┌────────────┐  ┌────────────┐  ┌──────────────────────┐  │
│  │ UI Layer   │  │ BLoC State │  │ Local Database       │  │
│  │ (Widgets)  │←→│ Management │←→│ (Isar - NoSQL)       │  │
│  └────────────┘  └────────────┘  └──────────────────────┘  │
│                         ↕                    ↕                │
│                  ┌─────────────────────────────┐             │
│                  │   Sync Engine (Offline-First)│             │
│                  │   - Queue operations         │             │
│                  │   - Conflict resolution      │             │
│                  │   - Retry logic              │             │
│                  └─────────────────────────────┘             │
└─────────────────────────────────────────────────────────────┘
                              ↕ (When online)
                    ┌──────────────────────┐
                    │   API Gateway (AWS)   │
                    └──────────────────────┘
                              ↕
┌─────────────────────────────────────────────────────────────┐
│              Serverless Backend (.NET 10 + AWS)              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│  │ Lambda       │  │ DynamoDB     │  │ S3               │  │
│  │ Functions    │←→│ (NoSQL DB)   │  │ (File Storage)   │  │
│  │ (Native AOT) │  │              │  │                  │  │
│  └──────────────┘  └──────────────┘  └──────────────────┘  │
│                                                               │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│  │ CloudWatch   │  │ CloudFront   │  │ Cognito          │  │
│  │ (Monitoring) │  │ (CDN)        │  │ (Auth)           │  │
│  └──────────────┘  └──────────────┘  └──────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                              ↕
┌─────────────────────────────────────────────────────────────┐
│           Infrastructure as Code (AWS CDK)                   │
│  - TypeScript-based infrastructure definitions               │
│  - Automated deployment pipelines                            │
│  - Environment management (dev, staging, prod)               │
└─────────────────────────────────────────────────────────────┘
```

---

## Core Architectural Principles

### 1. Offline-First (Non-Negotiable)

**Principle**: Every feature MUST work without internet connectivity.

**Implementation**:
- Local database (Isar) as source of truth
- Sync queue for pending operations
- Background sync when connectivity available
- Conflict resolution (server wins, client merges)

**Why**: Ethiopian users face intermittent connectivity and expensive data.

### 2. Serverless & Scalable

**Principle**: Pay only for what you use, scale automatically.

**Implementation**:
- AWS Lambda for compute (ARM64 for cost savings)
- DynamoDB for database (on-demand billing)
- CloudFront for CDN (edge caching)
- No always-on servers

**Why**: Low traffic initially, need to scale affordably.

### 3. Native AOT Compilation

**Principle**: .NET backend uses Native AOT for fast cold starts.

**Implementation**:
- Source generators for JSON serialization
- No reflection-heavy libraries
- Explicit dependency injection
- Trimmed binaries

**Why**: Lambda cold starts must be <2 seconds for acceptable UX on 3G.

### 4. Multi-Language Support

**Principle**: Support Amharic, Afan Oromo, and English natively.

**Implementation**:
- Flutter l10n with .arb files
- Ethiopic script support (Ge'ez/Amharic is LTR, not RTL)
- Server-side language detection
- Localized error messages

**Why**: Linguistic diversity in Ethiopia requires native language support.

---

## Technology Stack

### Mobile (Client)
- **Framework**: Flutter 3.27.x
- **Language**: Dart 3.6.x
- **State Management**: BLoC pattern
- **Local Database**: Isar (NoSQL, fast, offline-first)
- **HTTP Client**: Dio with retry interceptors
- **Localization**: Flutter l10n (intl package)

### Backend (Server)
- **Runtime**: .NET 10 with Native AOT
- **Compute**: AWS Lambda (ARM64 architecture)
- **Database**: DynamoDB (NoSQL, serverless)
- **Storage**: S3 (file uploads, media)
- **CDN**: CloudFront (static assets, API caching)
- **Auth**: AWS Cognito (user authentication)
- **Monitoring**: CloudWatch (logs, metrics, alarms)

### Infrastructure
- **IaC**: AWS CDK (TypeScript)
- **CI/CD**: GitHub Actions
- **Environments**: dev, staging, prod

---

## Key Components

### Mobile Client

#### 1. UI Layer
- **Technology**: Flutter widgets
- **Pattern**: Atomic design (atoms → molecules → organisms)
- **Localization**: AppLocalizations for all text
- **Accessibility**: Screen reader support, high contrast

#### 2. State Management (BLoC)
- **Pattern**: Business Logic Component (BLoC)
- **Events**: User actions trigger events
- **States**: UI reacts to state changes
- **Benefits**: Testable, predictable, reactive

#### 3. Data Layer
- **Repository Pattern**: Abstract data sources
- **Local Data Source**: Isar database
- **Remote Data Source**: HTTP API client
- **Offline-First Logic**: Try local → check connectivity → fetch remote

#### 4. Sync Engine
- **Queue**: Pending operations stored locally
- **Retry Logic**: Exponential backoff
- **Conflict Resolution**: Server version wins, client merges
- **Idempotency**: Safe to retry operations

### Backend Services

#### 1. Lambda Functions
- **Pattern**: One function per API endpoint
- **Runtime**: .NET 10 Native AOT
- **Memory**: 512MB (optimized for cost)
- **Timeout**: 10 seconds
- **Concurrency**: Limited to prevent runaway costs

#### 2. DynamoDB Tables
- **Listings Table**: Crop listings (type, quantity, location, price)
- **Users Table**: User profiles (farmers, truckers, buyers)
- **Trips Table**: Transport trips (pickup, delivery, status)
- **Sync Metadata**: Version tracking for conflict resolution
- **Billing**: On-demand (pay per request)

#### 3. S3 Buckets
- **Media Bucket**: Crop photos, delivery evidence, audio notes
- **Static Assets**: App resources
- **Lifecycle Policies**: Auto-delete old files

---

## Data Flow Patterns

See [Data Flow](./data-flow.md) for detailed data flow diagrams.

**Key Patterns**:
1. **Read**: Local DB → (if not found + online) → Remote API → Cache locally
2. **Write**: Local DB → Sync queue → (when online) → Remote API → Update local
3. **Sync**: Background job → Process queue → Resolve conflicts → Update local

---

## Security Architecture

### Authentication
- **Provider**: AWS Cognito
- **Flow**: OAuth 2.0 / OpenID Connect
- **Tokens**: JWT with refresh tokens
- **Storage**: Secure storage (Keychain/Keystore)

### Authorization
- **Pattern**: Role-based access control (RBAC)
- **Enforcement**: Lambda authorizers
- **Scopes**: User, moderator, admin

### Data Protection
- **In Transit**: TLS 1.3
- **At Rest**: AES-256 encryption (DynamoDB, S3)
- **Sensitive Data**: Minimal collection, encrypted locally

---

## Deployment Architecture

See [Deployment Architecture](./deployment-architecture.md) for detailed deployment diagrams.

**Environments**:
- **dev**: Development environment (auto-deploy from main branch)
- **staging**: Pre-production testing (manual deploy)
- **prod**: Production (manual deploy with approval)

---

## Scalability & Performance

### Mobile Performance
- **Target**: Works on 2GB RAM, Android 8+ devices
- **Optimization**: Lazy loading, image compression, minimal dependencies
- **Offline**: No network calls = fast

### Backend Performance
- **Cold Start**: <2 seconds (Native AOT on ARM64)
- **Warm Response**: <100ms
- **Throughput**: Auto-scales with Lambda
- **Cost**: ~$5-10/month for 1000 users

---

## Related Documentation

- **[Component Relationships](./component-relationships.md)** - How components interact
- **[Data Flow](./data-flow.md)** - Detailed data flow patterns
- **[Deployment Architecture](./deployment-architecture.md)** - AWS infrastructure
- **[Local Reality](../local_reality/README.md)** - Ethiopian context

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Owner**: Architecture Team

