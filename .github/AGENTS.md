# .github/AGENTS.md

This repo is public; workflows run on code from anyone who opens a PR.

- Never use `pull_request_target`.
- Workflows that use repository secrets (API keys, tokens you added) are `workflow_dispatch`
  only. The ephemeral built-in `GITHUB_TOKEN` is the one exception: `cd.yml` uses it via
  `workflow_run`, gated to successful `ci` runs of pushes to `main` in this repository.
- Keep `permissions:` minimal and explicit per workflow.
- `ci.yml` builds with `-warnaserror` and runs all tests in Release; the local gate in the root
  `AGENTS.md` mirrors it — change both together.
- Dependabot PRs: review and merge together with the owner, not automatically.
