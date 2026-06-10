#!/usr/bin/env bash
# scripts/ai-dashboard.sh
# ===========================================================================
# Agent Control Room — a tmux dashboard for the Claude <-> Codex pipeline.
#
# Lays out four panes:
#   ┌──────────────────────────────┬──────────────────────────────┐
#   │ Orchestrator                 │ Live Status                  │
#   │ (workflow controller / state)│ (agent · phase · transfers)  │
#   ├──────────────────────────────┼──────────────────────────────┤
#   │ Claude Output (full log)     │ Codex Output (full log)      │
#   └──────────────────────────────┴──────────────────────────────┘
#
# USAGE:
#   scripts/ai-dashboard.sh "Implement feature X"   # start a task + watch it
#   scripts/ai-dashboard.sh                          # monitor the latest run
#   scripts/ai-dashboard.sh --monitor                # explicit monitor mode
#   scripts/ai-dashboard.sh --status-view            # (internal) live renderer
#
# The dashboard is a *viewer*. When given a task it prepares a run and launches
# the orchestrator in the Orchestrator pane; otherwise it attaches to the latest
# run read-only. It never merges/pushes/deploys.
# ===========================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/ai-common.sh
source "$SCRIPT_DIR/lib/ai-common.sh"

SESSION="ai-control"

# ---------------------------------------------------------------------------
# Live status renderer (runs inside the "Live Status" pane).
# Set AI_DASH_ONCE=1 to render a single frame and exit (for tests/CI).
# ---------------------------------------------------------------------------
status_view() {
  local cs root once
  root="$(ai_repo_root)"
  cs="$(ai_cs_file)"
  once="${AI_DASH_ONCE:-0}"

  while true; do
    clear 2>/dev/null || printf '\033c'
    if [ ! -f "$cs" ]; then
      echo "  No active run."
      echo "  Start one with:  scripts/ai-orchestrator.sh \"<task>\""
    else
      local run_id task state phase agent xfer decision status disp upd run_dir
      run_id="$(jq -r '.run_id // "—"' "$cs")"
      task="$(jq -r '.task // "—"' "$cs" | cut -c1-60)"
      state="$(jq -r '.state // "—"' "$cs")"
      phase="$(jq -r '.phase // "—"' "$cs")"
      agent="$(jq -r '.active_agent // "—"' "$cs")"
      xfer="$(jq -r '.last_transfer // "None"' "$cs")"
      decision="$(jq -r '.latest_decision // "None"' "$cs")"
      status="$(jq -r '.latest_status // "—"' "$cs")"
      disp="$(jq -r '.dispute_open // "false"' "$cs")"
      upd="$(jq -r '.updated_at // "—"' "$cs")"
      run_dir="$(jq -r '.run_dir // ""' "$cs")"

      printf "  \033[1;36m🛰  AGENT CONTROL ROOM\033[0m\n"
      printf "  ────────────────────────────────────────────\n"
      printf "  Run            : %s\n" "$run_id"
      printf "  Task           : %s\n" "$task"
      printf "  ────────────────────────────────────────────\n"
      printf "  Current Agent  : \033[1;33m%s\033[0m\n" "$agent"
      printf "  Current Phase  : %s\n" "$phase"
      printf "  Current State  : \033[1;32m%s\033[0m\n" "$state"
      printf "  Last Transfer  : %s\n" "$xfer"
      printf "  Latest Decision: %s\n" "$decision"
      printf "  Latest Status  : %s\n" "$status"
      printf "  Updated        : %s\n" "$upd"

      if [ "$disp" = "true" ]; then
        printf "\n  \033[1;31m⚖  DISPUTE OPEN — USER DECISION REQUIRED\033[0m\n"
        printf "  \033[1;31m   Resolve docs/ai-plans/disputes.md, then:\033[0m\n"
        printf "  \033[1;31m   scripts/ai-orchestrator.sh --resume %s\033[0m\n" "$run_id"
      fi

      local ef="$root/$run_dir/events.jsonl"
      if [ -n "$run_dir" ] && [ -f "$ef" ]; then
        printf "\n  Recent events:\n"
        tail -n 8 "$ef" 2>/dev/null | jq -r '"   \(.timestamp[11:19])  \((.agent + "      ")[0:6])  \(.event)  ·  \(.summary)"' 2>/dev/null \
          | cut -c1-72
      fi
    fi
    [ "$once" = "1" ] && break
    sleep 2
  done
}

