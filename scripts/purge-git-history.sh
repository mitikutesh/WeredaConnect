#!/bin/bash
# Script to purge private keys from git history using BFG Repo-Cleaner
# WARNING: This rewrites git history. All collaborators must re-clone after running.

set -e

echo "🔍 Checking for BFG Repo-Cleaner..."
if ! command -v bfg &> /dev/null; then
    echo "❌ BFG not found. Installing..."
    
    # Download BFG (requires Java)
    if ! command -v java &> /dev/null; then
        echo "❌ Java is required to run BFG. Install Java first:"
        echo "   macOS: brew install openjdk"
        echo "   Ubuntu: sudo apt-get install default-jre"
        exit 1
    fi
    
    echo "📥 Downloading BFG Repo-Cleaner..."
    curl -L https://repo1.maven.org/maven2/com/madgag/bfg/1.14.0/bfg-1.14.0.jar -o /tmp/bfg.jar
    BFG_CMD="java -jar /tmp/bfg.jar"
else
    BFG_CMD="bfg"
fi

echo ""
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

# Clone a fresh copy for BFG (BFG requires a bare clone)
echo "📥 Creating fresh clone for BFG..."
cd ..
git clone --mirror WeredaConnect WeredaConnect-mirror.git
cd WeredaConnect-mirror.git

# Run BFG to delete files
echo "🔥 Purging private keys from history..."
$BFG_CMD --delete-files "server.test.pem*" .

# Clean up
echo "🧹 Cleaning up repository..."
git reflog expire --expire=now --all
git gc --prune=now --aggressive

# Push cleaned history
echo "📤 Pushing cleaned history..."
echo "⚠️  This will force-push to origin. Last chance to abort!"
read -p "Force-push cleaned history? (yes/no): " push_confirm
if [ "$push_confirm" == "yes" ]; then
    git push --force-with-lease --all
    git push --force-with-lease --tags
    echo "✅ Git history purged successfully!"
else
    echo "⚠️  History cleaned locally but NOT pushed."
    echo "   To push later: cd ../WeredaConnect-mirror.git && git push --force-with-lease --all"
fi

# Clean up mirror
cd ..
echo "🧹 Cleaning up mirror clone..."
rm -rf WeredaConnect-mirror.git

# Update local repo
cd WeredaConnect
echo "🔄 Updating local repository..."
git fetch origin
git reset --hard origin/$(git branch --show-current)

echo ""
echo "✅ Done! Verify with:"
echo "   git log --all --full-history --source -- '*pem*'"
echo ""
echo "⚠️  IMPORTANT: All collaborators must re-clone:"
echo "   git clone <repo-url>"

