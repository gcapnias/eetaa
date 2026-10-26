# Issue tracker: beads_rust

Issues and Specs for this repository live in the local beads_rust tracker, managed with the `br` CLI.

## Rules

- **Never edit `.beads/` files directly** — always use the `br` CLI; direct edits corrupt the SQLite database.
- **Never run bare `bv`** — it opens an interactive TUI and blocks the session; always use `--robot-*` flags (`bv --robot-next`, `bv --robot-triage`, etc.).
- **Always use welformed markdown** for descriptions.

## Setup

```bash
# Verify br is installed
br version

# Initialise the workspace (idempotent — safe to re-run)
br init
```

Issue IDs use the `{prfx}` prefix (e.g. `{prfx}-1`), set via `br config set issue_prefix {prfx}`.
The canonical key is `issue_prefix` (aliases: `issue-prefix`, `prefix`) — confirm with
`br config schema`. There is no `id.prefix` key; setting one writes config `br` ignores.

## Conventions

- **Resolve the actor at runtime**: `ACTOR="${BR_ACTOR:-assistant}"` and pass `--actor "$ACTOR"` to mutating commands.
- **Use structured output**: prefer `--json` for reads, lists, and automation; use `--format toon` when token budget is tight.
- **Create an issue**: `br create --actor "$ACTOR" --title "$title" --description "$description" --type <task|bug|feature|epic|question|docs> --priority <0=critical|1=high|2=medium|3=low|4=backlog> --labels backend,auth` (default priority: 2)
- **Quick capture**: `br q --actor "$ACTOR" "Quick note"` — fast capture, outputs ID only
- **Read an issue**: `br show {issue-id} --json`; fetch discussion with `br comments list {issue-id} --json`
- **List issues**: `br list --json` with filters as needed; use `br ready --json` for actionable work and `br blocked --json` for blocked work
- **Comment on an issue**: write the comment text to a file first, then `br comments add --actor "$ACTOR" {issue-id} --file {path} --json`. **Never** pass multi-line or markdown-bearing text via `--message`/`-m` with an inline heredoc (e.g. `--message "$(cat <<'EOF' ... EOF)"`) — a literal `"` anywhere in the content silently truncates/corrupts everything from that character onward with no error, since the heredoc's captured output re-enters shell quoting when substituted into the double-quoted `--message` argument. `--message` is fine only for single-line text with no quote characters.
- **Update an issue**: `br update --actor "$ACTOR" {issue-id} --status <open|in_progress|closed> --priority 1 --add-label triage-reviewed --claim --json` (`--claim` is shorthand for claim-and-start)
- **Apply / remove labels**: `br label add {issue-id} label-a label-b` / `br label remove {issue-id} label-a`
- **Close / reopen**: `br close --actor "$ACTOR" {issue-id} --reason "$reason"` / `br reopen --actor "$ACTOR" {issue-id}`
- **Manage dependencies**: `br dep add {child-id} {parent-id}` and verify graph health with `br dep cycles --json` (it must return empty)
- **Sync is explicit**: `br sync --flush-only && git add .beads/` — always run before any git commit that touches bead state; `br` never runs git commands for you
- **Triage label vocabulary** is defined in `triage-labels.md`

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repository treats external PRs as feature requests; `/triage` reads this flag.)_

Note: Any code-hosting PR workflow is separate and should only be linked from the relevant bead via comments or external references.

## When a skill says "publish to the issue tracker"

Create a new bead with `br create --actor "$ACTOR" --title "$title" --description "$description"`, including the appropriate type, priority, labels, and dependencies.

## When a skill says "fetch the relevant ticket"

Run `br show {issue-id} --json` and, if the discussion matters, `br comments list {issue-id} --json`.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single Beads issue with **child** issues as tickets.

- **Map**: an `epic` labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body. `br create --actor "$ACTOR" --title "$title" --description-file {path} --type epic --labels wayfinder:map --json`. Create it unassigned: assignment is the claim signal.
- **Child ticket**: create an issue with `--parent {map-id}` (which creates Beads' native parent-child dependency) and label it `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Do not apply `needs-triage`: wayfinder tickets are triaged by construction. Create it unassigned; once claimed, Beads assigns it to the driving actor.
- **Blocking**: Beads' native `blocks` dependency is the canonical representation. `br dep add {blocked-ticket-id} {blocker-id} --type blocks --actor "$ACTOR" --json` reads as "the blocked ticket depends on the blocker." A ticket is unblocked when every blocker is closed.
- **Frontier query**: `br ready --parent {map-id} --unassigned --sort oldest --json`. It returns open, unblocked, unassigned children; take the first result. Use `--recursive` if the map contains nested child groups.
- **Claim**: `br update --actor "$ACTOR" {ticket-id} --claim --json` - the session's first write. It atomically assigns the ticket to the actor and starts it.
- **Resolve**: write the answer to a file, then `br comments add --actor "$ACTOR" {ticket-id} --file {path} --json`; close it with `br close --actor "$ACTOR" {ticket-id} --reason "$reason" --json`; then append a context pointer (gist + link) to the map's Decisions-so-far using `br update --actor "$ACTOR" {map-id} --description-file {path} --json`. Findings and prototypes stay as files in the repository and are linked from the ticket, not pasted into it.
