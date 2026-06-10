# shellcheck shell=bash
# scripts/lib/ai-common.sh
# ===========================================================================
# Shared library for the Claude <-> Codex AI orchestration "control room".
# SOURCE this file; do not execute it:
#   source "$(dirname "$0")/lib/ai-common.sh"
#
# Responsibilities (the observability core):
#   - logging helpers (console)
#   - repo-root / run-directory / state-file resolution
#   - the formal state machine (states -> phase + active agent)
#   - a per-run JSON state file (audit) + a global live current-state.json
#   - a structured EVENT stream (events.jsonl)
#   - structured LOGS: status / transfers / decisions / disputes / claude / codex
#   - artifact tracking (.ai/artifacts/index.json)
#   - CLI invocation wrappers for Claude and Codex (with AI_DRY_RUN support)
#   - structured-marker parsing for review verdicts / dispute detection
#
# Configuration (override via environment):
#   CLAUDE_BIN   command used to invoke Claude     (default: claude)
#   CODEX_BIN    command used to invoke Codex       (default: codex)
#   CLAUDE_MODEL optional model alias for Claude    (default: unset -> CLI default)
#   CODEX_MODEL  optional model for Codex           (default: unset -> CLI default)
#   AI_DRY_RUN   1 = no CLI invoked; deterministic placeholder artifacts so the
#                pipeline + state machine + dashboard can be exercised tokenless.
# ===========================================================================

set -o pipefail

CLAUDE_BIN="${CLAUDE_BIN:-claude}"
CODEX_BIN="${CODEX_BIN:-codex}"
AI_DRY_RUN="${AI_DRY_RUN:-0}"

# ---------------------------------------------------------------------------
# Console logging
# ---------------------------------------------------------------------------
if [ -t 1 ]; then
  _C_RESET="\033[0m"; _C_BLUE="\033[1;34m"; _C_GREEN="\033[1;32m"
  _C_YELLOW="\033[1;33m"; _C_RED="\033[1;31m"; _C_DIM="\033[2m"; _C_CYAN="\033[1;36m"
else
  _C_RESET=""; _C_BLUE=""; _C_GREEN=""; _C_YELLOW=""; _C_RED=""; _C_DIM=""; _C_CYAN=""
fi

log_step() { printf "${_C_BLUE}▶ %s${_C_RESET}\n" "$*"; }
log_info() { printf "  %s\n" "$*"; }
log_ok()   { printf "${_C_GREEN}✅ %s${_C_RESET}\n" "$*"; }
log_warn() { printf "${_C_YELLOW}⚠️  %s${_C_RESET}\n" "$*"; }
log_err()  { printf "${_C_RED}❌ %s${_C_RESET}\n" "$*" >&2; }
log_dim()  { printf "${_C_DIM}%s${_C_RESET}\n" "$*"; }
log_xfer() { printf "${_C_CYAN}🔄 %s${_C_RESET}\n" "$*"; }

die() { log_err "$*"; exit 1; }

# ---------------------------------------------------------------------------
# Timestamps
# ---------------------------------------------------------------------------
ai_now_utc() { date -u '+%Y-%m-%dT%H:%M:%SZ'; }          # for events.jsonl
ai_now_iso() { date '+%Y-%m-%dT%H:%M:%S%z'; }            # local, for state.json
ai_now_hm()  { date '+%H:%M'; }                          # for status.log

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
ai_repo_root()    { git rev-parse --show-toplevel 2>/dev/null || die "Not inside a git repository."; }
ai_plans_dir()    { echo "$(ai_repo_root)/docs/ai-plans"; }
ai_runs_dir()     { echo "$(ai_repo_root)/.ai/runs"; }
ai_run_dir()      { echo "$(ai_runs_dir)/$1"; }
ai_latest_link()  { echo "$(ai_runs_dir)/latest"; }
ai_state_dir()    { echo "$(ai_repo_root)/.ai/state"; }
ai_cs_file()      { echo "$(ai_state_dir)/current-state.json"; }
ai_artifacts_dir(){ echo "$(ai_repo_root)/.ai/artifacts"; }
ai_artifacts_index() { echo "$(ai_artifacts_dir)/index.json"; }
ai_prompts_dir()  { echo "$(ai_repo_root)/.ai/orchestrator/prompts"; }
ai_templates_dir(){ echo "$(ai_repo_root)/.ai/templates"; }

