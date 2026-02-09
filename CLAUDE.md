# 📜 CLAUDE.md - Constitution (Root of Trust)

## Purpose
This document serves as the **constitutional foundation** for AI-assisted development on the WeredaConnect project. It defines the core principles, constraints, and decision-making framework that all AI agents must follow.

## Project Identity

**WeredaConnect** is a hyperlocal civic engagement platform designed for Ethiopian communities, with a focus on:
- **Offline-first architecture** (3G/intermittent connectivity)
- **Cultural relevance** (Amharic, Afan Oromo, English)
- **Rural accessibility** (low-end Android devices)
- **Data efficiency** (expensive mobile data costs)

## Core Principles

### 1. **Local Reality First**
- All technical decisions must account for Ethiopian infrastructure constraints
- Optimize for offline functionality and minimal data usage
- Test on low-end devices (2GB RAM, Android 8+)

### 2. **Cultural Authenticity**
- Localization is not just translation—adapt UX for local context
- Use agricultural/market terminology familiar to rural users
- Respect local communication patterns and hierarchies

### 3. **Solo Developer Sustainability**
- Prioritize simplicity over cleverness
- Maintain clear documentation for context switching
- Automate repetitive tasks to preserve cognitive energy

### 4. **Production Readiness**
- Every feature must work offline before going online
- Security and privacy are non-negotiable
- Performance budgets: <3s load time on 3G, <50MB app size

## Technical Constraints

### Mobile (Flutter)
- **Offline-first**: Isar database for local storage
- **Sync engine**: Conflict resolution with server
- **Languages**: Amharic, Afan Oromo, English (l10n atoms)
- **Target**: Android 8+ (API 26+), 2GB RAM minimum

### Server (.NET 10)
- **Native AOT**: Serverless, cold-start optimized
- **Compliance**: See `server/AOT_COMPLIANCE.md` for banned libraries
- **Architecture**: Event-driven, idempotent operations

### Infrastructure (AWS CDK)
- **Cost-conscious**: Optimize for low-traffic scenarios
- **Resilient**: Handle intermittent client connections
- **Observable**: CloudWatch metrics for debugging

## AI Agent Guidelines

### Before Making Changes
1. **Read** `docs/project-background.md` for product definition
2. **Read** `STATE.md` to understand current context
3. **Check** `TASK.md` for active priorities
4. **Verify** `.ai/standards.md` for coding patterns
5. **Consult** `docs/local_reality/` for domain constraints

### When Proposing Solutions
- **Ask** before installing dependencies
- **Explain** trade-offs in Ethiopian context
- **Validate** against offline-first requirements
- **Document** decisions in appropriate spec files

### Git Commit Messages

All commits **must** follow [Conventional Commits](https://www.conventionalcommits.org/).

**Format:**
```
<type>(<scope>): <subject>

[optional body]

[optional footer]
```

**Rules:**
- **Subject line**: imperative mood, lowercase, no period, max 72 characters
- **Types**: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`, `ci`, `perf`, `build`
- **Scopes**: `mobile`, `server`, `infra`, `docs`, `l10n`, `sync`, `escrow`, `auth`
- **Body**: explain *what* and *why* (not how), wrap at 72 characters
- **Footer**: reference issues (`Closes #123`), note breaking changes (`BREAKING CHANGE:`)
- **Breaking changes**: add `!` after type/scope — `feat(server)!: change sync protocol`

**Examples:**
```
feat(mobile): add voice-to-text crop listing in Amharic
fix(sync): resolve conflict when offline queue has duplicate entries
docs(specs): add ADR for Telebirr escrow flow
chore(infra): update Lambda memory to 512MB
refactor(server): extract escrow validation into service layer
test(mobile): add offline sync queue retry tests
ci: add AOT compliance check to PR workflow
```

**Forbidden:**
- Generic messages: `fix bug`, `update code`, `WIP`, `misc changes`
- Missing type/scope: `add new feature`
- Future tense: `will add` (use imperative: `add`)
- Past tense: `added feature` (use imperative: `add feature`)

### Session Handoffs
- **Update** `STATE.md` before token limits
- **Run** `scripts/checkpoint.sh` to automate handoff
- **Commit** work-in-progress with clear messages following the convention above

## Decision Framework

When faced with technical choices, prioritize in this order:

1. **Does it work offline?** (Non-negotiable)
2. **Is it culturally appropriate?** (User trust)
3. **Can it run on low-end devices?** (Accessibility)
4. **Is it maintainable solo?** (Sustainability)
5. **Does it minimize data usage?** (Cost to users)

## Forbidden Patterns

❌ **Never** assume reliable internet connectivity  
❌ **Never** use reflection-heavy libraries in .NET AOT  
❌ **Never** create files without explicit user request  
❌ **Never** commit/deploy without user approval  
❌ **Never** ignore localization requirements  

## Success Metrics

A feature is "done" when:
- ✅ Works offline with graceful sync
- ✅ Tested on 2GB RAM Android device
- ✅ Localized in all three languages
- ✅ Documented in `specs/` with context
- ✅ Passes security threat model review

## Living Document

This constitution evolves with the project. Update it when:
- Core assumptions change (e.g., new target market)
- Technical constraints shift (e.g., .NET version upgrade)
- Lessons learned from production incidents

**Last Updated**: 2026-02-09  
**Version**: 1.0.0

