# Assistant

A Telegram bot that quietly records messages in PostgreSQL and replies with a confirmation. This is
the M1 "foundation" milestone: no LLM, no product features yet — just the storage and delivery
pipeline that later milestones build on.

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

## 5. Smoke checklist

- On first startup with no family yet, the log prints a one-time claim code — send
  `/claim <code>` to the manager bot in a DM. It replies confirming you're the platform owner.
- Send `/newbot <role>` (e.g. `/newbot general`) → the manager replies with a Telegram creation
  link. Tap it, confirm creation in Telegram's own UI — the manager starts polling the new bot
  automatically, no restart needed.
- Add the new role bot to a group → every family owner gets a DM with Yes/No buttons; tapping
  Yes lets it start recording messages there.
- Have an unrecognized Telegram account message the role bot → every owner gets an Allow/Deny DM
  for that user.
- `/settings` on the manager bot lists bots/places/users with buttons that actually change
  behavior (Disable a bot and confirm it stops replying; Remove a place and confirm new messages
  from it start a fresh approval).
- Restart the process (or container) and resend a message you already sent before restarting to
  any bot → no duplicate row, no duplicate reply, for every bot independently.

## Rollback

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
