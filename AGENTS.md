# AGENTS.md

Family assistant: Telegram role-bots backed by LLMs (C# / .NET 10, PostgreSQL).

Instructions for any coding agent (Claude Code, Codex, Copilot, Cursor, …) working in this repo.
`AGENTS.md` files are the only instruction files; don't add tool-specific ones (`CLAUDE.md`,
`.cursorrules`, `copilot-instructions.md`, …). Keep the rules tool-neutral: name a tool's
command only as an example next to a generic fallback.

## Repositories

- This repo (`assistant`) is **public**: code, tests, CI/CD, deploy files, and every rule on
  *how* the project is developed (process, planning, delivery, agent instructions).
- The **private** sibling repo `../assistant-specs` holds the *product*: requirements, specs,
  plans, decisions, status (`docs/superpowers/specs`, `docs/superpowers/plans`). Read them there;
  never copy them here. `docs/` is gitignored in this repo on purpose. Development rules go here,
  not there; the specs repo's `AGENTS.md` keeps only its layout and privacy rules.

## Planning and working with the owner

- **Iterative delivery.** Foundation and delivery pipeline first, then features, one milestone at
  a time. Write a milestone's spec (in the specs repo) when the milestone starts, get the owner's
  approval, then write the plan.
- **Plans for weaker implementers.** Plans are executed by fresh, less capable models with zero
  context: exact file paths, complete code, exact package versions, exact commands with expected
  output, a verification after each step, feature-specific pitfalls named. No "etc.", "similar to
  Task N" or unstated decisions.
