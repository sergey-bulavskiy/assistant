# Family assistant

This repository contains a small .NET 10 Telegram bot under development. An allowed user can send `/record <text>` to extract an explicit value and unit through OpenAI and store it in PostgreSQL, then use `/last` to read the latest observation in the same chat and topic. The current extraction accepts positive decimal values in `mmol/L` or `mg/dL`; it does not provide medical interpretation. Recipes and photo input are not implemented.

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