require_cmd() { command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"; }

# ---------------------------------------------------------------------------
# Concurrency lock — ONE pipeline run at a time.
# Two concurrent runs fight over .ai/runs/latest + current-state.json and burn
# tokens on duplicate work. The lock is an atomically-created DIRECTORY at
# .ai/state/orchestrator.lock with a meta file (pid / run_id / started).
# ---------------------------------------------------------------------------
ai_lock_dir() { echo "$(ai_state_dir)/orchestrator.lock"; }

# ai_lock_acquire <run_id-or-pending>  — dies with a clear message if an
# ACTIVE run holds the lock; auto-clears a stale lock (owner pid dead).
ai_lock_acquire() {
  local run_id="$1" d meta pid owner started
  d="$(ai_lock_dir)"; meta="$d/meta"
  mkdir -p "$(ai_state_dir)"
  if mkdir "$d" 2>/dev/null; then
    printf 'pid=%s\nrun_id=%s\nstarted=%s\n' "$$" "$run_id" "$(ai_now_iso)" > "$meta"
    AI_LOCK_HELD=1
    return 0
  fi
  pid="$(sed -n 's/^pid=//p' "$meta" 2>/dev/null | head -1)"
  owner="$(sed -n 's/^run_id=//p' "$meta" 2>/dev/null | head -1)"
  started="$(sed -n 's/^started=//p' "$meta" 2>/dev/null | head -1)"
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
    log_err "Another orchestration run is ACTIVE — refusing to start a second one."
    log_info "  active run : ${owner:-unknown}"
    log_info "  pid        : $pid   (started ${started:-?})"
    log_info "  watch it   : scripts/ai-dashboard.sh --status-view"
    log_info "  stop it    : kill $pid    (then re-run; stale lock auto-clears)"
    log_info "  lock       : $d"
    exit 1
  fi
  log_warn "Stale orchestrator lock (pid ${pid:-?} not running) — clearing it."
  rm -f "$meta"; rmdir "$d" 2>/dev/null || true
  mkdir "$d" 2>/dev/null || die "Could not acquire lock at $d (remove it manually if stale)."
  printf 'pid=%s\nrun_id=%s\nstarted=%s\n' "$$" "$run_id" "$(ai_now_iso)" > "$meta"
  AI_LOCK_HELD=1
}

# ai_lock_note <run_id> — update the run id in the lock meta once known.
ai_lock_note() {
  [ "${AI_LOCK_HELD:-0}" = "1" ] || return 0
  printf 'pid=%s\nrun_id=%s\nstarted=%s\n' "$$" "$1" "$(ai_now_iso)" > "$(ai_lock_dir)/meta"
}

ai_lock_release() {
  [ "${AI_LOCK_HELD:-0}" = "1" ] || return 0
  rm -f "$(ai_lock_dir)/meta"
  rmdir "$(ai_lock_dir)" 2>/dev/null || true
  AI_LOCK_HELD=0
  return 0
}

# ---------------------------------------------------------------------------
# Run identity
# ---------------------------------------------------------------------------
ai_new_run_id() {
  local task="$1" slug stamp
  stamp="$(date '+%Y%m%d-%H%M%S')"
  slug="$(printf '%s' "$task" \
    | tr '[:upper:]' '[:lower:]' \
    | tr -cs 'a-z0-9' '-' \
    | sed -E 's/^-+|-+$//g' \
    | cut -c1-40)"
  [ -n "$slug" ] || slug="task"
  echo "${stamp}-${slug}"
}

# ---------------------------------------------------------------------------
# Formal state machine — states map to a PHASE and an ACTIVE AGENT.
# Canonical reference: .ai/orchestrator/state-machine.md
# ---------------------------------------------------------------------------
ai_phase_for_state() {
  case "$1" in
    TASK_CREATED)               echo "Intake" ;;
    PLAN_DRAFTING|PLAN_SELF_REVIEW|PLAN_REVIEW_BY_CODEX|PLAN_REVISION|PLAN_APPROVED) echo "Planning" ;;
    IMPLEMENTATION_IN_PROGRESS|IMPLEMENTATION_SELF_REVIEW) echo "Execution" ;;
    CODE_REVIEW_BY_CODEX|REVIEW_RESOLUTION) echo "Review" ;;
    DISPUTE_OPEN|USER_DECISION_REQUIRED) echo "Dispute" ;;
    READY_FOR_APPROVAL)         echo "Approval" ;;
    COMPLETED)                  echo "Done" ;;
    FAILED)                     echo "Failed" ;;
    *)                          echo "Idle" ;;
  esac
}

