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
  - N4: a single worker processes its bot's updates sequentially, one at a time. For a General
    assistant bot this means a slow/hung LLM call (up to `LLM_CALL_TIMEOUT_SECONDS`, or ~2x that
    while also waiting for a concurrency slot — see `LlmGateway.CompleteAsync`) delays every other
    update waiting behind it in that same chat's worker, including unrelated chats of the same bot.
    This is by design in M3a, not a bug to "fix" by making the worker concurrent — that would need
    its own design pass (ordering guarantees, per-chat isolation) out of scope here.
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

## LLM gateway and the Claude Code CLI provider (`Llm/`)

- `ClaudeCliChatClient` runs `claude -p` as a child process, once per call, through `IProcessRunner`
  (never the real CLI in tests — `FakeProcessRunner` stands in). Its exact argument list is spec
  §8.5's, not a guess — if you change it, re-read that section and this plan's "Decisions made by the
  plan" #8 in the specs repo (`2026-10-01-m3a-general-assistant.md`) first: it flags an open tension
  between dropping `--setting-sources` (not in §8.5's list) and the top-level "no user/project/local
  settings loaded" security requirement. **Never add `--bare` back** — it ignores
  `CLAUDE_CODE_OAUTH_TOKEN` and keeps tools available, which is unsafe for this specific use, not
  merely redundant.
- **Never** add a log line that could print the prompt, the model's answer, or raw stdout/stderr from
  the CLI process — only exit code, duration and exception *type* are safe to log (same rule as
  `src/Assistant.Host/AGENTS.md`'s message-text rule, extended to the CLI's own output). An
  authentication failure is the one exception with its own fixed marker line
  (`"claude-cli authentication failed"`) — never append the CLI's own text to it.
- The child process gets a from-scratch environment (`ProcessStartInfo.Environment.Clear()` then only
  `PATH`/`HOME`/`CLAUDE_CODE_OAUTH_TOKEN`/`DISABLE_AUTOUPDATER`/
  `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`/`TZ`/`CLAUDE_CODE_MAX_OUTPUT_TOKENS`) — it must never see
  `TELEGRAM_MANAGER_BOT_TOKEN`, `TOKEN_ENCRYPTION_KEY`, the connection string, or `ANTHROPIC_API_KEY`
  (which would outrank the OAuth token and bill the API outside all budgets). If `ClaudeCliOptions` or
  the environment dictionary ever grows a new entry, ask whether it belongs in that child process at
  all before adding it.
- The CLI itself is **never baked into this image** (licence requirement, spec §8.10) —
  `ClaudeCliInstallerHostedService` installs the pinned version (`CLAUDE_CLI_VERSION`) into
  `$CLAUDE_HOME` (a dedicated Docker volume) at startup, in the background, and never throws out of
  the app on failure. Don't "simplify" by adding an install step back to the Dockerfile.
- `IModelAvailability` is in-memory and process-lifetime only (lost on restart, by design — spec
  3.1), with one exception the CLI-install service relies on: every `claude-cli` entry starts marked
  unavailable at DI composition time (before the app finishes starting) and only becomes available
  once the install/version-check succeeds. Don't "fix" the general lost-on-restart behaviour by
  persisting it; the first call after a restart re-discovering availability is the intended behaviour
  for limit-based unavailability.
  - **Why `DateTimeOffset.MaxValue` and not a separate "install pending" flag** (S3): reusing the
    same mark-unavailable-until mechanism every other refusal already uses means `ModelStatus`/
    `/model` need no new state to display, and `ClaudeCliInstallerHostedService.MarkAvailable`
    clearing it is exactly the same call every other "the model works again" path already makes —
    no parallel bookkeeping to keep in sync.
  - **The consequence this has for `GeneralAssistant`**: its own "model unavailable, retry at X"
    message only shows a retry time when it is within 7 days (spec — showing "retry in 4881 days"
    for a `MaxValue` mark would be nonsensical and would also leak that the CLI is still installing
    as a real ETA). That 7-day cutoff is the only thing hiding `MaxValue` from the user; it is not a
    general "don't show far-future retries" policy, so don't repurpose it for anything else without
    re-checking this still holds.
- A model entry's `Name` (after the colon in `LLM_MODELS`) must be unique across the whole catalog —
  `/model <name>` and `chat_settings.preferred_model` both key off it, with no provider prefix
  attached, so a collision between two providers' names would let `/model` silently switch provider.
- `BudgetNoticeSender`'s dedup insert (the `budget_notices` row whose unique index is the actual
  dedup) runs through its own `AssistantDbContext`, built from `IDbContextFactory<AssistantDbContext>`,
  never through the request's shared, scoped context. A failed insert on a *shared* context would
  leave the entity tracked as `Added`, and the next unrelated `SaveChangesAsync` on that same context
  (e.g. `LlmGateway`'s own call-recording save) would try to re-insert it. Keep any future DB write
  inside `BudgetNoticeSender` on this same throwaway-context pattern, not on `_db`.
- `LlmGateway` sends budget notices (`IBudgetNoticeSender`) only *after* a call's cost is already
  recorded, re-evaluating the budget status at that point -- not with the pre-call status used for
  candidate filtering -- and only outside the concurrency gate (after it is released). The check is
  awaited with its own short timeout (`BudgetNoticeCheckTimeout`, 5s) so a slow/unreachable Telegram
  API or DB never delays the reply by more than that; any exception is caught and logged by type only,
  never rethrown onto the reply path.
- A `ModelLimitReachedException`'s `Scope` matters: `Provider` marks every `claude-cli` catalog entry
  unavailable (an account-wide session/weekly/spend/usage limit), `Model` marks only the one that was
  called (a model-named limit like "Opus limit"). Getting this backwards either over-disables working
  models or under-disables ones that are actually rate-limited.
