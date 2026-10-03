# Extraction evals

These evals measure how well the health bot's extraction turns a message into records. The file
`cases/extraction.jsonl` holds invented messages, each with the expected result and a recorded model
answer. The result is pass/fail per case; there are no statistics.

## Replay (every test run)

Every `dotnet test` (CI included) feeds each case's `recorded_output` through `ExtractionParser`,
`HealthEventValidator` and `SafetyRuleEvaluator` (published-guideline default rules, no earlier
readings, "now" = the message time) and compares the outcome with `expected`. It needs no model,
network or key. There is one test row per case (`ExtractionReplayTests`). Replay re-implements the
validate-then-evaluate glue of `HealthAssistant` instead of calling it, so it tests the
deterministic pieces (parser, validator, rules), not `HealthAssistant` itself.

## Case format

One JSON object per line, with the fields:

- `id`
- `now` (local time, `yyyy-MM-ddTHH:mm`)
- `time_zone`
- `text`
- `critical`
- `expected`: `events`, `unclear`, optional `is_question`, and `alert` (required for critical cases;
  `null` means no alert)
- `recorded_output` (an object or a string)

Comparison rules in short: events match in any order on `type` and the listed payload fields
(`value`, `context`; `kind`, `units`, `name`; `meal_kind`; `code`; `kg`; `systolic`, `diastolic`,
`pulse`). `at` is the stated local time; no `at` means no time was stated. Unclear reasons are
compared as a sorted list. The alert is the most severe rule decision (`rule_key`, `level`).
`critical` marks glucose or blood pressure values and red-flag symptoms.

## Live run (opt-in, local only)

Never run in CI. Command:

```bash
EVALS_LIVE=1 EVALS_LLM_MODELS=anthropic:<model> ANTHROPIC_API_KEY=… LLM_PRICES='<model>=1/5' \
  LLM_BUDGET_DAILY_USD=1 LLM_BUDGET_MONTHLY_USD=5 \
  dotnet test tests/Assistant.Evals -c Release --filter "FullyQualifiedName~ExtractionLiveTests" \
  --logger "console;verbosity=detailed"
```

- Without `EVALS_LIVE=1` the live test is skipped.
- `EVALS_LLM_MODELS` uses the `LLM_MODELS` format; only its first entry is evaluated.
- The provider's own variables are read exactly as the app reads them. Paid entries still need
  `LLM_PRICES` and `LLM_BUDGET_*` to be accepted, but budgets and limits are not enforced in live
  runs: a run is one short call per case.
- `claude-cli:<model>` needs `CLAUDE_CODE_OAUTH_TOKEN` and the pinned CLI already installed at
  `$CLAUDE_HOME/.local/bin/claude` (Linux, macOS or WSL); the eval never installs it.
- Output: `PASS <id>` / `FAIL <id> (critical): …` per case and a totals line.
- Pass bar: the test fails unless at least 90% of the cases and every critical case pass. This is
  the bar for using a model as the `fast` model of the health role.
- Results from the CLI do not carry over exactly to API models; rerun when the provider changes.

## Recording

Add `EVALS_RECORD=1` to a live run to store the model's answer as `recorded_output` of every case
that passed. Failed cases keep their old answer, so replay stays green. The file is rewritten in
place; review the diff before committing. Record only from these invented cases.

## Private cases

`EVALS_CASES_FILE=<path>` adds the cases of another file in the same format to both replay and live
runs. The file must live outside this repository (the loader refuses a path inside it) and must
never be committed. Its ids must not repeat the public ones. With the variable set, the private case ids appear in
test names and TRX output (local only; do not share or commit that output).

## Rules for cases

Invented messages only, neutral wording, no personal data (see the privacy rules in the root
`AGENTS.md`). Never paste a real message, value or time. Symptom codes come only from the public
list.
