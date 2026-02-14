#!/bin/bash
# Manual git history purge using git filter-branch
# WARNING: This rewrites git history. All collaborators must re-clone after running.

set -e

echo "⚠️  WARNING: This will rewrite git history!"
echo "   All collaborators must re-clone the repository after this."
echo ""
read -p "Continue? (yes/no): " confirm
if [ "$confirm" != "yes" ]; then
    echo "Aborted."
    exit 0
fi

# Create backup branch
echo "📦 Creating backup branch..."
git branch backup-before-purge-$(date +%Y%m%d-%H%M%S) || true

# Remove files from all commits
echo "🔥 Purging private keys from history..."
git filter-branch --force --index-filter \
  'git rm --cached --ignore-unmatch server/localstack-data/cache/server.test.pem* || true' \
  --prune-empty --tag-name-filter cat -- --all

# Clean up refs
echo "🧹 Cleaning up refs..."
rm -rf .git/refs/original/
git reflog expire --expire=now --all
git gc --prune=now --aggressive

# Verify
echo "🔍 Verifying purge..."
if git log --all --full-history --source -- "*pem*" | grep -q "server.test.pem"; then
    echo "❌ Purge failed - pem files still in history"
    exit 1
else
    echo "✅ Purge successful - no pem files found in history"
fi

# Push
echo ""
echo "📤 Ready to push cleaned history"
echo "⚠️  This will force-push to origin. Last chance to abort!"
read -p "Force-push cleaned history? (yes/no): " push_confirm
if [ "$push_confirm" == "yes" ]; then
    git push --force-with-lease --all
    git push --force-with-lease --tags
    echo "✅ Git history purged and pushed successfully!"
else
    echo "⚠️  History cleaned locally but NOT pushed."
    echo "   To push later: git push --force-with-lease --all"
fi

echo ""
echo "✅ Done! Verify with:"
echo "   git log --all --full-history --source -- '*pem*'"
echo ""
echo "⚠️  IMPORTANT: All collaborators must re-clone:"
echo "   rm -rf WeredaConnect && git clone <repo-url>"

