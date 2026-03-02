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

## Key Features

- **Voice-to-Text Crop Listings** — Record in Amharic, Afan Oromo, or English; auto-categorized into searchable map
- **Offline-First Logistics** — List crops, find transport, connect with buyers without connectivity; auto-sync when online
- **CBE Bank Escrow** — Funds held via CBE bank transfer, released on verified delivery (GPS + photo + QR)
- **Fayda ID Verification** — Biometric identity verification for all financial transactions
- **Multi-Language** — Amharic (አማርኛ), Afan Oromo (Afaan Oromoo), English
- **Low-End Device Optimized** — Android 8+ (API 26), 2GB RAM, <50MB APK, <100MB memory

## AI-Assisted Development

Start with `.ai/quick-start.md` for constraints, navigation, and patterns. Then read `STATE.md` → `TASK.md` for current context.

## Component Documentation

| Component | README | Testing | Key Constraint |
|-----------|--------|---------|----------------|
| **Mobile** (Flutter) | `mobile/README.md` | `mobile/TESTING.md` | Offline-first, 2GB RAM, BLoC pattern |
| **Server** (.NET 10 AOT) | `server/README.md` | `server/TESTING.md` | No reflection libs — see `server/AOT_COMPLIANCE.md` |
| **Infrastructure** (CDK) | `infra/README.md` | `infra/TESTING.md` | ARM64, on-demand billing, me-south-1 |

## More Documentation

- **Full docs index**: `docs/README.md`
- **Ethiopian context**: `docs/local_reality/README.md`
- **Security & QA**: `qa/README.md`
- **API contracts**: `specs/contracts/`

---

**Developer**: Mitiku Teshome | **Email**: mitikutes@gmail.com
**Last Updated**: 2026-03-02 | **Version**: 0.2.0 (pre-alpha)