ai_agent_for_state() {
  case "$1" in
    PLAN_DRAFTING|PLAN_SELF_REVIEW|PLAN_REVISION|IMPLEMENTATION_IN_PROGRESS|IMPLEMENTATION_SELF_REVIEW|REVIEW_RESOLUTION) echo "Claude" ;;
    PLAN_REVIEW_BY_CODEX|CODE_REVIEW_BY_CODEX) echo "Codex" ;;
    DISPUTE_OPEN|USER_DECISION_REQUIRED|READY_FOR_APPROVAL|COMPLETED) echo "User" ;;
    *) echo "Orchestrator" ;;
  esac
}

# ---------------------------------------------------------------------------
# Per-run state file (audit trail)
# ---------------------------------------------------------------------------
ai_state_file() { echo "$(ai_run_dir "$1")/state.json"; }

ai_state_init() {
  local run_id="$1" task="$2" file ts
  require_cmd jq
  file="$(ai_state_file "$run_id")"
  ts="$(ai_now_iso)"
  jq -n \
    --arg run_id "$run_id" --arg task "$task" --arg ts "$ts" \
    '{
      run_id: $run_id, task: $task,
      state: "TASK_CREATED", phase: "Intake", active_agent: "Orchestrator",
      dispute_open: false, resume_from: "",
      created_at: $ts, updated_at: $ts,
      history: [ { state: "TASK_CREATED", at: $ts } ]
    }' > "$file"
}

ai_state_get() { jq -r '.state' "$(ai_state_file "$1")"; }

# ai_state_set <run_id> <new_state> [dispute_open(true|false)]
# Updates the per-run state.json, mirrors the live current-state.json, and
# emits a STATE_CHANGED event. This is the single choke-point for transitions.
ai_state_set() {
  local run_id="$1" new_state="$2" dispute="${3:-}" file tmp ts phase agent
  require_cmd jq
  file="$(ai_state_file "$run_id")"
  tmp="${file}.tmp"
  ts="$(ai_now_iso)"
  phase="$(ai_phase_for_state "$new_state")"
  agent="$(ai_agent_for_state "$new_state")"
  jq \
    --arg state "$new_state" --arg ts "$ts" --arg dispute "$dispute" \
    --arg phase "$phase" --arg agent "$agent" \
    '
    .state = $state | .phase = $phase | .active_agent = $agent | .updated_at = $ts
    | (if $dispute == "true" then .dispute_open = true
       elif $dispute == "false" then .dispute_open = false else . end)
    | .history += [ { state: $state, at: $ts } ]
    ' "$file" > "$tmp" && mv "$tmp" "$file"
  log_dim "    state -> $new_state  (phase: $phase · agent: $agent)"

  # Mirror to the live dashboard view.
  ai_cs_set "$run_id" state "$new_state"
  ai_cs_set "$run_id" phase "$phase"
  ai_cs_set "$run_id" active_agent "$agent"
  ai_cs_set "$run_id" updated_at "$ts"
  [ "$dispute" = "true" ]  && ai_cs_set "$run_id" dispute_open "true"
  [ "$dispute" = "false" ] && ai_cs_set "$run_id" dispute_open "false"
  ai_emit_event "$run_id" "$agent" "STATE_CHANGED" "$phase" "" "→ $new_state"
}

# ai_state_field <run_id> <key> <value>  — set an arbitrary top-level field.
ai_state_field() {
  local run_id="$1" key="$2" value="$3" file tmp
  require_cmd jq
  file="$(ai_state_file "$run_id")"; tmp="${file}.tmp"
  jq --arg k "$key" --arg v "$value" '.[$k] = $v' "$file" > "$tmp" && mv "$tmp" "$file"
}

ai_state_field_get() { jq -r --arg k "$2" '.[$k] // ""' "$(ai_state_file "$1")"; }

