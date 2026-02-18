#!/bin/bash
# Auto-format files after Edit/Write tool calls.
# PostToolUse hooks receive JSON on stdin with tool_input.file_path.

INPUT=$(cat)
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // empty')

if [ -z "$FILE_PATH" ]; then
  exit 0
fi

if [[ "$FILE_PATH" =~ \.cs$ ]]; then
  dotnet format server/WeredaConnect.Server.sln --include "$FILE_PATH" 2>/dev/null
elif [[ "$FILE_PATH" =~ \.dart$ ]]; then
  dart format "$FILE_PATH" 2>/dev/null
fi

exit 0
