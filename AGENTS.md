# AGENTS.md

## Subagent Supervision and Reporting

Remain responsible for every agent you spawn. Monitor delegated work using the available coordination tools, and proactively relay results, blockers, and questions requiring user input to the main conversation. Do not wait for the user to request updates or end your turn merely because work has been delegated. Honor any requested confirmation checkpoint before proceeding with dependent work.

## Agent skills

### Issue tracker

Issues and PRDs are tracked using beads_rust (`br`). See `docs/agents/issue-tracker.md`.

### Triage labels

Canonical triage roles mapped 1:1 to default labels (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout (`GLOSSARY.md` and `docs/adr/` at the repo root). See `docs/agents/domain.md`.

### Git Commits

Use `/ce-commit` skill to create a git commit with a clear, value-communication message.

### Firecrawl Developer Index

Use `/firecrawl-developer-index` skill to search when the question is how a library or API behaves, what an error means, or whether a bug was fixed; prefer this over a general web page.

## Workspace folders

### `.worktrees/`

Create all linked Git worktrees under the primary repository's `.worktrees/<task-name>/` directory. This applies to spikes, research, implementation, and delegated agents, keeping their files within the writable workspace.

Resolve the destination from the primary checkout, even when working inside a linked worktree. Before creating a worktree, verify that its resolved absolute path is inside the primary repository's `.worktrees/` directory. Pass that path explicitly to `git worktree add`.

Contents are gitignored. Manage linked checkouts with Git worktree commands; preserve their work until cleanup is authorized.

### `.scratch/`

Temporary space for ad-hoc operations (downloads, intermediate payloads, one-off notes). Gitignored — nothing placed here is tracked or expected to survive across sessions. Safe to write to freely; safe to delete contents at any time.

### `.firecrawl/`

Output space for the firecrawl agent (fetched pages, extracted content). Gitignored — nothing placed here is tracked or expected to survive across sessions.

### `archive/`

Long-term storage for artifacts that are no longer actively used but need to be preserved. Notes and research materials can be placed here for future reference. Unlike `.scratch/` and `.firecrawl/`, files in `archive/` are committed and expected to remain intact over time.
