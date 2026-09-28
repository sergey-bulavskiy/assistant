# src/Assistant.Host/AGENTS.md

- Config comes from environment variables: `TELEGRAM_MANAGER_BOT_TOKEN` (the manager bot's own
  token; role-bot tokens are obtained at runtime through Telegram Managed Bots, not from env),
  `TOKEN_ENCRYPTION_KEY` (base64, 32 bytes — encrypts role-bot tokens at rest),
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
- Each bot (manager and every role bot) stores its own update offset in `bots.last_update_id`,
  replacing M1's single-row `bot_state` table, so restarts resume every bot independently.
  `BotPollingCoordinator` (singleton hosted service) runs one `BotPollingWorker` per active `bots`
  row and can start a new worker at runtime, without a process restart, when a role bot finishes
  creation. Each worker keeps its own per-update failure count and skips poison updates after
  `PoisonUpdateFailureCap` attempts.
