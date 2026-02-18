# F-01: Git History Purge Runbook

**Status**: Ready for execution
**Risk Level**: HIGH - Destructive, irreversible operation
**Estimated Duration**: 30-60 minutes (excluding team coordination)
**Last Updated**: 2026-02-17

## Summary

Private key files were committed to the repository in commit `e3c2e8ea` and removed in commit `5ec0a1a9`. Although the files are no longer in the working tree, they remain accessible in git history. This runbook purges them permanently using `git-filter-repo`.

### Files to Purge

| File | Type | Severity |
|------|------|----------|
| `server/localstack-data/cache/server.test.pem.key` | Private key | HIGH |
| `server/localstack-data/cache/server.test.pem.crt` | Certificate | LOW |
| `server/localstack-data/cache/server.test.pem` | Certificate bundle | LOW |

### Commits Affected

- `e3c2e8ea` - Added the files (docs: add E2E Local Testing Guide...)
- `5ec0a1a9` - Removed the files (fix(server): remove private keys...)

---

## Pre-requisites

### 1. Tools Required

- `git-filter-repo` (confirmed available at `/opt/homebrew/bin/git-filter-repo`)
- `git` (standard)

### 2. Team Notification

Before proceeding, notify all contributors:

```
Subject: [WeredaConnect] Scheduled git history rewrite - action required

All contributors must:
1. Push any pending branches to origin BEFORE the maintenance window
2. Do NOT push during the maintenance window
3. After the purge, delete your local clone and re-clone the repository
   (or follow the re-sync steps provided after completion)

Maintenance window: [INSERT DATE/TIME]
Estimated duration: 30-60 minutes
```

### 3. Backup

```bash
# Create a full mirror backup BEFORE any changes
cd ~
git clone --mirror git@github.com:mitikutesh/WeredaConnect.git WeredaConnect-backup-$(date +%Y%m%d).git

# Verify the backup is complete
cd WeredaConnect-backup-$(date +%Y%m%d).git
git log --oneline -5
git branch -a
```

### 4. Verify the Problem

Confirm the files exist in history:

```bash
cd /path/to/WeredaConnect

# Should return 2 commits
git log --all --full-history -- "server/localstack-data/cache/server.test.pem.key"

# Verify the key content is retrievable from history (confirms the leak)
git show e3c2e8ea:server/localstack-data/cache/server.test.pem.key | head -1
# Expected output: -----BEGIN RSA PRIVATE KEY-----
```

---

## Purge Procedure

### Step 1: Work from a fresh clone

```bash
# Use a fresh clone to avoid local state issues
cd ~
git clone git@github.com:mitikutesh/WeredaConnect.git WeredaConnect-purge
cd WeredaConnect-purge
```

### Step 2: Run git-filter-repo

```bash
# Purge all three files from all branches and tags
git filter-repo \
  --invert-paths \
  --path "server/localstack-data/cache/server.test.pem.key" \
  --path "server/localstack-data/cache/server.test.pem.crt" \
  --path "server/localstack-data/cache/server.test.pem"
```

**What this does:**
- Rewrites every commit in all branches that touched these files
- Removes the file content from all tree objects
- Creates new commit hashes for all affected commits and their descendants
- Does NOT modify the working tree (files are already removed)
- Automatically removes the `origin` remote as a safety measure

### Step 3: Verify the purge locally

```bash
# Should return NO results
git log --all --full-history -- "server/localstack-data/cache/server.test.pem.key"

# Double-check: try to access the file at the old commit (will fail since hash changed)
# The old commit hash e3c2e8ea will no longer exist
git rev-parse e3c2e8ea 2>&1
# Expected: "unknown revision or path not in the working tree"

# Verify repository integrity
git fsck --full

# Verify current branch still has expected content
git log --oneline -10
```

### Step 4: Force-push to origin

