# Issue tracker: Azure DevOps

Issues and Specs for this repository live as Azure DevOps work items. Use the Azure CLI's `azure-devops` extension (`boards` / `repos` / `devops invoke`) for all operations.

**Temporary files** (ticket descriptions, comments, JSON payloads) go in the repository's temporary space for ad-hoc operations. Look up its location in `CLAUDE.md` / `AGENTS.md` each session rather than reusing a remembered path. If none is defined, ask the user.

## Setup procedure

*This section is for `setup-matt-pocock-skills`. Apply these steps to the copy at `docs/agents/issue-tracker.md` and never to this template. Then delete the section from the copy.*

1. **Fill the coordinates** below. `{repository-tag}` is usually the repository name, so confirm it with the user. Don't write the temp folder path into the copy.
2. **Read the project's work item types and states** from Azure DevOps:

   ```powershell
   az-cli devops invoke --area wit --resource workitemtypes --route-parameters project={project-name} --api-version 7.1 --org https://dev.azure.com/{org} --query "value[].name" -o tsv
   az-cli devops invoke --area wit --resource workitemtypestates --route-parameters project={project-name} "type={type}" --api-version 7.1 --org https://dev.azure.com/{org} --query "value[].{name:name, category:category}" -o tsv
   ```

   Each state carries a `category` (`Proposed` / `InProgress` / `Resolved` / `Completed` / `Removed`).
