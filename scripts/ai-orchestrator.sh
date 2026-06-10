#!/usr/bin/env bash
# scripts/ai-orchestrator.sh
# ===========================================================================
# Claude <-> Codex multi-agent development orchestrator for WeredaConnect.
#
#   Claude = Lead Planner + Lead Executor
#   Codex  = Independent Reviewer + Validator
#   User   = Final authority on unresolved disputes
#
# Pipeline (see .ai/orchestrator/state-machine.md):
#   TASK_CREATED
#     -> PLAN_DRAFTING -> PLAN_SELF_REVIEW -> PLAN_REVIEW_BY_CODEX
#        -> PLAN_REVISION -> PLAN_APPROVED                         (plan-phase)
#     -> IMPLEMENTATION_IN_PROGRESS -> IMPLEMENTATION_SELF_REVIEW  (execution-phase)
#     -> CODE_REVIEW_BY_CODEX -> REVIEW_RESOLUTION
#        -> READY_FOR_APPROVAL                                     (review-phase)
#   Any unresolved disagreement -> DISPUTE_OPEN -> USER_DECISION_REQUIRED (stop)
#   User marks COMPLETED after approving;  hard errors -> FAILED.
#
# This orchestrator NEVER merges, pushes, deploys, or releases. It stops at
# READY_FOR_APPROVAL and hands control back to the user.
#
# All activity is observable in real time via:  scripts/ai-dashboard.sh
#
# Exit codes: 0 ok/finished · 2 usage error · 3 stopped for user decision
# ===========================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/ai-common.sh
source "$SCRIPT_DIR/lib/ai-common.sh"

help() {
  cat <<'EOF'
ai-orchestrator.sh — Claude <-> Codex development orchestrator

USAGE:
  ai-orchestrator.sh "<task>"            Run the full pipeline for a task
  ai-orchestrator.sh --task-file <path>  Read the task from a file
  ai-orchestrator.sh --plan-only "<task>"  Stop after the plan is approved
  ai-orchestrator.sh --prepare "<task>"  Create a run (TASK_CREATED) without
                                         running phases; prints the run id.
                                         (used by ai-dashboard.sh)
  ai-orchestrator.sh --resume <run_id>   Continue / re-run a stopped run
  ai-orchestrator.sh --complete <run_id> Mark a READY_FOR_APPROVAL run COMPLETED
  ai-orchestrator.sh --status <run_id>   Print a run's state + artifacts
  ai-orchestrator.sh --events <run_id>   Pretty-print the run's event stream
  ai-orchestrator.sh --list              List all runs and their states
  ai-orchestrator.sh --dry-run "<task>"  Walk the pipeline without any model
  -h | --help                            This help

ENV: CLAUDE_BIN CODEX_BIN CLAUDE_MODEL CODEX_MODEL AI_DRY_RUN
EOF
}

# --- Arg parsing -----------------------------------------------------------
MODE="run"          # run | resume | status | list | prepare | complete | events
PLAN_ONLY=0
TASK=""
TASK_FILE=""
TARGET_RUN=""

while [ $# -gt 0 ]; do
  case "$1" in
    -h|--help)      help; exit 0 ;;
    --dry-run)      export AI_DRY_RUN=1; shift ;;
    --plan-only)    PLAN_ONLY=1; shift ;;
    --task-file)    TASK_FILE="${2:?--task-file needs a path}"; shift 2 ;;
    --prepare)      MODE="prepare"; TASK="${2:?--prepare needs a task}"; shift 2 ;;
    --resume)       MODE="resume"; TARGET_RUN="${2:?--resume needs a run id}"; shift 2 ;;
    --complete)     MODE="complete"; TARGET_RUN="${2:?--complete needs a run id}"; shift 2 ;;
    --status)       MODE="status"; TARGET_RUN="${2:?--status needs a run id}"; shift 2 ;;
    --events)       MODE="events"; TARGET_RUN="${2:?--events needs a run id}"; shift 2 ;;
    --list)         MODE="list"; shift ;;
    --)             shift; TASK="$*"; break ;;
    -*)             die "Unknown option: $1 (try --help)" ;;
    *)              TASK="$*"; break ;;
  esac
done

require_cmd git
require_cmd jq