- **Plans contain the feature, not the repo's process.** Branching, privacy, git, local gate,
  review, PR/CI/merge flow and environment pitfalls are defined in this file and the local
  guides. A plan points to them once in its header ("follow `../assistant/AGENTS.md` and the
  local guides") and never restates or overrides them. If a plan needs a new repo-wide rule,
  change this file instead.
- **Coordinate, don't grind.** The main agent coordinates; research, plan writing,
  implementation and reviews go to cheaper subagents with self-contained briefs. Specify the
  model explicitly.
- **Read official docs first** (e.g. a tool's GitHub README) before probing an API by trial and
  error.
- **Use applicable .NET skills when available.** Check the official
  [dotnet/skills catalog](https://github.com/dotnet/skills) for exact task matches. Its
  [`csharp-refactoring` skill](https://github.com/dotnet/skills/tree/main/plugins/dotnet/skills/csharp-refactoring)
  covers behavior-preserving C# refactors. For framework upgrades, use the applicable skill from
  the [`dotnet-upgrade` plugin](https://github.com/dotnet/skills/tree/main/plugins/dotnet-upgrade);
  test writing and audits are covered in `tests/AGENTS.md`, and schema migrations in
  `src/Assistant.Infrastructure/AGENTS.md`. The catalog currently has no dedicated general
  architecture, code review, code style, or EF Core schema migration skill. For these areas, this
  file and the relevant local guide remain the source of project rules. Skills support the
  scheduled review described below; they do not add another review gate. Check the catalog for
  future additions rather than assuming a skill exists.
- **Ask before outward actions** the owner hasn't authorized in the current session (merges,
  pushes to `main`, publishing, deploying on the home PC, changing the owner's machine settings).

## Layout and local guides

| Path | What | Local guide |
|---|---|---|
| `src/Assistant.Domain` | Entities, no dependencies | `src/Assistant.Domain/AGENTS.md` |
| `src/Assistant.Application` | Role-bot update handling, reply policy, command parsing, General assistant (`Messages/GeneralAssistant.cs`), shared addressing rule (`Messages/Addressing.cs`), private trace contracts and transport hooks (`Diagnostics/`), health assistant (`Health/`: `HealthAssistant` dispatches to `HealthCommands`, `HealthMessagePipeline`, `HealthAnswers`, `HealthConfirmations`); ports (`IMessageStore`, `ITelegramClient`, `IClock`, `IApprovalService`, `ICurrentFamily`, `IManagerUpdateHandler`, `ITokenEncryptor`, `ILlmGateway`, `IChatSettingsStore`, `ILlmUsageQuery`, `IHealthProfileStore`, `IEventStore`, `ISafetyAlertStore`, `IPendingRecordStore`, `IFamilyOwnership`, `IRolePrompts`, …) | — |
| `src/Assistant.Application/Health` | Health interpretation, owner profile fields and consultation; `HealthConsultationContext` builds one bounded raw diary/profile/conversation snapshot; `Documents/` holds the document extraction port | `src/Assistant.Application/Health/AGENTS.md` |
| `src/Assistant.Application/Vet` | Cat profile/text diary, independent event/reply intent, immutable source/result/work recovery, scoped reviews, history and revision/action mutation ports | `src/Assistant.Application/Vet/AGENTS.md` |
| `src/Assistant.Infrastructure` | EF Core + Npgsql, Telegram.Bot attachment transport, bot polling (`Bots/`), manager bot commands (`Manager/`), approvals (`Families/`), token encryption, LLM gateway, native Codex images and CLI/API providers (`Llm/`), health stores and bounded document text extraction (`Health/`), private bounded diagnostics (`Diagnostics/`), role prompt loader (`Roles/`) | `src/Assistant.Infrastructure/AGENTS.md` |
| `src/Assistant.Host` | ASP.NET host, startup (config validation, migrations, claim code), `/health` | `src/Assistant.Host/AGENTS.md` |
| `src/Assistant.TraceExport` | Read-only local export of private diagnostic timelines to an explicit path outside repositories | `src/Assistant.Infrastructure/AGENTS.md` |
| `roles/` | Role prompt sources (`roles/<role>/*.md`), generic, embedded into Infrastructure at build time | `src/Assistant.Infrastructure/AGENTS.md` |
| `tests/` | Unit + integration (IntegreSQL) tests; extraction evals (`Assistant.Evals`: recorded answers replayed in every run, live model run opt-in); opt-in real-Telegram smoke test (`Assistant.SmokeTests`) | `tests/AGENTS.md` |
| `deploy/` | Production compose, `.env.example` | `deploy/AGENTS.md` |
| `.github/` | CI/CD workflows, Dependabot | `.github/AGENTS.md` |

Before editing files under a path with a local guide, read that guide. Rules live in exactly one
file: a rule that applies to one folder goes in that folder's guide, not here. Add a new local
guide only for knowledge that is not obvious from the code (a gotcha, an invariant, a command),
and add it to the table above.

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

A plain `dotnet test` skips the smoke test and the live extraction evals; both are opt-in (`tests/AGENTS.md`).

Faster local loop and test infrastructure details: `tests/AGENTS.md`.

## Delivering a change

Every change, including docs, goes through these steps. Do not report a change as done before
step 9.

The owner is the only developer. Each check runs **once**, where it is cheapest: tests run
locally and again in CI (CI costs no model tokens), but a change gets **one** model review, not
one per stage. Keep command output small: read logs only when something failed.

1. **Branch.** Never commit to `main`. Name branches `feat/…`, `fix/…` or `chore/…`.
2. **Local gate.** Run the build and test commands above with quiet output
   (`dotnet build -c Release -warnaserror -v q -nologo`,
   `dotnet test -c Release -v q --logger "console;verbosity=minimal"`); both must pass. If you
   could not run a step (e.g. Docker unavailable), say so explicitly — never claim it passed.
   Skip it for changes that touch no code, tests or build files (docs, `AGENTS.md`, workflows):
   CI runs anyway. After fixing review findings, rerun only the build and the affected tests;
   CI runs the full suite.
3. **Docs current.** A PR that adds, changes or removes behaviour updates, in the same PR, every
   doc that describes it: `README.md` (what the bots do, commands, setup, smoke checklist), the
   layout table above, the local `AGENTS.md` guides, `deploy/.env.example`, and code comments
   that name the changed behaviour or config. Don't leave milestone-specific docs behind once the
   milestone is done. Update `docs/status.md` in `../assistant-specs` after merging.
4. **Privacy check.** Read `git diff main...HEAD`, the commit messages and the PR text you are
   about to publish against the privacy rules above. Every commit on the branch is published and
   stays reachable on GitHub even after a squash merge, so also read `git log -p main..HEAD`:
   wording that was added and later removed still leaks. If any commit contains something that
   must not be public, squash or rewrite the branch **before the first push**.
5. **Review — once, sized to the change.** Against the checklist below, by a fresh reviewer (a
   subagent or a separate session with no stake in the change). Fix or explicitly answer each
   blocking and should-fix finding.
   - Docs, `AGENTS.md`, Dependabot and other chore changes: no model review; step 4 is enough.
   - Code changes: one review, using the tool's built-in review command if it has one (e.g.
     `/code-review` in Claude Code), otherwise a fresh subagent given this checklist.
   - Work executed task by task with per-task reviews (e.g. a subagent-per-task plan executor):
     those reviews plus one final whole-branch review are the review. Do not run another one.
   - A dedicated test-quality review (e.g. the `test-quality-auditor` agent from the
     `dotnet-test` plugin, see `tests/AGENTS.md`) only when the PR adds new test classes or
     rewrites existing tests, not for small test edits.
   - Do not rerun a full review after fixing findings; check the fixes yourself.
6. **PR.** `git push -u origin <branch>`, then `gh pr create` with a neutral title and a body
   saying what changed and how it was verified.
7. **CI.** Wait without streaming progress:
   `gh pr checks <n> --watch --fail-fast > /dev/null; echo "exit=$?"` (if it reports no checks
   yet, retry after a few seconds). On failure: `gh run view <run-id> --log-failed`, fix, push,
   wait again. A red or pending check is not done.
   **AI CI review** (`claude-review` label, `.github/AGENTS.md`) is **not** part of the flow;
   run it only when the owner asks. Then: `gh pr edit <n> --add-label claude-review`, wait for its run
   (`gh run list --workflow claude-review.yml --limit 1`,
   `gh run watch <run-id> --exit-status > /dev/null`), read its summary
   (`gh pr view <n> --comments`) and inline comments
   (`gh api repos/{owner}/{repo}/pulls/<n>/comments --jq '.[] | .path + ":" + (.line|tostring) + " " + .body'`),
   and fix or explicitly answer every blocking and should-fix finding.
8. **Merge.** Squash merge (`gh pr merge --squash --delete-branch`, author email per Git
   below) only when CI is green and the owner has approved merging in the current session.
9. **CD.** After merge, `gh run list --workflow cd --limit 1` (CD starts only after the `ci` run
   on `main` finishes — retry until a run for the merge commit appears), then
   `gh run watch <run-id> --exit-status > /dev/null; echo "exit=$?"`. CD builds the `sha-<7>`
   image, runs the real-Telegram smoke test against it (only when the repository variable
   `SMOKE_ENABLED` is `true`; otherwise `smoke` is skipped and the run warns), and then promotes `latest`.
   Report the deployed image tag (`sha-<first 7 of the merge commit>`) once `promote` succeeded.
   If `smoke` fails, `latest` is unchanged (the home PC keeps the previous release): say so and
   investigate; the change is merged but not delivered. Re-run failed jobs for transient failures (only the newest run promotes).

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
7. **Docs**: behaviour changes come with the doc updates from delivery step 3; a gotcha or
   invariant goes into the matching local `AGENTS.md`.

Report findings as blocking / should-fix / nit. Review comments on the public repo follow the
privacy rules too.

## Git

- Commits use the repo-local git identity (`git config user.name` / `user.email`) as is. Don't
  change it or override it per command. Squash merges use the same email:
  `gh pr merge --squash --author-email "$(git config user.email)"`.
- Commit messages end with a `Co-Authored-By:` trailer for the model that wrote them.

## Environment pitfalls (Windows dev PC)

- **Other agents may be working at the same time**, in this checkout, another worktree, or another
  machine, and may be running builds or tests. Before starting any task, inspect `git status` and
  running `dotnet`/`testhost` processes. Parallel workers may have merged changes from other
  worktrees since your last pull. Synchronize from the latest `main` before planning,
  implementation, review, or docs work: in an appropriate clean `main` checkout run
  `git pull --ff-only origin main`. If already in a feature worktree, fetch `origin` and safely
  incorporate `origin/main` into your owned branch before proceeding; do not run `git pull origin
  main` on a feature branch and treat it as updating local `main`. Create new worktrees from the
  refreshed remote branch (`git worktree add ../assistant-<topic> -b <branch> origin/main`).
  Preserve local and other-session work; never switch branches, stash, reset, or overwrite another
  checkout. If synchronization cannot fast-forward or be reconciled safely, stop and resolve that
  state before proceeding. Don't run builds or tests the task doesn't need; a failing test may be
  one another agent is fixing. Locked files in `bin/`/`obj/` usually mean another agent's run.
- Git Bash mangles Windows backslash paths in `sed`/heredocs (`\a`, `\s`) — use forward slashes,
  a file-editing tool, or PowerShell for such edits.
- A corporate NuGet source may exist on the machine; `nuget.config` pins nuget.org. Keep it.
- `gh api` endpoints must be written without a leading slash in Git Bash (`gh api repos/...`).
- The machine may lack the SDK pinned in `global.json` (check `dotnet --list-sdks`), and Docker
  image pulls may hang behind a corporate proxy. If the local gate cannot run, CI is the gate:
  say so in the PR body and in your report.