# ---------------------------------------------------------------------------
# Live current-state.json  (.ai/state/current-state.json) — single source of
# truth for the dashboard. Mutated incrementally by the helpers below.
# ---------------------------------------------------------------------------
ai_cs_init() {
  local run_id="$1" task="$2" ts; ts="$(ai_now_iso)"
  require_cmd jq
  mkdir -p "$(ai_state_dir)"
  jq -n \
    --arg run_id "$run_id" --arg task "$task" --arg ts "$ts" \
    --arg run_dir ".ai/runs/$run_id" \
    '{
      run_id: $run_id, task: $task,
      state: "TASK_CREATED", phase: "Intake", active_agent: "Orchestrator",
      last_transfer: "None", latest_decision: "None", latest_status: "Run created",
      latest_event: "TASK_CREATED", dispute_open: "false",
      updated_at: $ts, run_dir: $run_dir
    }' > "$(ai_cs_file)"
}

# ai_cs_set <run_id> <key> <string-value>  (run_id is accepted for symmetry).
ai_cs_set() {
  local key="$2" value="$3" f tmp
  require_cmd jq
  f="$(ai_cs_file)"
  [ -f "$f" ] || { mkdir -p "$(ai_state_dir)"; echo '{}' > "$f"; }
  tmp="${f}.tmp"
  jq --arg k "$key" --arg v "$value" '.[$k] = $v' "$f" > "$tmp" && mv "$tmp" "$f"
}

# Point .ai/runs/latest at a run and prime the live view + logs.
ai_set_latest() {
  local run_id="$1"
  ln -sfn "$run_id" "$(ai_latest_link)" 2>/dev/null || true
}

# ---------------------------------------------------------------------------
# Event stream — events.jsonl in the run dir (one JSON object per line).
# Schema: { timestamp, agent, event, phase, artifact, summary }
# ---------------------------------------------------------------------------
ai_events_file() { echo "$(ai_run_dir "$1")/events.jsonl"; }

# ai_emit_event <run_id> <agent> <event> <phase> <artifact> <summary>
ai_emit_event() {
  local run_id="$1" agent="$2" event="$3" phase="$4" artifact="$5" summary="$6" f
  require_cmd jq
  f="$(ai_events_file "$run_id")"
  jq -cn \
    --arg ts "$(ai_now_utc)" --arg agent "$agent" --arg event "$event" \
    --arg phase "$phase" --arg artifact "$artifact" --arg summary "$summary" \
    '{timestamp:$ts, agent:$agent, event:$event, phase:$phase, artifact:$artifact, summary:$summary}' \
    >> "$f"
  ai_cs_set "$run_id" latest_event "$event ($agent)"
}

# ---------------------------------------------------------------------------
# Structured logs (all in the run dir; reachable via .ai/runs/latest/)
# ---------------------------------------------------------------------------
ai_init_logs() {
  local run_id="$1" d; d="$(ai_run_dir "$run_id")"
  : > "$d/status.log"; : > "$d/transfers.log"; : > "$d/claude.log"
  : > "$d/codex.log";  : > "$d/decisions.log"; : > "$d/disputes.log"
  : > "$d/events.jsonl"
}

# ai_status <run_id> <message>  — high-level milestone (status.log + event).
ai_status() {
  local run_id="$1" msg="$2"
  printf '[%s] %s\n' "$(ai_now_hm)" "$msg" >> "$(ai_run_dir "$run_id")/status.log"
  ai_cs_set "$run_id" latest_status "$msg"
  log_info "$msg"
}

# ai_transfer <run_id> <from> <to> <artifact> <reason>  — explicit handoff.
ai_transfer() {
  local run_id="$1" from="$2" to="$3" artifact="$4" reason="$5" d
  d="$(ai_run_dir "$run_id")"
  {
    echo "TRANSFER:"
    echo "FROM: $from"
    echo "TO:   $to"
    echo
    echo "Artifact:"
    echo "$artifact"
    echo
    echo "Reason:"
    echo "$reason"
    echo
    echo "At: $(ai_now_utc)"
    echo "---"
  } >> "$d/transfers.log"
  printf '[%s] Transfer → %s (%s)\n' "$(ai_now_hm)" "$to" "$reason" >> "$d/status.log"
  ai_cs_set "$run_id" last_transfer "$from → $to · $reason"
  ai_emit_event "$run_id" "$from" "TRANSFER" "$(ai_phase_for_state "$(ai_state_get "$run_id")")" "$artifact" "$from → $to: $reason"
  log_xfer "Transfer: $from → $to ($reason)"
}

