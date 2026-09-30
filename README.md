# Assistant

A family assistant built from Telegram bots. A **manager bot** sets up a family and creates
**role bots** for it; role bots quietly record the messages of approved people in approved chats
into PostgreSQL. There is no LLM yet: role bots only store messages and confirm them in private
chats. Later milestones add the assistant features on top of this pipeline.

## What it does

**Manager bot** (the bot whose token you configure):

| Command | Who | What |
|---|---|---|
| `/claim <code>` | anyone, once | Creates the family and makes the sender its first owner. While no family exists, each start prints a fresh code in the log. |
| `/newbot <role>` | owners | Replies with a Telegram link that creates a new role bot (role: up to 64 characters). Once confirmed, the bot starts polling without a restart. The role is kept for a day, across restarts, until you confirm. |
| `/settings` | owners | Lists bots, places and users with buttons: disable/enable/remove a bot, disable/enable/remove a place, allow/deny a user still waiting for approval, disable/enable a user, make an approved user an owner. |

**Role bots:**

- A new **place** (a group the bot is added to, or a forum topic it sees for the first time) needs
  an owner's Yes/No in a DM from the manager bot before any message from it is stored.
- A new **user** writing to any of the family's bots needs an owner's Allow/Deny; once allowed, they
  can use every bot of the family.
- Messages from approved users in approved places are stored exactly once per Telegram update, even
  across restarts. In private chats the bot replies `Получил ✅ #<id>`; in groups it stays silent.
- `/start` (private chats) and `/version` (any chat) reply with a greeting and the running version.

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

## 6. Smoke test

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

## Privacy note

No personal data is stored in this repository. All data the bot collects at runtime lives in the
`postgres` container's Docker volume, on your own machine.
