# Issue tracker: Forgejo

Issues and specs for this repo live as issues on the self-hosted Forgejo (the `origin` remote).
Use `forgejo-pr` for every operation: it is on the PATH and already authorized. Do not use `gh`
(GitHub is only the public mirror, writes there are blocked), `tea`, or the raw API, and do not look for tokens.
The repo is taken from the Forgejo remote; pass `--repo <owner>/<name>` only outside a clone.

## Conventions

- **Create an issue**: `forgejo-pr issue create -t "..." -F - --label a,b [--blocked-by 3,4] <<'MD'` … `MD`.
  Unknown labels abort with the list of existing ones; `forgejo-pr issue labels --ensure` creates the skill labels.
- **Read an issue**: `forgejo-pr issue view <number> --comments` (body, labels, assignees, open blockers, comments).
- **List issues**: `forgejo-pr issue list --state open --label ready-for-agent --json`
  (`number, title, state, labels, assignees, body`; `--label` with several names means all must match).
- **Comment on an issue**: `forgejo-pr issue comment <number> -F - <<'MD'` … `MD` (or `-b "..."`).
- **Apply / remove labels**: `forgejo-pr issue edit <number> --add-label "..." --remove-label "..."`.
- **Edit title / body**: `forgejo-pr issue edit <number> -t "..." -F -`.
- **Close**: `forgejo-pr issue close <number> -c "..."`; `forgejo-pr issue reopen <number>`.
- **Link work to an issue**: put `Closes #<n>` (one line per issue) in the PR body; Forgejo closes the issues on merge.
  A commit on the default branch with `Closes #<n>` in its message does the same.

## Pull requests

PRs live on the same Forgejo, see the Forgejo section in `~/.claude/CLAUDE.md` for the workflow
(push branch, `forgejo-pr create`, then `forgejo-pr checks --watch --logs`; merge only when asked).

- **Draft PR**: `forgejo-pr create --draft …` (Forgejo marks drafts with a `WIP:` title prefix);
  **mark ready**: `forgejo-pr ready <number>`.
- **Read a PR**: `forgejo-pr view <number> --comments` and `forgejo-pr diff <number>`.
- **Comment on a PR**: `forgejo-pr comment <number> -F -`.

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature requests; `/triage` reads this flag.)_
This is a single-maintainer instance: there are no external PRs, so `/triage` only looks at issues.

Forgejo shares one number space across issues and PRs, so a bare `#42` may be either:
`forgejo-pr issue view 42` says "(ist ein PR …)" when it is one; then use `forgejo-pr view 42`.

## When a skill says "publish to the issue tracker"

Create a Forgejo issue with `forgejo-pr issue create`. A spec gets the `spec` label; its tickets reference it
with `Part of #<spec>` at the top of the body.

## When a skill says "fetch the relevant ticket"

Run `forgejo-pr issue view <number> --comments`.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: a single issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body.
  `forgejo-pr issue create --label wayfinder:map`.
- **Child ticket**: Forgejo has no sub-issues. Put `Part of #<map>` at the top of the child body and add the child
  to a task list (`- [ ] #<child>`) in the map body (`forgejo-pr issue edit <map> -F -`).
  Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is assigned to the repository owner (the owner in the `origin` URL).
- **Blocking**: Forgejo's native issue dependencies: `forgejo-pr issue block <child> --by <blocker>[,<blocker>]`
  (or `--blocked-by` at creation; `issue unblock` removes an edge). `forgejo-pr issue view <n>` prints
  `blockiert von: #…` listing only open blockers. A ticket is unblocked when every blocker is closed.
- **Frontier query**: `forgejo-pr issue list --state open --json`, keep the map's children (task list order),
  drop any with an assignee or with a `blockiert von:` line in `issue view`; first in map order wins.
- **Claim**: `forgejo-pr issue edit <n> --assign <owner>` (the repository owner), the session's first write.
- **Resolve**: `forgejo-pr issue comment <n> -F -` with the answer, then `forgejo-pr issue close <n>`,
  then tick the child in the map's task list and append a context pointer (gist + link) to Decisions-so-far.

## History

Tickets from before the switch to Forgejo live in `.scratch/` as read-only archive. Open ones were migrated;
each migrated issue names its old file. Do not create new files there.

## Repo-specific conventions

- **Decisions**: the decisions the user made in chat go into a `## Decisions` section of the spec issue
  (`forgejo-pr issue edit <spec> -F -`), or a comment on it when they come later. Every implementation and review
  prompt points to that issue. Older features keep theirs in `.scratch/<feature-slug>/DECISIONS.md`.
- **Human checks**: acceptance criteria that only a person can perform (real interactive apps, the user's WLAN,
  credentials) go in a separate `## Human checks` section of the issue body, as a checklist with each item `Not run`.
  They never block an automated run: the issue keeps `ready-for-agent`; once only the human checks are left,
  swap it for `ready-for-human`.
- **Facts in issues and prompts**: host names, test-data paths and environment facts live in `AGENTS.md` and
  `docs/agents/deployment.md`. Issue bodies, comments and workflow prompts link there instead of restating them.
