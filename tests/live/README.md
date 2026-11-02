# Operator-assisted foundation check

This check uses a dedicated Telegram bot and the selected test chat. It starts a disposable
PostgreSQL container and the locally built application. The operator uses an existing Telegram
user session; the runner does not create or control that session. Never use a production bot,
database, or deployment. Ensure no other process polls the dedicated bot.

Requirements: Python 3, Docker, .NET 10, a clean committed checkout, the
application's required environment variables, an authorized Telegram user session, and a private
evidence directory outside every repository. Set `TELEGRAM_BOT_TOKEN`, `OPENAI_API_KEY`,
`OPENAI_MODEL`, `SUBJECT_ID` (a new synthetic GUID), `ALLOWED_USERS`, and `ALLOWED_CHATS` in
the runner's environment without printing secrets. The target chat and bot must be verified
before starting; the selected General topic requires no thread argument. Do not use real
personal data. Preserve the private receipt, including failures, outside repositories.

Run the offline checks first:

```text
python -B -m unittest discover -s tests/live -p "test_*.py" -v
```

Then run `python -B tests/live/run.py <private-evidence-directory>`. At the first prompt, send
`/record 7.8 mmol/L` as the authorized Telegram user and observe the bot's reply. Enter one JSON
line with `bot_id`, `chat_id`, `message_id`, `reply_bot_id`, `reply_to`, and `reply_text`.
`message_id` identifies the user's command; `reply_to` and `reply_bot_id` come from the observed
bot reply. The runner checks these against the independently queried database. After its process
restart prompt, send `/last` in the same chat. Enter one JSON line with `chat_id`, `message_id`,
`reply_bot_id`, `reply_to`, and `reply_text`; this `message_id` identifies the `/last` command.
Use observed Telegram values, never values inferred from the database or expected output. The
runner enforces deadlines and records the outcome. Missing access is not a pass.

The runner rebuilds the clean committed solution without incremental compilation and records
the commit and binary hash before starting Docker. If a container creation result is uncertain
and automatic cleanup cannot confirm ownership, inspect the unique name `assistant-live-<run_id>`
and its `assistant-live-run` label from the private receipt. Remove a container manually only
after its exact name and label both match that run ID. Do not inspect or remove unrelated
containers. Record any unresolved cleanup as a failure.

The runner verifies this one synthetic reading and its source, a restart, and deterministic
readback. It does not establish recipe, photo, medical, or multi-subject behavior.

## Saved-recipe scenario

Set `ASSISTANT_LIVE_SCENARIO=recipe` in the runner environment and run the same command. Use
the authorized user session to send `/prefer Please avoid sesame in the recipe`; the first JSON
witness has `bot_id`, `chat_id`, `message_id`, `reply_bot_id`, `reply_to`, and `reply_text`.
After the runner restarts the process, send `/recipe` and provide the same witness fields for
that command and its linked reply. The runner writes the selected synthetic recipe to the
private receipt. The independent verification agent inspects its title, ingredients and steps
for coherence and for sesame or obvious translations and synonyms, then enters
`{"semantic_check":"pass"}` only if that specific example passes. Finally send `/shopping` and
provide `chat_id`, `message_id`, `reply_bot_id`, `reply_to`, and `reply_text` from the observed
command and linked reply. The runner checks the exact saved recipe, source and ingredient amounts
and rejects any additional recipe-provider request. It does not directly observe the live provider
request body; a separate offline database-to-handler test checks that the saved preference is sent.
This single example does not establish general preference compliance or medical safety.
