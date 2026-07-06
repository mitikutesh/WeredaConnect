#!/usr/bin/env bash
# Design-token guardrail (design brief 2026-07-06, migration step 6).
#
# Blocks the two patterns that eroded the design system before:
#   1. Raw Material palette colors (Colors.red/orange/green/…) used for
#      semantic meaning instead of AppTheme status tokens.
#   2. New raw Color(0x…) hex literals outside the theme file (warning).
#
# Run manually or from CI:  ./scripts/check_design_tokens.sh
set -euo pipefail
cd "$(dirname "$0")/.."

fail=0

# Colors.white/black/transparent are legitimate (on-primary text, overlays).
banned='Colors\.(red|orange|green|amber|teal|indigo|deepOrange|blue|grey|purple|pink|cyan|lime|yellow)\b'
hits=$(grep -rnE "$banned" mobile/lib --include="*.dart" | grep -v "shared/theme/app_theme.dart" || true)
if [ -n "$hits" ]; then
  echo "FAIL: raw Material palette colors found — use AppTheme tokens instead:"
  echo "$hits"
  fail=1
fi

hex=$(grep -rnE 'Color\(0x[0-9A-Fa-f]{8}\)' mobile/lib --include="*.dart" | grep -v "shared/theme/app_theme.dart" || true)
if [ -n "$hex" ]; then
  echo "WARN: raw Color(0x…) literals outside app_theme.dart (migrate to tokens):"
  echo "$hex"
fi

if [ "$fail" -eq 1 ]; then
  exit 1
fi
echo "OK: design-token check passed"
