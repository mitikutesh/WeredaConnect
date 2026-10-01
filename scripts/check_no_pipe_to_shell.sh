#!/usr/bin/env bash
# scripts/check_no_pipe_to_shell.sh
#
# Fails when CI config or repo scripts download code and run it in a shell
# in the same step, for example `curl ... | bash`, `bash <(curl ...)`,
# `sh -c "$(curl ...)"` or `eval "$(curl ...)"`.
#
# WHY:
#   CI-01 / CHAIN-10 (docs/security/security-assessment-2026-06-20.md).
#   pr-validation.yml used to install Maestro with `curl ... | bash`, which
#   runs whatever the remote host serves, unverified, on a runner in a repo
#   whose deploy workflows hold AWS OIDC credentials. Download a pinned
#   release, check its SHA-256, then run it (see the Maestro step in
#   .github/workflows/pr-validation.yml).
#
# Scope:
#   .github/ and scripts/, files *.yml, *.yaml and *.sh. Comment-only lines
#   are ignored. The self-test strings below spell the downloaders in
#   capitals so this file never matches its own patterns; the self-test
#   lowercases them before scanning.
#
# Exit codes:
#   0  clean
#   1  pipe-to-shell found (lines printed to stderr), or self-test failure
#   2  scan error (missing path, grep failure)
#
# Usage:
#   bash scripts/check_no_pipe_to_shell.sh               # scan .github/ and scripts/
#   bash scripts/check_no_pipe_to_shell.sh <path>...     # scan specific paths
#   bash scripts/check_no_pipe_to_shell.sh --self-test
#
# Caveats:
#   The grep is line-oriented. A command split over several lines with `\`,
#   or a download saved to a file or variable and executed later, is not
#   detected; code review covers those. `curl x || bash y.sh` is flagged
#   (accepted false positive, none exist today).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

S='(bash|sh|zsh|dash|ksh)'
# curl ... | [sudo [-E]] [/usr][/bin/][env ]bash
P1="(curl|wget)[^#]*\|[[:space:]]*(sudo([[:space:]]+-[^[:space:]]+)*[[:space:]]+)?(/usr)?(/bin/)?(env[[:space:]]+)?${S}([^[:alnum:]_.-]|\$)"
# bash <(curl ...), bash -s < <(curl ...), source <(curl ...), . <(curl ...)
P2="(${S}|source|\.)[[:space:]]+(-[^[:space:]]+[[:space:]]+)*(<[[:space:]]*)?<\([[:space:]]*(curl|wget)"
# sh -c "$(curl ...)", eval "$(curl ...)", eval "`curl ...`"
P3="(${S}[[:space:]]+-c|eval)[[:space:]]+[\"']?(\\\$\(|\`)[[:space:]]*(curl|wget)"

