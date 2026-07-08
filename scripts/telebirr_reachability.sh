#!/usr/bin/env bash
#
# Telebirr gateway reachability diagnostic — helper for the "can we reach the
# sandbox?" question (Tier 3 blocker #2). See .ai/telebirr-testing.md.
#
# Run it from different vantage points to determine which integration path applies:
#   • from your laptop (no VPN)      → expect NOT reachable
#   • from your Ethiopian VPN        → if reachable, the gateway is GEO-FENCED to
#                                       Ethiopia → use the httpProxy egress path
#   • --proxy <ethiopian-proxy>      → validate a candidate Ethiopian proxy BEFORE
#                                       putting it in the Telebirr secret (httpProxy)
#   • from your deployed NAT egress  → if Telebirr allow-listed your EIP and it is
#                                       reachable, the allow-list path works
#
# Usage:
#   scripts/telebirr_reachability.sh [--proxy http://[user:pass@]host:port]
#
set -uo pipefail

PROXY=""
while [ $# -gt 0 ]; do
  case "$1" in
    --proxy) PROXY="${2:-}"; shift 2 ;;
    -h|--help)
      grep '^#' "$0" | sed 's/^#\s\?//'; exit 0 ;;
    *) echo "unknown arg: $1 (see --help)"; exit 2 ;;
  esac
done

# label|host:port  — sandbox is what the current credentials target.
HOSTS=(
  "sandbox|developerportal.ethiotelebirr.et:38443"
  "production|app.ethiotelebirr.et:38443"
)

echo "Telebirr gateway reachability check"
if [ -n "$PROXY" ]; then echo "path: via proxy $PROXY"; else echo "path: direct (this machine's egress)"; fi
echo

reachable=0
for entry in "${HOSTS[@]}"; do
  label="${entry%%|*}"
  hostport="${entry##*|}"
  args=(-sS -o /dev/null --connect-timeout 8 -k -w "%{http_code}")
  [ -n "$PROXY" ] && args+=(-x "$PROXY")
  code="$(curl "${args[@]}" "https://${hostport}/" 2>/dev/null)"
  rc=$?
  # rc 6=DNS fail, 7=connection refused, 28=timeout → not reachable.
  # rc 0 or any TLS-level error means TCP connected → reachable at the network layer.
  if [ "$rc" = 6 ] || [ "$rc" = 7 ] || [ "$rc" = 28 ]; then
    printf "  \xe2\x9d\x8c %-11s %s : NOT reachable (curl rc=%s)\n" "$label" "$hostport" "$rc"
  else
    printf "  \xe2\x9c\x85 %-11s %s : REACHABLE (curl rc=%s, http=%s)\n" "$label" "$hostport" "$rc" "$code"
    reachable=1
  fi
done

echo
echo "Interpretation:"
echo "  • Not reachable from your laptop, reachable from an Ethiopian VPN/proxy"
echo "      → geo-fenced to Ethiopia; set the secret's httpProxy to an Ethiopian egress."
echo "  • Reachable only after Telebirr allow-lists your NAT EIP"
echo "      → allow-list path works; no proxy needed."
exit $(( reachable == 1 ? 0 : 1 ))
