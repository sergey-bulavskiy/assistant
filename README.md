# Assistant

A family assistant built from Telegram bots. A **manager bot** sets up a family and creates
**role bots** for it; role bots quietly record the messages of approved people in approved chats
into PostgreSQL, one role bot -- the **General assistant** -- can also answer with a real LLM
(a CLI subscription provider or a deliberately configured API provider) once configured, and a **health** role bot keeps a household member's tracking profile and safety
thresholds. Later milestones add more assistant
features on top of this pipeline.

## What it does

**Manager bot** (the bot whose token you configure):

| Command | Who | What |
|---|---|---|
| `/claim <code>` | anyone, once | Creates the family and makes the sender its first owner. While no family exists, each start prints a fresh code in the log. |
| `/newbot <role>` | owners | Replies with a Telegram link that creates a new role bot (role: up to 64 characters). Once confirmed, the bot starts polling without a restart. The role is kept for a day, across restarts, until you confirm. |
| `/settings` | owners | Lists bots, places and users with buttons: disable/enable/remove a bot or place, set General's "Отвечать на все: выкл/вкл" or Health's "Отвечать на вопросы без упоминания: выкл/вкл" for one approved place, allow/deny a user awaiting approval, disable/enable a user, or make an approved user an owner. A place line shows the bot username and its forum topic id, if any. |
| `/usage` | owners, private chat only | Platform-wide spend/state for today and this calendar month (if budgets are configured) plus a per-bot/per-model call/token/cost breakdown for your own family. |

**Role bots:**

- A new **place** (a group the bot is added to, or a forum topic it sees for the first time) needs
  an owner's Yes/No in a DM from the manager bot before any message from it is stored.
- A new **user** writing to any of the family's bots needs an owner's Allow/Deny; once allowed, they
  can use every bot of the family.
- Messages from approved users in approved places are stored exactly once per Telegram update, even
  across restarts. In private chats the bot replies `Получил ✅ #<id>`; in groups it stays silent.
- `/start` (private chats) and `/version` (any chat) reply with a greeting and the running version.

**General assistant** (a role bot created with `/newbot general`): answers with a real LLM instead
of just storing messages.

- Private chats: replies to every ordinary message. Approved groups/topics: only when addressed
  (mentioned by @username, or replied to) — a plain message in a group it's in is still stored, just
  not answered.
- **Reply to all:** an owner can switch "Отвечать на все" on for one group or one forum topic in the
  manager's `/settings`. The assistant then answers every ordinary text message there, not only
  mentions and replies. Each place has its own switch; a topic does not inherit the whole chat's.
  Commands, edits and non-text messages are still ignored, and the rate/daily limits and budgets
  still apply. When such a message can't be answered (limit, budget, models unavailable, an error,
  or the assistant isn't configured), the bot stays silent instead of posting the refusal;
  mentioned or replied messages still get the usual refusal text.
- `/tokens` (any approved member, in any chat or topic with the assistant): answered calls, input
  and output tokens and the models used in this chat/topic since the last `/new` (or since this
  feature was installed, if `/new` was never used there). Calls made before that are not counted.
- `/new` starts a fresh conversation in that chat/topic (earlier messages stop being sent as
  context). `/model` shows the configured models and lets you pin this chat to one (`/model auto`
  returns to the default). `/version` as usual.
- Needs `LLM_MODELS` and `CLAUDE_CODE_OAUTH_TOKEN` configured (see "Set up the General assistant"
  below) — without them it replies "Ассистент пока не настроен." and every other bot keeps working
  normally.

**Health assistant** (a role bot created with `/newbot health`): a health tracking assistant for one
household member. One health bot tracks exactly one person (its profile, created with
published-guideline default thresholds on its first message). Turn off Group Privacy for it
(step 5) before adding it to the tracking group or topic.

> **It does not replace a doctor.** It checks recorded readings against safety rules and sends fixed
> alerts, but those rules are unconfirmed until the doctor's thresholds are set with `/threshold`
> (alerts say "не подтверждено врачом" until then). Enter the doctor's thresholds as soon as you
> have them, and call the doctor or emergency services whenever in doubt — don't wait for the bot.

