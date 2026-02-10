# 🌍 WeredaConnect

**Offline-first agritech logistics platform for Ethiopian rural communities**

Connecting farmers, transport providers, and urban wholesalers with voice-to-text in local languages, built for 3G networks, low-end Android devices, and intermittent connectivity.

---

## 🎯 Project Vision

WeredaConnect solves the rural logistics crisis where Ethiopian smallholder farmers lose up to **40% of their crops** due to lack of transport and market access. The platform connects farmers directly with truckers and buyers, eliminating expensive middlemen while enabling truck drivers to find loads for empty return trips. Designed for the realities of rural Ethiopia: expensive data, unreliable connectivity, low-literacy users, and diverse languages.

---

## 🏗️ Project Structure

```
WeredaConnect/
├── CLAUDE.md                 # 📜 Constitution (AI agent guidelines)
├── STATE.md                  # ⏸ Save Point (session checkpoint)
├── TASK.md                   # 🎯 Current Mission (active tasks)
├── .ai/                      # 🧠 Governance (agents, standards, tech stack)
├── .agents/                  # 👮 Agent SOPs (workflows, checklists)
├── docs/                     # 📚 Knowledge Repository
│   └── local_reality/        # Ethiopian context & constraints
├── specs/                    # 📋 Feature Specifications & ADRs
├── mobile/                   # 📱 Flutter Android App
│   ├── lib/
│   │   ├── core/             # Sync engine, Isar database
│   │   ├── features/         # Feature modules (clean architecture)
│   │   └── shared/
│   │       └── l10n/         # Amharic, Afan Oromo, English
├── server/                   # ⚡ .NET 10 Serverless Backend
│   ├── src/                  # Lambda functions, services
│   └── AOT_COMPLIANCE.md     # Native AOT rules & banned libs
├── infra/                    # ☁️ AWS CDK Infrastructure
│   └── lib/                  # Stacks, constructs, configs
├── scripts/
│   └── checkpoint.sh         # 🤖 Automated session handoff
└── qa/                       # 🔐 Threat models, security reviews
```

---

## 🚀 Quick Start

### Prerequisites
- **Mobile**: Flutter 3.27.x, Dart 3.6.x
- **Server**: .NET 10 SDK
- **Infrastructure**: Node.js 20.x, AWS CDK CLI
- **Tools**: Git, AWS CLI

### Setup

#### 1. Clone Repository
```bash
git clone https://github.com/yourusername/WeredaConnect.git
cd WeredaConnect
```

#### 2. Mobile Setup
```bash
cd mobile
flutter pub get
flutter pub run build_runner build --delete-conflicting-outputs
flutter run
```

#### 3. Server Setup
```bash
cd server
dotnet restore
dotnet build
dotnet test
```

#### 4. Infrastructure Setup
```bash
cd infra
npm install
cdk synth
cdk deploy --context env=dev
```

---

## 🌟 Key Features

### Voice-to-Text Crop Listings
- Record voice notes in **Amharic**, **Afan Oromo**, or **English**
- Auto-categorize into searchable listings with map location
- Designed for low-literacy rural users
- Example: *"I have 50 quintals of Teff in Ada'a ready for pickup"*

### Offline-First Logistics Marketplace
- List crops, find transport, connect with buyers—all offline
- Local-first data storage with Isar database
- Automatic sync when connection is restored
- Conflict resolution for concurrent edits

### Telebirr Escrow & Fayda ID Integration
- Secure payments via **Telebirr escrow** (funds released on verified delivery)
- **Fayda ID** biometric verification for all users
- **Micro-insurance** automatic coverage per trip (AICE integration)
- QR delivery tokens for proof of delivery

### Multi-Language Support
- **Amharic** (አማርኛ): Official language
- **Afan Oromo** (Afaan Oromoo): Largest ethnic group
- **English**: Urban educated population

### Optimized for Low-End Devices
- Target: Android 8+ (API 26), 2GB RAM, $50 phones
- APK size: <50MB
- Memory usage: <100MB
- Battery efficient: <5% per hour

### Cost-Conscious
- Minimal data usage (<1MB per session)
- Data saver mode
- Compressed payloads (gzip/brotli)
- On-demand AWS billing (low traffic optimized)

