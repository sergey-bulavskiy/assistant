# Assistant

A family assistant built from Telegram bots. A **manager bot** sets up a family and creates
**role bots** for it; role bots quietly record the messages of approved people in approved chats
into PostgreSQL, one role bot -- the **General assistant** -- can also answer with a real LLM
(the Claude Code CLI on a Claude subscription) once configured, and a **health** role bot keeps a household member's tracking profile and safety
thresholds. Later milestones add more assistant
features on top of this pipeline.

## What it does

**Manager bot** (the bot whose token you configure):

| Command | Who | What |
|---|---|---|
| `/claim <code>` | anyone, once | Creates the family and makes the sender its first owner. While no family exists, each start prints a fresh code in the log. |
| `/newbot <role>` | owners | Replies with a Telegram link that creates a new role bot (role: up to 64 characters). Once confirmed, the bot starts polling without a restart. The role is kept for a day, across restarts, until you confirm. |
| `/settings` | owners | Lists bots, places and users with buttons: disable/enable/remove a bot, disable/enable/remove a place, turn **reply to all messages** on or off for a General assistant's place ("Отвечать на все: выкл/вкл"), allow/deny a user still waiting for approval, disable/enable a user, make an approved user an owner. A forum topic's place line shows its topic id. |
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
The model only turns the message into records (glucose, insulin, meal, symptom, weight, blood
pressure); the bot validates them, saves them and sets ✍ on the message (👍 where ✍ is not allowed).
Otherwise it stays silent. If a reading cannot be recorded (an unknown unit, an implausible value, a
time it cannot place) it asks once, as a reply. If the model is unavailable or its answer is
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

**Questions.** The bot answers a question only when it is addressed: in a private chat every message
is, in a group or topic only a message that mentions the bot (`@username`) or replies to one of its
messages (the place's "reply to all" setting is ignored for this bot). The extraction call tells
whether a message is a question; an addressed question is answered even when readings were recorded
from the same message (the answer's context already includes them), but never when the message got a
clarification, a quick-scan reply or a safety alert (that fixed reply is the answer) or is an edit.
An addressed new message that produced nothing at all (extraction worked, but no reading, no question,
no clarification or alert, e.g. a greeting) gets one short fixed hint as a reply, "Слушаю. Запишите
показатель (например: сахар 5.8 после обеда) или задайте вопрос.", at most once per 5 minutes per chat/topic
(in memory); the hint also fires on any addressed group reply to the bot (e.g. a thank-you after an answer), under the same throttle; very short or emoji-only texts are not extracted and get no hint. If extraction fails on an addressed question, only the
failure notice is sent, no answer. The answer is a second model call on the `smart` tier
(`LLM_MODELS` order) with the profile's context: the stage week, the context note (`/setnote`), the
thresholds with their source and the readings of the last 24 hours, plus the last few messages of the
chat. Answers never contain dose advice: besides the instruction in the prompt, a fixed filter in
code replaces any answer that looks like dose advice with "Я не даю советов по дозам лекарств. Это
вопрос к врачу — …". The filter is conservative and may over-refuse a harmless answer: it also
refuses a number with a dose unit next to a time of day, and any answer with letters other than Latin
or Cyrillic (answers are asked for in Russian, or in English for an English question). Every answer
ends with "Не заменяю врача." When the model is unavailable the bot replies with the General
assistant's short notices ("Слишком много запросов, подождите минуту.", …). Answers are kept as
conversation context; alerts and other fixed texts are not.

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
`CLAUDE_CODE_OAUTH_TOKEN` and `LLM_MODELS`. Every other `LLM_*` limit already has a working default
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

Skip this section if you don't want an LLM-backed bot yet — every `LLM_*` variable is optional and
the rest of the bots work with none of them set. Steps in this exact order (spec §8.11):

1. On a desktop with a browser, generate a Claude Code CLI auth token (requires a Claude
   subscription, not an API key):

   ```bash
   claude setup-token
   ```

   This prints a token to your terminal without saving it anywhere — copy it somewhere you'll paste
   it into `deploy/.env` in step 3.
2. Send `/newbot general` to the manager bot (same flow as any other role bot). If this bot will be
   used in groups, also turn off **Group Privacy** for it in BotFather (the manager bot's own
   creation flow doesn't do this for you) — otherwise it never sees group messages to check for a
   mention/reply.
3. Put the token from step 1 and at least `LLM_MODELS` into `deploy/.env`, e.g.:

   ```
   CLAUDE_CODE_OAUTH_TOKEN=<the token from step 1>
   LLM_MODELS=claude-cli:sonnet,claude-cli:haiku
   ```

   The first `LLM_MODELS` entry is tried first; later ones are the fallback chain when one runs out
   of usage limits. Every other `LLM_*` variable (rate/day caps, context size, timeouts) already has
   a working default from `deploy/docker-compose.yml`, and `CLAUDE_CLI_VERSION` (the pinned CLI
   version) defaults inside the app itself — see `deploy/.env.example` to override any of them.
4. Pull the updated `deploy/docker-compose.yml` (it now mounts a `claude-home` volume and reads the
   new variables) and run:

   ```bash
   docker compose -f deploy/docker-compose.yml up -d
   ```

   **This manual step matters even if Watchtower is already running**: Watchtower only replaces the
   image on the *existing* container — it does not pick up new environment variables or new volume
   mounts from a changed compose file. The `claude-home` volume and every `LLM_*`/`CLAUDE_CLI_VERSION`
   variable only take effect after this explicit `docker compose up -d`.

**Notes:**
- The General assistant runs the Claude Code CLI (`claude -p`) as its model provider — this is a
  deliberate exception to this project's normal "no coding-agent tooling as a runtime dependency"
  stance, chosen because it works on a Claude subscription with no separate API billing. Direct
  Anthropic/OpenAI API providers are also available behind the same interface, with no change to how
  `/model`/`/new` or any role-bot code works — see "Set up API providers and budgets" below.
- The CLI itself is **not** part of this (public) image — it's proprietary software, so a background
  service installs the pinned version (`CLAUDE_CLI_VERSION`) into its own dedicated volume the first
  time the container starts with a `claude-cli:` entry configured. Until that finishes (usually a few
  seconds), `/model` shows every `claude-cli` model as unavailable; check the container logs for
  `claude-cli install failed` if it doesn't clear up shortly — a persistent failure most often means
  the container can't reach `claude.ai` to run the installer.

## 7. Set up API providers and budgets (optional)

Skip this if `claude-cli` (the previous section) is enough for now — every variable here is optional
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
- `/newbot health`, turn off its Group Privacy, then in a private chat with it: `/thresholds` lists
  the defaults, each "не подтверждено врачом"; `/setstart` with a date exactly three weeks ago, then
  `/week` → "Неделя: 3 нед. 0 дн."; `/threshold glucose.any low_alert 4.0` → `/thresholds` shows that
  rule as "врач"; `/threshold glucose.any default` restores it.
- In a private chat with the health bot: "вес 70.5" → ✍ on the message and `/today` shows
  "вес 70.5 кг"; "сахар 400" → "Не понял «400» — уточните единицы (нужно в ммоль/л)."; `/undo` →
  "Удалено: …" and the ✍ disappears.
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
  followed by "Не заменяю врача.". In the tracking group the same question without a mention → no
  answer; with `@<health bot username>` → an answer as a reply.
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
