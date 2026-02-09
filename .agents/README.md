# 👮 Agent SOPs (Standard Operating Procedures)

## Purpose
This directory contains **detailed workflows and checklists** for AI agents working on WeredaConnect. Each SOP ensures consistent, high-quality work.

---

## Directory Structure

```
.agents/
├── README.md              # This file
├── task-protocol.md       # How to claim tasks with @Scope tags
├── branch-protocol.md     # Scoped branching rules
├── feature_workflow.md    # How to implement a new feature
├── bug_fix_workflow.md    # How to fix bugs
├── deployment_checklist.md # Pre-deployment validation
└── session_handoff.md     # How to hand off between sessions
```

---

## Multi-Agent Coordination

Before starting any work, read these two protocols:

### Task Claiming
Read `task-protocol.md` — claim tasks with `@Scope` tags in `TASK.md` before starting work. This prevents duplicate effort across agents and sessions.

### Scoped Branching
Read `branch-protocol.md` — use `<scope>/<description>` branch naming and keep one scope per branch. This prevents cross-module merge conflicts.

Both protocols reference `.ai/ownership.md` for file ownership boundaries.

---

## When to Use SOPs

### Feature Development
1. Read `feature_workflow.md`
2. Follow the checklist step-by-step
3. Update `TASK.md` as you progress
4. Run `scripts/checkpoint.sh` before session ends

### Bug Fixes
1. Read `bug_fix_workflow.md`
2. Reproduce the issue offline first
3. Fix and test thoroughly
4. Document the root cause in commit message

### Deployment
1. Read `deployment_checklist.md`
2. Validate all items before deploying
3. Never deploy without user approval
4. Monitor CloudWatch after deployment

### Session Handoffs
1. Read `session_handoff.md`
2. Run `scripts/checkpoint.sh`
3. Update `STATE.md` with context
4. Commit work-in-progress

---

## Creating New SOPs

When you discover a repeatable process:
1. Document it as a new SOP in this directory
2. Use checklist format for clarity
3. Include Ethiopian context considerations
4. Reference relevant files in `CLAUDE.md`, `.ai/`, etc.

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0