# ai_decision <run_id> <message>  — accepted/rejected review outcomes.
ai_decision() {
  local run_id="$1" msg="$2"
  printf '[%s] %s\n' "$(ai_now_hm)" "$msg" >> "$(ai_run_dir "$run_id")/decisions.log"
  ai_cs_set "$run_id" latest_decision "$msg"
  ai_emit_event "$run_id" "$(ai_agent_for_state "$(ai_state_get "$run_id")")" "DECISION" "$(ai_phase_for_state "$(ai_state_get "$run_id")")" "" "$msg"
}

# ai_dispute_log <run_id> <message>  — active disagreement.
ai_dispute_log() {
  local run_id="$1" msg="$2"
  printf '[%s] %s\n' "$(ai_now_hm)" "$msg" >> "$(ai_run_dir "$run_id")/disputes.log"
  ai_emit_event "$run_id" "User" "DISPUTE_OPENED" "Dispute" "disputes.md" "$msg"
}

# ---------------------------------------------------------------------------
# Artifact tracking — .ai/artifacts/index.json
# Schema: { "artifacts": [ {name, path, creator, phase, run_id, created_at, modified_at} ] }
# ---------------------------------------------------------------------------
ai_artifacts_init() {
  require_cmd jq
  mkdir -p "$(ai_artifacts_dir)"
  [ -f "$(ai_artifacts_index)" ] || echo '{"artifacts":[]}' > "$(ai_artifacts_index)"
}

# ai_register_artifact <run_id> <name> <creator> <phase> <path>
ai_register_artifact() {
  local run_id="$1" name="$2" creator="$3" phase="$4" path="$5" idx tmp ts
  require_cmd jq
  ai_artifacts_init
  idx="$(ai_artifacts_index)"; tmp="${idx}.tmp"; ts="$(ai_now_utc)"
  jq \
    --arg name "$name" --arg path "$path" --arg creator "$creator" \
    --arg phase "$phase" --arg run_id "$run_id" --arg ts "$ts" \
    '
    if (.artifacts | map(.path) | index($path)) != null then
      .artifacts |= map(if .path == $path
        then .modified_at = $ts | .phase = $phase | .run_id = $run_id
        else . end)
    else
      .artifacts += [{name:$name, path:$path, creator:$creator, phase:$phase,
                      run_id:$run_id, created_at:$ts, modified_at:$ts}]
    end
    ' "$idx" > "$tmp" && mv "$tmp" "$idx"
  ai_emit_event "$run_id" "$creator" "ARTIFACT_CREATED" "$phase" "$path" "Produced $name"
}

# ---------------------------------------------------------------------------
# Failure marking
# ---------------------------------------------------------------------------
ai_mark_failed() {
  local run_id="$1" reason="${2:-unspecified error}"
  command -v jq >/dev/null 2>&1 || return 0
  [ -f "$(ai_state_file "$run_id")" ] || return 0
  ai_state_set "$run_id" "FAILED" >/dev/null 2>&1 || true
  ai_status "$run_id" "FAILED: $reason" 2>/dev/null || true
}

# ---------------------------------------------------------------------------
# Structured-marker parsing (review verdicts / dispute detection)
# ---------------------------------------------------------------------------
ai_marker() {
  local key="$1" file="$2"
  [ -f "$file" ] || { echo ""; return 0; }
  # Liberal in what we accept (Postel's law): real models often wrap the marker
  # in Markdown — `VERDICT: X`, **VERDICT:** X, > VERDICT: X. We match the key
  # after any run of leading non-alphanumerics (so "the VERDICT:" in prose does
  # NOT match), strip the key + any decoration around the value, and take the
  # LAST such line. `|| true` keeps a truly-absent marker NON-FATAL under
  # `set -o pipefail` + `set -e` (yields "" / exit 0 — "unknown", not a crash).
  { grep -E "^[^A-Za-z0-9]*${key}:" "$file" || true; } \
    | tail -n1 \
    | sed -E "s/^[^A-Za-z0-9]*${key}:[[:space:]]*//" \
    | sed -E 's/^[^A-Za-z0-9]+//' \
    | sed -E 's/[`*_[:space:]]+$//'
  return 0
}

