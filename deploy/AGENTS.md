# deploy/AGENTS.md

These tracked files describe the previous application and are retained for its existing deployment. They are not a deployment recipe for the replacement. Image publication and promotion are on hold; cutover and handling saved data require separate owner authorization.

- Never read, print or commit `deploy/.env`; it contains real secrets.
- Do not operate the home containers, runtime, volumes, credentials or database without explicit authorization. The source replacement does not authorize deleting data or deploying.
- Preserve the legacy compose file and example configuration for reference; changes to them do not change the running host. Keep placeholder values generic.
