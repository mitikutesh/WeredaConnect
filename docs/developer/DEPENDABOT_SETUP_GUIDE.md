# Dependabot Setup Guide

## ✅ What Was Configured

Dependabot has been configured for WeredaConnect with the following setup:

### 📦 Package Ecosystems Monitored

1. **Flutter/Dart** (`mobile/pubspec.yaml`)
   - Weekly checks every Monday at 9:00 AM (Addis Ababa time)
   - Groups minor/patch updates to reduce PR noise
   - Auto-labels PRs with `dependencies`, `mobile`, `flutter`

2. **npm/TypeScript** (`infra/package.json`)
   - Weekly checks for AWS CDK and dev dependencies
   - Groups AWS CDK packages together
   - Auto-labels PRs with `dependencies`, `infra`, `aws-cdk`

3. **.NET/NuGet** (`server/WeredaConnect.Server.csproj`)
   - Weekly checks with AOT compatibility awareness
   - Groups AWS SDK and Lambda packages
   - Auto-labels PRs with `dependencies`, `server`, `dotnet`

4. **GitHub Actions** (`.github/workflows/*.yml`)
   - Weekly checks for action updates
   - Groups all action updates together
   - Auto-labels PRs with `dependencies`, `ci`, `github-actions`

### 🤖 Auto-Merge Workflow

Created `.github/workflows/dependabot-auto-merge.yml` that:
- ✅ Auto-approves minor and patch updates
- ✅ Auto-merges after all tests pass
- ⚠️ Requires manual review for major updates
- 📝 Adds helpful comments to PRs

## 🚀 Next Steps: Enable GitHub Security Features

### Step 1: Enable Dependabot Alerts (2 minutes)

1. Go to your repository on GitHub
2. Click **Settings** → **Code security and analysis**
3. Enable the following:
   - ✅ **Dependency graph** (should already be enabled)
   - ✅ **Dependabot alerts** - Click "Enable"
   - ✅ **Dependabot security updates** - Click "Enable"

### Step 2: Enable Secret Scanning (1 minute)

In the same **Code security and analysis** page:
- ✅ **Secret scanning** - Click "Enable"
- ✅ **Push protection** - Click "Enable" (prevents committing secrets)

### Step 3: Configure Dependabot (Already Done! ✅)

The `.github/dependabot.yml` file has been created with optimal settings.

### Step 4: Test Dependabot (5 minutes)

Trigger a manual Dependabot check:

```bash
# Commit and push the new configuration
git add .github/dependabot.yml .github/workflows/dependabot-auto-merge.yml SECURITY.md
git commit -m "chore(ci): configure Dependabot for dependency scanning"
git push
```

Within a few minutes, Dependabot will:
1. Scan all dependency files
2. Create PRs for outdated dependencies
3. Label and assign PRs automatically

### Step 5: Review First Dependabot PR (10 minutes)

When Dependabot creates its first PR:

1. **Check the PR description** - Dependabot provides:
   - Changelog links
   - Release notes
   - Compatibility score
   - Commit details

2. **Verify tests pass** - All CI workflows must pass:
   - Mobile tests
   - Server tests
   - Infrastructure tests
   - PR validation

3. **For .NET updates**: Check `server/AOT_COMPLIANCE.md`
   - Ensure new versions are AOT-compatible
   - Test locally with `dotnet publish`

4. **For Flutter updates**: Test on low-end device
   - Ensure app still works on 2GB RAM devices
   - Check APK size hasn't increased significantly

5. **Approve or request changes**
   - Minor/patch: Auto-approved and merged
   - Major: Manual review required

## 📋 Dependabot PR Workflow

### Minor/Patch Updates (Automated)
```
Dependabot creates PR
    ↓
Auto-approve workflow runs
    ↓
All tests pass
    ↓
Auto-merge (squash)
    ↓
Done! ✅
```

### Major Updates (Manual Review)
```
Dependabot creates PR
    ↓
Workflow adds review checklist comment
    ↓
You review manually
    ↓
You approve and merge
    ↓
Done! ✅
```

## 🔧 Customization Options

### Ignore Specific Dependencies

Edit `.github/dependabot.yml` to ignore updates:

```yaml
ignore:
  - dependency-name: "isar"
    versions: ["4.x"]  # Ignore v4 major updates
```

### Change Update Frequency

```yaml
schedule:
  interval: "daily"  # Options: daily, weekly, monthly
```

### Adjust PR Limits

```yaml
open-pull-requests-limit: 10  # Default is 5
```

## 📊 Monitoring Dependabot

### View Dependabot Status

1. Go to **Insights** → **Dependency graph** → **Dependabot**
2. See all open/closed Dependabot PRs
3. View security alerts

### View Security Alerts

1. Go to **Security** → **Dependabot alerts**
2. See all vulnerable dependencies
3. Click "Create Dependabot security update" to auto-fix

## ⚠️ Important Notes

### .NET AOT Compatibility

**Always check AOT compatibility** before merging .NET dependency updates:

```bash
cd server
dotnet publish -c Release
```

If compilation fails, the dependency is not AOT-compatible. See `server/AOT_COMPLIANCE.md`.

### Flutter Version Constraints

Ensure Flutter SDK version in `mobile/pubspec.yaml` matches your local version:

```yaml
environment:
  sdk: '>=3.6.0 <4.0.0'
  flutter: '>=3.27.0'
```

### AWS CDK Version Alignment

Keep `aws-cdk` (CLI) and `aws-cdk-lib` (library) versions aligned:

```json
"aws-cdk": "^2.170.0",
"aws-cdk-lib": "^2.170.0"
```

## 🎯 Success Metrics

After setup, you should see:

- ✅ Dependabot PRs created weekly
- ✅ Security alerts for vulnerable dependencies
- ✅ Auto-merged minor/patch updates
- ✅ Manual review requests for major updates
- ✅ No secrets committed (push protection)

## 🆘 Troubleshooting

### Dependabot Not Creating PRs

1. Check `.github/dependabot.yml` syntax
2. Verify repository settings have Dependabot enabled
3. Check Dependabot logs: **Insights** → **Dependency graph** → **Dependabot**

### Auto-Merge Not Working

1. Ensure branch protection rules allow auto-merge
2. Check workflow permissions in `.github/workflows/dependabot-auto-merge.yml`
3. Verify all required status checks pass

### Too Many PRs

Reduce `open-pull-requests-limit` in `.github/dependabot.yml`:

```yaml
open-pull-requests-limit: 3
```

## 📚 Additional Resources

- [Dependabot Documentation](https://docs.github.com/en/code-security/dependabot)
- [Dependabot Configuration Options](https://docs.github.com/en/code-security/dependabot/dependabot-version-updates/configuration-options-for-the-dependabot.yml-file)
- [GitHub Secret Scanning](https://docs.github.com/en/code-security/secret-scanning)
- [WeredaConnect Security Policy](../../SECURITY.md)