# ai_marker_or <key> <file> <default>
# Like ai_marker, but a missing/empty marker degrades to <default> (and warns to
# stderr, so a command-substitution still captures only the value). Use the SAFE
# default at each call site — i.e. the one that keeps a human in the loop.
ai_marker_or() {
  local v; v="$(ai_marker "$1" "$2")"
  if [ -z "$v" ]; then
    { log_warn "Marker '$1' missing/empty in $(basename "$2") — defaulting to '$3' (safe path)."; } >&2
    v="$3"
  fi
  printf '%s' "$v"
}

# ai_dispute_status <validation_file>
# POSITIVE-EVIDENCE dispute detection. A missing marker must NOT fabricate a
# dispute (observed false positive: Codex wrote `VERDICT: APPROVED` with no
# DISPUTE_STATUS line and the old OPEN-default stopped the run). A dispute is
# OPEN only with positive evidence: an explicit `DISPUTE_STATUS: OPEN` or
# named `UPHELD:` finding ids.
ai_dispute_status() {
  local f="$1" ds up
  ds="$(ai_marker DISPUTE_STATUS "$f")"
  up="$(ai_marker UPHELD "$f" | tr '[:upper:]' '[:lower:]' | tr -d ' ')"
  case "$up" in ""|none|"(none)"|-|–|n/a|nil|"(empty)") up="" ;; esac
  if [ -n "$up" ]; then
    if [ "$ds" != "OPEN" ]; then
      { log_warn "UPHELD findings present ($up) without 'DISPUTE_STATUS: OPEN' — treating as OPEN."; } >&2
    fi
    echo "OPEN"; return 0
  fi
  case "$ds" in
    OPEN) echo "OPEN" ;;
    NONE) echo "NONE" ;;
    *)
      { log_warn "No DISPUTE_STATUS / UPHELD markers in $(basename "$f") — no positive evidence of a dispute; treating as NONE."; } >&2
      echo "NONE" ;;
  esac
}

# _artifact_ok <file>
# Reject garbage agent outputs that the CLI returned with exit 0 — observed in
# the wild: a 15-byte file containing exactly `Execution error`, which then got
# reviewed as if it were the plan. Invalid when: missing/empty, tiny (<40
# bytes), or the first non-blank line is a known CLI failure signature.
_artifact_ok() {
  local f="$1" sz first
  [ -s "$f" ] || return 1
  sz="$(wc -c < "$f" | tr -d ' ')"
  [ "$sz" -ge 40 ] || return 1
  first="$(grep -m1 -vE '^[[:space:]]*$' "$f" 2>/dev/null || true)"
  case "$first" in
    "Execution error"*|"API Error"*|"Error:"*|"Request timed out"*|*"socket connection was closed"*)
      return 1 ;;
  esac
  return 0
}

