# 🚀 Getting Started with WeredaConnect

## Welcome!

You've just created a **production-ready project structure** for AI-assisted development. This guide will help you understand what was created and how to use it.

---

## 📁 What Was Created?

### ✅ 20 Documentation Files
- Root governance: `CLAUDE.md`, `STATE.md`, `TASK.md`, `README.md`
- AI governance: `.ai/agents.md`, `.ai/standards.md`, `.ai/tech_stack.md`
- Development guides: READMEs in every major directory
- Critical references: `server/AOT_COMPLIANCE.md`, `docs/local_reality/README.md`
- Localization: 3 language files (English, Amharic, Afan Oromo)

### ✅ 30+ Directories
- Organized by concern: mobile, server, infra, docs, specs, qa
- Ready for development: core/, features/, shared/ in mobile
- Structured for scale: Functions/, Models/, Services/ in server

### ✅ 1 Automation Script
- `scripts/checkpoint.sh` - Automated session handoff

---

## 🎯 Your First Steps

### 1. **Read the Constitution** (5 minutes)
```bash
cat CLAUDE.md
```
This defines the **core principles** of the project:
- Offline-first is non-negotiable
- Ethiopian context is critical
- Solo developer sustainability matters

### 2. **Understand the Context** (10 minutes)
```bash
cat docs/local_reality/README.md
```
This explains **why** certain decisions were made:
- 3G networks, expensive data
- Low-end Android devices
- Cultural and linguistic considerations

### 3. **Review the Tech Stack** (5 minutes)
```bash
cat .ai/tech_stack.md
```
This lists **exact versions** and **banned libraries**:
- Flutter 3.27.x, .NET 10, Node 20.x
- ❌ Newtonsoft.Json, Entity Framework Core, Moq

### 4. **Check AOT Compliance** (CRITICAL - 10 minutes)
```bash
cat server/AOT_COMPLIANCE.md
```
This prevents **hours of wasted work**:
- Native AOT has strict limitations
- Reflection-based libraries will fail at build time
- Required patterns for JSON, DI, etc.

---

## 🛠️ Initialize Your Projects

### Mobile (Flutter)
```bash
cd mobile
flutter create . --org com.weredaconnect --platforms android
flutter pub get
flutter pub run build_runner build --delete-conflicting-outputs
```

### Server (.NET)
```bash
cd server
dotnet new classlib -n WeredaConnect.Server -o src
dotnet new xunit -n WeredaConnect.Server.Tests -o tests
dotnet new sln
dotnet sln add src/WeredaConnect.Server.csproj
dotnet sln add tests/WeredaConnect.Server.Tests.csproj
```

### Infrastructure (CDK)
```bash
cd infra
npm init -y
npm install aws-cdk-lib constructs
npm install --save-dev typescript @types/node
npx tsc --init
```

---

## 🧠 Working with AI Agents

### Before Each Session
1. **Read** `STATE.md` to understand current context
2. **Check** `TASK.md` for active priorities
3. **Review** `.ai/agents.md` for agent roles

### During Development
1. **Consult** `.ai/standards.md` for coding patterns
2. **Verify** `server/AOT_COMPLIANCE.md` before adding dependencies
3. **Reference** `docs/local_reality/README.md` for Ethiopian context

### Before Ending Session
```bash
./scripts/checkpoint.sh
```
This updates `STATE.md` with:
- Last 5 git commits
- Current task status
- Uncommitted changes
- Resumption prompt

**Note**: Requires at least one git commit to work.

---

## 📝 Your First Commit

```bash
# Initialize git (if not already done)
git init

# Add all files
git add .

# Commit with descriptive message
git commit -m "feat: initial project structure with AI-assisted framework

- Add root governance files (CLAUDE.md, STATE.md, TASK.md)
- Create .ai/ directory with agent roles and standards
- Set up mobile/ structure for Flutter offline-first app
- Set up server/ structure for .NET 10 Native AOT
- Add Ethiopian context documentation
- Create automation script for session handoffs
- Include localization for Amharic, Afan Oromo, English"

# Now checkpoint.sh will work!
./scripts/checkpoint.sh
```

---

## 🎨 Project Philosophy

### The "Magnetic" Framework
This structure is designed to **attract and retain context** for AI agents:

1. **Constitution** (`CLAUDE.md`): Core principles that never change
2. **State** (`STATE.md`): Current context that updates frequently
3. **Tasks** (`TASK.md`): Active work that guides daily focus
4. **Standards** (`.ai/`): Patterns that ensure consistency

### Why This Matters for Solo Developers
- **Context switching**: You'll forget details; the structure remembers
- **AI handoffs**: Token limits hit; `checkpoint.sh` preserves state
- **Decision fatigue**: Standards prevent re-deciding the same things
- **Technical debt**: AOT_COMPLIANCE.md prevents costly mistakes

---

## 🌍 Ethiopian Context Awareness

Every technical decision in this project considers:

### Network Reality
- 3G speeds (1-5 Mbps)
- Intermittent connectivity
- Expensive data (~$0.50-1.00/GB)

### Device Reality
- 2-4GB RAM typical
- Android 8-12
- Degraded batteries

### Cultural Reality
- 3 languages (Amharic, Afan Oromo, English)
- Low digital literacy
- Privacy concerns

**Always validate features against `docs/local_reality/README.md`**

---

## 🔐 Security First

Before implementing any feature:
1. Read `qa/README.md` for security checklist
2. Create a threat model in `qa/threat_models/`
3. Review against OWASP Mobile/API Top 10
4. Test offline scenarios thoroughly

---

## 📚 Key Documents Reference

| Document | Purpose | When to Read |
|----------|---------|--------------|
| `CLAUDE.md` | Constitutional principles | Before starting project |
| `STATE.md` | Current session context | Start of each session |
| `TASK.md` | Active tasks | Daily |
| `.ai/agents.md` | AI agent roles | When invoking AI agents |
| `.ai/standards.md` | Coding patterns | Before writing code |
| `.ai/tech_stack.md` | Technology versions | Before adding dependencies |
| `server/AOT_COMPLIANCE.md` | Native AOT rules | Before ANY .NET dependency |
| `docs/local_reality/README.md` | Ethiopian context | Before designing features |
| `qa/README.md` | Security checklist | Before implementing features |

---

## 🚦 Development Workflow

### 1. Plan
- Update `TASK.md` with new tasks
- Create feature spec in `specs/features/`
- Create threat model in `qa/threat_models/`

### 2. Implement
- Follow `.ai/standards.md` patterns
- Test offline scenarios first
- Localize in all 3 languages

### 3. Test
- Unit tests (>80% coverage)
- Integration tests (offline flows)
- Manual testing (low-end device)

### 4. Review
- Security checklist (`qa/README.md`)
- AOT compliance (`server/AOT_COMPLIANCE.md`)
- Ethiopian context (`docs/local_reality/README.md`)

### 5. Handoff
```bash
./scripts/checkpoint.sh
git add .
git commit -m "feat: [description]"
```

---

## 🎯 Success Metrics

A feature is "done" when:
- ✅ Works offline with graceful sync
- ✅ Tested on 2GB RAM Android device
- ✅ Localized in Amharic, Afan Oromo, English
- ✅ Documented in `specs/` with context
- ✅ Passes security threat model review
- ✅ AOT build succeeds (for server changes)
- ✅ Data usage <1MB per typical session

---

## 🆘 Common Questions

### Q: Why so much documentation?
**A**: Solo developers forget context. This structure is your external memory.

### Q: Why the .ai/ directory?
**A**: AI agents need consistent guidelines. This prevents re-explaining the same constraints.

### Q: Why AOT_COMPLIANCE.md?
**A**: Native AOT fails at build time if you use reflection. This prevents wasting hours.

### Q: Why local_reality/?
**A**: Ethiopian constraints are non-obvious. This prevents building for US/Europe by accident.

### Q: Can I change the structure?
**A**: Yes! Update `.ai/standards.md` when patterns emerge. The structure serves you, not vice versa.

---

## 🎉 You're Ready!

You now have:
- ✅ A production-ready project structure
- ✅ AI-assisted development framework
- ✅ Ethiopian context awareness
- ✅ Security-first mindset
- ✅ Automation for session handoffs

**Next steps:**
1. Initialize your projects (Flutter, .NET, CDK)
2. Make your first commit
3. Start building features!

---

**Created**: 2026-02-09  
**For**: Solo developers building offline-first civic tech for Ethiopia  
**With**: AI-assisted development in mind

