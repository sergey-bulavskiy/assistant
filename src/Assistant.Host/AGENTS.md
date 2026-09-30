# src/Assistant.Host/AGENTS.md

- Config comes from environment variables: `TELEGRAM_MANAGER_BOT_TOKEN` (the manager bot's own
  token; role-bot tokens are obtained at runtime through Telegram Managed Bots, not from env),
  `TOKEN_ENCRYPTION_KEY` (base64, 32 bytes — encrypts role-bot tokens at rest),
  `ConnectionStrings__Assistant`, optional `Database__MigrationMaxAttempts` /
  `Database__MigrationRetryDelaySeconds`, and the optional `LLM_*` variables plus
  `CLAUDE_CODE_OAUTH_TOKEN`/`CLAUDE_CLI_VERSION` for the General assistant (all optional;
  empty/invalid `LLM_*` means LLM is off, logged once at startup, never a crash — see
  `src/Assistant.Infrastructure/AGENTS.md`'s LLM section; `CLAUDE_CLI_VERSION` falls back to this
  build's pinned default). `GIT_SHA` / `BUILD_TIME` are baked in by the Dockerfile; `HOME`/
  `CLAUDE_HOME` are fixed container paths, not owner-configurable. A new setting needs: validation, an
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
- Bot polling itself (`BotPollingCoordinator`, one worker per bot) lives in
  `src/Assistant.Infrastructure/Bots`; see that folder's guide.
