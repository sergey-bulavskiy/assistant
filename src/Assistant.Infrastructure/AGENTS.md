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

## Bot polling (`Bots/`)

- Each bot (manager and every role bot) stores its own update offset in `bots.last_update_id`,
  so restarts resume every bot independently. `BotPollingCoordinator` (singleton hosted service)
  runs one `BotPollingWorker` per active `bots` row and starts or stops workers at runtime, without
  a process restart, when a role bot finishes creation or is disabled/enabled/removed in
  `/settings`. Each worker keeps its own per-update failure count and skips poison updates after
  `PoisonUpdateFailureCap` attempts.
- Allowed updates differ per bot: the manager gets messages, callback queries and `managed_bot`
  events; role bots get messages and `my_chat_member` (added to / removed from a chat).

## Manager bot and approvals (`Manager/`, `Families/`)

- The role chosen in `/newbot` waits in `pending_bot_creations` (`PendingBotCreations`) until
  Telegram reports the created bot. It is removed only in the transaction that saves the bot, so
  a failed attempt leaves it for the redelivered update; entries older than a day are ignored.
- Which owner DMs to edit once an approval is resolved is in memory only (`ApprovalService`): after
  a restart, a late tap still resolves correctly but the other owners' DMs keep their buttons.
- Callback data carries sequential ids (`place_approve:1`), so every callback re-checks that the
  tapping user is an approved owner of the row's family.

## Telegram

- The `telegram` named HttpClient has its loggers removed on purpose: Bot API URLs contain the
  token. Any new HttpClient that talks to an API with secrets in the URL needs the same.
- `TelegramClientAdapter` is the only place that touches `Telegram.Bot` types; map to our own
  types in `TelegramUpdateMapper` so Application stays library-free.
- Groups: bots see ordinary group messages only with privacy mode off or admin rights. Bots
  created through Managed Bots start with privacy mode on (README step 5). Chats can
  migrate (group → supergroup changes the chat id); migrations are recorded in `chat_migrations`.