Every new ordinary text message in the health bot's chats (at least 3 characters, not only emoji)
goes to the model once, on the `fast` tier: `LLM_FAST_MODELS` first, then the rest of `LLM_MODELS`.
An owner can enable answers to unmentioned questions for one approved group or topic in `/settings`.
The existing `reply_to_all` flag is off for new Health places, and a one-time migration turns off
previously unused Health flags. General settings are unchanged. When enabled, a Health question
gets an answer without a mention after the existing alert and clarification checks; ordinary
chatter remains silent. Direct mentions and replies work with the setting off. Recording and
confirmation run regardless of this answer setting.
The model turns the message into records (glucose, insulin, meal, symptom, weight, blood
pressure, or a note); the bot validates them, saves them and sets ✍ on the message (👍 where ✍ is not allowed).
The model can also record short observations as notes in the person's words, with one to five
model-selected tags. A note appears in `/today` as `заметка: … #tag`, gets the same ✍ reaction,
and can be edited, undone or deleted like another record. Notes do not trigger safety alerts.
Otherwise it stays silent. If a reading cannot be recorded (an unknown unit, an implausible value, a
time it cannot place) it asks once, as a reply. Unrelated numbers, including arithmetic operands,
are not readings and do not call for a measurement clarification. Bare ambiguous reading reports
still get a clarification; a message mixing arithmetic and health readings still records the readings.
If the model is unavailable or its answer is
unreadable, nothing is recorded and the bot replies "⚠️ Не смог обработать сообщение — ничего не
записано. …" (at most once per 10 minutes per chat or topic). Editing a message (up to 24 hours after sending it) reads it again, with one more model call:
readings that did not change stay as they are (same number in `/today`, no repeated alert), changed
or removed readings are deleted, and new ones are recorded. A changed dangerous value gets its alert
again. If no readings are left, the ✍ disappears. Editing a message whose readings were removed
with `/undo` or `/del` records them again as new ones (the edit is the new truth). If the edit cannot be read (model unavailable), the
earlier records stay and the usual notice or quick-scan alert is sent. Edited commands are not run
again.

