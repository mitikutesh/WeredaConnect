#!/usr/bin/env bash
# Verify that the LIVE TLS chains of the API and webapp hosts still lead to one
# of the Amazon Trust Services roots bundled in
# mobile/lib/core/network/amazon_trust_roots.dart.
#
# The Android app trusts ONLY that bundle (CA pinning). If AWS ever moves a
# host to a CA outside the bundle, every request from the app is rejected, so
# this check runs in CI (mobile-tests.yml) and can be run by hand:
#   ./scripts/check_cert_pins.sh                      # default dev hosts
#   ./scripts/check_cert_pins.sh api.example.com:443  # explicit host[:port]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DART_BUNDLE="$ROOT/mobile/lib/core/network/amazon_trust_roots.dart"
DEFAULT_HOSTS=(
  "vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com"
  "dev-app.weredaconnect.com"
)
HOSTS=("$@")
if [ ${#HOSTS[@]} -eq 0 ]; then HOSTS=("${DEFAULT_HOSTS[@]}"); fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Pull the PEM blocks out of the Dart constant.
awk '/-----BEGIN CERTIFICATE-----/{p=1} p{print} /-----END CERTIFICATE-----/{p=0}' \
  "$DART_BUNDLE" > "$work/bundle.pem"
roots=$(grep -c "BEGIN CERTIFICATE" "$work/bundle.pem" || true)
if [ "$roots" -lt 1 ]; then
  echo "FAIL: no PEM blocks found in $DART_BUNDLE" >&2
  exit 1
fi
echo "Bundled roots: $roots"

status=0
for target in "${HOSTS[@]}"; do
  host="${target%%:*}"
  port="${target##*:}"
  [ "$port" = "$host" ] && port=443

  # Fetch the served chain (s_client may exit non-zero after the handshake;
  # only an empty result is a failure) and split it one file per certificate.
  chain="$(openssl s_client -connect "$host:$port" -servername "$host" -showcerts </dev/null 2>/dev/null || true)"
  if [ -z "$chain" ]; then
    echo "FAIL: $host:$port: could not fetch certificate chain" >&2
    status=1
    continue
  fi
  chain_dir="$work/$host"
  mkdir -p "$chain_dir"
  printf '%s\n' "$chain" | awk -v dir="$chain_dir" \
    '/BEGIN CERT/{f=1;n++} f{print > (dir "/" n ".pem")} /END CERT/{f=0}'
  leaf="$chain_dir/1.pem"
  if [ ! -s "$leaf" ]; then
    echo "FAIL: $host:$port: empty chain" >&2
    status=1
    continue
  fi
  cat "$chain_dir"/[2-9].pem > "$chain_dir/intermediates.pem" 2>/dev/null || true
  untrusted=()
  [ -s "$chain_dir/intermediates.pem" ] && untrusted=(-untrusted "$chain_dir/intermediates.pem")

  if openssl verify -CAfile "$work/bundle.pem" "${untrusted[@]}" "$leaf" >/dev/null 2>&1; then
    leaf_end=$(openssl x509 -in "$leaf" -noout -enddate | cut -d= -f2)
    echo "OK:   $host:$port chains to a bundled Amazon root (leaf expires $leaf_end)"
  else
    echo "FAIL: $host:$port does NOT chain to any bundled root; the Android app will reject it" >&2
    openssl s_client -connect "$host:$port" -servername "$host" -showcerts </dev/null 2>/dev/null \
      | grep -E "^ [0-9] s:| i:" >&2 || true
    status=1
  fi
done

exit $status
