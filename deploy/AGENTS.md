# deploy/AGENTS.md

- `deploy/.env` is off limits (root `AGENTS.md` privacy rules). Only `.env.example` (placeholder
  values) is tracked; keep it in sync with the variables `docker-compose.yml` uses.
- `docker-compose.yml` runs `postgres` (named volume `postgres-data`), `app`
  (`ghcr.io/sergey-bulavskiy/assistant:${IMAGE_TAG:-latest}`) and Watchtower. Watchtower only
  touches containers with the `com.centurylinklabs.watchtower.enable` label — keep it off
  `postgres`.
- Rollback = set `IMAGE_TAG=sha-<7>` in `.env` and `docker compose up -d`; Watchtower then leaves
  the pinned tag alone.
- `POSTGRES_PASSWORD` is interpolated into a connection string: letters and digits only.
- Deploying or restarting anything on the owner's machine is an outward action — ask first.
- `latest` is moved only by CD's `promote` job, after the real-Telegram smoke test passes when the
  gate is enabled (repository variable `SMOKE_ENABLED`, see `.github/AGENTS.md`). A `sha-` tag whose
  smoke run failed exists in GHCR but was never promoted; never pin it for rollback.
- A new named volume (e.g. `claude-home`) or new env var in `docker-compose.yml` only takes effect
  after `git pull` + `docker compose -f deploy/docker-compose.yml up -d` on the host: Watchtower only
  replaces the `app` image on the *existing* container, it never re-reads the compose file itself.