# ---------------------------------------------------------------------------
# tmux helpers
# ---------------------------------------------------------------------------
send() { tmux send-keys -t "$SESSION:control.$1" "$2" C-m; }

build_dashboard() {
  local task="${1:-}" root run_id orch
  root="$(ai_repo_root)"
  orch="$SCRIPT_DIR/ai-orchestrator.sh"

  command -v tmux >/dev/null 2>&1 || {
    log_warn "tmux is not installed — cannot render the dashboard."
    log_info "Install tmux, or run the orchestrator directly:"
    log_info "  scripts/ai-orchestrator.sh \"<task>\""
    log_info "  scripts/ai-orchestrator.sh --events <run_id>   # text event stream"
    exit 1
  }

  # Decide the target run.
  if [ -n "$task" ]; then
    log_step "Preparing run for task: $task"
    run_id="$("$orch" --prepare "$task" | tail -n1)"
    [ -n "$run_id" ] || die "Failed to prepare a run."
    log_ok "Run prepared: $run_id"
  else
    if [ ! -e "$(ai_latest_link)" ]; then
      log_warn "No active/last run to monitor."
      log_info "Start one with:  scripts/ai-dashboard.sh \"<task>\""
      exit 1
    fi
    run_id="$(basename "$(readlink "$(ai_latest_link)" 2>/dev/null || echo '')")"
    log_step "Monitoring latest run: ${run_id:-latest}"
  fi

  # (Re)create the session.
  tmux has-session -t "$SESSION" 2>/dev/null && tmux kill-session -t "$SESSION"
  tmux new-session -d -s "$SESSION" -n control -c "$root"
  tmux set-option -t "$SESSION" pane-border-status top >/dev/null 2>&1 || true
  tmux set-option -t "$SESSION" pane-border-format " #{pane_title} " >/dev/null 2>&1 || true

  # 2x2 grid: 0=TL 1=TR 2=BL 3=BR
  tmux split-window -h -t "$SESSION:control"
  tmux split-window -v -t "$SESSION:control.0"
  tmux split-window -v -t "$SESSION:control.1"
  tmux select-layout -t "$SESSION:control" tiled >/dev/null

  tmux select-pane -t "$SESSION:control.0" -T "Orchestrator"  >/dev/null 2>&1 || true
  tmux select-pane -t "$SESSION:control.1" -T "Live Status"   >/dev/null 2>&1 || true
  tmux select-pane -t "$SESSION:control.2" -T "Claude Output" >/dev/null 2>&1 || true
  tmux select-pane -t "$SESSION:control.3" -T "Codex Output"  >/dev/null 2>&1 || true

  local L="$root/.ai/runs/latest"

  # Orchestrator pane
  if [ -n "$task" ]; then
    send 0 "cd '$root' && '$orch' --resume '$run_id'; echo; echo '── orchestrator finished (state above) — this pane is interactive now ──'"
  else
    send 0 "cd '$root' && echo 'Monitoring $run_id — workflow controller log (status.log):' && tail -n +1 -F '$L/status.log'"
  fi
  # Live Status pane
  send 1 "cd '$root' && '$SCRIPT_DIR/ai-dashboard.sh' --status-view"
  # Claude / Codex full logs
  send 2 "cd '$root' && tail -n +1 -F '$L/claude.log'"
  send 3 "cd '$root' && tail -n +1 -F '$L/codex.log'"

  tmux select-pane -t "$SESSION:control.0" >/dev/null 2>&1 || true

  log_ok "Dashboard ready (session: $SESSION)."
  # AI_DASH_NO_ATTACH=1 leaves the session running but does not attach
  # (used by tests/CI to validate the layout without a TTY).
  if [ "${AI_DASH_NO_ATTACH:-0}" = "1" ]; then
    log_info "AI_DASH_NO_ATTACH=1 — session left detached. Attach with: tmux attach -t $SESSION"
    return 0
  fi
  if [ -n "${TMUX:-}" ]; then
    tmux switch-client -t "$SESSION"
  else
    tmux attach-session -t "$SESSION"
  fi
}

# ---------------------------------------------------------------------------
# Entry
# ---------------------------------------------------------------------------
case "${1:-}" in
  -h|--help)
    sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    exit 0 ;;
  --status-view)
    status_view ;;
  --monitor)
    build_dashboard "" ;;
  "")
    build_dashboard "" ;;
  *)
    build_dashboard "$*" ;;
esac
