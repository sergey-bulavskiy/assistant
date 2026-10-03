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
- Message storage must stay idempotent per Telegram `update_id` and per message key (restart +
  redelivery, including after an idle offset re-base, must not create duplicates or duplicate
  replies); integration tests in `tests/.../Persistence` cover it.
- Health tables (`health_profiles`, `safety_rules`, `events`, `safety_alerts`) are family-scoped **and fail closed**:
  `HealthProfileStore`, `EventStore` and `SafetyAlertStore` take `familyId` on every call and throw `InvalidOperationException` when
  `ICurrentFamily.FamilyId` is unset or another family. Never `IgnoreQueryFilters()` on them, and
  never touch them from a fresh DI scope (`ICurrentFamily` is unset there; the `BudgetNoticeSender`
  fresh-scope pattern works only for the unfiltered `budget_notices`). `safety_rules.profile_id` is
  the model's first real FK (cascade delete, no navigation properties). A profile is created with its
  default rules (`SafetyRuleDefaults`) in one transaction.
- Role prompts: `roles/<role>/*.md` at the repo root are compiled into this assembly as embedded
  resources named `roles/<dir>/<file>` (`%(RecursiveDir)` gives `\` on Windows; `RolePrompts`
  normalizes it). The csproj fails the build if `roles/health/prompt.md` or `extract.md` is missing.
  The Dockerfile copies `roles/`, and `.dockerignore` (which excludes `*.md`) re-includes
  `roles/**/*.md`; keep both when touching either file. A missing prompt is an Error at startup and
  turns that role's LLM features off, never the app.
- `events`: soft delete only (`deleted_at`, `delete_reason`); every read filters `deleted_at IS NULL`.
  `events.profile_id` is a FK with `Restrict` (a profile with events cannot be deleted). `payload` is
  jsonb holding one of Application's `HealthEventPayloads` records (snake_case), validated by
  `HealthEventValidator` before saving; Postgres reformats jsonb text, so tests parse it. `bot_id` is
  the Telegram bot id and `source_message_id` is `messages.id`; `/del` as a reply finds the source
  through `messages`. Pass only offset-0 `DateTimeOffset` values (Npgsql rejects others).
- An edited message's events are replaced by `EventStore.ReplaceMessageEventsAsync` with one
  `SaveChanges` (a failure keeps the earlier events). An unchanged event (same type, `occurred_at`
  and payload, compared with `HealthEventPayloads.SameJson` because Postgres reformats jsonb) keeps
  its row and id, so its `safety_alerts` claim still stops a repeated alert; the others are
  soft-deleted with `delete_reason = edit`. Never delete `safety_alerts` rows: old alerts stay as
  history.
  Rules are re-run on every edit, but a kept row keeps the flags from its first save; flags are
  informational only, alerts are decided by the `safety_alerts` claims. `/undo` targets the message
  with the highest source message id (not the newest `occurred_at`). Soft-deleted rows are never
  matched on edit, so editing an undone or deleted message records its readings again as new rows.

## Bot polling (`Bots/`)

- Each bot (manager and every role bot) stores its own update offset in `bots.last_update_id`,
  so restarts resume every bot independently. `BotPollingCoordinator` (singleton hosted service)
  runs one `BotPollingWorker` per active `bots` row and starts or stops workers at runtime, without
  a process restart, when a role bot finishes creation or is disabled/enabled/removed in
  `/settings`. Each worker keeps its own per-update failure count and skips poison updates after
  `PoisonUpdateFailureCap` attempts.
  - Idle re-base: after a week without updates Telegram picks the next `update_id` randomly
    (Bot API, `Update.update_id`), possibly below `last_update_id`, which `MessageStore.StoreAsync`
    would confirm and drop forever. `StoreAsync` stamps `bots.last_update_at` whenever it advances
    the offset (offset-only updates too); before each poll the worker calls
    `RebaseOffsetIfIdleAsync`, which resets `last_update_id` to 0 when `last_update_at` is older than
    `BotPollingWorker.OffsetRebaseIdleThreshold` (3 days) or null. The first update stored afterwards
    re-bases the offset. Re-basing is safe because Telegram keeps unconfirmed updates only 24h and
    the worker confirms every stored update on its next poll; a redelivered message is caught
    by the unique (bot, chat, message id) key as `Duplicate` (no reply). The threshold must satisfy
    threshold > 24h and threshold + 24h < 7 days (`last_update_at` is when we processed an update,
    up to 24h after it was created). It also covers a bot that was disabled or the host that was
    down for that long. Rows that existed before the migration (null `last_update_at`) re-base once
    right after deploy; at most a batch younger than 24h that was stored but not yet confirmed can
    be redelivered then (messages dedupe by the unique key; non-message updates may be handled twice).
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
- `ManagerUpdateHandler` dispatches and keeps `/claim`, `/newbot`, `/usage` routing and the
  approval callbacks (`place_approve`/`place_deny`, `member_allow`/`member_deny`). `/settings` and
  its callbacks (`bot_*`, `settingsplace_*`, `member_disable`/`member_enable`/`member_makeowner`)
  live in `SettingsCommandHandler`. A new settings callback must be added to its `CallbackActions`
  set, or the dispatcher answers "Пока не реализовано". Every owner re-check goes through
  `ManagerOwnership.IsApprovedOwnerAsync`.
- The reply-to-all buttons carry the **target** state (`settingsplace_autoreply_on:<id>` /
  `settingsplace_autoreply_off:<id>`), so two quick taps on one button can't flip it back. The
  callback also rejects a place whose bot is not `general` (`BotRoles.IsGeneral`, the same rule
  `UpdateHandler` routes by). `places.reply_to_all` is per row: a topic's row never inherits the
  chat-wide row's flag.
- Role-bot owner checks (`IFamilyOwnership` → `FamilyOwnership`) reuse
  `ManagerOwnership.IsApprovedOwnerAsync`. Removing a bot in `/settings` deletes its places but
  leaves a health bot's profile and rules (health data is never deleted implicitly).

## Telegram

- The `telegram` named HttpClient has its loggers removed on purpose: Bot API URLs contain the
  token. Any new HttpClient that talks to an API with secrets in the URL needs the same.
- `TelegramClientAdapter` is the only place that touches `Telegram.Bot` types; map to our own
  types in `TelegramUpdateMapper` so Application stays library-free.
- Groups: bots see ordinary group messages only with privacy mode off or admin rights. Bots
  created through Managed Bots start with privacy mode on (README step 5). Chats can
  migrate (group → supergroup changes the chat id); migrations are recorded in `chat_migrations`.
- Reactions (`SetReactionAsync`): one `ReactionTypeEmoji`, or an empty list to clear. Bots may use
  only the emoji Telegram allows (✍ U+270D without a variation selector, and 👍, are allowed); a
  group can restrict reactions further, so callers fall back and never fail on a reaction.

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
- `BudgetNoticeSender.NotifyPeriodAsync` checks whether a (kind, start, threshold) notice already
  exists (a plain `AnyAsync`, through its own fresh scope) *before* ever attempting the insert --
  the common case (every call after the first one for a given crossing) never even reaches the
  insert, so it never logs an Error for what is really just "already sent". The insert itself (plus
  its unique-violation catch) still runs unconditionally after that check: two callers can both pass
  the `AnyAsync` read before either has inserted, so the insert+catch is what actually prevents a
  double-send under a real race, not the check. Both the check and the dedup insert (the
  `budget_notices` row whose unique index is the actual dedup) run through their own
  `AssistantDbContext`, resolved from a brand-new DI scope created via
  `IServiceScopeFactory.CreateAsyncScope()`, never through the request's shared, scoped context. A
  failed insert on a *shared* context would leave the entity tracked as `Added`, and the next
  unrelated `SaveChangesAsync` on that same context (e.g. `LlmGateway`'s own call-recording save)
  would try to re-insert it. The new scope's `ICurrentFamily` is never set, which is fine since
  `budget_notices` has no family filter. Keep any future DB write inside `BudgetNoticeSender` on
  this same fresh-scope pattern, not on `_db`. Each admin send is wrapped in its own try/catch too --
  one owner's send failing (e.g. they blocked the manager bot) must never stop the others' DMs.
- `safety_alerts` dedup works differently and must stay that way: `SafetyAlertStore.TryClaimAsync`
  runs one raw `INSERT … SELECT … FROM events … ON CONFLICT (event_id, rule_key) DO NOTHING`
  (`Database.ExecuteSqlInterpolatedAsync`) on the **request's** context, and the caller sends the
  alert only when exactly 1 row was affected. Nothing is tracked and a conflict is not an exception,
  so the shared context is never poisoned; a fresh DI scope (the `budget_notices` pattern) would
  have no `ICurrentFamily`, which the fail-closed health stores refuse. The `SELECT` also refuses an
  event of another family or a deleted one. The statement names the table and columns by hand: keep
  it in sync with `SafetyAlertConfiguration` (`SafetyAlertStoreTests` catch a mismatch). Nullable
  parameters are wrapped in `CAST(… AS numeric|integer)` so Postgres knows their type.
- `LlmGateway` never awaits the post-call budget-notice check on the reply path at all: once a
  call's cost is recorded, it hands the whole re-evaluate-and-notify step to `IBudgetNoticeDispatcher`
  (`BudgetNoticeDispatcher.cs`) and discards the returned `Task`
  (`_ = _budgetNoticeDispatcher.Dispatch();`) -- the dispatcher creates its *own* DI scope (its own
  `AssistantDbContext`, its own `IBudgetGuard`/`IBudgetNoticeSender`) inside a `Task.Run`, so the
  request's shared, scoped `_db` is never touched by it and a slow/unreachable Telegram API or DB
  can never delay the reply by any amount, not just by a bounded timeout. The dispatcher re-evaluates
  budget status fresh (not the pre-call status used for candidate filtering) and bounds its own work
  with a short timeout (`BudgetNoticeDispatcher`'s `CheckTimeout`, 5s); any exception (including that
  timeout firing) is caught and logged by type only, inside the dispatcher, never rethrown anywhere.
  `IBudgetNoticeDispatcher` exists specifically as a test seam: `Dispatch()` returns the started
  `Task` *without having awaited it* so a test can await it directly for deterministic, bounded
  completion -- the reply path itself must still only ever discard what it returns, never await it.
- A `ModelLimitReachedException`'s `Scope` matters: `Provider` marks every `claude-cli` catalog entry
  unavailable (an account-wide session/weekly/spend/usage limit), `Model` marks only the one that was
  called (a model-named limit like "Opus limit"). Getting this backwards either over-disables working
  models or under-disables ones that are actually rate-limited.
- **API providers (`anthropic:`/`openai:` entries):** `IChatClientProvider` registers one client per
  catalog entry for these two (keyed `"prefix:modelName"`), not one shared client per prefix like
  `claude-cli` — each entry gets its own `IChatClient` but all entries of the same provider share one
  `HttpClient` (confirmed safe: neither SDK's `IChatClient.Dispose()` disposes a passed-in
  `HttpClient`). Both official SDKs are constructed with `MaxRetries = 0` (fallback is this gateway's
  job, not the SDK's), a proxy-aware `HttpClient` built by `ProxyHandlerFactory`, and
  `HttpClient.Timeout = Timeout.InfiniteTimeSpan` (each factory already sets the SDK's own timeout to
  `callTimeout + 5s`, and the gateway's own `CancelAfter(callTimeout)` always wins that race). A 429
  or 400 is classified into `ModelLimitReachedException`'s `Provider`/`Model` scope by reading the
  response **error body** (`error.type`/`error.details.error_code`/`error.message` for Anthropic,
  `error.code`/`error.type` for OpenAI), not by whether a `Retry-After` header is present — both a
  genuine rate limit and a provider-side spend cap return the same 429 shape. **Never** pass
  `ANTHROPIC_API_KEY`/`OPENAI_API_KEY`/`ANTHROPIC_PROXY`/`OPENAI_PROXY` anywhere near
  `ClaudeCliOptions` or the CLI child process — they belong only inside
  `InfrastructureServiceCollectionExtensions.cs`'s DI wiring (the `LlmOptions` construction and the
  `IChatClientProvider` factory lambda), confirmed by a regression test that builds a real
  `ClaudeCliChatClient` call with these set as real process environment variables and asserts the
  child environment contains neither.
- An invalid `ANTHROPIC_PROXY`/`OPENAI_PROXY` (`ProxyHandlerFactory.Create` fails) drops every
  `LLM_MODELS`/`LLM_FAST_MODELS` entry of that one provider (one Error each), the same way any other
  invalid paid entry (missing key, missing price, no budget configured) is dropped individually — it
  never throws and never stops `claude-cli` entries or the other provider from working. If every
  surviving entry turns out to need a now-invalid proxy, LLM itself falls back off, same "zero valid
  models means off" rule `LlmConfigParser.Parse` already uses for an empty `LLM_MODELS`.
- `OPENAI_BASE_URL` is validated the same way, at the same place (right next to the proxy checks in
  `InfrastructureServiceCollectionExtensions.cs`), for the same reason: `OpenAiChatClientFactory.Create`
  does a bare `new Uri(baseUrl)` with no try/catch, inside the `IChatClientProvider` singleton
  factory lambda -- an invalid value there would throw while resolving that lambda and break every
  entry of every provider, including `claude-cli`, not just `openai:` ones. An invalid value drops
  every `openai:` entry (one Error, naming only the variable), never the raw value.
- Group authorship for the two API providers is folded into the message text itself
  (`AuthorFoldingChatClient`, wrapping the SDK client inside both `AnthropicChatClientFactory` and
  `OpenAiChatClientFactory`) as `[author]: text`, since neither SDK's Messages API has a per-message
  author field the way `claude-cli`'s own `<msg author="...">` framing does. Don't add this folding
  to `ContextBuilder` or `LlmGateway` itself -- it is provider-specific and must stay out of
  `claude-cli`'s own path, which keeps reading `ChatMessage.AuthorName` directly.
- Soft budget state's candidate order (`LlmGateway.ApplyBudgetFilter`): the chat's own current
  preference stays first if Soft allows it at all, then every `LLM_FAST_MODELS` entry in *that
  variable's own* declared order (not the main `LLM_MODELS` chain's -- the owner may rank them
  differently there), then every zero-price entry in chain order. Don't "simplify" this back to
  filtering the chain in place; a multi-entry `LLM_FAST_MODELS` list needs its own order honoured
  independently of where those models happen to sit in the main chain.
- Tiers: `ModelCatalog.GetCandidateOrder` gives `smart` the `LLM_MODELS` chain and `fast` the
  `LLM_FAST_MODELS` entries in their own order followed by the remaining `LLM_MODELS` entries, so a
  `fast` call still works with no fast model configured or all of them out of limits.
- **Budgets are platform-wide by design, not per-family** — `BudgetGuard`'s spend query and
  `BudgetNoticeSender`'s dedup insert both use `IgnoreQueryFilters()` deliberately, with a comment
  saying so each time. Don't "fix" this to be family-scoped; that would silently defeat the whole
  point (a family could spend without the platform-wide cap ever tripping). `BudgetState` (not the
  rounded, display-only `Percent`) is what every threshold comparison and notice dedup must use.
- A proxy URL (`ANTHROPIC_PROXY`/`OPENAI_PROXY`) may carry embedded credentials — never log it, not
  even at Debug, not even in an exception message. `ProxyHandlerFactory` never returns the URL itself
  on failure, only the variable name (`$"{variableName} must be an absolute http://, https:// or
  socks5:// proxy URL."`); if you add any logging near proxy handling, log only whether a proxy is
  configured, never its value.
- `llm_calls.chat_id`/`topic_id`/`trigger_message_id` come from `LlmRequest` and are copied into
  every attempt row. `trigger_message_id` is the triggering message's `messages.id` (the same id
  space as the `/new` cutoff in `chat_settings.context_start_message_id`), never the Telegram
  message id. A null means the row is not counted (rows from before these columns existed, or an
  unstored trigger). `/tokens` (`LlmUsageQuery`) sums only `Ok` rows after the cutoff, scoped by
  family. `llm_calls.bot_id` holds the bot's **Telegram** id (as `messages.bot_id` does), not
  `bots.id`.
