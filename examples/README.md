# Examples

## Execute a workflow from the command line

The file in this directory is a small, editable workflow demonstrating the
version 2 schema:

```powershell
FreshStart.exe --execute=.\examples\safe-privacy.json --yes
```

The command is non-interactive and requires `--yes` or `--silent`. Review every
step before execution. FreshStart validates the plan and reports skipped,
failed, and rollback information in an HTML/JSON report under
`%AppData%\FreshStart\reports`.
