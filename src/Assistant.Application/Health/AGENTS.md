# Health consultation invariants

- Shared reminder/expectation commands are intercepted before Health interpretation. Expected-event
  commands require an existing profile and never create clinical records. Only active confirmed
  glucose/insulin/meal/symptom/weight/blood-pressure facts in the exact subject/bot/place count, using
  inclusive occurrence-time bounds from saved fixed-offset midnight through deadline plus grace.
  Confirmations, source edits/deletes and document-caption mutations participate in the Infrastructure
  expected-event ordering gate. A satisfied day never reopens after a correction or deletion.

- One interpretation determines event intents and independent `needs_reply`. Application eligibility
  is private/addressed/exact-approved-place; `is_question` never authorizes an answer. Edits never consult.
- Within one parse, deduplicate exact known candidates after existing type/intent normalization.
  All scalar fields and ordinal tag sequences participate; preserve null versus empty tags and tag
  order/case. Never merge different times, values, units, intents or uncertainty, deduplicate across
  sources, rewrite raw model output or change unknown-type clarification and control flags.
- Validate/save clear reports, send deterministic and quick-scan alerts, then attempt the consultation,
  then pending confirmation. A consultation handles uncertainty without a second fixed clarification.
- `HealthConsultationContext` is the single answer snapshot/budget seam. System/runtime plus all
  message texts share `MaxInputChars`; complete oldest readings, notes, history, document text, then
  inventory metadata are removed with honest coverage markers. Document text has its own 20,000-character
  ceiling including JSON/provenance/markers, newest posted date/id first, stopping at a partial prefix.
  Profile/instructions/thresholds and current turn have priority. No derived
  trends, fixed diary count ceilings, pending facts or diagnostic traces enter this snapshot.
- Profile fields use single-field store updates; legacy profile commands must preserve new columns.
  Never send sensitive profile fields to interpretation or ordinary logs.
- Confirmation callbacks require the pending row's exact profile, bot, chat, nullable topic and
  non-null saved prompt message before accepting, declining or expiring it. Family scope is enforced
  by the pending store; a mismatched callback receives only the generic resolved acknowledgement.
- Valid nonedited document identity is durably admitted after current authorization before transport
  storage. Unsupported formats still receive this protection; malformed missing file identity does not.
  File bytes are transient only. Duplicate/recovery paths never replay initial caption interpretation.
- Caption goes directly through the ordinary recording/safety/eligible-answer pipeline, with command
  and model-undo mutation disabled. Body text is untrusted consultation JSON, never diary/profile input.
  Document edits are ignored; replacement requires delete and repost. `/docs` has no model/download.
- Missing document content is requested as pasted/recognized text, readable-text PDF or UTF-8
  .txt/.md within 20 MB. The answer prompt must not ask for uploaded photos/scans as recovery input.
- Source `/del` atomically tombstones document/admission, caption facts and pending confirmations;
  late model/extraction results cannot recreate them. Event-only deletion retains an active document
  reaction. Close returned pending prompts only after commit, using generic text.
- Processing is serialized per bot. Recovery is ten due sources per minute; durable attempts are capped
  at three with one/five-minute transient backoff. Persist delivery attempt before transport; never
  automatically resend an uncertain acknowledgement. Retain the gate until synchronous parsing
  actually returns after cancellation, and discard results from lost leases.
