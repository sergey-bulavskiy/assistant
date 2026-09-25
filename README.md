# Assistant

A Telegram bot that quietly records messages in PostgreSQL and replies with a confirmation. This is
the M1 "foundation" milestone: no LLM, no product features yet — just the storage and delivery
pipeline that later milestones build on.

## Privacy

This project is designed to later handle personal and health data at runtime, in the database. **No
personal data lives in this repository** — not in code, tests, commit history or this README. See
`CLAUDE.md` for the full rules.

## Prerequisites

- A Windows PC (or any Docker host) with **Docker Desktop** installed, WSL2 backend enabled, and
  **set to start automatically with Windows** (Settings → General) — the bot only runs while Docker
  is running.
- A Telegram account to create the bot.

## 1. Create the bot in BotFather

1. Open a chat with [@BotFather](https://t.me/BotFather) in Telegram.
2. Send `/newbot`, follow the prompts, and choose a name and username.
3. BotFather replies with a token like `123456789:AAExampleTokenValueDoNotUseThisOne`. Copy it — this
   is `TELEGRAM_BOT_TOKEN`.

## 2. Find your Telegram user id

Message [@userinfobot](https://t.me/userinfobot) (or any similar bot) and it replies with your
numeric Telegram user id. This is temporary: M1 reads allowed user ids from `.env`; M2 replaces this
with proper approvals through a manager bot.

## 3. Configure `.env`

```bash
cp deploy/.env.example deploy/.env
```

Edit `deploy/.env`:

```
TELEGRAM_BOT_TOKEN=<the token from BotFather>
ALLOWED_USER_IDS=<your user id>,<any other allowed user id>
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

- On startup, the bot sends a `🟢 Запущен <sha>` message to the **first** id listed in
  `ALLOWED_USER_IDS` — check that user's DM with the bot.
- Send the bot a direct message → it replies `Получил ✅ #<id>`.
- Send `/version` (anywhere) → it replies with the running commit SHA and build time.
- Add the bot to a group and post a message → the message is stored silently (no reply). If the bot
  never reacts to group messages, it likely needs **admin rights** in that group, or **privacy mode
  disabled** in BotFather (`/setprivacy`) — Telegram bots cannot see ordinary group messages
  otherwise.
- Restart the container (`docker compose -f deploy/docker-compose.yml restart app`) and resend a
  message you already sent before restarting → no duplicate row, no duplicate reply.

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
