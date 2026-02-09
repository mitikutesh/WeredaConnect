# 📋 Feature Specifications

## Purpose
This directory contains **detailed specifications** for features, decisions, and architectural changes. It serves as the project's memory for "why we built it this way."

---

## Directory Structure

```
specs/
├── README.md              # This file
├── features/              # Feature specifications
│   └── [feature-name].md
├── decisions/             # Architecture decision records (ADRs)
│   └── [YYYY-MM-DD]-[decision].md
├── migrations/            # Breaking changes and migration guides
│   └── [version]-[change].md
└── templates/             # Templates for new specs
    ├── feature_template.md
    └── adr_template.md
```

---

## Creating a Feature Spec

1. Copy `templates/feature_template.md`
2. Name it descriptively: `offline-report-creation.md`
3. Fill in all sections (see template for details)
4. Link to it from `TASK.md` when implementing

### Feature Spec Sections
- **Problem**: What user problem does this solve?
- **Solution**: How does the feature work?
- **Offline Behavior**: Critical for WeredaConnect
- **Localization**: How is it adapted for Amharic/Oromo?
- **Technical Design**: Architecture, data models, APIs
- **Testing**: How to validate it works
- **Rollout**: Deployment strategy

---

## Creating an Architecture Decision Record (ADR)

1. Copy `templates/adr_template.md`
2. Name it with date: `2026-02-09-use-isar-for-offline-storage.md`
3. Document the decision, context, and consequences
4. Reference it in code comments when relevant

### ADR Sections
- **Status**: Proposed, Accepted, Deprecated, Superseded
- **Context**: What problem are we solving?
- **Decision**: What did we decide?
- **Consequences**: What are the trade-offs?
- **Alternatives Considered**: What else did we evaluate?

---

## Migration Guides

When making breaking changes:
1. Create a migration guide in `migrations/`
2. Document the old behavior, new behavior, and migration steps
3. Include code examples
4. Link from release notes

---

## Best Practices

### Write for Future You
- Assume you'll forget why you made this decision
- Document the context, not just the outcome
- Include links to relevant discussions, issues, or research

### Ethiopian Context
- Always include a section on offline behavior
- Document localization considerations
- Note any cultural adaptations

### Keep It Updated
- Mark specs as "Implemented" when done
- Update ADRs if decisions change (mark old ones as "Superseded")
- Archive outdated specs to `specs/archive/`

---

## Examples

### Good Feature Spec Title
✅ `offline-report-creation.md` - Clear, descriptive

### Bad Feature Spec Title
❌ `feature-1.md` - Not descriptive
❌ `reports.md` - Too vague

### Good ADR Title
✅ `2026-02-09-use-isar-for-offline-storage.md` - Dated, clear

### Bad ADR Title
❌ `database-decision.md` - No date, vague

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0

