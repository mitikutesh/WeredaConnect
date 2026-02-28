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
