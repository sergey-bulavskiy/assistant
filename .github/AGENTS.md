# .github/AGENTS.md

- Never use `pull_request_target`; keep workflow permissions minimal and explicit.
- CI always runs the classifier and its tests. Only Markdown documentation changes skip application checks; Markdown under `src` or `roles` can be application input, except `AGENTS.md` guides. Missing or unavailable diff bases and empty diffs require application checks. Rename detection is disabled so both paths are classified.
- Application, build and workflow changes run the Release build, tests and a local Docker image build without push. The always-running `gate` checks classifier and applicable job outcomes. A change to required repository checks needs owner authorization.
- CD is on hold and must not build, publish or promote images. A replacement cutover requires separate owner approval; the legacy `latest` remains unchanged.
- The optional `claude-review` workflow runs only on the owner's request, and its secrets are restricted to trusted same-repository events. Dependabot changes are reviewed with the owner, not merged automatically.