# --- Read-only sub-commands ------------------------------------------------
if [ "$MODE" = "list" ]; then
  log_step "AI orchestration runs"
  shopt -s nullglob
  for d in "$(ai_runs_dir)"/*/; do
    rid="$(basename "$d")"
    [ "$rid" = "latest" ] && continue
    [ -f "$d/state.json" ] || continue
    printf "  %-46s %-26s %s\n" "$rid" "$(jq -r '.state' "$d/state.json")" "$(jq -r '.phase // ""' "$d/state.json")"
  done
  exit 0
fi

if [ "$MODE" = "status" ]; then
  sf="$(ai_state_file "$TARGET_RUN")"
  [ -f "$sf" ] || die "No such run: $TARGET_RUN"
  log_step "Run $TARGET_RUN"
  jq '{run_id, state, phase, active_agent, dispute_open, created_at, updated_at, task}' "$sf"
  echo
  log_info "Artifacts:"
  ls -1 "$(ai_run_dir "$TARGET_RUN")" | sed 's/^/    /'
  exit 0
fi

if [ "$MODE" = "events" ]; then
  ef="$(ai_events_file "$TARGET_RUN")"
  [ -f "$ef" ] || die "No events for run: $TARGET_RUN"
  log_step "Event stream — $TARGET_RUN"
  jq -r '"  \(.timestamp)  \(.agent | (. + "          ")[0:11])  \(.event | (. + "                  ")[0:18])  \(.summary)"' "$ef"
  exit 0
fi

if [ "$MODE" = "complete" ]; then
  sf="$(ai_state_file "$TARGET_RUN")"
  [ -f "$sf" ] || die "No such run: $TARGET_RUN"
  RUN_ID="$TARGET_RUN"
  st="$(ai_state_get "$RUN_ID")"
  if [ "$st" != "READY_FOR_APPROVAL" ]; then
    log_warn "Run is in state '$st', not READY_FOR_APPROVAL. Marking COMPLETED anyway (user action)."
  fi
  ai_state_set "$RUN_ID" "COMPLETED"
  ai_status "$RUN_ID" "User marked run COMPLETED"
  log_ok "Run $RUN_ID marked COMPLETED."
  exit 0
fi

# ===========================================================================
# Run creation + pipeline driver
# ===========================================================================

# create_run "<task>"  — sets RUN_ID/RUN_DIR and primes all observability state.
create_run() {
  local task="$1"
  RUN_ID="$(ai_new_run_id "$task")"
  RUN_DIR="$(ai_run_dir "$RUN_ID")"
  mkdir -p "$RUN_DIR"

  {
    echo "# Task — run $RUN_ID"
    echo
    echo "- **Created:** $(ai_now_iso)"
    echo "- **Branch:** $(git rev-parse --abbrev-ref HEAD)"
    echo "- **Base ref:** $(git rev-parse HEAD)"
    echo
    echo "## Description"
    echo
    echo "$task"
  } > "$RUN_DIR/task.md"

  ai_state_init "$RUN_ID" "$task"
  ai_init_logs "$RUN_ID"
  ai_artifacts_init
  ai_set_latest "$RUN_ID"
  ai_cs_init "$RUN_ID" "$task"
  ai_register_artifact "$RUN_ID" "task.md" "User" "Intake" ".ai/runs/$RUN_ID/task.md"
  ai_status "$RUN_ID" "Task created"
  ai_emit_event "$RUN_ID" "User" "TASK_CREATED" "Intake" ".ai/runs/$RUN_ID/task.md" "$task"
}

run_phase() {
  local script="$1"; shift
  set +e
  "$SCRIPT_DIR/$script" "$@"
  local rc=$?
  set -e
  if [ "$rc" -eq 3 ]; then
    print_stop_banner
    exit 3
  elif [ "$rc" -ne 0 ]; then
    ai_mark_failed "$RUN_ID" "phase $script failed (exit $rc)"
    die "Phase failed: $script (exit $rc)"
  fi
}

print_stop_banner() {
  echo
  log_warn "════════════════════════════════════════════════════════════"
  log_warn " WORKFLOW STOPPED — USER DECISION REQUIRED"
  log_warn " A Claude ↔ Codex disagreement could not be auto-resolved."
  log_warn " Review:  docs/ai-plans/disputes.md"
  log_warn "          .ai/runs/$RUN_ID/disputes.md"
  log_warn " Resume:  scripts/ai-orchestrator.sh --resume $RUN_ID"
  log_warn "════════════════════════════════════════════════════════════"
}

drive_from_state() {
  local state
  state="$(ai_state_get "$RUN_ID")"
  log_info "Current state: $state"

  case "$state" in
    DISPUTE_OPEN|USER_DECISION_REQUIRED)
      die "Run $RUN_ID has an OPEN dispute. Resolve docs/ai-plans/disputes.md, then --resume." ;;
  esac

  # --- Plan phase (until PLAN_APPROVED) ---
  case "$state" in
    TASK_CREATED|PLAN_DRAFTING|PLAN_SELF_REVIEW|PLAN_REVIEW_BY_CODEX|PLAN_REVISION)
      run_phase plan-phase.sh "$RUN_ID"
      state="$(ai_state_get "$RUN_ID")" ;;
  esac

  if [ "$PLAN_ONLY" = "1" ] && [ "$state" = "PLAN_APPROVED" ]; then
    log_ok "Plan approved. --plan-only set; stopping before execution."
    finalize_summary
    return 0
  fi

  # --- Execution phase ---
  case "$state" in
    PLAN_APPROVED|IMPLEMENTATION_IN_PROGRESS)
      run_phase execution-phase.sh "$RUN_ID"
      state="$(ai_state_get "$RUN_ID")" ;;
  esac

  # --- Review phase ---
  case "$state" in
    IMPLEMENTATION_SELF_REVIEW|CODE_REVIEW_BY_CODEX|REVIEW_RESOLUTION)
      run_phase review-phase.sh "$RUN_ID"
      state="$(ai_state_get "$RUN_ID")" ;;
  esac

  finalize_summary
}

finalize_summary() {
  local state run_dir
  state="$(ai_state_get "$RUN_ID")"
  run_dir="$(ai_run_dir "$RUN_ID")"
  echo
  log_step "FINAL SUMMARY — run $RUN_ID"
  log_info "Final state : $state"
  log_info "Task        : $(jq -r '.task' "$(ai_state_file "$RUN_ID")")"
  log_info "Run dir     : .ai/runs/$RUN_ID/  (alias: .ai/runs/latest/)"
  log_info "Plans (live): docs/ai-plans/"
  log_info "Live view   : scripts/ai-dashboard.sh        (or --events $RUN_ID)"
  echo
  if [ "$state" = "READY_FOR_APPROVAL" ]; then
    log_ok "Pipeline complete and READY FOR APPROVAL."
    log_warn "Reminder: the orchestrator does NOT merge / push / deploy / release."
    log_info "Next (manual): review the diff, then commit/PR/merge yourself."
    log_info "  git -C \"\$(git rev-parse --show-toplevel)\" diff $(cat "$run_dir/base-ref.txt" 2>/dev/null || echo HEAD)"
    log_info "  scripts/ai-orchestrator.sh --complete $RUN_ID   # to mark done"
  elif [ "$state" = "PLAN_APPROVED" ]; then
    log_ok "Plan is approved. Run without --plan-only (or --resume) to execute."
  fi
}

# ===========================================================================
# Entry: prepare | resume | fresh run
# ===========================================================================
if [ "$MODE" = "prepare" ]; then
  create_run "$TASK"
  log_step "AI ORCHESTRATOR — prepared run (no phases run)"
  log_info "Run id : $RUN_ID"
  # Emit the run id on stdout (last line) for capture by ai-dashboard.sh.
  echo "$RUN_ID"
  exit 0
fi

if [ "$MODE" = "resume" ]; then
  RUN_ID="$TARGET_RUN"
  [ -f "$(ai_state_file "$RUN_ID")" ] || die "No such run: $RUN_ID"
  ai_set_latest "$RUN_ID"
  log_step "Resuming run $RUN_ID"

  if [ "$(ai_state_field_get "$RUN_ID" dispute_open 2>/dev/null)" = "true" ] \
     || [ "$(ai_state_get "$RUN_ID")" = "USER_DECISION_REQUIRED" ]; then
    resume_from="$(ai_state_field_get "$RUN_ID" resume_from)"
    log_warn "Dispute was open. Assuming the user recorded a decision in disputes.md."
    log_info "Clearing dispute and re-running the '$resume_from' phase."
    ai_status "$RUN_ID" "User resolved dispute; resuming '$resume_from' phase"
    if [ "$resume_from" = "CODE" ]; then
      ai_state_set "$RUN_ID" "IMPLEMENTATION_SELF_REVIEW" "false"
    else
      ai_state_set "$RUN_ID" "PLAN_DRAFTING" "false"
    fi
  fi
  drive_from_state
  exit 0
fi

# --- Fresh run -------------------------------------------------------------
if [ -n "$TASK_FILE" ]; then
  [ -f "$TASK_FILE" ] || die "Task file not found: $TASK_FILE"
  TASK="$(cat "$TASK_FILE")"
fi
[ -n "$TASK" ] || { help; die "No task provided."; }

create_run "$TASK"

log_step "AI ORCHESTRATOR — new run"
log_info "Run id : $RUN_ID"
log_info "Task   : $TASK"
log_info "Watch  : scripts/ai-dashboard.sh   (live control room)"
[ "$AI_DRY_RUN" = "1" ] && log_warn "DRY RUN: models will not be invoked (placeholder artifacts)."
echo

drive_from_state
