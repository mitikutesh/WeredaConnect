# 🎯 TASK.md - Current Mission

## Purpose
This file tracks the **active work** for the current development cycle. It's the single source of truth for "what am I doing right now?"

---

## 🎯 Current Sprint Goal

**Create Production-Ready Project Structure**

Establish the foundational folder structure and governance files for WeredaConnect, implementing the "Magnetic" AI-assisted development framework.

---

## 📋 Active Tasks

> **Task Claiming:** All tasks use `@Scope` tags per `.agents/task-protocol.md`. Claim a task by adding your scope tag and committing before starting work.

### Phase 1: Foundation (Current)
- [x] `@Architect` Define project structure requirements
- [ ] `@Architect` Create root governance files (CLAUDE.md, STATE.md, TASK.md)
- [ ] `@Architect` Create .ai/ governance directory
- [ ] `@Architect` Create .agents/ directory for agent SOPs
- [ ] `@Architect` Create docs/ structure with local_reality/
- [ ] `@Architect` Create specs/ directory for feature tickets

### Phase 2: Mobile Structure
- [ ] `@Mobile` Create mobile/ directory
- [ ] `@Mobile` Create mobile/lib/core/ (sync engine, Isar)
- [ ] `@Mobile` Create mobile/lib/features/
- [ ] `@Mobile` Create mobile/lib/shared/
- [ ] `@Localization` Create mobile/lib/shared/l10n/ (Amharic/Oromo/English)
- [ ] `@Localization` Add sample .arb files for localization

### Phase 3: Server Structure
- [ ] `@Backend` Create server/ directory
- [ ] `@Backend` Create server/AOT_COMPLIANCE.md
- [ ] `@Backend` Create server/src/ structure
- [ ] `@Backend` Add .NET project scaffolding notes

### Phase 4: Infrastructure & Automation
- [ ] `@Infra` Create infra/ directory for AWS CDK
- [ ] `@Infra` Create scripts/ directory
- [ ] `@Infra` Create scripts/checkpoint.sh automation
- [ ] `@Infra` Make checkpoint.sh executable
- [ ] `@Security` Create qa/ directory for threat models

### Phase 5: Documentation
- [ ] `@Architect` Create .ai/agents.md (role definitions)
- [ ] `@Architect` Create .ai/standards.md (coding patterns)
- [ ] `@Architect` Create .ai/tech_stack.md (framework versions)
- [ ] `@Architect` Create docs/local_reality/README.md (Ethiopian context)

---

## 🚀 Next Immediate Actions

1. Complete root governance files creation
2. Create .ai/ directory with governance documents
3. Build out mobile/ folder structure
4. Create server/ structure with AOT compliance guide
5. Add automation scripts

---

## 🎯 Definition of Done

This task is complete when:
- ✅ All directories exist with proper structure
- ✅ All governance files are created and populated
- ✅ checkpoint.sh script is executable and tested
- ✅ AOT_COMPLIANCE.md has banned libraries documented
- ✅ l10n/ structure is ready for localization files
- ✅ README files exist in key directories explaining their purpose

---

## 🔗 Related Documents

- **Constitution**: `CLAUDE.md` - Core principles and constraints
- **State**: `STATE.md` - Current session context
- **Standards**: `.ai/standards.md` - Coding patterns (to be created)

---

## 📝 Notes

- This is the **initial structure setup** - no code implementation yet
- Focus on creating a framework that supports AI-assisted development
- Prioritize files that prevent common mistakes (AOT_COMPLIANCE.md, l10n/)
- Automation (checkpoint.sh) is critical for solo developer efficiency

---

**Created:** 2026-02-09  
**Last Updated:** 2026-02-09  
**Status:** In Progress

