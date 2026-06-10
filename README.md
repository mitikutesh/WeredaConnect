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
├── .claude/                  # 🤖 Claude Code agents, commands, hooks
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

## AI Collaboration Workflow

WeredaConnect uses a **Claude ↔ Codex multi-agent workflow** for development.
Two AI agents work as adversarial peers, with the human as the final authority:

- **Claude** — Lead **Planner** & Lead **Executor** (drafts plans, implements code, responds to reviews).
- **Codex** — Independent **Reviewer** & **Validator** (reviews the plan and the code, re-validates rejected findings).
- **You (the user)** — **Final authority**: resolve disputes, approve, and ship.

The whole choreography is file-based, auditable, and observable in real time via
a tmux **control room**. Full protocol: [`.ai/protocol.md`](.ai/protocol.md) ·
flow: [`.ai/orchestrator/workflow.md`](.ai/orchestrator/workflow.md) ·
states: [`.ai/orchestrator/state-machine.md`](.ai/orchestrator/state-machine.md) ·
diagram: [`docs/ai-plans/workflow-diagram.md`](docs/ai-plans/workflow-diagram.md) ·
live artifacts: [`docs/ai-plans/`](docs/ai-plans/).

### Planning flow

```
Claude drafts plan + self-review  →  Codex reviews (classified findings + verdict)
   →  Claude accepts/rejects each  →  Codex validates rejected ones  →  Claude finalizes
```

### Execution flow

```
Claude implements the FINAL plan (working tree only)  →  orchestrator captures a git diff
```

### Review flow

```
Codex reviews the diff (classified findings + verdict)  →  Claude evaluates + fixes accepted
   →  Codex validates rejected ones  →  READY_FOR_APPROVAL
```

Every finding is classified **Critical / Important / Optional / Invalid** with a
description, reasoning, recommended action, and risk level.

### Dispute flow

If Claude rejects a Critical/Important finding and Codex upholds it, a **dispute**
is opened: the workflow **stops**, a record is written to
[`docs/ai-plans/disputes.md`](docs/ai-plans/disputes.md), and **you decide**.
After recording your decision, resume the run.

### Live control room

```
┌──────────────────────────────┬──────────────────────────────┐
│ Orchestrator (state/phase)   │ Live Status (agent · events) │
├──────────────────────────────┼──────────────────────────────┤
│ Claude Output (full log)     │ Codex Output (full log)      │
└──────────────────────────────┴──────────────────────────────┘
```

A tmux dashboard shows, at all times, which agent is active, what it's doing,
which artifact it produced, and every ownership transfer (with the reason).
Backed by a structured event stream (`.ai/runs/<run>/events.jsonl`), segregated
logs (`status` / `transfers` / `decisions` / `disputes` / `claude` / `codex`),
a live `.ai/state/current-state.json`, and an artifact index
(`.ai/artifacts/index.json`).

### Running it

```bash
# Full pipeline WITH the live control room (recommended)
scripts/ai-dashboard.sh "Add phone-number validation to the login screen"

# Full pipeline without the dashboard
scripts/ai-orchestrator.sh "Add phone-number validation to the login screen"

# Planning only (stop after the plan is approved — great for design review)
scripts/ai-orchestrator.sh --plan-only "Refactor the sync-queue retry policy"

# From a task file (for detailed, multi-line tasks)
scripts/ai-orchestrator.sh --task-file specs/features/F-XX-thing.md

# Resume after resolving a dispute (or to continue a stopped run)
scripts/ai-orchestrator.sh --resume <run_id>

# Observe / inspect
scripts/ai-dashboard.sh                       # monitor the latest run
scripts/ai-orchestrator.sh --list
scripts/ai-orchestrator.sh --status <run_id>
scripts/ai-orchestrator.sh --events <run_id>

# Walk the pipeline without invoking any model (smoke test)
scripts/ai-orchestrator.sh --dry-run "anything"
```

> **The orchestrator never merges, pushes, deploys, or releases.** It stops at
> `READY_FOR_APPROVAL` and hands control back to you. Requires the `claude`
> and `codex` CLIs (plus `git`, `jq`, and `tmux` for the dashboard); override the
> CLIs with `CLAUDE_BIN` / `CODEX_BIN`.

## Component Documentation

| Component | README | Testing | Key Constraint |
|-----------|--------|---------|----------------|
| **Mobile** (Flutter) | `mobile/README.md` | `mobile/TESTING.md` | Offline-first, 2GB RAM, BLoC pattern |
| **Server** (.NET 10 AOT) | `server/README.md` | `server/TESTING.md` | No reflection libs — see `server/AOT_COMPLIANCE.md` |
| **Infrastructure** (CDK) | `infra/README.md` | `infra/TESTING.md` | ARM64, on-demand billing, eu-north-1 (dev) / me-south-1 (staging/prod) |

## More Documentation

- **Full docs index**: `docs/README.md`
- **Ethiopian context**: `docs/local_reality/README.md`
- **Security & QA**: `qa/README.md`
- **API contracts**: `specs/contracts/`

---

**Developer**: Mitiku Teshome | **Email**: mitikutes@gmail.com
**Last Updated**: 2026-03-02 | **Version**: 0.2.0 (pre-alpha)