# ---------------------------------------------------------------------------
# CLI invocation wrappers — stream full output to claude.log / codex.log and
# emit AGENT_INVOKED / AGENT_COMPLETED events for the dashboard.
# ---------------------------------------------------------------------------
# invoke_claude <run_id> <phase> <label> <out_file> <permission_mode> <prompt_file> [ctx...]
invoke_claude() {
  local run_id="$1" phase="$2" label="$3" out="$4" mode="$5" prompt_file="$6"; shift 6
  local clog prompt ctx tmp ctxlist=""
  clog="$(ai_run_dir "$run_id")/claude.log"
  mkdir -p "$(dirname "$clog")"   # self-heal if the run dir was removed mid-run
  prompt="$(cat "$prompt_file")"
  for ctx in "$@"; do
    [ -f "$ctx" ] || continue
    prompt+=$'\n\n=== CONTEXT: '"$(basename "$ctx")"$' ===\n'
    prompt+="$(cat "$ctx")"
    ctxlist+="$(basename "$ctx") "
  done

  {
    echo
    echo "════════ CLAUDE · $phase · $label · $(ai_now_utc) ════════"
    echo "prompt-template: $prompt_file"
    echo "context-files:   ${ctxlist:-(none)}"
    echo "output-artifact: $(basename "$out")"
    echo "permission-mode: $mode"
    echo "────────────────────────────────────────────────────────"
  } >> "$clog"
  ai_emit_event "$run_id" "Claude" "AGENT_INVOKED" "$phase" "$(basename "$out")" "Claude: $label"

  if [ "$AI_DRY_RUN" = "1" ]; then
    _dry_artifact "$out" "claude" "$mode" "$prompt_file"
    echo "[dry-run] produced $(basename "$out") (no model invoked)" >> "$clog"
    ai_emit_event "$run_id" "Claude" "AGENT_COMPLETED" "$phase" "$(basename "$out")" "Claude finished (dry-run): $label"
    return 0
  fi

  require_cmd "$CLAUDE_BIN"
  local model_args=() attempts="${AI_MAX_ATTEMPTS:-2}" attempt=1
  [ -n "${CLAUDE_MODEL:-}" ] && model_args=(--model "$CLAUDE_MODEL")
  tmp="${out}.partial"
  # Validate-and-retry: the CLI can exit 0 yet emit garbage ("Execution error").
  while :; do
    echo "[attempt $attempt/$attempts]" >> "$clog"
    # AI_ORCH_GUARD=1 arms the PreToolUse hook tripwire: pipeline-spawned
    # Claude can never commit/push/merge/deploy even in a relaxed permission
    # mode. ${arr[@]+…} guards empty-array expansion (set -u, bash 3.2).
    if AI_ORCH_GUARD=1 "$CLAUDE_BIN" -p "$prompt" \
          --permission-mode "$mode" \
          --output-format text \
          ${model_args[@]+"${model_args[@]}"} \
          > "$tmp" 2> >(sed 's/^/[claude] /' | tee -a "$clog" >&2); then
      cat "$tmp" >> "$clog"; mv "$tmp" "$out"
      if _artifact_ok "$out"; then
        break
      fi
      mv "$out" "${out}.bad${attempt}" 2>/dev/null || true
      log_warn "Claude returned an invalid artifact (attempt $attempt/$attempts) — kept as $(basename "$out").bad${attempt}"
      echo "[invalid artifact — saved as $(basename "$out").bad${attempt}]" >> "$clog"
    else
      cat "$tmp" >> "$clog" 2>/dev/null || true; rm -f "$tmp"
      log_warn "Claude CLI invocation failed (attempt $attempt/$attempts)"
      echo "[CLI failure on attempt $attempt]" >> "$clog"
    fi
    if [ "$attempt" -ge "$attempts" ]; then
      ai_emit_event "$run_id" "Claude" "AGENT_FAILED" "$phase" "$(basename "$out")" "Claude failed after $attempts attempt(s): $label"
      die "Claude failed after $attempts attempt(s) (see $clog)."
    fi
    ai_emit_event "$run_id" "Claude" "AGENT_RETRY" "$phase" "$(basename "$out")" "Retrying Claude: $label"
    attempt=$((attempt+1))
    sleep 3
  done
  ai_emit_event "$run_id" "Claude" "AGENT_COMPLETED" "$phase" "$(basename "$out")" "Claude finished: $label"
}

