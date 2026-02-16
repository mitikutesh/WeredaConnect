# Session Handoff Protocol

## Purpose
Ensure seamless context preservation when switching between AI sessions or reaching token limits. Prevents loss of progress and maintains project continuity.

---

## When to Hand Off

Trigger a session handoff when:
- **Token limit approaching** (>80% of context window used)
- **Session ending** (planned break, end of work day)
- **Context switch** (switching to different feature/scope)
- **Stuck on a problem** (need fresh perspective)

---

## Handoff Checklist

### 1. Save Current Work
- [ ] Commit all changes (even if work-in-progress)
- [ ] Use clear commit message (see `CLAUDE.md` for format)
- [ ] Push to remote branch (if applicable)

### 2. Update STATE.md
- [ ] Run `./scripts/checkpoint.sh` (automates most of this)
- [ ] Or manually update:
  - [ ] Timestamp
  - [ ] Active branch
  - [ ] Last commit hash
  - [ ] Uncommitted changes
  - [ ] Current context (what you were working on)

### 3. Update TASK.md
- [ ] Mark completed tasks as `[x]`
- [ ] Update in-progress tasks with current status
- [ ] Add notes on blockers or next steps
- [ ] Keep `@Scope` tags on claimed tasks

### 4. Document Context
Add to `STATE.md` → "Notes for Future Self":
- [ ] What you were working on
- [ ] What you just completed
- [ ] What's next (immediate next steps)
- [ ] Any blockers or issues encountered
- [ ] Any decisions made (link to ADRs if created)

---

## Automated Handoff

### Using checkpoint.sh
```bash
./scripts/checkpoint.sh
```

This script automatically:
1. Captures git status (branch, commits, uncommitted changes)
2. Updates `STATE.md` with timestamp and context
3. Prompts for notes on current work
4. Commits the checkpoint

**Recommended**: Run this before every session end.

---

## Manual Handoff (if script unavailable)

### Update STATE.md Manually

```markdown
## 🛑 Session Checkpoint

**Timestamp:** 2026-02-10 14:30:00 EET  
**Status:** Manual checkpoint  
**Active Branch:** mobile/add-crop-listing  

---

## 📍 Current Context

### Last Git Commits
- abc1234: feat(mobile): add crop listing UI (5 minutes ago)
- def5678: feat(mobile): add Isar schema for crops (1 hour ago)

---

## 🚧 Current Task Status

**In Progress:**
- [ ] `@Mobile` Implement crop listing feature
  - ✅ Created Isar schema
  - ✅ Built UI screens
  - 🚧 Working on sync queue integration
  - ⏭️ Next: Test offline behavior

**Blocked:**
- [ ] `@Server` Need sync endpoint for crop listings (waiting on Server scope)

---

## 💡 Notes for Future Self

- Crop listing UI is complete but not yet connected to sync queue
- Need to add idempotency key to CropListing model before syncing
- Remember to test offline scenario (airplane mode)
- Voice-to-text integration is next after sync works
```

---

## Resumption Prompt

When starting a new session, use this prompt:

```
Read STATE.md and TASK.md. Continue work based on the last commit and incomplete tasks.

Current branch: [branch name from STATE.md]
Last commit: [commit message from STATE.md]
Current task: [task description from TASK.md]
```

---

## Best Practices

### Do's ✅
- **Commit often**: Even work-in-progress (use `chore(<scope>): wip <description>` format)
- **Be specific**: "Working on sync queue integration" not "working on feature"
- **Link to files**: Mention specific files you were editing
- **Note decisions**: Document any architectural choices made
- **Update timestamps**: Always include current timestamp

### Don'ts ❌
- **Don't leave uncommitted changes** (unless absolutely necessary)
- **Don't use vague notes**: "Almost done" is not helpful
- **Don't skip TASK.md updates**: Future you needs to know what's complete
- **Don't forget to push**: Remote backup prevents data loss

---

## Common Handoff Scenarios

### Scenario 1: Token Limit Approaching
```
1. Commit current work: `git commit -m "chore(mobile): wip crop listing sync integration"`
2. Run `./scripts/checkpoint.sh`
3. Add note: "Token limit reached. Next: complete sync queue integration."
4. Start new session with resumption prompt
```

### Scenario 2: Switching Scopes
```
1. Complete current task or commit WIP
2. Update TASK.md (mark progress)
3. Run `./scripts/checkpoint.sh`
4. Switch branch: `git checkout main && git checkout -b backend/sync-endpoint`
5. Claim new task in TASK.md with `@Backend` tag
```

### Scenario 3: Stuck on a Problem
```
1. Document the problem in STATE.md → "Notes for Future Self"
2. Include what you've tried
3. Run `./scripts/checkpoint.sh`
4. Ask human developer for help (or start fresh session with new perspective)
```

---

## Verification

Before ending session, verify:
- [ ] All changes committed (or documented why not)
- [ ] `STATE.md` updated with current context
- [ ] `TASK.md` reflects current progress
- [ ] Notes are clear enough for someone else to continue
- [ ] Branch pushed to remote (if applicable)

---

## Emergency Handoff

If session crashes or token limit hit unexpectedly:

1. **Immediately** commit any changes: `git commit -m "emergency checkpoint"`
2. Push to remote: `git push origin [branch]`
3. Manually update `STATE.md` with minimal context
4. Resume in new session with: "Emergency handoff. Read STATE.md for context."

---

**Last Updated**: 2026-02-10  
**Version**: 1.0.0

