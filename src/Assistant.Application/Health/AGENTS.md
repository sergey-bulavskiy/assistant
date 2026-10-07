# Health consultation invariants

- One interpretation determines event intents and independent `needs_reply`. Application eligibility
  is private/addressed/exact-approved-place; `is_question` never authorizes an answer. Edits never consult.
- Validate/save clear reports, send deterministic and quick-scan alerts, then attempt the consultation,
  then pending confirmation. A consultation handles uncertainty without a second fixed clarification.
- `HealthConsultationContext` is the single answer snapshot/budget seam. System/runtime plus all
  message texts share `MaxInputChars`; complete oldest readings, notes, then history are removed with
  honest coverage markers. Profile/instructions/thresholds and current turn have priority. No derived
  trends, fixed diary count ceilings, pending facts or diagnostic traces enter this snapshot.
- Profile fields use single-field store updates; legacy profile commands must preserve new columns.
  Never send sensitive profile fields to interpretation or ordinary logs.
- Confirmation callbacks require the pending row's exact profile, bot, chat, nullable topic and
  non-null saved prompt message before accepting, declining or expiring it. Family scope is enforced
  by the pending store; a mismatched callback receives only the generic resolved acknowledgement.
