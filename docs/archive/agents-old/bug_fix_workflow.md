# Bug Fix Workflow

## Purpose
Step-by-step checklist for fixing bugs in WeredaConnect. Ensures root cause analysis, proper testing, and prevention of regressions.

---

## Investigation

### 1. Reproduce the Bug
- [ ] Read the bug report (GitHub issue, user feedback, logs)
- [ ] Reproduce locally on development environment
- [ ] **Reproduce offline**: Try to reproduce with network disconnected
- [ ] Test on low-end device (2GB RAM, Android 8) if mobile bug
- [ ] Document exact steps to reproduce

### 2. Identify Root Cause
- [ ] Check CloudWatch logs (server bugs)
- [ ] Check Flutter logs (mobile bugs)
- [ ] Review recent commits (was it introduced recently?)
- [ ] Check if it's an offline sync issue (common in WeredaConnect)
- [ ] Identify which scope owns the buggy code (see `.ai/ownership.md`)

### 3. Claim the Task
- [ ] Create task in `TASK.md` with `@Scope` tag
- [ ] Commit the claim before starting work
- [ ] Create branch: `<scope>/fix-<bug-description>`

---

## Fix

### 4. Write a Failing Test
- [ ] Write a test that reproduces the bug
- [ ] Verify the test fails before the fix
- [ ] Include offline scenario if relevant
- [ ] Test edge cases (null values, empty lists, etc.)

### 5. Implement the Fix
- [ ] Make the minimal change to fix the bug
- [ ] Avoid refactoring unrelated code (scope creep)
- [ ] Ensure fix works offline (if applicable)
- [ ] Check AOT compliance if server fix (see `server/AOT_COMPLIANCE.md`)

### 6. Verify the Fix
- [ ] Run the failing test—it should now pass
- [ ] Run full test suite (ensure no regressions)
- [ ] Test manually in the same scenario as the bug report
- [ ] Test offline behavior (disconnect network)
- [ ] Test on low-end device if mobile bug

---

## Testing

### 7. Regression Testing
- [ ] Run all existing tests for the affected module
- [ ] Test related features (might be affected by the fix)
- [ ] Test in all three languages (Amharic, Afan Oromo, English)
- [ ] Test sync scenarios (offline → online)

### 8. Manual Testing Checklist
- [ ] Reproduce original bug—verify it's fixed
- [ ] Test on 2GB RAM Android 8 emulator (mobile bugs)
- [ ] Test with slow 3G connection (Chrome DevTools throttling)
- [ ] Test offline mode (airplane mode)
- [ ] Test sync after reconnection
- [ ] Verify no new errors in CloudWatch logs

---

## Documentation

### 9. Document the Fix
- [ ] Write clear commit message (see `CLAUDE.md` for format)
  - Format: `fix(<scope>): <description>`
  - Example: `fix(sync): resolve duplicate entries in offline queue`
- [ ] Include root cause in commit body
- [ ] Reference issue number: `Closes #123`
- [ ] Add code comment explaining the fix (if not obvious)

### 10. Update Documentation (if needed)
- [ ] Update feature spec if behavior changed
- [ ] Add to troubleshooting guide if user-facing bug
- [ ] Document workaround if fix is not immediate

---

## Deployment

### 11. Pre-Deployment Checklist
- [ ] All tests pass (unit, integration, manual)
- [ ] Validate AOT compilation if server fix
- [ ] Check APK size (should not increase)
- [ ] Review security implications (if applicable)
- [ ] Get human developer approval

### 12. Deploy
- [ ] Deploy to staging first
- [ ] Verify fix on staging
- [ ] Monitor CloudWatch for errors
- [ ] Deploy to production
- [ ] Monitor for 24 hours

---

## Post-Fix

### 13. Prevent Future Bugs
- [ ] Add test coverage for the bug scenario
- [ ] Document the bug pattern in code comments
- [ ] Update coding standards if pattern is common
- [ ] Share lessons learned in team notes (if applicable)

### 14. Close the Loop
- [ ] Mark task as complete `[x]` in `TASK.md`
- [ ] Close GitHub issue with fix details
- [ ] Notify user who reported the bug (if applicable)

---

## Common Bug Patterns in WeredaConnect

### Offline Sync Issues
- **Symptom**: Data not syncing after reconnection
- **Common causes**: Sync queue not processing, idempotency key conflicts
- **Fix pattern**: Check sync queue logic, verify idempotency keys

### AOT Compilation Failures
- **Symptom**: Build fails with `dotnet publish /p:PublishAot=true`
- **Common causes**: Reflection usage, banned libraries
- **Fix pattern**: Check `server/AOT_COMPLIANCE.md`, use source generators

### Localization Bugs
- **Symptom**: Text not displaying in Amharic/Oromo
- **Common causes**: Missing `.arb` entries, font issues
- **Fix pattern**: Add missing translations, verify Noto Sans Ethiopic font

### Low-End Device Performance
- **Symptom**: App crashes or lags on 2GB RAM devices
- **Common causes**: Memory leaks, large images, heavy UI
- **Fix pattern**: Optimize images, reduce memory footprint, profile with DevTools

---

## Commit Message Examples

### Good
```
fix(sync): resolve duplicate entries in offline queue

When network reconnects, sync queue was processing the same
operation multiple times due to missing idempotency check.
Added idempotency key validation before enqueueing.

Closes #45
```

### Bad
```
fix bug
```

---

**Last Updated**: 2026-02-10  
**Version**: 1.0.0

