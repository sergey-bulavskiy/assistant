# Smoke test (real Telegram)

Drives the app through real Telegram: one throwaway user account (the owner, via MTProto)
talks to a smoke manager bot and a role bot running in a disposable Docker stack (fresh Postgres +
the app image). It covers the M2 checklist: claim, private chat, group, forum topic, `/settings`
(disable/enable/remove), restart without duplicates. Unknown-user approval and second-owner
promotion need a second real user; the integration tests cover them (`ApprovalServiceTests`,
`ManagerUpdateHandlerCallbackTests`).

It is **opt-in**: a plain `dotnet test` skips it. Once the gate is enabled (below), CD runs it
against the just-built `sha-` image and promotes `latest` only if it passes.

For optional supervised local checks with an existing account, see
[Telegram MCP smoke checks](TELEGRAM-MCP.md). That workflow is separate from this suite and CD.

## Enabling the CD gate

The gate is off by default: while the repository variable `SMOKE_ENABLED` is unset, CD skips the
smoke job and promotes `latest` as before (with a warning in the run). Enable it only when both are true:

1. The scenario has passed locally against real Telegram (`SMOKE=1 dotnet test ...`, below).
2. The GitHub Environment `smoke` (restricted to `main`) holds the secrets from the settings table
   (`SMOKE_TG_API_ID`, `SMOKE_TG_API_HASH`, `SMOKE_OWNER_SESSION`, `SMOKE_MANAGER_BOT_TOKEN`,
   `SMOKE_MANAGER_BOT_USERNAME`, `SMOKE_ROLE_BOT_TOKEN`, `SMOKE_ROLE_BOT_USERNAME`,
   `SMOKE_GROUP_TITLE`, `SMOKE_FORUM_TITLE`), using bots, groups and a session string of their own
   (not the ones used locally).

Then set the repository variable: Settings → Secrets and variables → Actions → Variables →
`SMOKE_ENABLED` = `true`. Setting it before that makes every CD run fail at `smoke`, and `latest`
stops moving. To switch the gate off again, delete the variable.

## One-time setup (a human, once)

1. One throwaway Telegram account (the "owner") and an `api_id`/`api_hash` from
   https://my.telegram.org (API development tools). Use dedicated numbers: Telegram may restrict
   automated accounts.
2. In BotFather create the **smoke manager bot** (`/newbot`). Then `/mybots` → the bot → Bot
   Settings → enable **"Allow bot to manage other bots"**. Confirm it is on.
3. In BotFather create a **role bot** by hand (`/newbot`). The test inserts it into the smoke
   database instead of creating it through `/newbot` (Telegram needs a human tap for that).
   Disable privacy mode for it **before** it is added to the groups (BotFather → `/setprivacy` →
   the bot → Disable): the scenario sends plain messages into groups and topics.
4. With the owner account create a normal group and a forum supergroup (Group settings → Topics).
   The owner must be admin in both. Note both titles exactly. Add the role bot to both groups by
   hand, once, as an ordinary member (the test never adds or removes it; the first message in each
   chat triggers the approval DM).
5. Generate the account's session string (asks for the phone number and the login code sent by
   Telegram, and the 2FA password if set):

   ```bash
   dotnet run --project tests/Assistant.SmokeTests.Login
   ```

   It prints a base64 string. That string is a secret.
6. Put the values into `tests/Assistant.SmokeTests/smoke.env` (gitignored) for local runs, and into
   the GitHub Environment `smoke` secrets for CD:

| Setting | Meaning |
|---|---|
| `SMOKE_TG_API_ID`, `SMOKE_TG_API_HASH` | From my.telegram.org |
| `SMOKE_OWNER_SESSION` | Session string from step 5 |
| `SMOKE_MANAGER_BOT_TOKEN`, `SMOKE_MANAGER_BOT_USERNAME` | Smoke manager bot (username without `@`) |
| `SMOKE_ROLE_BOT_TOKEN`, `SMOKE_ROLE_BOT_USERNAME` | Hand-made role bot |
| `SMOKE_GROUP_TITLE`, `SMOKE_FORUM_TITLE` | Exact titles from step 4 |
| `SMOKE_IMAGE` | Optional. Image to test (CD sets it to the `sha-` tag). Unset: built from the `Dockerfile` |

`smoke.env` is `KEY=VALUE` lines; real environment variables win over it.

## Run it locally

Docker must be running. From the repo root:

```bash
SMOKE=1 dotnet test tests/Assistant.SmokeTests -c Release
```

PowerShell: `$env:SMOKE = "1"; dotnet test tests/Assistant.SmokeTests -c Release`.

A failure names the step (`Smoke step '7 settings' failed: ...`).

## Rules and gotchas

- **One poller per bot token.** Do not run the test locally with the same bot tokens (or the same
  test groups) while CD's smoke job is running: two pollers on one token conflict and steps time
  out. Use a separate smoke manager bot, role bot and pair of groups for local runs, and a separate
  session string (run the login tool again for the same account). Never reuse the CD session
  string locally: using one session from two places at once makes Telegram revoke it.
- Test messages are invented; never send real content from the test account.
- Session expired (`Telegram asked for 'phone_number'`): regenerate it with the login tool and update
  `smoke.env` and the GitHub secret.
- Telegram flood limits: the driver pauses after each send. If a run still hits a limit, wait and rerun.
- Each run adds one forum topic. That is harmless.

## Not automated

- Step 1 of the old checklist (the BotFather toggle) is the one-time setup above.
- Managed-bot creation through `/newbot` (a human confirms in Telegram's UI). The hand-made role bot stands in.
- Open observations to check by hand when relevant: whether there is a cap on managed bots per
  manager. The test requires privacy mode disabled for the role bot.
