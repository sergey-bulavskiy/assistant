# tests/AGENTS.md

## Writing tests

- Generate or extend tests with the **`code-testing-agent` skill** from the `dotnet-test` plugin
  of [dotnet/skills](https://github.com/dotnet/skills). It is a *skill* to load, not an agent
  name. Install once (Claude Code / Copilot CLI):
  `/plugin marketplace add dotnet/skills`, then `/plugin install dotnet-test@dotnet-agent-skills`.
  If the skill is unavailable, say so and write the tests by hand under the same rules.
- The framework here is **xUnit**; keep generated tests in the existing projects, folders and
  style, and use the fakes and fixtures below rather than new mocking libraries.

## Test quality (review rejects tests that break these)

Every test must be able to fail for a real bug. No tests that check nothing.

- Assert on observable behaviour: stored rows, replies sent, returned values, state transitions,
  secrets absent from logs. Not "does not throw", not "result is not null" alone, not that a mock was
  called with whatever it was set up to return.
- No tests of the framework, language, DI container wiring or trivial getters/setters, and no
  tests that re-implement the production logic to compute the expected value.
- Write the test from the requirement, then check it fails when the behaviour is broken (revert
  or break the code once and watch it go red). Say in the PR that you did this for new tests.
- Cover edge and failure paths that matter here: duplicates/redelivery, restarts, cancellation,
  invalid input, transient DB/Telegram failures.
- Prefer an integration test through IntegreSQL over a unit test with a faked store when the
  behaviour depends on SQL, constraints or transactions.
- Deleting or weakening an existing assertion needs a reason in the PR description.

## Infrastructure

- **Synthetic data only.** Invented texts, ids like `111`/`222`, fake tokens like `test-token`.
  Never paste real messages, user ids or chat ids, even "anonymized".
- `Assistant.UnitTests`: no I/O. Use the fakes in `Assistant.UnitTests/Fakes`
  (`FakeTelegramClient`, `FakeMessageStore`, `FixedClock`) instead of mocking libraries.
- The real Claude Code CLI is never invoked by any automated test — `ClaudeCliChatClient` is tested
  only against `FakeProcessRunner` (`Assistant.UnitTests/Fakes`), and `LlmGateway`/`GeneralAssistant`
  are tested only against fake `IChatClient`/`ILlmGateway` implementations
  (`Assistant.IntegrationTests/Llm/ScriptedChatClient.cs`, `Assistant.UnitTests/Fakes/FakeLlmGateway.cs`).
  A `CLAUDE_CODE_OAUTH_TOKEN` is never required to build or run this repo's test suite.
- No test for the Anthropic/OpenAI providers ever reaches the network — both are exercised only
  through a fake `HttpMessageHandler` returning canned 429/other responses
  (`AnthropicChatClientFactoryTests`, `OpenAiChatClientFactoryTests`). Neither an `ANTHROPIC_API_KEY`
  nor an `OPENAI_API_KEY` is ever required to build or run this repo's test suite.
- `Assistant.IntegrationTests`: every test gets a fresh, migrated database from IntegreSQL
  (`Infrastructure/IntegreSqlPool.cs`). Derive DB tests from `IntegrationTestBase`; host tests use
  `AssistantWebApplicationFactory`, which swaps in a fake Telegram client, a controllable
  `TestClock` and failure injectors. Never point tests at a shared or real database.
- By default the pool starts Postgres + IntegreSQL via Testcontainers (Docker required). Fast loop,
  from the repo root:

  ```bash
  docker compose -f docker-compose.tests.yml up -d
  INTEGRESQL_URL=http://localhost:15000/ TEST_PG_HOST=localhost TEST_PG_PORT=15432 dotnet test
  docker compose -f docker-compose.tests.yml down
  ```

- Image versions and IntegreSQL pool settings are duplicated in `IntegreSqlPool.cs` and
  `docker-compose.tests.yml`; change both together.
- The template DB is keyed by a hash of the migration ids, so adding a migration rebuilds it
  automatically. `NpgsqlConnection.ClearAllPools()` after migrating the template is required
  (Postgres refuses `CREATE DATABASE ... TEMPLATE` while connections are open) — don't remove it.
- Intermittent IntegreSQL 423/503/500 on CI usually means pool exhaustion, not a flaky test; read
  the comments in `IntegreSqlPool.cs` before changing pool sizes.

## Smoke test (`Assistant.SmokeTests`)

- Opt-in: every test uses `[SmokeFact]`, skipped unless `SMOKE=1`. Never remove that gate: a plain
  `dotnet test` must not start containers or log in to Telegram. Run it with
  `SMOKE=1 dotnet test tests/Assistant.SmokeTests -c Release` (setup and secrets:
  `tests/Assistant.SmokeTests/README.md`).
- CD runs it against the `sha-` image before `latest` is promoted (`.github/AGENTS.md`).
- One ordered scenario; the first failing step stops the run and names itself. The scenario matches
  the bot's literal Russian texts and button labels: change them together with `ManagerUpdateHandler`,
  `SettingsCommandHandler`, `ApprovalService` and `ReplyPolicy`.
- One poller per bot token: don't run it locally with the same bots/groups while CD's smoke job runs.
- Synthetic data only, like every other test; the account and bots are throwaway.
