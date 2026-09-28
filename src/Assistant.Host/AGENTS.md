# src/Assistant.Host/AGENTS.md

- Config comes from environment variables: `TELEGRAM_BOT_TOKEN`, `ALLOWED_USER_IDS`
  (comma-separated; the first id is the owner and gets the startup message),
  `ConnectionStrings__Assistant`, optional `Database__MigrationMaxAttempts` /
  `Database__MigrationRetryDelaySeconds`. `GIT_SHA` / `BUILD_TIME` are baked in by the Dockerfile.
  A new setting needs: validation, an entry in `deploy/.env.example` and `deploy/docker-compose.yml`
  if required in production, and a README update.
- `BotOptions` are validated before migrations run, so bad config fails fast.
- **Logging (Serilog, compact JSON to stdout)**: never log message text, user data or the token.
  Log exception *types* (`ex.GetType().Name`), not messages, where the message could contain data;
  use `SecretRedactor` when a string might contain the token.
- **One poller per bot token.** Two processes polling the same token conflict; stop the running
  instance before starting another against the same bot.
- `/health` reports polling health (`PollingHealth`); `--healthcheck` is the container
  HEALTHCHECK entry point and calls `/health` on port 8080.
- The update offset is stored in the database (`bot_state`), so restarts resume where they left
  off. `PollingService` keeps a per-update failure count and skips poison updates after
  `PoisonUpdateFailureCap` attempts.
