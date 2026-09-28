# .github/AGENTS.md

This repo is public; workflows run on code from anyone who opens a PR.

- Never use `pull_request_target`.
- Workflows that use repository secrets (API keys, tokens you added) run only on triggers that
  need write access, and never on code from forks. Allowed: `workflow_dispatch`, and
  `pull_request` `labeled` guarded by `github.event.pull_request.head.repo.full_name ==
  github.repository` (`claude-review.yml`). `cd.yml` uses only the ephemeral `GITHUB_TOKEN` via
  `workflow_run`, gated to successful `ci` runs of pushes to `main` in this repository.
- `claude-review.yml` (secret `CLAUDE_CODE_OAUTH_TOKEN`) reviews a PR when the `claude-review`
  label is added; remove and re-add the label to review again after new pushes. Its prompt
  points at the checklist in the root `AGENTS.md` — keep review rules there, not in the workflow.
- Keep `permissions:` minimal and explicit per workflow.
- `ci.yml` builds with `-warnaserror` and runs all tests in Release; the local gate in the root
  `AGENTS.md` mirrors it — change both together.
- Dependabot PRs: review and merge together with the owner, not automatically.