---

## 🧠 AI-Assisted Development

This project uses a **"Magnetic" framework** for AI-assisted development:

### Core Documents
- **`CLAUDE.md`**: Constitutional principles, decision framework
- **`STATE.md`**: Session checkpoint for context preservation
- **`TASK.md`**: Current active tasks and priorities

### Before Starting Work
1. Read `CLAUDE.md` for project principles
2. Read `STATE.md` for current context
3. Check `TASK.md` for active priorities
4. Review `.ai/standards.md` for coding patterns

### Before Ending Session
```bash
./scripts/checkpoint.sh
```
This updates `STATE.md` with current progress for seamless handoff.

---

## 📱 Mobile Architecture

### Tech Stack
- **Framework**: Flutter 3.27.x
- **Database**: Isar (offline-first NoSQL)
- **State Management**: BLoC pattern
- **Networking**: Dio with retry logic
- **Localization**: flutter_localizations

### Key Patterns
- **Offline-first repository**: Local DB → Sync queue → Server
- **Clean architecture**: Data, Domain, Presentation layers
- **Sync engine**: Conflict resolution with server

See `mobile/README.md` for details.

---

## ⚡ Server Architecture

### Tech Stack
- **Framework**: .NET 10 with Native AOT
- **Runtime**: AWS Lambda (ARM64)
- **Database**: DynamoDB (on-demand billing)
- **API**: API Gateway (HTTP API)

### Key Patterns
- **Idempotent APIs**: Safe to retry operations
- **Conflict resolution**: Server wins, client merges
- **Event-driven**: DynamoDB Streams for async processing

### ⚠️ Critical
**Before adding dependencies, check `server/AOT_COMPLIANCE.md`**  
Native AOT has strict limitations (no reflection, no EF Core, etc.)

See `server/README.md` for details.

---

## ☁️ Infrastructure

### AWS Services
- **Lambda**: Serverless compute (.NET 10 AOT, ARM64)
- **DynamoDB**: NoSQL database (on-demand)
- **S3 + CloudFront**: Static assets, CDN
- **API Gateway**: HTTP API (cost-optimized)
- **CloudWatch**: Monitoring, logs, alarms

### Cost Optimization
- ARM64 Lambda (20% cheaper)
- On-demand DynamoDB (no wasted capacity)
- HTTP API (70% cheaper than REST API)
- Reserved concurrency limits (prevent runaway costs)

See `infra/README.md` for details.

---

## 🌍 Ethiopian Context

### Infrastructure Constraints
- **Network**: 3G (1-5 Mbps), intermittent connectivity
- **Devices**: 2-4GB RAM, Android 8-12
- **Data costs**: ~$0.50-1.00 per GB (expensive)
- **Power**: Unreliable electricity, degraded batteries

### Cultural Considerations
- **Languages**: Amharic, Afan Oromo, English
- **Literacy**: ~52% nationally, lower in rural areas
- **Trust**: Low for new apps, especially those handling financial transactions
- **Privacy**: Concerns about government surveillance

See `docs/local_reality/README.md` for comprehensive context.

---

## 🔐 Security & Quality

### Security Practices
- End-to-end encryption for sensitive data
- Minimal data collection (privacy by design)
- Regular dependency updates
- Threat modeling for all features

### Testing
- **Unit tests**: >80% coverage target
- **Integration tests**: Critical offline flows
- **Manual testing**: Low-end Android devices
- **Penetration testing**: Before production launch

See `qa/README.md` for security checklists and threat models.

---

## 🤝 Contributing

This is currently a **solo developer project**, but contributions are welcome!

1. Read `CLAUDE.md` for project principles
2. Check `TASK.md` for current priorities
3. Follow `.ai/standards.md` for coding patterns
4. Test offline scenarios thoroughly
5. Validate against Ethiopian context (`docs/local_reality/`)

---

## 📄 License

TBD (To be determined)

---

## 📞 Contact

- **Developer**: Mitiku Teshome
- **Email**: mitikutes@gmail.com
- **Security**: security@weredaconnect.com (to be set up)

---

**Last Updated**: 2026-02-09  
**Version**: 0.1.0 (pre-alpha)  
**Status**: Initial structure setup

