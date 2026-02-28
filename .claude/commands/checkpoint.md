Run a session checkpoint to prepare for handoff.

Steps:
1. Run `bash scripts/checkpoint.sh` to auto-generate STATE.md from current git state
2. Show the user the updated STATE.md content
3. If there are uncommitted changes, list them and ask the user if they want to commit before ending the session
4. Summarize what was accomplished this session and what's next based on TASK.md
