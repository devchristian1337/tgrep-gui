# Issue tracker: GitHub

Issues and specs live in GitHub Issues for `devchristian1337/tgrep-gui`. Use the `gh` CLI from this clone; it resolves the repository from the Git remote. When running elsewhere, pass `--repo devchristian1337/tgrep-gui`.

## Conventions

- Create: `gh issue create --title "..." --body-file <path>`.
- Read the ticket and discussion: `gh issue view <number> --comments`. Fetch structured details and labels with `gh issue view <number> --json number,title,body,labels,comments`.
- List: `gh issue list --state open --json number,title,body,labels,comments`, with appropriate label and state filters.
- Comment: `gh issue comment <number> --body-file <path>`.
- Apply or remove labels: `gh issue edit <number> --add-label "..."` or `--remove-label "..."`. Use the mapping in `docs/agents/triage-labels.md`.
- Close: `gh issue close <number> --comment "..."`.

For multiline issue bodies and comments, write the exact text to a temporary file and pass `--body-file` to preserve formatting and avoid shell interpolation.

When a skill says "publish to the issue tracker", create a GitHub issue. When it says "fetch the relevant ticket", read the issue body, labels, and comments.

## Pull requests as a triage surface

**PRs as a request surface: no.**

GitHub issues and pull requests share a number space. If a reference is ambiguous, resolve it with `gh pr view <number>` and fall back to `gh issue view <number>`.

## Wayfinding operations

For skills that organize work around a map issue:

- Map: one issue labelled `wayfinder:map`, containing Notes, Decisions-so-far, and Fog.
- Child tickets: link issues as GitHub sub-issues. If unavailable, use a task list in the map and a `Part of #<map>` line in each child. Use `wayfinder:<type>` labels for `research`, `prototype`, `grilling`, or `task`.
- Blocking: use native GitHub issue dependencies when available; otherwise record `Blocked by: #<number>` references in the child. A ticket is unblocked when all blockers are closed.
- Frontier: inspect open children in map order and select the first without open blockers or an assignee.
- Claim: assign the ticket to the driving developer with `gh issue edit <number> --add-assignee @me`.
- Resolve: record the answer in a comment, close the child, then add a brief finding and link to the map's Decisions-so-far.