3. **Rewrite the "Types and states" table** with the project's real values. Pick the type for Specs/features (`{spec-type}`) and for implementation sub-tasks (`{task-type}`), and confirm both with the user. Add rows only for other types the user actually works with (e.g. Epic/Feature parents), not every type the process defines. If there are none, delete the "(other types in use, if any)" row. **Close with** is the type's `Completed` state. **Finished** lists its `Completed` and `Removed` states.
4. **Replace every placeholder in the copy** with the literal names: `{spec-type}`, `{task-type}`, `{spec-close-state}`, `{task-close-state}`, `{finished-states}` (the union of all rows' Finished states, quoted and comma-separated for WIQL, e.g. `'Closed', 'Removed'`), and the coordinates. The copy must use only the project's real type and state names.

## Coordinates for this repository

| Thing            | Value                          |
| ---------------- | ------------------------------ |
| Organization     | `https://dev.azure.com/{org}`  |
| Project          | `{project-name}`               |
| Repository       | `{repository-name}`            |
| Repo scope tag   | `{repository-tag}`             |
| Default assignee | `{user-email}`                 |
| CLI command      | **`az-cli` only.** Never `az`. |

**The CLI is invoked as `az-cli`, in PowerShell.** It resolves to `~\.local\bin\az-cli.ps1`. This is a deliberate repository convention, so don't substitute bare `az`. Bash does not resolve `.ps1`, so check for it with `Get-Command az-cli` in PowerShell.

**Three invariants:**

1. **Every work item carries the tag `{repository-tag}`.** `{project-name}` spans several repositories, past and future, and this tag is the only thing that scopes an item to this one.
2. **Every query filters on it** with `AND [System.Tags] CONTAINS '{repository-tag}'`. Without the filter, a query returns the whole project.
3. **Every result is filtered on it, including reads by id.** `show` and `relation show` return any item in the project. An item without `{repository-tag}` belongs to another repository: don't act on it, and tell the user. Never infer ownership from other tags, because another repository's tags can't be known in advance.

Install and authenticate with `az-cli extension add --name azure-devops` and `az-cli devops login --organization https://dev.azure.com/{org}`. **Never set defaults** (`az-cli devops configure --defaults`): a stale default silently sends writes to another project. Pass `--org` on every command, and `--project` where it is accepted.

## Types and states

| Role                           | Type          | States                      | Close with           | Finished                 |
| ------------------------------ | ------------- | --------------------------- | -------------------- | ------------------------ |
| Spec / feature / wayfinder map | `{spec-type}` | `{state}` / `{state}` / ... | `{spec-close-state}` | `{state}` / `{state}`    |
| Sub-task / wayfinder ticket    | `{task-type}` | `{state}` / `{state}` / ... | `{task-close-state}` | `{state}` / `{state}`    |
| *(other types in use, if any)* | `{type}`      | `{state}` / `{state}` / ... | `{close-state}`      | `{state}` / `{state}`    |

Use only the states in this table. Any other value is rejected with `The field 'State' contains the value '...' that is not in the list of supported values`.

## Conventions

- **Descriptions and comments always go through a temp file, never inline.** Inline bodies break on quotes and punctuation. Write the body with the **Write tool** to an absolute path in the repository's temp folder (see the top of this file), and hold that path in `$file`. Pass it as `--description "@$file"` or `--discussion "@$file"` and keep the double quotes, because a bare `@name` is PowerShell splatting. Delete the file afterwards.
- **Work item bodies are always HTML** (`<p>`, `<h2>`, `<ul><li>`, `<code>`), never Markdown.
- Triage state is tracked via **Tags** (see `triage-labels.md`).

**Create** a Spec/feature:

```powershell
az-cli boards work-item create --title $title --type "{spec-type}" --description "@$file" `
  --assigned-to {user-email} --fields "Tags={repository-tag}" `
  --org https://dev.azure.com/{org} --project {project-name}
```

To create a sub-task, run the same command with `--type "{task-type}"`. To create an item already in triage, send `--fields "Tags={repository-tag}; needs-triage"`. **Wayfinder items are created without `--assigned-to`**, because assignment is the claim signal (see "Wayfinding operations").

- **Read**: `az-cli boards work-item show --id {id} --org https://dev.azure.com/{org}` (includes `relations`). Check `System.Tags` for `{repository-tag}` before acting (invariant 3).
- **List open items**:

  ```powershell
  az-cli boards query --wiql "SELECT [System.Id], [System.Title], [System.State], [System.Tags], [System.AssignedTo] FROM WorkItems WHERE [System.TeamProject] = '{project-name}' AND [System.Tags] CONTAINS '{repository-tag}' AND [System.State] NOT IN ({finished-states}) ORDER BY [System.ChangedDate] DESC" --org https://dev.azure.com/{org} --project {project-name}
  ```

  Add `AND [System.Tags] CONTAINS 'needs-triage'` (or another role tag) to scope the list to a triage state.
- **Comment**: `az-cli boards work-item update --id {id} --discussion "@$file" --org https://dev.azure.com/{org}`
- **Tag**: `az-cli boards work-item update --id {id} --fields "Tags={repository-tag}; ready-for-agent" --org https://dev.azure.com/{org}`
- **Assign**: `az-cli boards work-item update --id {id} --assigned-to {user-email} --org https://dev.azure.com/{org}`
- **Close**: `az-cli boards work-item update --id {id} --state "{spec-close-state}" --org https://dev.azure.com/{org}`. Use `{task-close-state}` for a `{task-type}`.

### Parameter traps

- **`show`, `update` and `relation` commands don't accept `--project`.** Passing it prints a bare usage footer.
- **`--fields` has two grammars.** On `create` / `update` it takes `"field=value"` assignments. On `show` it is a projection: quote the list, because PowerShell turns an unquoted `a,b` into an array, and add `--expand none`, because the default `all` is rejected alongside `--fields`. For example: `--fields "System.Id,System.Tags,System.State" --expand none`.
- **Keep `--query` simple.** The `az-cli` wrapper mangles escaped double quotes inside a JMESPath query (`fields.\"System.Title\"`). Use forms without inner double quotes, or leave `--query` out and read the JSON.
- **Tags are replaced, not appended.** Read the current tags and re-send the full set, including `{repository-tag}`. Azure DevOps sorts tags alphabetically, so check that tags are present and ignore their order.
- **`--description` replaces the whole body.** To append, read `System.Description` into a temp file, splice it at a unique anchor, and write it back with `--description "@$file"`. Azure DevOps normalises HTML on write: `&mdash;` becomes `—`, `"` becomes `&quot;`, and a space is added before some closing tags. That is expected, not corruption, so don't expect a byte-identical read-back.

## Pull requests as a triage surface

**PRs as a request surface: no.** *(Set to `yes` if this repository treats external PRs as feature requests; `/triage` reads this flag.)*

When set to `yes`:

- **Read a PR**: `az-cli repos pr show --id {pr-id} --org https://dev.azure.com/{org}`
- **List open PRs**: `az-cli repos pr list --status active --repository {repository-name} --org https://dev.azure.com/{org} --project {project-name}`
- **Comment**: `az-cli repos pr` has no comment command, so post a thread through the REST API. PR comment content is Markdown, not HTML. Write the comment with the Write tool to a `.md` file in the temp folder, and hold its path in `$md` and the JSON file's path in `$file`. Build the JSON with PowerShell rather than by hand, so the content is escaped correctly:
  `@{ comments = @(@{ parentCommentId = 0; content = (Get-Content $md -Raw); commentType = 1 }); status = 1 } | ConvertTo-Json -Depth 5 | Set-Content $file -Encoding utf8NoBOM`
  Then post it:
  `az-cli devops invoke --area git --resource pullRequestThreads --route-parameters project={project-name} repositoryId={repository-name} pullRequestId={pr-id} --http-method POST --in-file "$file" --api-version 7.1 --org https://dev.azure.com/{org}`
- **Link to a work item**: `az-cli repos pr work-item add --id {pr-id} --work-items {work-item-id} --org https://dev.azure.com/{org}`

PRs and work items are separate number spaces: `#42` is always a work item.

## When a skill says "publish to the issue tracker"

Create a `{spec-type}` for a Spec/feature, or a `{task-type}` for a sub-task, as in "Create" above. Tag it `{repository-tag}`, assign it to `{user-email}`, and pass the HTML description from a temp file.

## When a skill says "fetch the relevant ticket"

Run `az-cli boards work-item show --id {id} --org https://dev.azure.com/{org}`. If the item lacks `{repository-tag}`, stop and tell the user (invariant 3). Also, run `az-cli devops invoke --area wit --resource comments --route-parameters project {project-name} workItemId={id} --http-method GET --api-version 7.1--org https://dev.azure.com/{org}` to fetch the comments of the ticket in question.

## Wayfinding operations

Used by `/wayfinder`. The **map** is one work item and its **children** are the tickets. All of them carry the `{repository-tag}` tag, and all are created **unassigned**.

- **Map**: a `{spec-type}` tagged `{repository-tag}; wayfinder:map`, holding the Notes / Decisions-so-far / Fog body (`--description "@$file"`).
- **Child ticket**: a `{task-type}` tagged `{repository-tag}; wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`), with no `needs-triage` tag. Link it to the map with `az-cli boards work-item relation add --id {ticket} --relation-type parent --target-id {map} --org https://dev.azure.com/{org}`.
- **Blocking**: use the native dependency link, `az-cli boards work-item relation add --id {blocked} --relation-type predecessor --target-id {blocker} --org https://dev.azure.com/{org}`, which reads as "blocker comes first". A ticket is unblocked when every `Predecessor` is in `{finished-states}`. Check the link direction once before wiring a whole map.
- **Frontier query**: run `az-cli boards work-item relation show --id {map} --org https://dev.azure.com/{org}` and keep the `Child` relations. It returns links only, so read each child with `show` to get its tags, state, assignee and `Predecessor` links. Drop children that lack `{repository-tag}`, are finished, have an unfinished `Predecessor`, or have an assignee. The first remaining child in map order wins. `relation show` returns the friendly names `Child` / `Predecessor`. There is no `relation list` subcommand.
- **Claim**: `az-cli boards work-item update --id {ticket} --assigned-to {user-email} --org https://dev.azure.com/{org}`. This is the session's first write.
- **Resolve**: post the answer with `--discussion "@$file"` and set `{task-close-state}`. Then append a link and gist to the map's Decisions-so-far. Findings and prototypes stay as files in the repository and are linked, not pasted.
