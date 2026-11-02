# Family assistant

This repository contains a small .NET 10 Telegram bot under development. An allowed user can send `/record <text>` to extract an explicit value and unit through OpenAI and store it in PostgreSQL, then use `/last` to read the latest observation in the same chat and topic. The current extraction accepts positive decimal values in `mmol/L` or `mg/dL`; it does not provide medical interpretation.

`/prefer <text>` stores one food preference of up to 500 characters for the configured subject in the current bot, chat and topic. A new preference replaces the previous one in that scope. `/recipe` asks OpenAI for a recipe using that preference and saves it with ingredient amounts in grams. `/shopping` reads the saved recipe's ingredients and amounts without another model call. Generated recipes require user judgment: structural validation does not guarantee that a preference was followed or establish dietary safety. Preferences are not shared across topics. Photo input is not implemented.

If a recipe attempt fails or its outcome is uncertain, send a new `/recipe` command to try again. Redelivery of the same command will not make another model request, even if the application stopped before making the original request.

Replies are linked to the command when it is available. If that message was deleted, the bot sends the result in the same chat and topic without a reply link.

The application requires `TELEGRAM_BOT_TOKEN`, `OPENAI_API_KEY`, `OPENAI_MODEL`, `DATABASE_CONNECTION`, `SUBJECT_ID` (a nonempty GUID), and comma-separated `ALLOWED_USERS` and `ALLOWED_CHATS`. Keep credentials and real identifiers outside the repository. It applies its EF migrations at startup, so use only a dedicated, isolated database for development and tests. Never point this replacement at the existing home database.

Build and run the checks:

```bash
dotnet build -c Release -warnaserror -v q -nologo
dotnet test -c Release -v q --logger "console;verbosity=minimal"
python -B -m unittest discover -s .github/scripts -p test_classify.py -v
python -B -m unittest discover -s tests/live -p "test_*.py" -v
```

The PostgreSQL integration tests require Docker. The live runner under `tests/live/` is a separate opt-in, operator-assisted proof and uses a dedicated bot, synthetic data and a disposable database. Offline checks alone do not establish a live pass.

CI checks documentation changes without building the application. Application, build and workflow changes also build and test the application and build an image locally. Deployment and image publication are on hold until a separate cutover decision. The files under `deploy/` describe the previous application and are retained for its existing deployment, not for this replacement.
