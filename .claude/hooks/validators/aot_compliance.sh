#!/bin/bash
# PostToolUse hook — validate .cs files for AOT compliance.
# Only runs on C# files edited/written by Edit or Write tools.
# Receives JSON on stdin: { "tool_name": "...", "tool_input": { ... } }

INPUT=$(cat)
TOOL_NAME=$(echo "$INPUT" | jq -r '.tool_name // empty')
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // empty')
CONTENT=$(echo "$INPUT" | jq -r '.tool_input.content // empty')

# Only check Edit and Write tools on .cs files
if [[ "$TOOL_NAME" != "Edit" && "$TOOL_NAME" != "Write" ]]; then
  exit 0
fi

if [[ ! "$FILE_PATH" =~ \.cs$ ]]; then
  exit 0
fi

# For Write tool, check the content directly. For Edit, check the file on disk.
if [[ "$TOOL_NAME" == "Write" && -n "$CONTENT" ]]; then
  CHECK_TEXT="$CONTENT"
elif [[ -f "$FILE_PATH" ]]; then
  CHECK_TEXT=$(cat "$FILE_PATH")
else
  exit 0
fi

VIOLATIONS=""

# Check for Newtonsoft.Json
if echo "$CHECK_TEXT" | grep -qE '(using\s+Newtonsoft\.Json|Newtonsoft\.Json)'; then
  VIOLATIONS="${VIOLATIONS}\n- Newtonsoft.Json detected: use System.Text.Json with JsonSerializerContext source generators"
fi

# Check for Moq
if echo "$CHECK_TEXT" | grep -qE '(using\s+Moq|new\s+Mock<)'; then
  VIOLATIONS="${VIOLATIONS}\n- Moq detected: use NSubstitute instead (Moq uses reflection, breaks AOT)"
fi

# Check for Entity Framework
if echo "$CHECK_TEXT" | grep -qE '(using\s+Microsoft\.EntityFrameworkCore|DbContext|DbSet<)'; then
  VIOLATIONS="${VIOLATIONS}\n- Entity Framework Core detected: use AWSSDK.DynamoDBv2 with manual marshalling"
fi

# Check for reflection APIs
if echo "$CHECK_TEXT" | grep -qE '(Type\.GetType|Activator\.CreateInstance|Assembly\.Load|MethodInfo\.Invoke)'; then
  VIOLATIONS="${VIOLATIONS}\n- Reflection API detected: AOT does not support runtime reflection"
fi

# Check for AutoMapper
if echo "$CHECK_TEXT" | grep -qE '(using\s+AutoMapper|IMapper|CreateMap<)'; then
  VIOLATIONS="${VIOLATIONS}\n- AutoMapper detected: use manual mapping (AutoMapper uses reflection)"
fi

if [[ -n "$VIOLATIONS" ]]; then
  echo "AOT COMPLIANCE VIOLATION in $FILE_PATH:"
  echo -e "$VIOLATIONS"
  echo ""
  echo "See server/AOT_COMPLIANCE.md for approved alternatives."
  exit 2
fi

exit 0
