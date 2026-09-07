# Issue tracker: Azure DevOps

Issues and PRDs for this repo live as Azure DevOps work items. Use the Azure CLI's `azure-devops` extension (`boards` / `repos` command groups) for all operations.

**Invoke it through the wrapper for your shell**: `az-cli` on PowerShell/Windows, `az` on Bash. The examples below are written `az-cli`; substitute the one your shell actually has, and check with `Get-Command az-cli` / `command -v az` before assuming — a wrapper that isn't installed fails identically to a wrong command name.

## Setup

```powershell
# Install extension if not present
az-cli extension add --name azure-devops

# Authenticate
az-cli devops login --organization https://dev.azure.com/{org}
```

**Do not set machine-level defaults.** `az-cli devops configure --defaults organization=... project=...` looks like a convenience but is a footgun: any pre-existing default silently redirects writes to another project, and the failure is invisible — the command succeeds and the work item appears somewhere else. Pass `--org` explicitly on every command, and `--project` on the two commands that accept it (see below).

## Conventions

- Use PowerShell variables for work item title/description/comment to avoid quoting issues and command line length limits.
- Work item types depend on the project's process template — **Basic** template: `Issue` (features/PRDs), `Task` (implementation sub-tasks); **Agile** template: `User Story`, `Bug`, `Task`
- Triage state is tracked via **Tags** (see `triage-labels.md` for the role strings)
- Comments append to the work item's discussion thread via `--discussion`
- **Create a work item**: `az-cli boards work-item create --title $title --type "Issue" --org https://dev.azure.com/{org} --project {project}`
- **Create a task**: `az-cli boards work-item create --title $title --type "Task" --org https://dev.azure.com/{org} --project {project}`
- **Create a work item with description**: add `--description $description` to either of the above
- **Read a work item**: `az-cli boards work-item show --id {work-item-id} --org https://dev.azure.com/{org}`
- **List open work items**: `az-cli boards query --wiql "SELECT [System.Id], [System.Title], [System.State], [System.Tags] FROM WorkItems WHERE [System.State] <> 'Closed' ORDER BY [System.ChangedDate] DESC" --org https://dev.azure.com/{org} --project {project}`
- **Add a comment**: `az-cli boards work-item update --id {work-item-id} --discussion $comment --org https://dev.azure.com/{org}`
- **Apply a triage tag**: `az-cli boards work-item update --id {work-item-id} --fields "Tags=needs-triage" --org https://dev.azure.com/{org}`
- **Apply multiple tags**: `az-cli boards work-item update --id {work-item-id} --fields "Tags=ready-for-agent; needs-info" --org https://dev.azure.com/{org}`
- **Resolve** — **the terminal state depends on the work item type.** Under the Basic process an `Issue` closes to `Closed`, but a `Task` closes to **`Done`**; `--state Closed` on a Task fails with `The field 'State' contains the value 'Closed' that is not in the list of supported values`. Basic Task states are `To Do` / `Doing` / `Done`. Anything testing for "finished" must accept **both** `Done` and `Closed` (and `Removed`), or resolved tickets keep reading as open.
  - Close an Issue: `az-cli boards work-item update --id {id} --state Closed --org https://dev.azure.com/{org}`
  - Finish a Task: `az-cli boards work-item update --id {id} --state Done --org https://dev.azure.com/{org}`

### Parameter traps

Four places where this CLI's flags do not mean what they look like. Each one fails quietly or with an unhelpful usage footer rather than a clear error.

- **`--project` exists on `create` and `delete` only.** It is *not* a parameter of `show` or `update` — that is the documented signature, not a quirk. Passing it prints a bare usage footer with no explanatory line, which reads like a malformed command. A work item id is globally unique, so reads and updates don't need it.
- **`--fields` has two opposite grammars.** On `show` it is a **projection**: a comma-separated list of field *names* (`--fields System.Id,System.State`). On `create` / `update` it is an **assignment**: space-separated `"field=value"` pairs (`--fields "Tags=needs-triage"`). Same flag, same `-f` short form. Passing `field=value` to `show` asks for a nonexistent field instead of setting anything.
- **Tags are replaced, not appended**, and their order is not preservable. `--fields "Tags=..."` overwrites the entire tag set, so read the current tags first and re-send the ones you want to keep. Azure DevOps also **normalises tags into alphabetical order on write**, so never assert, verify, or "fix" tag order — only presence. Colons survive fine, so `wayfinder:map` is safe.
- **`--description` takes HTML and replaces the whole body.** To append a section, read the current `System.Description`, splice against a unique anchor substring, and write the result back. Azure DevOps also **decodes HTML entities server-side** — send `&mdash;` and it stores and returns a real em dash. That is correct behaviour, not corruption: never "fix" it, and don't assert that a written payload reads back byte-identical.