```bash
# Re-add the remote (git-filter-repo removes it as a safety measure)
git remote add origin git@github.com:mitikutesh/WeredaConnect.git

# Force-push ALL branches and tags
git push origin --force --all
git push origin --force --tags
```

**WARNING**: This overwrites the remote history. All open pull requests will be invalidated.

### Step 5: GitHub cache cleanup

GitHub caches commit data. To ensure the old commits are garbage-collected:

1. Go to **Settings > Actions > General** for the repository (if applicable)
2. Contact GitHub support if the repository is public, requesting a garbage collection of dangling objects
3. For private repositories, GitHub will garbage-collect orphaned objects automatically within ~90 days

Additionally, invalidate any cached CI artifacts:

```bash
# Delete all GitHub Actions caches (if applicable)
gh cache list | awk '{print $1}' | xargs -I{} gh cache delete {}
```

---

## Post-Purge Steps

### 1. All Contributors Must Re-sync

Each contributor must do ONE of:

**Option A: Fresh clone (recommended)**
```bash
cd ~
rm -rf WeredaConnect
git clone git@github.com:mitikutesh/WeredaConnect.git
```

**Option B: Reset existing clone (advanced)**
```bash
cd WeredaConnect
git fetch origin
git checkout main
git reset --hard origin/main
git reflog expire --expire=now --all
git gc --prune=now --aggressive
```

### 2. Verify Purge on Fresh Clone

```bash
cd WeredaConnect  # fresh clone
git log --all --full-history -- "server/localstack-data/cache/server.test.pem.key"
# Expected: no output (empty)

git log --all --diff-filter=A -- "server/localstack-data/cache/"
# Expected: no output (empty)
```

### 3. Key/Certificate Rotation

Even though these were LocalStack test keys, follow rotation best practices:

1. **Regenerate LocalStack test certificates**: LocalStack will auto-generate new ones on next startup. No manual action needed.
2. **Audit for reuse**: Verify the leaked test keys were NOT reused for any other purpose (staging, dev environments, etc.).
3. **Verify `.gitignore`**: Confirm `server/localstack-data/**` is in `.gitignore` (already done in commit `5ec0a1a9`).

```bash
# Verify .gitignore protection
grep "localstack-data" .gitignore
# Expected: server/localstack-data/**
```

### 4. Update GitHub Branch Protection

If branch protection rules reference specific commit SHAs, update them since all commit hashes have changed.

---

## Rollback Plan

If something goes wrong during the purge:

### Before force-push (easy rollback)

Simply discard the purge clone and use the original:

```bash
rm -rf ~/WeredaConnect-purge
# The original repository is untouched
```

### After force-push (restore from backup)

```bash
cd ~/WeredaConnect-backup-YYYYMMDD.git

# Force-push the backup back to origin
git push --mirror git@github.com:mitikutesh/WeredaConnect.git
```

**Note**: After restoring, the private keys will be back in history. You would need to re-run the purge procedure.

---

## Verification Checklist

After the entire procedure is complete, verify:

- [ ] `git log --all -- "server/localstack-data/cache/server.test.pem.key"` returns empty
- [ ] `git log --all -- "server/localstack-data/cache/server.test.pem.crt"` returns empty
- [ ] `git log --all -- "server/localstack-data/cache/server.test.pem"` returns empty
- [ ] `git fsck --full` reports no errors
- [ ] All branches are intact on origin (`git branch -r`)
- [ ] CI/CD pipelines still pass
- [ ] `.gitignore` contains `server/localstack-data/**`
- [ ] All contributors have re-synced
- [ ] Backup archive stored securely (then delete after 30 days)

---

## References

- [git-filter-repo documentation](https://htmlpreview.github.io/?https://github.com/newren/git-filter-repo/blob/docs/html/git-filter-repo.html)
- [GitHub: Removing sensitive data](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository)
- Related issue: PR #1 review comment (original detection)
- Related commit: `5ec0a1a9` (file removal, but not history purge)
