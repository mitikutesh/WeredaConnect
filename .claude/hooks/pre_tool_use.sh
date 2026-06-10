#!/bin/bash
# PreToolUse hook — block dangerous operations.
# Receives JSON on stdin: { "tool_name": "...", "tool_input": { ... } }

INPUT=$(cat)
TOOL_NAME=$(echo "$INPUT" | jq -r '.tool_name // empty')
COMMAND=$(echo "$INPUT" | jq -r '.tool_input.command // empty')
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // empty')

# Only check Bash, Edit, and Write tools
if [[ "$TOOL_NAME" != "Bash" && "$TOOL_NAME" != "Edit" && "$TOOL_NAME" != "Write" ]]; then
  exit 0
fi

# --- Orchestrator guard (AI_ORCH_GUARD=1 is set by scripts/lib/ai-common.sh
# for pipeline-spawned Claude). The execution phase may run with relaxed
# permissions (the repo path's trailing space breaks acceptEdits), so this
# hook enforces the orchestrator's hard promise: NEVER commit/push/merge/
# deploy/release — the user ships. Interactive sessions are unaffected. ---
if [[ "${AI_ORCH_GUARD:-0}" == "1" && "$TOOL_NAME" == "Bash" && -n "$COMMAND" ]]; then
  if echo "$COMMAND" | grep -qE '(^|[;&|[:space:]])git[[:space:]]+(commit|push|merge)([[:space:]]|$)|gh[[:space:]]+pr[[:space:]]+(merge|create)|cdk[[:space:]]+deploy|scripts/deploy\.sh|npm[[:space:]]+publish'; then
    echo "BLOCKED (orchestrator guard): commit/push/merge/deploy/release are user-only actions. Leave changes in the working tree."
    exit 2
  fi
fi

# --- Bash guardrails ---
if [[ "$TOOL_NAME" == "Bash" && -n "$COMMAND" ]]; then
  # Block rm -rf on critical directories
  if echo "$COMMAND" | grep -qE 'rm\s+(-[a-zA-Z]*r[a-zA-Z]*f|--recursive)\s.*(mobile/|server/|infra/|\.ai/|specs/|\.claude/)'; then
    echo "BLOCKED: rm -rf on a critical directory (mobile/, server/, infra/, .ai/, specs/, .claude/)"
    exit 2
  fi

  # Block git push --force to main/master
  if echo "$COMMAND" | grep -qE 'git\s+push\s+.*--force.*\s+(main|master)'; then
    echo "BLOCKED: force push to main/master is not allowed"
    exit 2
  fi
  if echo "$COMMAND" | grep -qE 'git\s+push\s+.*\s+(main|master)\s+.*--force'; then
    echo "BLOCKED: force push to main/master is not allowed"
    exit 2
  fi
fi

# --- File write guardrails ---
if [[ "$TOOL_NAME" == "Edit" || "$TOOL_NAME" == "Write" ]]; then
  # Block writing to .env files (allow .env.example)
  if [[ "$FILE_PATH" =~ \.env$ ]] && [[ ! "$FILE_PATH" =~ \.env\.example$ ]]; then
    echo "BLOCKED: writing to .env files is not allowed (use .env.example instead)"
    exit 2
  fi
fi

exit 0