if [[ "${1:-}" == "--self-test" ]]; then
  T="$(mktemp -d)"
  trap 'rm -rf "$T"' EXIT

  # Must be flagged: each line goes in its own *.yml file (--include drops
  # other extensions, even for explicit file arguments).
  # shellcheck disable=SC2016  # literal test strings, no expansion wanted
  MUST_FLAG=(
    'CURL -Ls "https://get.example.test" | bash'
    'CURL -fsSL https://x.test/i.sh | sudo -E bash -s -- --flag'
    'WGET -qO- https://x.test/i.sh | sh'
    'CURL -fsSL https://x.test/i.sh | tee i.sh | bash'
    'CURL -fsSL https://x.test/i.sh|bash'
    'bash <(CURL -fsSL https://x.test/i.sh)'
    'sh -c "$(CURL -fsSL https://x.test/i.sh)"'
    'run: "CURL -fsSL https://x.test/i.sh | bash"'
    "bash -c 'CURL -fsSL https://x.test/i.sh | bash'"
    'CURL -fsSL https://x.test/i.sh | bash;'
    'CURL -fsSL https://x.test/i.sh | /bin/bash'
    'CURL -fsSL https://x.test/i.sh | sudo /bin/bash'
    'CURL -fsSL https://x.test/i.sh | /usr/bin/env bash'
    'eval "$(CURL -fsSL https://x.test/i.sh)"'
    'eval "`CURL -fsSL https://x.test/i.sh`"'
    'source <(CURL -fsSL https://x.test/i.sh)'
    'bash -s < <(CURL -fsSL https://x.test/i.sh)'
  )
  # Must pass: all lines in one *.yml file.
  # shellcheck disable=SC2016  # literal test strings, no expansion wanted
  MUST_PASS=(
    '  # CURL -Ls https://x.test/i.sh | bash'
    'echo "$SHA  $zip" | sha256sum --check --strict -'
    '--health-cmd "CURL -f http://localhost:4566/_localstack/health || exit 1"'
    'CURL -fsSL -o "$zip" https://x.test/maestro.zip'
    'CURL -s https://x.test/a.json | jq .'
    "timeout 60 bash -c 'until CURL -f http://localhost:4566/_localstack/health; do sleep 2; done'"
    'CURL -s https://x.test/a | shasum -a 256'
    'CURL -s https://x.test/a | sha256sum'
    'eval "$(ssh-agent -s)"'
  )

  lower() { sed -e 's/CURL/curl/g' -e 's/WGET/wget/g'; }

  fail=0
  i=0
  for line in "${MUST_FLAG[@]}"; do
    i=$((i + 1))
    f="$T/flag_$i.yml"
    printf '%s\n' "$line" | lower > "$f"
    rc=0
    bash "$0" "$f" >/dev/null 2>&1 || rc=$?
    if [[ "$rc" -eq 1 ]]; then
      echo "  ok   flagged: $(cat "$f")"
    else
      echo "  FAIL missed (exit $rc): $(cat "$f")" >&2
      fail=1
    fi
  done

  printf '%s\n' "${MUST_PASS[@]}" | lower > "$T/pass.yml"
  rc=0
  out="$(bash "$0" "$T/pass.yml" 2>&1)" || rc=$?
  if [[ "$rc" -eq 0 ]]; then
    echo "  ok   ${#MUST_PASS[@]} near-misses passed"
  else
    echo "  FAIL near-miss file flagged (exit $rc):" >&2
    echo "$out" >&2
    fail=1
  fi

  # Directory scan recurses and honours --include.
  mkdir -p "$T/dir/sub"
  printf '%s\n' "${MUST_FLAG[0]}" | lower > "$T/dir/sub/x.yaml"
  printf '%s\n' "${MUST_FLAG[0]}" | lower > "$T/dir/sub/x.txt"
  rc=0
  out="$(bash "$0" "$T/dir" 2>&1)" || rc=$?
  if [[ "$rc" -eq 1 ]] && grep -q 'x\.yaml:1:' <<<"$out" && ! grep -q 'x\.txt' <<<"$out"; then
    echo "  ok   directory scan"
  else
    echo "  FAIL directory scan (exit $rc):" >&2
    echo "$out" >&2
    fail=1
  fi

  # A missing path is a scan error (exit 2), never a silent pass.
  rc=0
  bash "$0" "$T/does-not-exist" >/dev/null 2>&1 || rc=$?
  if [[ "$rc" -eq 2 ]]; then
    echo "  ok   missing path is an error"
  else
    echo "  FAIL missing path returned exit $rc, want 2" >&2
    fail=1
  fi

  if [[ "$fail" -ne 0 ]]; then
    echo "Self-test FAILED." >&2
    exit 1
  fi
  echo "Self-test passed."
  exit 0
fi

if [[ "$#" -gt 0 ]]; then
  TARGETS=("$@")
else
  TARGETS=("$REPO_ROOT/.github" "$REPO_ROOT/scripts")
fi

for t in "${TARGETS[@]}"; do
  if [[ ! -e "$t" ]]; then
    echo "Scan target does not exist: $t" >&2
    exit 2
  fi
done

# -H forces "path:line:" output even for a single file argument (GNU grep
# omits the path then), which the comment filter below depends on.
rc=0
RAW="$(grep -rnHE --include='*.yml' --include='*.yaml' --include='*.sh' \
  -e "$P1" -e "$P2" -e "$P3" -- "${TARGETS[@]}")" || rc=$?
if [[ "$rc" -gt 1 ]]; then
  echo "grep failed (exit $rc)" >&2
  exit 2
fi

HITS="$(printf '%s\n' "$RAW" | grep -vE '^[^:]+:[0-9]+:[[:space:]]*#' | sed '/^$/d' || true)"

if [[ -n "$HITS" ]]; then
  echo "Pipe-to-shell install found (CI-01):" >&2
  echo "" >&2
  echo "$HITS" >&2
  echo "" >&2
  echo "Download a pinned release, verify its SHA-256, then run it." >&2
  echo "See the Maestro step in .github/workflows/pr-validation.yml." >&2
  exit 1
fi

echo "OK: no pipe-to-shell installs in ${TARGETS[*]}"
exit 0
