# Task Claim Protocol

## Purpose
Prevent duplicate work by requiring agents to **claim tasks before starting**. Uses `@Scope` tags in `TASK.md` to make ownership visible.

---

## Format

Tasks in `TASK.md` use this format:

```markdown
- [ ] unclaimed task (any scope can claim)
- [ ] `@Mobile` claimed by Mobile scope
- [ ] `@Backend` claimed by Backend scope
- [x] `@Mobile` completed by Mobile scope
```

---

## Rules

### 1. Claim Before You Start
Before beginning work on any task, add your `@Scope` tag to it in `TASK.md` and commit the claim.

**Valid scope tags:** `@Mobile`, `@Backend`, `@Infra`, `@Localization`, `@Testing`, `@Security`, `@Architect`

### 2. One Scope Per Task
A task can only have one `@Scope` tag. If a task needs work from multiple scopes, split it into separate tasks — one per scope.

### 3. Don't Touch Claimed Tasks
If a task already has another scope's tag, do not work on it. If you believe the task is stalled, create a new task requesting a status update.

### 4. Commit the Claim
The claim is only valid once committed to git. An uncommitted tag can be overwritten by another agent.

### 5. Complete What You Claim
Mark tasks `[x]` only when the work is done and committed. Don't mark tasks complete if the work is partial or untested.

---

## Workflow

```
1. Read TASK.md
2. Find an unclaimed task in your scope
3. Add @Scope tag → commit
4. Do the work
5. Mark [x] → commit
6. Move to next task
```

---

## Cross-Scope Dependencies

When your task depends on another scope's work:

1. Create a task for the other scope: `- [ ] @Backend provide sync endpoint for crop listings`
2. Add a note on your task: `Blocked by: @Backend sync endpoint`
3. Work on other unblocked tasks while waiting
4. When the dependency is resolved, resume your task

---

## Stale Claims

A claim is considered stale if:
- No commits related to the task in the last 48 hours
- The claiming session has ended without progress

To handle stale claims:
1. Create a task: `@Architect review stale claim on [task description]`
2. The human developer decides whether to reassign

---

**Last Updated**: 2026-02-09
**Version**: 1.0.0
