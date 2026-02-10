# Dependabot Quick Reference

## 🚀 Quick Start (5 Minutes)

### 1. Enable GitHub Security Features
Go to **Settings** → **Code security and analysis** and enable:
- ✅ Dependabot alerts
- ✅ Dependabot security updates  
- ✅ Secret scanning
- ✅ Push protection

### 2. Commit Configuration
```bash
git add .github/dependabot.yml .github/workflows/dependabot-auto-merge.yml SECURITY.md
git commit -m "chore(ci): configure Dependabot for dependency scanning"
git push
```

### 3. Wait for First PRs
Dependabot will scan and create PRs within 5-10 minutes.

---

## 📦 What Gets Scanned

| Ecosystem | Location | Schedule | Auto-Merge |
|-----------|----------|----------|------------|
| **Flutter/Dart** | `mobile/pubspec.yaml` | Mon 9am | Minor/Patch |
| **npm** | `infra/package.json` | Mon 9am | Minor/Patch |
| **.NET** | `server/*.csproj` | Mon 9am | Minor/Patch |
| **GitHub Actions** | `.github/workflows/` | Mon 9am | Minor/Patch |

---

## 🤖 Auto-Merge Behavior

### ✅ Auto-Merged (No Action Needed)
- Minor updates (1.2.0 → 1.3.0)
- Patch updates (1.2.0 → 1.2.1)
- After all tests pass

### ⚠️ Manual Review Required
- Major updates (1.x → 2.x)
- Breaking changes
- Security-critical packages

---

## 📋 Review Checklist for Major Updates

### .NET Dependencies
- [ ] Check `server/AOT_COMPLIANCE.md`
- [ ] Test AOT compilation: `cd server && dotnet publish -c Release`
- [ ] Verify no reflection-heavy libraries added

### Flutter Dependencies
- [ ] Test on 2GB RAM device
- [ ] Check APK size: `flutter build apk --release`
- [ ] Verify offline functionality still works

### AWS CDK Dependencies
- [ ] Review infrastructure changes: `cd infra && npm run diff`
- [ ] Check for breaking changes in CDK constructs
- [ ] Verify cost implications

### GitHub Actions
- [ ] Review workflow changes
- [ ] Test locally if possible
- [ ] Check for deprecated features

---

## 🔧 Common Commands

### View Dependabot Status
```bash
# On GitHub: Insights → Dependency graph → Dependabot
```

### Manually Trigger Dependabot
```bash
# On GitHub: Insights → Dependency graph → Dependabot → Check for updates
```

### Approve and Merge Dependabot PR
```bash
gh pr review <PR_NUMBER> --approve
gh pr merge <PR_NUMBER> --squash
```

### Close Unwanted Dependabot PR
```bash
gh pr close <PR_NUMBER>
# Or add to .github/dependabot.yml ignore list
```

---

## 🎯 Labels Used

| Label | Meaning |
|-------|---------|
| `dependencies` | All Dependabot PRs |
| `mobile` | Flutter/Dart updates |
| `server` | .NET updates |
| `infra` | AWS CDK updates |
| `ci` | GitHub Actions updates |
| `flutter` | Flutter-specific |
| `dotnet` | .NET-specific |
| `aws-cdk` | CDK-specific |
| `github-actions` | Actions-specific |

---

## ⚙️ Configuration Files

| File | Purpose |
|------|---------|
| `.github/dependabot.yml` | Dependabot configuration |
| `.github/workflows/dependabot-auto-merge.yml` | Auto-merge workflow |
| `SECURITY.md` | Security policy |
| `docs/developer/DEPENDABOT_SETUP_GUIDE.md` | Full setup guide |

---

## 🆘 Troubleshooting

### No PRs Created
1. Check Dependabot is enabled in Settings
2. Verify `.github/dependabot.yml` syntax
3. Check logs: Insights → Dependency graph → Dependabot

### Auto-Merge Not Working
1. Ensure all tests pass
2. Check branch protection rules
3. Verify workflow permissions

### Too Many PRs
Edit `.github/dependabot.yml`:
```yaml
open-pull-requests-limit: 3  # Reduce from 5
```

### Ignore Specific Dependency
Edit `.github/dependabot.yml`:
```yaml
ignore:
  - dependency-name: "package-name"
    versions: ["2.x"]
```

---

## 📊 Expected Weekly Activity

**Every Monday at 9:00 AM (Addis Ababa time):**
1. Dependabot scans all 4 ecosystems
2. Creates PRs for outdated dependencies
3. Auto-merge workflow runs
4. Minor/patch updates merge automatically
5. Major updates wait for your review

**Typical weekly PRs:** 2-5 PRs (varies by update availability)

---

## 🔒 Security Features Enabled

- ✅ **Dependabot alerts** - Notifies of vulnerable dependencies
- ✅ **Dependabot security updates** - Auto-creates PRs for security fixes
- ✅ **Secret scanning** - Detects committed secrets
- ✅ **Push protection** - Prevents committing secrets

---

## 📚 Learn More

- Full guide: `docs/developer/DEPENDABOT_SETUP_GUIDE.md`
- Security policy: `SECURITY.md`
- Dependabot docs: https://docs.github.com/en/code-security/dependabot

