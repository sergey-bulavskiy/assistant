# src/Assistant.Infrastructure/AGENTS.md

## Persistence (EF Core + Npgsql)

- Snake-case naming convention (`UseSnakeCaseNamingConvention`); entity config lives in
  `Persistence/Configurations`, one `IEntityTypeConfiguration` per entity.
- Migrations live in `Persistence/Migrations` and are applied at host startup
  (`DatabaseMigrator`, with retries). Create one from the repo root:

  ```bash
  dotnet tool restore
  dotnet ef migrations add <PascalCaseName> --project src/Assistant.Infrastructure
  ```

  `AssistantDbContextFactory` is the design-time factory, so no startup project or running
  database is needed. Never edit a migration that is already on `main`; add a new one.
- On a fresh database EF logs an Error about the missing `__EFMigrationsHistory` table. It is
  harmless.
- Message storage must stay idempotent per Telegram `update_id` (restart + redelivery must not
  create duplicates or duplicate replies); integration tests in `tests/.../Persistence` cover it.

## Telegram

- The `telegram` named HttpClient has its loggers removed on purpose: Bot API URLs contain the
  token. Any new HttpClient that talks to an API with secrets in the URL needs the same.
- `TelegramClientAdapter` is the only place that touches `Telegram.Bot` types; map to our own
  types in `TelegramUpdateMapper` so Application stays library-free.
- Groups: bots see ordinary group messages only with privacy mode off or admin rights. Chats can
  migrate (group → supergroup changes the chat id); migrations are recorded in `chat_migrations`.
