# .github/AGENTS.md

This repo is public; workflows run on code from anyone who opens a PR.

- Never use `pull_request_target`.
- Workflows that use repository secrets (API keys, tokens you added) run only on triggers that
  need write access, and never on code from forks. Allowed: `workflow_dispatch`, and
  `pull_request` `labeled` guarded by `github.event.pull_request.head.repo.full_name ==
  github.repository` (`claude-review.yml`). `cd.yml` runs via `workflow_run`, gated to successful
  `ci` runs of pushes to `main` in this repository; its `smoke` job uses the secrets of the GitHub
  Environment `smoke` (keep that environment restricted to `main`; smoke secrets appear in no other
  workflow).
- `cd.yml` has three jobs: `build-and-push` pushes only the `sha-<7>` image, `smoke`
  (real Telegram, `tests/Assistant.SmokeTests`) runs against that tag, `promote` retags it as
  `latest` only when smoke passed. Watchtower follows `latest`, so a failing smoke run never
  reaches the home PC. The gate is opt-in per repository: `smoke` runs only when the repository
  variable `SMOKE_ENABLED` is `true` (Settings → Secrets and variables → Actions → Variables); while
  it is unset, `smoke` is skipped and `promote` still runs with a warning, i.e. ungated. Set it
  only after the smoke test has passed live and the `smoke` environment secrets exist, otherwise
  every CD run fails at `smoke` and `latest` stops moving. Re-running a failed `smoke` job of the newest run is fine (the `sha-` image is reused);
  `promote` retags only when the run's commit is still the head of `main`, so re-running an
  older run never moves `latest` back. A third queued run supersedes a queued second one
  (concurrency group `smoke` does not cancel a running job).
- `claude-review.yml` (secret `CLAUDE_CODE_OAUTH_TOKEN`) reviews a PR when the `claude-review`
  label is added; remove and re-add the label to review again after new pushes. It is optional
  and runs only on the owner's request (see "Delivering a change" in the root `AGENTS.md`). Its prompt
  points at the checklist in the root `AGENTS.md` — keep review rules there, not in the workflow.
- Keep `permissions:` minimal and explicit per workflow.
- `ci.yml` builds with `-warnaserror` and runs all tests in Release; the local gate in the root
  `AGENTS.md` mirrors it — change both together.
- Before Docker fixtures start, CI merges Google's documented Docker Hub cache mirror into the
  disposable runner's existing daemon configuration, restarts that runner's Docker daemon and
  preloads the canonical `pgvector/pgvector:pg17` test image. Do not apply this step to a developer
  or home daemon. Keep fixture/Compose image references and Ryuk cleanup unchanged. The
  [official cache documentation](https://docs.cloud.google.com/artifact-registry/docs/pull-cached-dockerhub-images)
  requires daemon routing; a cache miss falls back to Docker Hub and can still be rate-limited.
  This does not prove digest equivalence with an older local image or change image provenance,
  and Docker Hub mirrors do not route the separate GHCR IntegreSQL image. No credentials are used.
- Dependabot PRs: review and merge together with the owner, not automatically.
