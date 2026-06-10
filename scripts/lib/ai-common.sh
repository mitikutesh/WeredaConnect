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
  [ -f "$file" ] || { echo ""; return; }
  grep -E "^[[:space:]]*${key}:[[:space:]]*" "$file" \
    | tail -n1 \
    | sed -E "s/^[[:space:]]*${key}:[[:space:]]*//" \
    | sed -E 's/[[:space:]]+$//'
}

# ---------------------------------------------------------------------------
# CLI invocation wrappers — stream full output to claude.log / codex.log and
# emit AGENT_INVOKED / AGENT_COMPLETED events for the dashboard.
# ---------------------------------------------------------------------------
# invoke_claude <run_id> <phase> <label> <out_file> <permission_mode> <prompt_file> [ctx...]
invoke_claude() {
  local run_id="$1" phase="$2" label="$3" out="$4" mode="$5" prompt_file="$6"; shift 6
  local clog prompt ctx tmp
  clog="$(ai_run_dir "$run_id")/claude.log"
  prompt="$(cat "$prompt_file")"
  for ctx in "$@"; do
    [ -f "$ctx" ] || continue
    prompt+=$'\n\n=== CONTEXT: '"$(basename "$ctx")"$' ===\n'
    prompt+="$(cat "$ctx")"
  done

  {
    echo
    echo "════════ CLAUDE · $phase · $label · $(ai_now_utc) ════════"
    echo "prompt-template: $prompt_file"
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
  local model_args=()
  [ -n "${CLAUDE_MODEL:-}" ] && model_args=(--model "$CLAUDE_MODEL")
  tmp="${out}.partial"
  # ${arr[@]+"${arr[@]}"} guards empty-array expansion under `set -u` on bash 3.2.
  if "$CLAUDE_BIN" -p "$prompt" \
        --permission-mode "$mode" \
        --output-format text \
        ${model_args[@]+"${model_args[@]}"} \
        > "$tmp" 2> >(sed 's/^/[claude] /' | tee -a "$clog" >&2); then
    cat "$tmp" >> "$clog"; mv "$tmp" "$out"
  else
    cat "$tmp" >> "$clog" 2>/dev/null || true; rm -f "$tmp"
    ai_emit_event "$run_id" "Claude" "AGENT_FAILED" "$phase" "$(basename "$out")" "Claude invocation failed"
    die "Claude invocation failed (see $clog)."
  fi
  ai_emit_event "$run_id" "Claude" "AGENT_COMPLETED" "$phase" "$(basename "$out")" "Claude finished: $label"
}

# invoke_codex <run_id> <phase> <label> <out_file> <prompt_file> [ctx...]
invoke_codex() {
  local run_id="$1" phase="$2" label="$3" out="$4" prompt_file="$5"; shift 5
  local cdlog prompt ctx
  cdlog="$(ai_run_dir "$run_id")/codex.log"
  prompt="$(cat "$prompt_file")"
  for ctx in "$@"; do
    [ -f "$ctx" ] || continue
    prompt+=$'\n\n=== CONTEXT: '"$(basename "$ctx")"$' ===\n'
    prompt+="$(cat "$ctx")"
  done

  {
    echo
    echo "════════ CODEX · $phase · $label · $(ai_now_utc) ════════"
    echo "prompt-template: $prompt_file"
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
  local model_args=()
  [ -n "${CODEX_MODEL:-}" ] && model_args=(--model "$CODEX_MODEL")
  if "$CODEX_BIN" exec "$prompt" \
        --sandbox read-only \
        --skip-git-repo-check \
        -C "$(ai_repo_root)" \
        ${model_args[@]+"${model_args[@]}"} \
        -o "$out" \
        > >(sed 's/^/[codex] /' | tee -a "$cdlog") 2>&1; then
    { echo "──── produced artifact ($(basename "$out")) ────"; cat "$out"; } >> "$cdlog"
  else
    ai_emit_event "$run_id" "Codex" "AGENT_FAILED" "$phase" "$(basename "$out")" "Codex invocation failed"
    die "Codex invocation failed (see $cdlog)."
  fi
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
