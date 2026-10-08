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
  The current [dotnet/skills](https://github.com/dotnet/skills) catalog has EF Core query and data
  access guidance, but no skill for creating this application's schema migrations. Follow this
  guide for EF migrations; do not apply .NET framework or test-framework migration instructions
  to the database schema.
- On a fresh database EF logs an Error about the missing `__EFMigrationsHistory` table. It is
  harmless.
- Message storage must stay idempotent per Telegram `update_id` and per message key (restart +
  redelivery, including after an idle offset re-base, must not create duplicates or duplicate
  replies); integration tests in `tests/.../Persistence` cover it.
- Health tables (`health_profiles`, `safety_rules`, `events`, `safety_alerts`, `pending_records`) are family-scoped **and fail closed**:
  `HealthProfileStore`, `EventStore`, `SafetyAlertStore` and `PendingRecordStore` take `familyId` on every call and throw `InvalidOperationException` when
  `ICurrentFamily.FamilyId` is unset or another family. Never `IgnoreQueryFilters()` on them, and
  never touch them from a fresh DI scope (`ICurrentFamily` is unset there; the `BudgetNoticeSender`
  fresh-scope pattern works only for the unfiltered `budget_notices`). `safety_rules.profile_id` is
  the model's first real FK (cascade delete, no navigation properties). A profile is created with its
  default rules (`SafetyRuleDefaults`) in one transaction.
- Nullable consultation profile fields have a 1,000-character bound each. `SaveFieldAsync` updates
  exactly one column plus audit fields using scoped SQL; never save new fields from a stale whole
  profile snapshot. Existing profile commands write only their original columns.
- Role prompts: `roles/<role>/*.md` at the repo root are compiled into this assembly as embedded
  resources named `roles/<dir>/<file>` (`%(RecursiveDir)` gives `\` on Windows; `RolePrompts`
  normalizes it). The csproj fails the build if Health or Vet `prompt.md` or `extract.md` is missing.
  The Dockerfile copies `roles/`, and `.dockerignore` (which excludes `*.md`) re-includes
  `roles/**/*.md`; keep both when touching either file. A missing prompt is an Error at startup and
  turns that role's LLM features off, never the app.
- `events`: soft delete only (`deleted_at`, `delete_reason`); every read filters `deleted_at IS NULL`.
  `events.profile_id` is a FK with `Restrict` (a profile with events cannot be deleted). `payload` is
  jsonb holding one of Application's `HealthEventPayloads` records (snake_case), validated by
  `HealthEventValidator` before saving; Postgres reformats jsonb text, so tests parse it. `bot_id` is
  the Telegram bot id and `source_message_id` is `messages.id`; `/del` as a reply finds the source
  through `messages`. Pass only offset-0 `DateTimeOffset` values (Npgsql rejects others).
  The `note` payload holds validated `{text,tags}` with text at most 500 characters and one to five
  normalized letter-only tags.
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
- `pending_records` holds values that are waiting for a Да/Нет tap (the health bot's "ask before
  recording" flow). Like `events`, its payload is a jsonb array of `NewHealthEvent`s kept as a JSON
  **string**, because Postgres otherwise reformats a stored jsonb object and the string form is what
  survives unchanged. A row leaves the `pending` status exactly once: `TryResolveAsync` runs a
  conditional `ExecuteUpdate … WHERE status = 'pending'` inside a transaction, and only when that
  update actually changes the row does it go on to run the caller's work — for Да, that means calling
  `EventStore.AddAsync` on the very same scoped context, so the insert lands in the same transaction
  as the status change. Any failure rolls both back together and the row is left `pending`. Never
  delete a `pending_records` row directly, and never change its status anywhere outside
  `TryResolveAsync`.

## Vet persistence (`Vet/`)

- The eight `vet_*` text tables are separate from Health. Filters fail closed when CurrentFamily
  is unset; stores also check family/bot/role and exact place. Set CurrentFamily in resumed scopes.
- Source/input/result GUIDs, long event/pending/action IDs and immutable original attribution form
  the text/photo extension contract. Text sources use slot 0. Do not add photo lifecycle tables to
  the text migration. Numeric values are exact positive decimals without a human range or grid.
- Per-bot PostgreSQL advisory transaction locking serializes source pointers, profile revisions,
  pending accept/decline/review changes and action writes. The lock must run within a transaction.
  Every mutation validates all expected revisions before any write and atomically commits action,
  event states, before/after provenance, pending resolution and durable outcome. Reused keys require
  an identical fingerprint; an empty change makes no action. Rollback detaches owned mutation entries.
- Active and deleted candidate identities share the uniqueness constraint. Restores retain IDs and
  increase revisions. Undo persists both reversed and protected subsets and never retargets on replay.
- Bot-wide history withholds IDs for other-place events; pending/source/mutation reads stay in place.
  Successful provider results and exact work plans remain durable across restart. Unknown provider
  dispatches are paused, not replayed. Only subscription-only chains enable Vet model work.

## Vet meter-photo persistence (`Vet/Photos/`)

- Photo scope includes family, internal bot, Telegram bot, chat and exact nullable topic. Recheck
  active bot/member/place and stable profile identity; filters alone grant no authority. Image slot 1
  is distinct from text/caption slot 0. Bind the exact stored incoming message before download/model
  work. Source sender is immutable; the executing/confirming actor is checked separately.
- Inputs, original references and extraction evidence are immutable. Edits advance only the source
  pointer; one stable candidate/event identity survives correction, deletion and explicit restore.
  Keep blob bytes out of ordinary message/context/trace projections. Hash sharing is family-scoped.
- Global capacity locking precedes bot transaction locking. Atomically reserve and reconcile actual
  content plus active download reservations against 1 GiB, input slots 10,000 and result/attempt slots
  10,000 across all families; return aggregate counts only. A full limit never evicts prior evidence.
  Each original is bounded 10 MiB and decoded JPEG/PNG 25 MP; preserve exact archived bytes.
- Claim and persisted dispatch are separate fences. Freeze GUID AttemptKey, actor/source/input/ref
  snapshots and lease before a call; M3 records the same key/source message before provider dispatch.
  Claimed-but-unstarted work may resume; expired dispatched work is unknown and remains charged.
  Successful immutable results replay without a second call. Lost ownership/edit/revocation may
  retain old evidence but cannot install it as current. Cancellation blocks new automatic calls,
  while admitted originals remain independently archivable; never free an unknown reservation.
- Every fact-changing review requires all delivered pages/hashes, exact prompt/revision/selection
  proof, fresh authority and expected candidate/input/result/reference/event revisions. Recheck
  pending and saved collisions under the same bot transaction, then commit dispositions, facts and
  action/outcome together. One stale item writes nothing. Rollback detaches owned tracking entries;
  replay returns the committed action and first actor. Reviewed keep/protected reversal can record
  a durable no-change audit action; evidence-only reviews cannot authorize facts.
  Deliver unresolved/protected evidence as a complete current-scoped notice without acceptance
  buttons or fact authority. Failed/unknown sends wait for explicit review retry; fully delivered
  notices do not occupy recovery selection.
- Caption actual insulin uses only TEXT identity. Persist unfiltered interpretation before image
  glucose reconciliation; image edits/reprocessing cannot reapply insulin. Image processing waits
  for an applied or failed/paused caption state, and failed captions supply no inferred context.
- Reprocess selection manifests are fixed (up to 10,000 inputs) and approved in full before windows
  of at most 50 run. Exact initial proof, actor and selected attempt/reference snapshots fence each
  window. Unknown attempts require reviewed acknowledgement of possible repeated usage. Compare
  stored successes without new calls; explicit selection/refresh can show retained historical
  evidence without moving the source pointer. Deleted/manual/excluded/cancelled identities require
  specific restoration proof. Ordinary Undo stays actor/place/action-time 24h; older selected reversal
  hashes the full shown immutable/current states and protects later changes.
- Original-byte deletion is owner-only, exact-place and complete-preview-bound. Tombstone only
  selected reference revisions; facts/provenance remain. Reclaim a family blob only after all its
  references are deleted and active readers release it. Count queued reclamation until bytes are
  gone, recover it after restart, and never reacquire explicitly deleted bytes from Telegram metadata.
- One scoped VetPhotoStore instance serves its typed ports and one scoped VetDiaryStore serves
  diary/photo/reversal ports. Keep transaction ownership shared. Background work sets CurrentFamily
  explicitly and uses bounded metadata queues, not blob projections. Photo logs use fixed categories
  or exception types; no captions/values/bytes/raw provider JSON/full hashes/token URLs. Trace/export
  contracts never carry original bytes or raw extraction payloads.

## Private diagnostic traces (`Diagnostics/`)

- Capture starts only after the existing role-bot authorization gates. A trace identifies a bot
  and update, so edits remain distinct while linking to the same stored source. Each model request
  uses a local attempt UUID, with the result linked to the actual successfully saved `llm_calls.id`; failed bookkeeping
  leaves that link null. A pre-call refusal has no provider attempt or call ID.
- Every trace write uses a fresh scope/context and a bounded timeout. Trace persistence must never
  poison the business context, change a reply, or fail the update. Admission, expiry and oldest-first
  cap eviction share a PostgreSQL transaction advisory lock; do not replace it with a process-only
  lock. Accounted bytes include serialized event detail and a 1,024-byte metadata reserve per trace
  and event, not PostgreSQL physical files.
  Appending cannot recreate an evicted or expired trace. Expiry cleanup commits bounded batches,
  so a later timeout cannot roll back earlier progress. Cleanup continues with capture disabled.
- Only closed trace DTOs may be persisted. Redact known configured credentials and runtime bot
  tokens centrally; never serialize headers, arbitrary options/response metadata, exception messages,
  hidden reasoning or raw CLI events. Register role tokens through `TelegramClientFactory` without
  recording their value. Trace reads never participate in conversation context.
- A model response, application disposition and text send are separate events. Replaced text is
  `not sent`; sends record attempted text and actual Telegram result per part. Allowlisted transport
  operations distinguish new text, text with buttons and edits; an edit includes its target message
  ID before transport. Content-free attempt/delivery linkage survives the text budget, still subject
  to the finite event/storage cap. A timeout/cancelled
  send has unknown delivery. Confirmation resolution links pending/source/actor identities and must
  follow the existing conditional business transaction. Do not change Health policy for diagnostics.
- `Assistant.TraceExport` is a separate local tool, with no polling, migrations or host endpoint.
  Use a read-only database transaction, fixed export DTO and an explicit absolute destination
  outside repositories; never print captured content or overwrite an existing file. Report actual
  retained coverage, truncation and cap evictions. Derive bounded timestamp-only gap observations
  from stored incoming originals/latest edits in the selected context and observation window;
  never reconstruct content or infer switch-state periods or causes from missing traces. Exported timelines do
  not claim exhaustive callback/reaction capture or guaranteed 60-day coverage.

## Bot polling (`Bots/`)

- Each bot (manager and every role bot) stores its own update offset in `bots.last_update_id`,
  so restarts resume every bot independently. `BotPollingCoordinator` (singleton hosted service)
  runs one `BotPollingWorker` per active `bots` row and starts or stops workers at runtime, without
  a process restart, when a role bot finishes creation or is disabled/enabled/removed in
  `/settings`. Each worker keeps its own per-update failure count and skips poison updates after
  `PoisonUpdateFailureCap` attempts. Retryable Vet pre-offset admission/transport persistence errors
  use a fixed-message wrapper and cannot advance the offset through that cap. Unsupported/malformed
  permanent input is handled normally. Before each Vet poll, one serialized recovery pass handles
  at most five linked durable sources after fresh authorization; other roles keep their existing flow.
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
- Allowed updates differ per bot (`BotPollingCoordinator.AllowedUpdates`): the manager gets messages,
  callback queries and `managed_bot` events; role bots get messages, callback queries (used by the
  Health confirmations and Vet reviewed decisions; other roles answer a tap with no text) and `my_chat_member`
  (added to / removed from a chat). A role bot's tap is re-checked in `UpdateHandler` through the
  read-only `IApprovalService.FindFamilyMemberStatusAsync`/`FindPlaceStatusAsync` methods; never
  reuse the `GetOrCreate…` methods for a tap, since those create rows and DM the owners.

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
- General reply-to-all buttons use `settingsplace_autoreply_on/off:<id>`; Health reply buttons
  use `settingsplace_healthquestions_on/off:<id>`, and Vet uses `settingsplace_vetquestions_on/off:<id>`.
  All carry the **target** state, so repeated taps
  do not flip it back. Each callback checks the bot's matching role and the owner's family.
  `places.reply_to_all` is per row: a topic never inherits the chat-wide row's flag. The
  `HealthTopicQuestionsDefaultOff` migration clears existing Health flags once; new rows default off.
- Role-bot owner checks (`IFamilyOwnership` → `FamilyOwnership`) reuse
  `ManagerOwnership.IsApprovedOwnerAsync`. Removing a bot in `/settings` deletes its places but
  leaves a health bot's profile and rules (health data is never deleted implicitly).

## Telegram

- Attachment descriptors are neutral metadata on `IncomingMessage`; captions remain `Text`,
  photo sizes are variants of one source, and media-group IDs identify transport albums.
  Mapping never downloads. Filename/MIME display metadata strips controls and is bounded to 255;
  opaque file IDs are not paths. Role handlers authorize and choose their own attachment source.
- `ITelegramClient.DownloadFileAsync` writes into a caller-owned stream, enforcing actual bytes
  as well as supplied size metadata, at most 20,000,000 bytes and a 60-second linked deadline.
  Caller cancellation propagates; fixed typed failures must not expose tokens/remote messages.
  A partial failed download is not usable input. Tracing forwards downloads without capturing bytes.

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

## Document text extraction (`Health/Documents/`)

- `DocumentTextExtractor` uses PdfPig 0.1.16 for validated PDF signatures and content-order page
  text, or strict UTF-8/BOM decoding for plain text/Markdown. Validate the entire bounded text input
  even after its retained 200,000-character cap; invalid tails must not become readable prefixes.
  Byte limit is 20,000,000 actual bytes. Never decode a renamed PDF/ZIP/binary file as plain text.
- Password-protected, encrypted, corrupt, unsupported and no-text files return fixed metadata-only
  failure categories. Do not expose parser diagnostics. PDF results preserve page boundaries and
  declare text-only coverage; no OCR, image, embedded-file, script or network extraction is added.
  Dispose the parser and owned memory, keep caller streams open, and check cancellation during
  copying/decoding and between pages. Synchronous PDF open/page parsing cannot be preempted.

## LLM gateway and CLI providers (`Llm/`)

- `ProcessRunner` decodes redirected stdout and stderr explicitly as UTF-8 before any provider
  parses them. Keep this independent of the host console code page; real CLI content stays out of logs.

- `codex-cli` uses supported ChatGPT file authentication, not API-key access. Its native executable
  must be exactly `0.160.1`; every call checks `--version` before execution. `CODEX_CLI_PATH` and
  `CODEX_HOME` are optional absolute local paths; production defaults are `/usr/local/bin/codex`
  and `/home/app/.codex`. Never read/log credential files or pass API credentials to CLI children.
- Each Codex call uses a fresh temporary workspace, ephemeral execution, ignored user config/rules,
  replaced system/model instructions and a reduced pinned model catalog. Shell, MCP/plugin/app,
  web, collaboration and other unwanted capabilities must remain disabled. A read-only sandbox
  alone is insufficient. Unexpected tool/events fail the call; raw JSON events stay in memory
  and never enter ordinary logs, response metadata or traces. Temporary files are cleaned up.
- Native image input is limited to the verified Codex 0.160.1/gpt-6.1-sol combination and one
  signature-validated JPEG/PNG up to 20,000,000 bytes. The temporary input file goes through
  native `--image`; `view_image` and all other tools remain disabled. No image bytes/paths/base64
  enter prompts, traces or logs. `CODEX_IMAGE_INPUT_ENABLED=false` explicitly disables the otherwise
  enabled capability in local host configuration; compose uses the default.
- Image callers supply a durable GUID AttemptKey and stored TriggerMessageId. Commit a shared
  `llm_calls` Dispatching row before provider dispatch; finalize that same unique row with usage.
  Failed final accounting refuses the answer. Uncertain dispatches become OutcomeUnknown, and
  reusing a key never dispatches again; another scope/model is refused. Guards create no attempt
  row. Legacy text keys remain null; there is no second photo-specific usage ledger.
- Codex final output must agree with `--output-last-message`; only final answer, token usage and
  sanitized failure categories leave the adapter. `LLM_MAX_OUTPUT_TOKENS` is an instruction target
  for this pinned CLI, not a verified hard generation limit. Final text is rejected above
  eight characters per configured token (file bytes are bounded at four times that), with bounded
  process streams and timeout/cancellation as separate limits. Do not promise a hard token cap.
- A `codex-cli` entry in either raw chain excludes legacy providers before validation. Both active
  chains must be subscription-only; stale General model preferences outside the active name-only
  catalog are ignored. Remove Codex from both chains for explicit manual rollback. Subscription
  price is forced to zero regardless of `LLM_PRICES`; token/call accounting is not quota accounting.

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


## Health document persistence

- `documents` retain extracted text and immutable source metadata; original file bytes are not archived.
  `source_message_id` is a required `messages.id` FK. The separate `health_document_admissions`
  table permits an unbound source before transport storage; uniqueness is family/Telegram bot/chat/message.
  Both tables' direct reads fail closed without current family. Their stores must additionally validate
  exact family/profile/internal bot/Telegram bot/source ownership; a query filter never grants access.
  Deletion is a tombstone, and a repost under a new Telegram message is a separate source.
- Document source transactions lock the exact family-scoped `messages` row first, before admission,
  document and pending transitions. Initial document-caption EventStore/PendingRecordStore writes
  identify both bound and unbound admissions by immutable incoming source identity and exact
  family/profile/internal-bot/Telegram-bot ownership, including tombstones. They use the same lock
  and recheck deletion; reuse the current pending-acceptance transaction, never
  nest one. No network/parse work under a source transaction. Ordinary text sources keep their path.
- Conditional lease operations require the exact owned Guid and unexpired lease. Five-minute leases
  renew every 30 seconds from independent DI scopes with current family set explicitly. A failed
  heartbeat cancels work; a stale owner cannot finalize or clear a successor's lease. Never share a
  request DbContext with heartbeat operations. Lease cancellation cannot hard-preempt PdfPig parsing.
- Context reads all active metadata without a date/count ceiling, then server-project only bounded
  newest text candidates. Do not load every 200,000-character body to choose a 20,000-character prompt.
  Preserve separate stored and prompt truncation flags, total coverage and immutable posted time.
- Admission/storage transient wrappers contain fixed text and no inner exception. Document download,
  parse and transport errors log exception types only; file IDs, filenames, captions and content are
  never ordinary logs. Original bytes are neither archived nor passed to diagnostic traces.
- Bounded filename/MIME normalization must cut before a complete UTF-16 surrogate pair, retaining
  recognized filename suffixes. Npgsql encodes strings strictly; a dangling surrogate rejects an
  otherwise valid upload before its transport offset commits. Keep existing metadata column bounds.

## General memory persistence (`Memory/`)

- Facts and derived summaries are separate. Fact rows are explicit user commands, exact-place scoped,
  family-tagged and retired with tombstones. Summaries never create or retire facts. Every store
  operation rechecks CurrentFamily, active General bot, approved member and exact place; private
  chat scope must match the requesting user.
- One PostgreSQL advisory transaction lock keyed by Telegram bot ID serializes memory writes,
  summary installation, General source-edit invalidation and context resets. Acquire it only inside
  the DB transaction; never retain a lock/transaction across model or Telegram calls.
- Summary preparation covers one bounded older window. Installation rechecks reset cutoff,
  source generation, previous watermark and exact selected-source fingerprint. Source edits
  increment generation and clear summaries covering that source in the message/offset transaction.
  /new clears summary coverage but preserves explicit facts; search cannot cross the reset boundary.
- NULLS NOT DISTINCT protects exact-place state uniqueness. Russian/simple expression GIN indexes
  cover incoming text retrieval. No raw payload, retained image, private trace or other place enters
  General memory context. Logs may contain fixed categories/types, never remembered text or queries.
