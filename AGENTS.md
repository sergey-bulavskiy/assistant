# AGENTS.md

Family assistant: Telegram role-bots backed by LLMs (C# / .NET 10, PostgreSQL).

Instructions for any coding agent (Claude Code, Codex, Copilot, Cursor, …) working in this repo.
`CLAUDE.md` files only import the `AGENTS.md` next to them; edit `AGENTS.md`, never the stubs.

## Repositories

- This repo (`assistant`) is **public**: code, tests, CI/CD, deploy files.
- Specs, plans and requirements live in the **private** sibling repo `../assistant-specs`
  (`docs/superpowers/specs`, `docs/superpowers/plans`). Read them there; never copy them here.
  `docs/` is gitignored in this repo on purpose.

## Layout and local guides

| Path | What | Local guide |
|---|---|---|
| `src/Assistant.Domain` | Entities, no dependencies | `src/Assistant.Domain/AGENTS.md` |
| `src/Assistant.Application` | Use cases, ports (`IMessageStore`, `ITelegramClient`, `IClock`) | — |
| `src/Assistant.Infrastructure` | EF Core + Npgsql, Telegram.Bot adapter | `src/Assistant.Infrastructure/AGENTS.md` |
| `src/Assistant.Host` | ASP.NET host, polling loop, `/health`, config | `src/Assistant.Host/AGENTS.md` |
| `tests/` | Unit + integration (IntegreSQL) tests | `tests/AGENTS.md` |
| `deploy/` | Production compose, `.env.example` | `deploy/AGENTS.md` |
| `.github/` | CI/CD workflows, Dependabot | `.github/AGENTS.md` |

Before editing files under a path with a local guide, read that guide. Rules live in exactly one
file: a rule that applies to one folder goes in that folder's guide, not here. Add a new local
guide only for knowledge that is not obvious from the code (a gotcha, an invariant, a command),
and add it to the table above together with a `CLAUDE.md` stub containing `@AGENTS.md`.

## Privacy rules (mandatory)

This project handles personal and health data at runtime. None of it may enter this repo.

- **No personal data anywhere in this repo**: no real names, health conditions, medications,
  measurements, schedules, addresses, chat IDs, user IDs or bot usernames. This applies to code,
  comments, commit messages, PR titles and descriptions, review comments, issue text, README,
  role prompts, config examples and test data.
- **Test and eval data are synthetic**: invented messages and values only, never copied from real
  chats, exports or the database.
- **Role prompts stay generic.** Anything specific to a person belongs in the database profile at
  runtime, not in `roles/`.
- **Never read, print or commit** `deploy/.env` (it holds real secrets). Never commit any `.env` files, tokens, API keys, Telegram exports, `media/`, backups or DB dumps.
- When a requirement from `../assistant-specs` mentions personal details, generalize it before
  it appears here (e.g. "a household member with a chronic condition", "glucose reading 7.8").
- If you notice personal data in a diff or in history, stop and tell the owner before pushing.

## Build and test

```bash
dotnet build -c Release -warnaserror   # same flags as CI; warnings are errors
dotnet test -c Release                 # integration tests need Docker running
```

Faster local loop and test infrastructure details: `tests/AGENTS.md`.

## Delivering a change

Every change, including docs, goes through these steps. Do not report a change as done before
step 7.

1. **Branch.** Never commit to `main`. Name branches `feat/…`, `fix/…` or `chore/…`.
2. **Local gate.** Run the build and test commands above; both must pass. If you could not run a
   step (e.g. Docker unavailable), say so explicitly — never claim it passed.
3. **Privacy check.** Read `git diff main...HEAD`, the commit messages and the PR text you are
   about to publish against the privacy rules above.
4. **Review.** Get a review against the checklist below from a fresh reviewer (a subagent or a
   separate session with no stake in the change). Fix or explicitly answer each finding.
   In Claude Code use the built-in `/code-review`; when tests changed, also run the
   `test-quality-auditor` agent from the `dotnet-test` plugin (see `tests/AGENTS.md`).
5. **PR.** `git push -u origin <branch>`, then `gh pr create` with a neutral title and a body
   saying what changed and how it was verified.
6. **CI.** `gh pr checks --watch`. On failure: `gh run view <run-id> --log-failed`, fix, push,
   watch again. A red or pending check is not done.
   **CI review:** if the PR adds or changes functionality (anything under `src/`, behaviour, config
   or schema), run the Claude CI review: `gh pr edit <n> --add-label claude-review`, then wait for
   its run (`gh run list --workflow claude-review.yml --limit 1`, `gh run watch <run-id>`) and read
   its summary (`gh pr view <n> --comments`) and inline comments
   (`gh api repos/{owner}/{repo}/pulls/<n>/comments --jq '.[] | .path + ":" + (.line|tostring) + " " + .body'`). Fix or explicitly answer every blocking and
   should-fix finding; after fixing, remove and re-add the label to review again. Optional for
   docs-only and chore PRs. It complements, never replaces, the local review in step 4.
7. **Merge.** Squash merge (`gh pr merge --squash --delete-branch`, author email per Git
   below) only when CI is green, required CI review findings are resolved, and the owner has
   approved merging in the current session.
8. **CD.** After merge, `gh run list --workflow cd --limit 1` (CD starts only after the `ci` run
   on `main` finishes — retry until a run for the merge commit appears), then
   `gh run watch <run-id>`.
   Report the deployed image tag (`sha-<first 7 of the merge commit>`). If CD fails, say so and
   investigate; the change is merged but not delivered.

Outward actions beyond this flow (force pushes, deleting remote branches other than the merged
one, changing repo settings, publishing packages) need the owner's explicit approval.

## Code review checklist

Reviewers (human, subagent or CI bot) check, in this order:

1. **Privacy**: any personal data, real identifiers, secrets or tokens in code, tests, logs,
   commit messages or PR text? Blocking.
2. **Secret leakage at runtime**: new log lines, exception messages or HTTP clients that could
   print the bot token, message text or user data (see `src/Assistant.Host/AGENTS.md`). Blocking.
3. **Correctness**: exactly-once handling of updates, idempotent storage, cancellation, retries.
4. **Tests**: new behaviour has tests that meet the quality rules in `tests/AGENTS.md` (each
   test can fail for a real bug; no tests that check nothing); test data is synthetic.
5. **Layering**: Domain has no dependencies; Application depends only on Domain and abstractions;
   infrastructure types do not leak into Application.
6. **Workflows**: `.github/AGENTS.md` rules hold.
7. **Local guides**: if the change adds a gotcha or invariant, the matching `AGENTS.md` is updated.

Report findings as blocking / should-fix / nit. Review comments on the public repo follow the
privacy rules too.

## Git

- Commits use the repo-local git identity (`git config user.name` / `user.email`) as is. Don't
  change it or override it per command. Squash merges use the same email:
  `gh pr merge --squash --author-email "$(git config user.email)"`.
- Commit messages end with a `Co-Authored-By:` trailer for the model that wrote them.

## Environment pitfalls (Windows dev PC)

- Git Bash mangles Windows backslash paths in `sed`/heredocs (`\a`, `\s`) — use forward slashes,
  a file-editing tool, or PowerShell for such edits.
- A corporate NuGet source may exist on the machine; `nuget.config` pins nuget.org. Keep it.
- `gh api` endpoints must be written without a leading slash in Git Bash (`gh api repos/...`).
- The machine may lack the SDK pinned in `global.json` (check `dotnet --list-sdks`), and Docker
  image pulls may hang behind a corporate proxy. If the local gate cannot run, CI is the gate:
  say so in the PR body and in your report.