**Safety alerts.** Every newly recorded reading is checked by fixed rules in code (never by the
model) against the profile's thresholds (`/thresholds`). A dangerous value or symptom gets a fixed
alert right away, as a reply in the same chat or topic: "⚠️ …" (contact the doctor) or "🚨 …"
(urgent: contact the doctor or call emergency services, with the profile's emergency phone). Alert
texts are fixed templates that point to the doctor's plan; they are never written by the model and
never suggest a medicine or a dose. Each alert names its threshold source: "не подтверждено врачом"
for the published-guideline defaults, "порог от врача" once an owner has entered the doctor's value
with `/threshold`. A glucose reading at or above the target for its context is only marked on the
record (no message). Readings older than 12 hours are recorded without an alert (a new symptom
posted with an older dangerous blood pressure reading still gets the combination alert). Each reading
alerts at most once, also when Telegram delivers the message again.

A quick scan of the message text also runs, so that dangerous values written in the supported
formats are not silently ignored. Supported formats: glucose as a keyword ("сахар", "глюкоза",
"глюкометр") followed within 30 characters by a number with up to two decimals ("сахар 2.5",
"глюкометр показал 2.55"); blood pressure as "150/95" or "150 на 95". It runs when the model's
answer is unusable or the model is unavailable, when the model returns a valid answer with no
readings and nothing unclear, and for a metric (glucose or blood pressure) the model recorded
nothing of while recording other readings. Nothing is recorded from the scan: a dangerous value
gets the fixed alert followed by "Ничего не записано — повторите сообщение позже.", and a value
that is not plausible (e.g. "сахар 250", most likely another unit) gets the clarification; neither
is throttled. Readings written any other way get only the failure notice when extraction failed.

**Questions.** In a private chat, the bot can answer a question without a mention. In a group or
topic, it answers when a message mentions the bot (`@username`), replies to one of its messages,
or the owner has enabled "Отвечать на вопросы без упоминания" for that approved place. The extraction call tells
whether a message is a question; an eligible question is answered even when readings were recorded
from the same message (the answer's context already includes them), but never when the message got a
clarification, a quick-scan reply or a safety alert for a recorded reading (that fixed reply is the answer) or is an edit.
An addressed new message that produced nothing at all (extraction worked, but no reading, no question,
no clarification or alert, e.g. a greeting) gets one short fixed hint as a reply, "Слушаю. Запишите
показатель (например: сахар 5.8 после обеда) или задайте вопрос.", at most once per 5 minutes per chat/topic
(in memory); the hint also fires on any addressed group reply to the bot (e.g. a thank-you after an answer), under the same throttle; very short or emoji-only texts are not extracted and get no hint. If extraction fails on an addressed question, only the
failure notice is sent, no answer. The answer is a second model call on the `smart` tier
(`LLM_MODELS` order) with the profile's context: the stage week, the context note (`/setnote`), the
thresholds with their source and the readings and notes of the last 24 hours, plus the last few messages of the
chat. Answers never contain dose advice: besides the instruction in the prompt, a fixed filter in
code replaces any answer that looks like dose advice with "Я не даю советов по дозам лекарств. Это
вопрос к врачу — …". The filter is conservative and may over-refuse a harmless answer: it also
refuses a number with a dose unit next to a time of day, and any answer with letters other than Latin
or Cyrillic (answers are asked for in Russian, or in English for an English question). Every answer
ends with "Не заменяю врача." When the model is unavailable the bot replies with the General
assistant's short notices ("Слишком много запросов, подождите минуту.", …). Answers are kept as
conversation context; alerts and other fixed texts are not.

**Ask before recording.** Besides the values themselves, the model also classifies what the sender
meant by each one. A value reported as a fact is recorded as described above. A value that only
appears inside a question or a hypothetical ("а 10 — это много?") is never recorded and gets no
buttons. When the wording could be read either way ("сахар 10 - высокий?"), nothing is recorded
immediately: once an eligible question has been answered, the bot posts
"Записать глюкоза 10.0 ммоль/л?" with **Да** and **Нет** buttons. Any approved family member may tap
either button, and whichever tap arrives first is the one that counts. Да saves the value under the
name of whoever sent the original message (✍ is then added to that message) and rewrites the button
message to "Записано: …"; Нет instead rewrites it to "Не записано." and nothing is saved. A second
tap after the first gets "Уже решено.", a tap more than 24 hours after the question gets "Время
вышло — напишите значение ещё раз.", and a tap from someone outside the family gets "У вас нет
прав.". Editing the original message closes its buttons the same way and re-reads the edited text
from scratch — edited messages are never asked about, so an unclear value in an edit is recorded
directly. Safety checks never wait for Да/Нет: a dangerous value still triggers its fixed alert right
away even if it was only asked about, and a later Да does not send that same alert a second time. If
the model omits the classification for a value, the bot falls back to its separate "is this a
question" flag: a value in a message flagged as a question is treated as asked-about, anything else
is recorded as usual.

**Undo by saying so.** Phrases such as "удали это", "не записывай" or "нет, я только спросил" — sent
either as a reply to a message or addressed to the bot directly — remove the records of exactly one
message, following the same target rules as `/undo`: first the message being replied to (deleting its
records, or closing its still-open Да/Нет question), and otherwise your own most recently recorded
message in that chat or topic from the last 24 hours, or your most recent still-open question if that
is more recent. Recognizing this kind of request is entirely the model's job — the code never removes
more than the one targeted message and never retracts an alert that was already sent. The bot answers
with "Удалено: …", "Не записано." or "Нечего отменять.". This only works when you post in the
tracking group under your own name rather than anonymously as the group, because an anonymous sender
can't be matched to their records; posting anonymously gets "Не могу определить автора — ответьте на
сообщение командой /del." instead.

Extraction makes one LLM call per text message in the health bot's chats. These calls count toward
`LLM_CALLS_PER_DAY` and `LLM_CALLS_PER_MINUTE`, which are per family, per UTC day, and shared with
the General bot — raise `LLM_CALLS_PER_DAY` accordingly. An answered question makes one more call
(`smart` tier) that counts the same way.

| Command | Who | What |
|---|---|---|
| `/start` | anyone, private chat | What the bot does and its commands. |
| `/week` | any approved member | Current stage week and day ("3 нед. 2 дн."), counted from the stage start date in the profile's time zone. |
| `/profile` | any approved member | Stage start date, stage week, time zone, emergency phone, context note, thresholds summary. |
| `/thresholds` | any approved member | Every safety rule with its values and source: "врач" (entered with `/threshold`) or "не подтверждено врачом" (published-guideline defaults). |
| `/today` | any approved member | Today's records (the profile's local day), oldest first, each with its number (`#12`). |
| `/notes` or `/notes <tag>` | any approved member | Last 10 notes across all dates, or up to 20 notes with the exact tag from the past 90 days, newest first; dates use the profile's time zone. |
| `/undo` | any approved member | Deletes the records of your latest recorded message in this chat or topic (up to 24 hours old). |
| `/del` | any approved member | As a reply to a message: deletes the records made from it. `/del 12` deletes record #12. |
| `/setstart ДД.ММ.ГГГГ` | owners | Sets the stage start date (not in the future, at most 300 days ago). |
| `/settz Area/City` | owners | Sets the profile's time zone (IANA id such as `Europe/Berlin`; default `UTC`). |
| `/setphone <text>` | owners | Emergency number text for alerts (default "103 или 112"; up to 100 characters). |
| `/setnote <text>` | owners | Context note for answering questions (up to 500 characters); `/setnote -` clears it. |
| `/threshold <rule> <field> <value>` | owners | Sets one value of a rule (only the fields `/thresholds` shows for it); the rule's source becomes "врач". Enter the doctor's values. |
| `/threshold <rule> default` | owners | Restores that rule's default values. |
| `/version` | anyone | Running version. |

Other members get "Только владелец семьи может менять профиль." for owner commands. Voice messages
and photos in a private chat get a note that only text is supported for now.

## Privacy

This project is designed to later handle personal and health data at runtime, in the database. **No
personal data lives in this repository** — not in code, tests, commit history or this README. See
`AGENTS.md` for the full rules.

## Private debug traces

Detailed diagnostics are off by default. Explicit local `DEBUG_TRACES_ENABLED=true` enables
continuous rolling capture for authorized role-bot interactions: the source, assembled model
request, parsed final response, application decisions, and attempted/sent text. Rejected or
replaced final answer text is private and labeled **not sent**; the actual replacement and its
delivery are recorded separately. Traces are separate from conversation history and never replayed.
Manager interactions and unauthorized updates are excluded. Credentials, hidden reasoning,
headers and raw process streams are excluded; known application credentials are redacted before
persistence. Redaction cannot identify every secret typed into ordinary message text, so protect
the database and local exports as sensitive data. Captured content never enters ordinary logs.

Retention is at most 60 days from interaction creation. The default cumulative detail allowance is
256 KiB per interaction, with 256 events, and total accounted storage is capped at
100 MiB. Oldest interactions may be evicted earlier; exports report actual retained coverage,
truncation and cap eviction counts. Accounting counts serialized detail plus a 1,024-byte metadata
reserve per trace and event, rather than PostgreSQL physical file size. Cleanup runs at startup
and hourly, including while capture is off.
Diagnostic write failures are best effort and do not change normal processing or delivery.

Optional local settings can reduce the limits: `DEBUG_TRACES_RETENTION_DAYS` (1–60),
`DEBUG_TRACES_MAX_DETAIL_BYTES` (1024–262144), `DEBUG_TRACES_MAX_STORAGE_BYTES`
(1048576–104857600), and `DEBUG_TRACES_MAX_EVENTS` (16–512). Invalid configuration disables
capture with a sanitized warning. Production compose defaults remain off; applying new compose
variables requires an explicitly authorized container recreation.

Use the separate read-only local export tool with `ConnectionStrings__Assistant` set privately.
Choose an explicit absolute output path outside repositories; the tool refuses existing files.
It starts no bot or host and applies no migrations:

```powershell
dotnet run --project src/Assistant.TraceExport -- --message-id 123 --out C:/private/trace.json
dotnet run --project src/Assistant.TraceExport -- --bot-id 111 --chat-id 222 --telegram-message-id 333 --out C:/private/trace.json
```

The second form resolves a Telegram source reference. An export contains only linked original,
edit and captured confirmation timelines, actual LLM-attempt summaries and delivery results.
Text edits are distinguished from new sends, including their target message IDs. Timeouts have
**unknown** delivery; generated or attempted text is never labeled delivered. Attempt links and
bounded delivery metadata survive exhaustion of the text allowance, subject to the event/storage cap.
Disabled periods, expiry, eviction and failed writes can leave gaps; callback/reaction capture
is not exhaustive. Coverage also lists up to 64 timestamp-only observations of incoming originals
or latest edits without a retained trace in the selected family/bot/chat/topic window, with a total
count and omitted count. Their cause and any continuous disabled period are unknown; no message
content is reconstructed. The tool reports not found when no linked trace remains and prints no content.

## Prerequisites

- A Windows PC (or any Docker host) with **Docker Desktop** installed, WSL2 backend enabled, and
  **set to start automatically with Windows** (Settings → General) — the bot only runs while Docker
  is running.
- A Telegram account to create the bot.

## 1. Turn your bot into the manager bot

This milestone's manager bot creates every other bot for you through Telegram's **Managed Bots**
feature — you no longer create bots by hand in BotFather except this first one.

1. Open a chat with [@BotFather](https://t.me/BotFather) in Telegram.
2. Send `/newbot`, follow the prompts, and choose a name and username. BotFather replies with a
   token like `123456789:AAExampleTokenValueDoNotUseThisOne`. Copy it — this is
   `TELEGRAM_MANAGER_BOT_TOKEN`.
3. Open BotFather's own Mini App (or send `/mybots` → your bot → Bot Settings) and enable
   **"Allow bot to manage other bots"** for this bot. Without this, `/newbot` in the running
   assistant will fail — Telegram will reject the creation link.

## 2. Generate a token encryption key

Role-bot tokens (fetched automatically when `/newbot` completes) are encrypted at rest. Generate a
random 32-byte key, base64-encoded:

```bash
openssl rand -base64 32
```

This is `TOKEN_ENCRYPTION_KEY`.

## 3. Configure `.env`

```bash
cp deploy/.env.example deploy/.env
```

Edit `deploy/.env`:

```
TELEGRAM_MANAGER_BOT_TOKEN=<the token from BotFather>
TOKEN_ENCRYPTION_KEY=<the base64 key from step 2>
POSTGRES_PASSWORD=<pick a password>
POSTGRES_DB=assistant
IMAGE_TAG=latest
```

To also enable the General assistant now (optional, can be done later — step 6): set
`LLM_MODELS`, `LLM_FAST_MODELS`, and complete supported provider login. Every other `LLM_*` limit already has a working default
from `deploy/docker-compose.yml` — leave them unset unless you have a reason to override one in
`.env` (see `deploy/.env.example`).

`POSTGRES_PASSWORD` must contain **only letters and digits** — it's interpolated directly into a
Postgres connection string in `deploy/docker-compose.yml`, and punctuation there (`:`, `@`, `/`,
etc.) can break parsing.

`deploy/.env` is gitignored — never commit it.

## 4. Run it

```bash
docker compose -f deploy/docker-compose.yml up -d
```

This starts three containers: `postgres` (with a named volume, so data survives restarts),
`app` (the bot, pulled from `ghcr.io/sergey-bulavskiy/assistant`), and `watchtower` (checks for a
new `app` image every 5 minutes and restarts it automatically — Postgres is never touched).

> **Note:** the first image the CD workflow publishes to GHCR is **private** by default, and
> Watchtower has no credentials to pull a private image. Make the package public once: GitHub →
> the repo's **Packages** tab → `assistant` → **Package settings** → **Change visibility** →
> **Public**.

## 5. For every new role bot: turn off Group Privacy

Bots created with `/newbot` have Telegram's Group Privacy mode on, so in groups they only see
commands and replies, not ordinary messages. Before adding a role bot to a group: in BotFather,
`/mybots` → the role bot → Bot Settings → **Group Privacy** → **Turn off**. (Making the bot a group
admin also works.)

## 6. Set up the General assistant (optional)

Empty `LLM_MODELS` keeps model access off. General replies and Health text extraction use the same
gateway, with separate smart and fast chains. The Linux amd64 image includes pinned Codex CLI
`0.160.1` at `/usr/local/bin/codex`; its private authentication volume is `codex-home`, mounted at
`/home/app/.codex`. Packaging does not prove account access, disabled tools or extraction quality.
Local runs can set absolute `CODEX_CLI_PATH` and `CODEX_HOME`; the CLI must still be exactly the
pinned native binary. Production compose uses the image paths and private auth volume.
Confirm these before switching an existing deployment; changing the home deployment requires the
owner's explicit approval.

1. Create a General bot with `/newbot general`; turn off Group Privacy for group use.
2. After deploying the reviewed image and compose changes, complete supported ChatGPT login in
   a one-off container with the application entrypoint overridden. The existing desktop session
   does not establish login inside this volume. Enable device authentication for the account if
   needed, complete the browser approval privately, then check status without reading credentials:

   ```bash
   docker compose -f deploy/docker-compose.yml run --rm --no-deps --entrypoint /usr/local/bin/codex app login --device-auth -c 'cli_auth_credentials_store="file"' -c 'forced_login_method="chatgpt"'
   docker compose -f deploy/docker-compose.yml run --rm --no-deps --entrypoint /usr/local/bin/codex app login status
   ```

   These supported pinned-release commands still require actual deployment account access.
   Keep the displayed device authorization code private. Do not inspect or print credentials,
   use API-key login for this switch, or start another app poller.
3. Prove one synthetic ordinary reply and structured extraction with effective tool/context
   isolation, through the existing gateway. Verify the selected fast model with the live extraction
   evals: at least 90% of all cases and every critical case must pass. See
   [the eval guide](tests/Assistant.Evals/README.md). Select an available model from this evidence;
   no identifier is implied by this example:

   ```text
   LLM_MODELS=codex-cli:<verified-model>
   LLM_FAST_MODELS=codex-cli:<verified-model>
   ```

   Both chains must contain only ChatGPT subscription entries. A Codex entry in either raw chain
   disables Claude/API entries; remove Codex from both chains for deliberate manual rollback.
   General preferences outside the active catalog are ignored. Health has no preferred-model
   override. Do not mistake a stale saved preference for an active fallback.
4. Recreate compose after approved configuration/login preparation:

   ```bash
   docker compose -f deploy/docker-compose.yml up -d
   ```

   Watchtower replaces the existing image; it cannot apply new environment variables or volume
   mounts. Never run two pollers against the same bot token. Verify the running image, one actual
   Telegram reply and provider/model/outcome metadata before calling the deployment switched.

Subscription calls retain call/token/attempt accounting with zero monetary `Cost`; this does not
mean unlimited quota or represent subscription charges. Auth, process or quota failure reports
unavailability and waits. It never automatically invokes Claude or a paid API. Raw CLI event
streams can contain private content and must never enter ordinary logs or traces. This adapter
scope is text only; photo support needs separate verification and a later request-contract change.
For Codex, `LLM_MAX_OUTPUT_TOKENS` is an instruction target rather than a verified hard generation
cap. The adapter rejects final text above eight characters per configured token and bounds final
file bytes/process streams; timeout and cancellation limit execution separately.

For a count-only audit of General preferences, privately query `chat_settings` for the number of
non-null `preferred_model` values outside the final configured model list. Do not return rows,
chat/topic identifiers or settings contents. No reset is required for safe routing; an optional
runtime reset needs approval and an operator-managed private backup if exact rollback is desired.

Claude remains available for deliberate manual rollback. Generate its subscription token with
`claude setup-token`, set `CLAUDE_CODE_OAUTH_TOKEN` and legacy model entries in both chains, then
recreate compose. Its proprietary CLI is installed at startup into `claude-home` only when active;
`CLAUDE_CLI_VERSION` retains the reviewed pinned default. Paid rollback prerequisites follow below.

## 7. Set up API providers and budgets (optional)

These providers are for deliberate manual rollback from subscription-only access; every variable is optional
and empty by default.

1. **Before putting a key in `.env`:** create a dedicated API key used only by this app (not a key
   shared with anything else), and set a monthly spend limit at the provider — a project budget in
   the OpenAI dashboard, or a monthly spend limit in the Anthropic Console. This app's own
   `LLM_BUDGET_*` variables are a second, independent guard on top of that provider-side cap, not a
   replacement for it: the app cannot verify the provider-side cap exists, so set it first.
2. Get an API key from Anthropic (`ANTHROPIC_API_KEY`) and/or OpenAI (`OPENAI_API_KEY`). Optionally
   `OPENAI_BASE_URL` (an OpenAI-compatible endpoint other than `api.openai.com`) and
   `ANTHROPIC_PROXY`/`OPENAI_PROXY` (`http://`, `https://` or `socks5://[user:pass@]host:port`, if
   this provider needs to be reached through a proxy from this network).
3. Add `anthropic:<model>`/`openai:<model>` entries to `LLM_MODELS` (alongside or instead of
   `claude-cli:` ones — every entry, regardless of provider, falls back to the next one in the order
   listed) and a matching price in `LLM_PRICES` for each one: comma-separated
   `name=input/output` entries, prices in USD per million tokens (e.g.
   `claude-haiku-4-5=1/5,gpt-6-luna=0.1/0.5` — see `deploy/.env.example` for current example model
   ids; check current provider docs for current ids and prices). A `claude-cli:` entry always costs 0
   toward budgets and needs no price entry. An `anthropic:`/`openai:` entry missing its API key or its
   price is dropped individually (one logged error) without stopping any other entry, including other
   `claude-cli:` models.
4. Set `LLM_BUDGET_DAILY_USD` and `LLM_BUDGET_MONTHLY_USD` (both required together — a paid entry
   stays disabled until both are set). Optionally `LLM_BUDGET_WARN_PERCENT` (default 80) and
   `LLM_BUDGET_HARD_PERCENT` (default 120). **The real ceiling this app will ever spend in a
   day/month is that number × `LLM_BUDGET_HARD_PERCENT` / 100, plus possible overshoot of a few calls
   already in flight when the cap is crossed** (so a $2 daily budget with the default hard% can in
   practice reach a bit over $2.40) — size the provider-side cap from step 1 with that real ceiling in
   mind, not the `LLM_BUDGET_*` number alone.
5. Optional: `LLM_FAST_MODELS` — a cheaper, ordered `provider:model` chain (every entry must already
   appear in `LLM_MODELS`). The health bot's extraction (`fast` tier) tries these first, then the rest
   of `LLM_MODELS`; without it the `fast` tier uses `LLM_MODELS` as is. The same list is used
   automatically once spend crosses 100% of either period, before the hard cutoff.
6. `docker compose -f deploy/docker-compose.yml up -d` (same note as the `claude-cli` section:
   Watchtower alone does not pick up new environment variables).

Budget state, checked after every call and before each new one:
- **Below warn%:** normal — every configured paid and subscription model stays usable.
- **Warn (≥ warn%, < 100%):** nothing is restricted yet; platform admins (owners of the first family)
  get a one-time DM through the manager bot the first time spend crosses this line for the day or the
  month.
- **Soft (≥ 100%, < hard%):** platform admins get another DM; new calls restrict to `LLM_FAST_MODELS`
  entries and zero-price (`claude-cli`) entries only — other paid models are skipped as unavailable.
- **Hard (≥ hard%):** platform admins get a third DM; only zero-price (`claude-cli`) entries still
  answer. If none is configured, the assistant refuses with a message naming when the budget resets.

`/usage` (manager bot, owners only, private chat) shows current spend and state for today and this
month (when budgets are configured) plus a per-bot/per-model call/token/cost breakdown for your own
family, covering both today and this calendar month.

## 8. Smoke test

An automated smoke test drives real Telegram through one throwaway account: once the repository
variable `SMOKE_ENABLED` is `true`, CD runs it on every merge to `main` and moves `latest` (what
Watchtower pulls) only if it passes; until then CD promotes without it. One-time setup, running it
locally and enabling the gate: `tests/Assistant.SmokeTests/README.md`. It does not cover creating a
bot through `/newbot`, an unknown user's approval or promoting a second owner; the manual
checklist below does.

Private tracing is checked separately with synthetic inputs and explicit local authorization:
confirm default-off writes no trace, then opt in in an isolated runtime, send a synthetic message,
and export it to a private absolute path. Check request/attempt/source linkage and each final text
delivery result; a replaced answer must show its original as `not sent`. Check an edit and a
confirmation remain distinct. Disable capture afterward and confirm cleanup still runs. This
check is not part of ordinary Telegram smoke and does not authorize capture in the home app.

Optional [supervised local Telegram MCP checks](tests/Assistant.SmokeTests/TELEGRAM-MCP.md)
use an existing account with dedicated test bots/chats and synthetic messages; they do not change
the automated suite or CD gate.
They include settings/topic isolation, restart observations and optional Health record/removal
checks; Health inputs require proof of an isolated disposable database and dedicated bot/profile.

Manual checklist, for after setup and after any release that changes bot behaviour. It creates
real bots and a real family.

- On startup with no family yet, the log prints a claim code (new on every start) — send
  `/claim <code>` to the manager bot in a DM. It replies confirming you're the platform owner.
- Send `/newbot <role>` (e.g. `/newbot general`) → the manager replies with a Telegram creation
  link. Tap it, confirm creation in Telegram's own UI → the manager DMs you that the bot is
  running, with no restart needed.
- Turn off Group Privacy for it (step 5), add it to a group → every family owner gets a DM with
  Yes/No buttons; tapping Yes lets it start recording messages there. The group stays silent;
  a private chat with the bot gets `Получил ✅ #<id>`.
- In a forum group, post in a topic the bot hasn't seen → a separate Yes/No DM for that topic.
- Have an unrecognized Telegram account message the role bot → every owner gets an Allow/Deny DM
  for that user. After Allow, that account can use every bot of the family.
- `/settings` on the manager bot lists bots/places/users matching reality. Disable a bot → it
  stops replying and storing; Enable → it resumes. Remove a place → a new message from it starts
  a fresh approval. "Сделать владельцем" on a user → they can use `/settings` and get owner DMs.
- With a General assistant in an approved group: `/settings` → tap "Отвечать на все: выкл" on that
  group's place → an ordinary message without a mention gets an answer; `/tokens` in the group then
  shows one answered call. Tap "Отвечать на все: вкл" → plain messages are ignored again.
- With a Health assistant in an approved forum topic: `/settings` → find that bot and topic, tap
  "Отвечать на вопросы без упоминания: выкл", then send a synthetic question without a mention in
  that topic. It gets an answer. An unrelated approved topic stays off. Tap the matching "вкл"
  control to stop passive answers; explicitly addressed questions still work.
- `/newbot health`, turn off its Group Privacy, then in a private chat with it: `/thresholds` lists
  the defaults, each "не подтверждено врачом"; `/setstart` with a date exactly three weeks ago, then
  `/week` → "Неделя: 3 нед. 0 дн."; `/threshold glucose.any low_alert 4.0` → `/thresholds` shows that
  rule as "врач"; `/threshold glucose.any default` restores it.
- In a private chat with the health bot: "вес 70.5" → ✍ on the message and `/today` shows
  "вес 70.5 кг"; "сахар 400" → "Не понял «400» — уточните единицы (нужно в ммоль/л)."; `/undo` →
  "Удалено: …" and the ✍ disappears.
- In a private Health chat, send "После прогулки легче сосредоточиться"; check ✍ and a "заметка:"
  line in `/today`. `/notes` shows the note; `/notes прогулка` finds it if the model chose that tag.
- In a private chat with the health bot: "сахар 2.5" → ✍ and "🚨 Глюкоза: 2.5. … Порог 3.0 — не
  подтверждено врачом. …"; "давление 150/95" → "⚠️ Верхнее давление: 150 — выше порога 140 (не
  подтверждено врачом). …"; `/threshold glucose.any low_alert 4.0`, then "сахар 3.9" → "⚠️ Глюкоза:
  3.9 — ниже порога 4.0 (порог от врача). …". With the model off, "сахар 2.5" → the alert followed by
  "Ничего не записано — повторите сообщение позже."
- In a private chat with the health bot: "вес 70.5", then edit it to "вес 71.5" → ✍ stays and
  `/today` shows only "вес 71.5 кг"; edit it to "просто текст" → the ✍ disappears and `/today` says
  "Сегодня записей нет.". "сахар 5.5", edited to "сахар 2.5" → the 🚨 alert once; edit it again to
  "Сахар 2.5" → no second alert.
- In a private chat with the health bot (stage start set, one reading posted): "какой сахар считается
  нормой натощак?" → an answer that can refer to the stage week or the thresholds, ending with "Не
  заменяю врача."; "на сколько единиц увеличить дозу?" → "Я не даю советов по дозам лекарств. …"
  followed by "Не заменяю врача.". In a tracking group with the Health question setting off, the
  same question without a mention gets no answer; with `@<health bot username>` it gets an answer.
  With the setting on for that place, the unmentioned question gets an answer too.
- In a private chat with the health bot: "сахар 10 - высокий?" → an answer, followed by "Записать
  глюкоза 10.0 ммоль/л?" with Да/Нет buttons; tapping Да → "Записано: …" and ✍ on the original
  question; tapping Да again → "Уже решено.". `/today` shows the value exactly once. "а если сахар
  2.5, что делать?" → the 🚨 alert fires immediately together with an answer, with no buttons and
  nothing added to `/today`. "сахар 9 - высокий?" followed by "нет, я только спросил" → "Не
  записано." and the buttons are removed. In the tracking group, a tap from an account that is not
  an approved family member → "У вас нет прав.".
- Send `/version` to the role bot → it replies with the running version.
- Restart the process (or container) and resend a message you already sent before restarting to
  any bot → no duplicate row, no duplicate reply, for every bot independently.

## Rollback

With the smoke gate enabled, `latest` only moves after the smoke test passes, so pinning is only
needed to go back to an older release. A `sha-` tag of a build that failed smoke exists in GHCR but
was never promoted; don't pin it.

Pin a previous image by setting `IMAGE_TAG` in `deploy/.env` to a known-good short SHA (from a past
successful build, e.g. `sha-abc1234`), then:

```bash
docker compose -f deploy/docker-compose.yml up -d
```

Watchtower then leaves `app` alone as long as `IMAGE_TAG` is pinned to that specific tag (it only
auto-updates `latest`).

## Running tests locally

```bash
dotnet test
```

Integration tests need Docker (they start short-lived Postgres + IntegreSQL containers via
Testcontainers automatically). For a faster local loop, start `docker-compose.tests.yml` once and
point the tests at it instead:

```bash
docker compose -f docker-compose.tests.yml up -d
INTEGRESQL_URL=http://localhost:15000/ TEST_PG_HOST=localhost TEST_PG_PORT=15432 dotnet test
docker compose -f docker-compose.tests.yml down
```

Extraction evals (`tests/Assistant.Evals`) run with every `dotnet test`: invented health messages
with recorded model answers are checked against the expected records and alerts, without calling a
model. A live run against a real model is opt-in and local only (`EVALS_LIVE=1`); see
`tests/Assistant.Evals/README.md`.

## Privacy note

No personal data is stored in this repository. All data the bot collects at runtime lives in the
`postgres` container's Docker volume, on your own machine.
