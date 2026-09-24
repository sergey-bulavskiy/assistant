# CLAUDE.md

Family assistant: Telegram role-bots backed by LLMs (C# / .NET, PostgreSQL).

## Repositories

- This repo (`assistant`) is **public**: code, tests, CI/CD, deploy files.
- Specs, plans and requirements live in the **private** sibling repo `../assistant-specs`
  (`docs/superpowers/specs`, `docs/superpowers/plans`). Read them there; never copy them here.
  `docs/` is gitignored in this repo on purpose.

## Privacy rules (mandatory)

This project handles personal and health data at runtime. None of it may enter this repo.

- **No personal data anywhere in this repo**: no real names, health conditions, medications,
  measurements, schedules, addresses, chat IDs, user IDs or bot usernames. This applies to code,
  comments, commit messages, PR titles and descriptions, issue text, README, role prompts,
  config examples and test data.
- **Test and eval data are synthetic**: invented messages and values only, never copied from real
  chats, exports or the database.
- **Role prompts stay generic.** Anything specific to a person belongs in the database profile at
  runtime, not in `roles/`.
- **Never commit** `.env` files, tokens, API keys, Telegram exports, `media/`, backups or DB dumps.
- When a requirement from `../assistant-specs` mentions personal details, generalize it before
  it appears here (e.g. "a household member with a chronic condition", "glucose reading 7.8").
- If you notice personal data in a diff or in history, stop and tell the user before pushing.

## Git

- Commits use the repo-local identity (GitHub username + noreply email). Don't change it.
- Workflows never use `pull_request_target`; workflows that need secrets are `workflow_dispatch` only.