Also worth knowing: `--expand` defaults to `all` on `show`, so a plain `work-item show` already includes the `relations` array.

### Multi-repo projects

An Azure DevOps project can hold several repositories, and work items belong to the *project*, not a repo. If this project holds more than one, give every work item a tag naming its repo and filter every query on that tag — otherwise a query returns the whole project's items. Because tag writes replace the whole set, the repo tag has to be re-sent on every tag update or the item becomes unattributable.

## Pull requests as a triage surface

**PRs as a request surface: no.** *(Set to `yes` if this repo treats external PRs as feature requests; `/triage` reads this flag.)*

When set to `yes`, PRs run through the same tags and states as work items, using `az-cli repos pr` commands:

- **Read a PR**: `az-cli repos pr show --id {pr-id} --org https://dev.azure.com/{org}`
- **List open PRs**: `az-cli repos pr list --status active --repository {repo} --org https://dev.azure.com/{org} --project {project}`
- **Add a comment**: `az-cli repos pr reviewer add --id {pr-id}` / `az-cli repos pr update --id {pr-id}` (discussion via the portal or REST; `az-cli repos pr` has no `--discussion` flag)
- **Link to a work item**: `az-cli repos pr work-item add --id {pr-id} --work-items {work-item-id}`

Note: Azure DevOps PRs and work items are separate number spaces — `#42` always refers to a work item; use `az-cli repos pr show` explicitly for PRs.

## When a skill says "publish to the issue tracker"

Create an Azure DevOps work item using `az-cli boards work-item create`. Use `Issue` type for features and PRDs (Basic template), `Task` for implementation sub-tasks. Pass `--description` directly rather than piping a file.

## When a skill says "fetch the relevant ticket"

Run `az-cli boards work-item show --id {work-item-id} --org https://dev.azure.com/{org}`. The user will normally pass the work item ID directly.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single work item with **child** work items as tickets.

- **Map**: an `Issue` tagged `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body. `az-cli boards work-item create --type "Issue" --fields "Tags=wayfinder:map"`.
- **Child ticket**: a `Task` linked to the map by Azure DevOps' **native parent/child link** — `az-cli boards work-item relation add --id {ticket} --relation-type parent --target-id {map} --org ...`. Tagged `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Skip the `needs-triage` tag: wayfinder tickets are triaged by construction. Once claimed, the ticket is assigned to the driving dev.
- **Blocking**: the **native predecessor/successor dependency link** — the canonical, UI-visible representation, and what renders the frontier in Azure Boards and Delivery Plans. `az-cli boards work-item relation add --id {blocked-ticket} --relation-type predecessor --target-id {blocker} --org ...` reads as "blocker must come first". A ticket carries **both** directions, so filter on `Predecessor` for its blockers and `Successor` for what it blocks. A ticket is unblocked when every predecessor is finished (`Done` **or** `Closed`). Verify the direction once per environment before wiring a whole map — inverting it renders the frontier backwards, which defeats the only reason to use native links.
- **Frontier query**: list the map's children (`az-cli boards work-item relation show --id {map} --org ...`, keeping relations whose `rel` is `Child`), then drop any that are finished, have an unfinished `Predecessor`, or carry a `System.AssignedTo` value; first in map order wins. Use `relation show` rather than plain `show` here — it fills `rel` with the friendly names `Child` / `Parent` / `Predecessor` / `Successor` instead of raw link-type strings. There is **no `relation list` subcommand**: only `add`, `remove`, `show` and `list-type`. Confirm the friendly names resolve in your org with `az-cli boards work-item relation list-type --org ...`; they map to `System.LinkTypes.Hierarchy-Reverse` / `-Forward` and `System.LinkTypes.Dependency-Reverse` / `-Forward`.
- **Claim**: `az-cli boards work-item update --id {ticket} --assigned-to {email} --org ...` — the session's first write. An open, unassigned ticket is unclaimed.
- **Resolve**: post the answer with `--discussion`, set the terminal state for the type (`Done` for a Task, `Closed` for the map's Issue), then append a context pointer (link + gist) to the map's Decisions-so-far. Findings and prototypes stay as files in the repo and are **linked** from the ticket, not pasted into it.
