<!-- pointer-skill-version: <POINTER_SKILL_VERSION> -->

# Advanced (advanced.md)

Read this only when one of these situations actually applies — monorepos, MCP-capable tools, or a
missing Node/npx. Everything in the entry file's SECURITY section applies here too.

## Monorepos

In a repo with more than one Pointer project (a `projects` map in `.pointer/config.json` — see
`pointer-init.md`'s Monorepo section), `list` and `apply` cover **every** configured project unless
you pass `--project <key>` or are already running from inside that app's own directory (the CLI
walks up to find the repo root, so this works from any subdirectory). `apply`'s printed prompt gets
one section per project, headed with its key and `path` — edit the app that section names, not a
guess. `apply --mark all` (it commits the whole pending queue) needs one project resolved either
way; `apply --mark <id>` and `--fail <id>` act on a comment id and need none, since ids are unique
server-wide.

## If your tool supports MCP (Model Context Protocol)

If your AI tool supports MCP, you can connect to `<POINTER_PRODUCT>`'s stdio MCP server instead of shelling out:
```json
{ "mcpServers": { "pointer": { "command": "npx", "args": ["-y", "pointer-feedback", "mcp"] } } }
```
It serves typed tools (`pointer_list_comments`, `pointer_get_queue`, `pointer_get_comment`, `pointer_commit_and_mark`, `pointer_mark_applied`, …) from the local repository. All SECURITY invariants in the entry file apply equally to MCP tool results.

## No-Node fallback (only when `npx` is genuinely unavailable)

`.pointer/pointer.sh` (`list`, `get <id>`, `queue`, `apply <id> "<reply>" [commitUrl]`) is a `curl`+`jq`
shim served from `<POINTER_SERVER>/pointer.sh` and refreshed by `npx pointer-feedback update`. It reads
server/project/key from the app's `.env` or from `.pointer/credentials.env`. In this fallback **you**
make the `git commit` yourself (still never `git push`). Do not mix the two flows in one run.