# invoke_codex <run_id> <phase> <label> <out_file> <prompt_file> [ctx...]
invoke_codex() {
  local run_id="$1" phase="$2" label="$3" out="$4" prompt_file="$5"; shift 5
  local cdlog prompt ctx ctxlist=""
  cdlog="$(ai_run_dir "$run_id")/codex.log"
  mkdir -p "$(dirname "$cdlog")"   # self-heal if the run dir was removed mid-run
  prompt="$(cat "$prompt_file")"
  for ctx in "$@"; do
    [ -f "$ctx" ] || continue
    prompt+=$'\n\n=== CONTEXT: '"$(basename "$ctx")"$' ===\n'
    prompt+="$(cat "$ctx")"
    ctxlist+="$(basename "$ctx") "
  done

  {
    echo
    echo "════════ CODEX · $phase · $label · $(ai_now_utc) ════════"
    echo "prompt-template: $prompt_file"
    echo "context-files:   ${ctxlist:-(none)}"
    echo "output-artifact: $(basename "$out")"
    echo "sandbox: read-only"
    echo "────────────────────────────────────────────────────────"
  } >> "$cdlog"
  ai_emit_event "$run_id" "Codex" "AGENT_INVOKED" "$phase" "$(basename "$out")" "Codex: $label"

  if [ "$AI_DRY_RUN" = "1" ]; then
    _dry_artifact "$out" "codex" "review" "$prompt_file"
    echo "[dry-run] produced $(basename "$out") (no model invoked)" >> "$cdlog"
    ai_emit_event "$run_id" "Codex" "AGENT_COMPLETED" "$phase" "$(basename "$out")" "Codex finished (dry-run): $label"
    return 0
  fi

  require_cmd "$CODEX_BIN"
  local model_args=() attempts="${AI_MAX_ATTEMPTS:-2}" attempt=1
  [ -n "${CODEX_MODEL:-}" ] && model_args=(--model "$CODEX_MODEL")
  # Validate-and-retry: the CLI can exit 0 yet emit garbage ("Execution error").
  while :; do
    echo "[attempt $attempt/$attempts]" >> "$cdlog"
    if "$CODEX_BIN" exec "$prompt" \
          --sandbox read-only \
          --skip-git-repo-check \
          -C "$(ai_repo_root)" \
          ${model_args[@]+"${model_args[@]}"} \
          -o "$out" \
          > >(sed 's/^/[codex] /' | tee -a "$cdlog") 2>&1; then
      if _artifact_ok "$out"; then
        { echo "──── produced artifact ($(basename "$out")) ────"; cat "$out"; } >> "$cdlog"
        break
      fi
      mv "$out" "${out}.bad${attempt}" 2>/dev/null || true
      log_warn "Codex returned an invalid artifact (attempt $attempt/$attempts) — kept as $(basename "$out").bad${attempt}"
      echo "[invalid artifact — saved as $(basename "$out").bad${attempt}]" >> "$cdlog"
    else
      log_warn "Codex CLI invocation failed (attempt $attempt/$attempts)"
      echo "[CLI failure on attempt $attempt]" >> "$cdlog"
    fi
    if [ "$attempt" -ge "$attempts" ]; then
      ai_emit_event "$run_id" "Codex" "AGENT_FAILED" "$phase" "$(basename "$out")" "Codex failed after $attempts attempt(s): $label"
      die "Codex failed after $attempts attempt(s) (see $cdlog)."
    fi
    ai_emit_event "$run_id" "Codex" "AGENT_RETRY" "$phase" "$(basename "$out")" "Retrying Codex: $label"
    attempt=$((attempt+1))
    sleep 3
  done
  ai_emit_event "$run_id" "Codex" "AGENT_COMPLETED" "$phase" "$(basename "$out")" "Codex finished: $label"
}

# Deterministic placeholder for AI_DRY_RUN — emits APPROVED so a dry run walks
# the happy path to READY_FOR_APPROVAL.
_dry_artifact() {
  local out="$1" agent="$2" mode="$3" prompt_file="$4"
  {
    echo "# [DRY RUN] $agent ($mode)"
    echo
    echo "_Generated with AI_DRY_RUN=1 — no model was invoked._"
    echo "_Prompt template: \`$prompt_file\`_"
    echo
    echo "This placeholder lets the orchestrator + state machine + dashboard be"
    echo "exercised without spending tokens. Replace by running with AI_DRY_RUN=0."
    echo
    echo "VERDICT: APPROVED"
    echo "EVALUATION_STATUS: ALL_RESOLVED"
    echo "DISPUTE_STATUS: NONE"
  } > "$out"
}

# ---------------------------------------------------------------------------
# Artifact mirroring — copy a per-run artifact to the canonical docs/ai-plans/
# filename so the latest run is browsable at a stable path.
# ai_mirror <run_id> <src-basename-in-run> <dest-basename-in-docs>
# ---------------------------------------------------------------------------
ai_mirror() {
  local run_id="$1" src="$2" dest="$3" run_dir docs
  run_dir="$(ai_run_dir "$run_id")"; docs="$(ai_plans_dir)"
  [ -f "$run_dir/$src" ] || { log_warn "mirror skipped (missing): $src"; return 0; }
  {
    echo "<!-- AUTO-MIRRORED from .ai/runs/$run_id/$src — do not edit here. -->"
    echo "<!-- Run: $run_id  •  Mirrored: $(ai_now_iso) -->"
    echo
    cat "$run_dir/$src"
  } > "$docs/$dest"
}

# ---------------------------------------------------------------------------
# Guard rails — these actions are NEVER taken by the orchestrator.
# ---------------------------------------------------------------------------
ai_assert_no_forbidden_action() {
  case "$1" in
    merge|push|deploy|release)
      die "Forbidden orchestrator action '$1'. User approval is required for merge/push/deploy/release." ;;
  esac
}
