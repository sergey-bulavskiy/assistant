# AGENTS.md

Family assistant replacement: one C# / .NET 10 Telegram executable with PostgreSQL and OpenAI.

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

- **Everything may be reconsidered with reasons.** Requirements, functionality, architecture
  and development rules are open to revision, including previously approved specs and ADRs.
  Briefly present the concrete problem/evidence, proposed alternative, expected benefit and
  cost or lost behaviour; recommend an option and let the owner decide whether to rework it.
  Prior approval is not proof that a choice is still justified. Until a replacement is agreed,
  follow the current decision; do not treat this rule as permission for unilateral scope changes.
- **Iterative delivery.** Foundation and delivery pipeline first, then features, one milestone at
  a time. Write a milestone's spec (in the specs repo) when the milestone starts, get the owner's
  approval, then write the plan.
- **Prove the simplest path first.** Before expanding feature scope, demonstrate one small,
  useful end-to-end scenario through real Telegram, including persisted state where relevant.
  Use its observed result to decide the next increment; testing rules live in `tests/AGENTS.md`.
- **Review the design before planning implementation.** A fresh reviewer checks each proposed
  milestone design before an executable plan or implementation starts. Ask which requirements
  serve a current user scenario, which can be dropped, and whether existing code or a smaller
  design is sufficient. Challenge speculative extensibility, new services, dependencies,
  abstractions, state machines and recovery guarantees; specs and ADRs are not proof of need.
  Record a concise verdict and resolve blocking and should-fix findings before proceeding.
  A material scope or architecture change reopens the affected design decision. This checks the
  proposed design; the existing code review checks its implementation. Documentation-only
  corrections do not need a separate design review.
- **Reviewers must challenge inherited assumptions.** Design and implementation reviewers
  should be deliberately skeptical and exacting, including about decisions made by earlier
  models. Compliance with a spec, ADR, plan or existing code is not sufficient: trace important
  constraints back to a current user need or demonstrated technical limitation. Distinguish
  owner requirements from model assumptions and derived choices. If an upstream choice is
  unjustified, identify the dependent decisions it affects and propose a smaller alternative;
  do not let repeated citations turn that assumption into an unquestionable requirement.
  Raise material upstream flaws even when the implementation follows the plan correctly.
  Resolve them with the owner before dependent work proceeds. Findings need concrete evidence
  and consequences; skepticism is not a quota of complaints or a reason for speculative rewrites.
- **Periodic simplification review.** At each milestone boundary, before planning the next,
  challenge the current requirements, specs, decisions and implementation against actual usage
  and current need. Review earlier when proposing a new subsystem or dependency. Include this
  in the existing change review when possible; it is not another per-PR review gate. Look for
  speculative requirements, model-generated overengineering, unused paths and simpler existing
  mechanisms. For up to three highest-impact findings, record the evidence, smaller alternative
  and owner's decision to keep, simplify, retire or defer. Keep product-specific findings and
  decisions in `../assistant-specs`. Findings do not authorize deleting behaviour/data or
  expanding scope; obtain the owner's decision before implementation unless already authorized.
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
  test writing and audits are covered in `tests/AGENTS.md`. The catalog currently has no dedicated general
  architecture, code review, code style, or EF Core schema migration skill. For these areas, this
  file and the relevant local guide remain the source of project rules. Skills support the
  scheduled review described below; they do not add another review gate. Check the catalog for
  future additions rather than assuming a skill exists.
- **Ask before outward actions** the owner hasn't authorized in the current session (merges,
  pushes to `main`, publishing, deploying on the home PC, changing the owner's machine settings).

## Layout and local guides

| Path | What | Local guide |
|---|---|---|
| `src/Assistant` | Telegram bot, OpenAI extraction and PostgreSQL persistence | — |
| `tests/` | Test rules and future isolated behavioral checks | `tests/AGENTS.md` |
| `deploy/` | Legacy production compose and example configuration; replacement cutover is not authorized | `deploy/AGENTS.md` |
| `.github/` | CI classifier and deployment hold | `.github/AGENTS.md` |

Read the local guide before editing its path. Rules live in exactly one file; add a local guide only for non-obvious knowledge and list it here.

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
dotnet build -c Release -warnaserror -v q -nologo
dotnet test -c Release -v q --logger "console;verbosity=minimal"
```

CI also builds the Docker image without publishing it when application checks are needed. PostgreSQL integration tests require Docker. Test rules are in `tests/AGENTS.md`.

## Delivering a change

1. Work on a `feat/`, `fix/` or `chore/` branch, never commit to `main`.
2. Run the local build and tests above for code or build changes; report anything not run. CI classifies documentation-only changes and always runs its gate; code, build and workflow changes run application checks. After review fixes rerun the build and affected tests.
3. Update applicable README, guides, examples and comments along with behavior. Update private `docs/status.md` after merge.
4. Before first push, inspect `git diff main...HEAD`, commit messages, PR text and `git log -p main..HEAD` for privacy. Rewrite any private content out of all commits before publication.
5. Code gets one fresh independent review against the checklist below; fix or answer blocking and should-fix findings. Docs and chore-only changes need the privacy check but no model review. A dedicated test-quality review applies when adding test classes or rewriting tests.
6. Push and create a neutral PR describing changes and verification; wait for CI and address failures. The always-running `gate` is the intended required check. Repository setting changes need owner approval.
7. Squash merge only after green CI and explicit owner approval in the current session, using the configured git email. Outward actions beyond this flow need explicit approval.
8. **Deployment is on hold.** CD publishes and promotes no image. A merged change is not a delivered replacement; cutover and saved-data handling require separate owner authorization and a reviewed plan.

## Code review checklist

Reviewers (human, subagent or CI bot) check, in this order:

1. **Privacy**: any personal data, real identifiers, secrets or tokens in code, tests, logs,
   commit messages or PR text? Blocking.
2. **Secret leakage at runtime**: new log lines, exception messages or HTTP clients that could
   print the bot token, message text or user data (avoid logging secrets and personal data). Blocking.
3. **Necessity and assumptions**: apply the skeptical-review rule above to the changed scope,
   including inherited requirements and architectural choices, not just adherence to the plan.
4. **Correctness**: exactly-once handling of updates, idempotent storage, cancellation, retries.
5. **Tests**: new behaviour has tests that meet the quality rules in `tests/AGENTS.md` (each
   test can fail for a real bug; no tests that check nothing); test data is synthetic.
6. **Simplicity**: keep the executable and dependencies proportionate to its actual behavior.
7. **Workflows**: `.github/AGENTS.md` rules hold.
8. **Docs**: behaviour changes come with the doc updates from delivery step 3; a gotcha or
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
