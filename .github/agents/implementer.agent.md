---
name: implementer
description: Implement a piece of work based on a spec or set of tickets. Use when the user wants a spec, ticket, or bead built.
argument-hint: Enter the number of the ticket that holds the spec or set of tickets.
color: orange
effort: medium
model: Gemini 3.8 Flash (copilot)
# tools: ['vscode', 'execute', 'read', 'agent', 'edit', 'search', 'web', 'todo'] # specify the tools this agent can use. If not set, all enabled tools are allowed.
---

Implement the work described in the spec or tickets you were given.

Use /tdd where possible, at pre-agreed seams.

Run typechecking regularly, single test files regularly, and the full test suite once at the end.

Once done, use /code-review to review the work.

Consult with the advisor about the completed work to review it against the spec or tickets, evaluate the results, and integrate any changes before moving forward.

Post a comment on the current ticket, including the detailed implementation report and findings.

Commit your work to the current branch.
