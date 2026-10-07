# src/Assistant.Host/AGENTS.md

- Config comes from environment variables: `TELEGRAM_MANAGER_BOT_TOKEN` (the manager bot's own
  token; role-bot tokens are obtained at runtime through Telegram Managed Bots, not from env),
  `TOKEN_ENCRYPTION_KEY` (base64, 32 bytes — encrypts role-bot tokens at rest),
  `ConnectionStrings__Assistant`, optional `Database__MigrationMaxAttempts` /
  `Database__MigrationRetryDelaySeconds`, and the optional `LLM_*` variables plus
  `CLAUDE_CODE_OAUTH_TOKEN`/`CLAUDE_CLI_VERSION` for the General assistant (all optional;
  empty/invalid `LLM_*` means LLM is off, logged once at startup, never a crash — see
  `src/Assistant.Infrastructure/AGENTS.md`'s LLM section; `CLAUDE_CLI_VERSION` falls back to this
  build's pinned default). ChatGPT subscription auth uses the private Codex home volume, not an
  API key or env token. Codex CLI `0.160.1` is pinned in the Linux amd64 image. `GIT_SHA` /
  `BUILD_TIME` are baked in by the Dockerfile; `HOME`/`CLAUDE_HOME` and the production `CODEX_HOME`
  are fixed container paths. Optional local `CODEX_CLI_PATH`/`CODEX_HOME` must be absolute; the CLI
  version is mandatory and has no override. The compose deployment uses image defaults. A new setting needs: validation, an
  entry in `deploy/.env.example` and `deploy/docker-compose.yml` if required in production, and a
  README update.
- `BotOptions` are validated before migrations run, so bad config fails fast.
- **Logging (Serilog, compact JSON to stdout)**: never log message text, user data or the token.
  Log exception *types* (`ex.GetType().Name`), not messages, where the message could contain data;
  use `SecretRedactor` when a string might contain the token.
- **One poller per bot token.** Two processes polling the same token conflict; stop the running
  instance before starting another against the same bot.
- `/health` reports polling health (`PollingHealth`); `--healthcheck` is the container
  HEALTHCHECK entry point and calls `/health` on port 8080.
- Private debug capture uses `DEBUG_TRACES_ENABLED` (default `false`),
  `DEBUG_TRACES_RETENTION_DAYS` (1–60; default 60), `DEBUG_TRACES_MAX_DETAIL_BYTES`
  (1024–262144; default 262144), `DEBUG_TRACES_MAX_STORAGE_BYTES`
  (1048576–104857600; default 104857600), and `DEBUG_TRACES_MAX_EVENTS`
  (16–512; default 256). Invalid settings disable capture with a fixed warning, never crash
  startup. Cleanup runs at startup and hourly even with capture off. Logs may report operational
  categories only; captured content belongs exclusively in the private trace tables and explicit
  local export. The gateway sees parsed final responses only, never raw provider process streams.
- Bot polling itself (`BotPollingCoordinator`, one worker per bot) lives in
  `src/Assistant.Infrastructure/Bots`; see the Infrastructure guide's polling section. Vet admission
  must succeed before its update offset advances. Its narrow bounded recovery hook starts with a
  fresh family scope and current authorization; unknown provider calls never resume automatically.

- An active Vet bot has one coordinator-owned lifetime joining its poller and independent
  `VetPhotoBackgroundLoop`. If either ends, cancel and await both; disable/remove/shutdown must
  drain both before disposing the token source or starting a replacement. The photo loop uses
  the same receiving bot client, never a second getUpdates poller.
- Polling durably admits/binds photo metadata before its offset commits; it does no image download
  or vision call. The photo loop uses fresh family scopes and current bot/member/place checks,
  bounded five-item passes and a global image execution gate of 1, then existing M3 guards. Saved
  results/reviews resume without redispatch. `/health` remains polling health; it does not certify
  photo queue progress, provider quota or the accuracy of a rendered reading.
- Photo extraction derives subscription-only eligibility from existing LLM chains and pins
  `gpt-6.1-sol` image capability. `CODEX_IMAGE_INPUT_ENABLED=false` is the existing local disable
  switch; no new Vet-photo configuration variable or paid-provider fallback exists.
