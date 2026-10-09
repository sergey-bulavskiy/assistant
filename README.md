# Assistant

A family assistant built from Telegram bots. A **manager bot** sets up a family and creates
**role bots** for it; role bots quietly record the messages of approved people in approved chats
into PostgreSQL, one role bot -- the **General assistant** -- can also answer with a real LLM
(a CLI subscription provider or a deliberately configured API provider) once configured. A **health**
role bot tracks one household member and safety thresholds; a **vet** role bot keeps one cat's
glucose and administered-insulin diary, reviewed meter-photo imports and consultation through the ChatGPT subscription.

## What it does

**Manager bot** (the bot whose token you configure):

| Command | Who | What |
|---|---|---|
| `/claim <code>` | anyone, once | Creates the family and makes the sender its first owner. While no family exists, each start prints a fresh code in the log. |
| `/newbot <role>` | owners | Replies with a Telegram link that creates a new role bot (role: up to 64 characters). Once confirmed, the bot starts polling without a restart. The role is kept for a day, across restarts, until you confirm. |
| `/settings` | owners | Lists bots, places and users with buttons: disable/enable/remove a bot or place, set General's "Отвечать на все: выкл/вкл" or Health/Vet's "Отвечать без упоминания: выкл/вкл" for one approved place, allow/deny a user awaiting approval, disable/enable a user, or make an approved user an owner. A place line shows the bot username and its forum topic id, if any. |
| `/usage` | owners, private chat only | Platform-wide spend/state for today and this calendar month (if budgets are configured) plus a per-bot/per-model call/token/cost breakdown for your own family. |

**Role bots:**

- A new **place** (a group the bot is added to, or a forum topic it sees for the first time) needs
  an owner's Yes/No in a DM from the manager bot before any message from it is stored.
- A new **user** writing to any of the family's bots needs an owner's Allow/Deny; once allowed, they
  can use every bot of the family.
- Messages from approved users in approved places are stored exactly once per Telegram update, even
  across restarts. In private chats the bot replies `Получил ✅ #<id>`; in groups it stays silent.
- `/start` (private chats) and `/version` (any chat) reply with a greeting and the running version.

**General assistant** (a role bot created with `/newbot general`): answers with a real LLM instead
of just storing messages.

- Private chats: replies to every ordinary message. Approved groups/topics: only when addressed
  (mentioned by @username, or replied to) — a plain message in a group it's in is still stored, just
  not answered.
- **Reply to all:** an owner can switch "Отвечать на все" on for one group or one forum topic in the
  manager's `/settings`. The assistant then answers every ordinary text message there, not only
  mentions and replies. Each place has its own switch; a topic does not inherit the whole chat's.
  Commands, edits and non-text messages are still ignored, and the rate/daily limits and budgets
  still apply. When such a message can't be answered (limit, budget, models unavailable, an error,
  or the assistant isn't configured), the bot stays silent instead of posting the refusal;
  mentioned or replied messages still get the usual refusal text.
- `/tokens` (any approved member, in any chat or topic with the assistant): answered calls, input
  and output tokens and the models used in this chat/topic since the last `/new` (or since this
  feature was installed, if `/new` was never used there). Calls made before that are not counted.
- `/new` starts a fresh conversation in that chat/topic (earlier messages stop being sent as
  context). `/model` shows the configured models and lets you pin this chat to one (`/model auto`
  returns to the default). `/version` as usual.
- Needs a valid `LLM_MODELS` chain and the selected provider's supported authentication (see
  "Set up the General assistant" below). Without model configuration it replies
  "Ассистент пока не настроен." and every other bot keeps working
  normally.

- General keeps a bounded, fallible summary of older conversation in the exact current chat/topic.
  An addressed answer folds at most one older window through the configured subscription gateway;
  unavailable or stale summary work falls back to recent context. Internal summary calls count in
  /tokens and /usage. Source edits invalidate affected summaries.
- /search <query> retrieves up to ten older incoming text messages after this place's last /new,
  with dates and source references. Search uses Russian and simple lexical matching, without
  embeddings or a provider call. It never searches another bot, chat or topic.
- /remember <text> saves an explicit fact (up to 500 characters); /memory lists this place's saved
  facts and their IDs; /forget <id> retires one. At most 50 active facts are allowed per place.
  /new excludes prior conversation/search/summary but keeps these deliberately saved facts. Retiring
  a fact removes it from the explicit memory section; it does not erase separate conversation mentions.

**Health assistant** (a role bot created with `/newbot health`): a health tracking assistant for one
household member. One health bot tracks exactly one person (its profile, created with
published-guideline default thresholds on its first message). Turn off Group Privacy for it
(step 5) before adding it to the tracking group or topic.

> **It does not replace a doctor.** It checks recorded readings against safety rules and sends fixed
> alerts, but those rules are unconfirmed until the doctor's thresholds are set with `/threshold`
> (alerts say "не подтверждено врачом" until then). Enter the doctor's thresholds as soon as you
> have them, and call the doctor or emergency services whenever in doubt — don't wait for the bot.

Every new ordinary text message in the health bot's chats (at least 3 characters, or shorter eligible text, not only emoji)
goes to the model once, on the `fast` tier: `LLM_FAST_MODELS` first, then the rest of `LLM_MODELS`.
An owner can enable replies without mentioning the bot for one approved group or topic in `/settings`.
The existing `reply_to_all` flag is off for new Health places, and a one-time migration turns off
previously unused Health flags. General settings are unchanged. When enabled, the model's independent
`needs_reply` decision permits an answer after safety checks; plain readings and chatter can remain
silent. Direct mentions and replies work with the setting off. Recording and
confirmation run regardless of this answer setting.
The model turns the message into records (glucose, insulin, meal, symptom, weight, blood
pressure, or a note); the bot validates them, saves them and sets ✍ on the message (👍 where ✍ is not allowed).
The model can also record short observations as notes in the person's words, with one to five
model-selected tags. A note appears in `/today` as `заметка: … #tag`, gets the same ✍ reaction,
and can be edited, undone or deleted like another record. Notes do not trigger safety alerts.
Otherwise it stays silent. If a reading cannot be recorded (an unknown unit, an implausible value, a
time it cannot place) it asks once, as a reply. Unrelated numbers, including arithmetic operands,
are not readings and do not call for a measurement clarification. Bare ambiguous reading reports
still get a clarification; a message mixing arithmetic and health readings still records the readings.
If the model is unavailable or its answer is
unreadable, nothing is recorded and the bot replies "⚠️ Не смог обработать сообщение — ничего не
записано. …" (at most once per 10 minutes per chat or topic). Editing a message (up to 24 hours after sending it) reads it again, with one more model call:
readings that did not change stay as they are (same number in `/today`, no repeated alert), changed
or removed readings are deleted, and new ones are recorded. A changed dangerous value gets its alert
again. If no readings are left, the ✍ disappears. Editing a message whose readings were removed
with `/undo` or `/del` records them again as new ones (the edit is the new truth). If the edit cannot be read (model unavailable), the
earlier records stay and the usual notice or quick-scan alert is sent. Edited commands are not run
again.

**Safety alerts.** Every newly recorded reading is checked by fixed rules in code (never by the
model) against the profile's thresholds (`/thresholds`). A dangerous value or symptom gets a fixed
alert right away, as a reply in the same chat or topic: "⚠️ …" (contact the doctor) or "🚨 …"
(urgent: contact the doctor or call emergency services, with the profile's emergency phone). Alert
texts are fixed templates that point to the doctor's plan; they are never written by the model and
never suggest a medicine or a dose. Each alert names its threshold source: "не подтверждено врачом"
for the published-guideline defaults, "порог от врача" once an owner has entered the doctor's value
with `/threshold`. A glucose reading at or above the target for its context is only marked on the
record (no message). Readings older than 12 hours are recorded without an alert (a new symptom
posted with an older dangerous blood pressure reading still gets the combination alert). Each reading
alerts at most once, also when Telegram delivers the message again.

A quick scan of the message text also runs, so that dangerous values written in the supported
formats are not silently ignored. Supported formats: glucose as a keyword ("сахар", "глюкоза",
"глюкометр") followed within 30 characters by a number with up to two decimals ("сахар 2.5",
"глюкометр показал 2.55"); blood pressure as "150/95" or "150 на 95". It runs when the model's
answer is unusable or the model is unavailable, when the model returns a valid answer with no
readings and nothing unclear, and for a metric (glucose or blood pressure) the model recorded
nothing of while recording other readings. Nothing is recorded from the scan: a dangerous value
gets the fixed alert followed by "Ничего не записано — повторите сообщение позже.", and a value
that is not plausible (e.g. "сахар 250", most likely another unit) gets the clarification; neither
is throttled. Readings written any other way get only the failure notice when extraction failed.

**Consultation.** New text is eligible for a reply in private chats, when it mentions the bot or
genuinely replies to it, or when the owner enables "Отвечать без упоминания" for that exact approved
place. The existing interpretation call independently returns `needs_reply`: a question, supported
request or greeting can receive one `smart` answer; plain readings and chatter can remain quiet.
`is_question` still controls the missing event-intent fallback, not whether to answer. Short eligible
greetings enter the same interpretation; whitespace and emoji alone remain quiet. Edits and failed
interpretations never produce a consultation. There is no fixed greeting hint.

Clear reported values are saved before answering; hypothetical or pending values stay outside the
confirmed diary. Fixed safety alerts, including quick-scan alerts, always precede the consultation.
When an answer is attempted, it handles uncertainty itself; no duplicate fixed clarification is sent,
including when the answer fails. Otherwise the existing fixed clarification behavior remains.
Pending Да/Нет confirmation follows the answer and is omitted when a value needs clarification.

The answer uses the profile, stage week, context note, sourced thresholds, raw active readings from
30 days and notes from 90 days, plus at most ten recent text messages from this bot/chat/topic. The
profile's emergency phone is excluded from answer context. One `LLM_MAX_INPUT_CHARS` bound covers
instructions, runtime, conversation and documents: oldest readings, notes, conversation, document
text and document inventory metadata are omitted in that order, with coverage notices. Only if
protected context plus the current turn alone exceeds the bound is current text explicitly truncated;
an unusably small bound returns the normal failure
notice without a model call. The model derives trends from raw data and acknowledges missing coverage.
Profile/diary/conversation are untrusted background, never instructions or permission to write.

Answers can discuss the supplied doctor's plan and help prepare summaries/questions without
diagnosing. Provider answers are sent with "Не заменяю врача."; no application dose-advice replacement
filter is applied. Provider failures use the existing short operational notices. Only successfully
sent answer parts become outgoing conversation; alerts and other fixed texts do not.

**Ask before recording.** Besides the values themselves, the model also classifies what the sender
meant by each one. A value reported as a fact is recorded as described above. A value that only
appears inside a question or a hypothetical ("а 10 — это много?") is never recorded and gets no
buttons. When the wording could be read either way ("сахар 10 - высокий?"), nothing is recorded
immediately: once an eligible question has been answered, the bot posts
"Записать глюкоза 10.0 ммоль/л?" with **Да** and **Нет** buttons. Any approved family member may tap
either button, and whichever tap arrives first is the one that counts. Да saves the value under the
name of whoever sent the original message (✍ is then added to that message) and rewrites the button
message to "Записано: …"; Нет instead rewrites it to "Не записано." and nothing is saved. A second
tap after the first gets "Уже решено.", a tap more than 24 hours after the question gets "Время
вышло — напишите значение ещё раз.", and a tap from someone outside the family gets "У вас нет
прав.". Editing the original message closes its buttons the same way and re-reads the edited text
from scratch — edited messages are never asked about, so an unclear value in an edit is recorded
directly. Safety checks never wait for Да/Нет: a dangerous value still triggers its fixed alert right
away even if it was only asked about, and a later Да does not send that same alert a second time. If
the model omits the classification for a value, the bot falls back to its separate "is this a
question" flag: a value in a message flagged as a question is treated as asked-about, anything else
is recorded as usual.

**Undo by saying so.** Phrases such as "удали это", "не записывай" or "нет, я только спросил" — sent
either as a reply to a message or addressed to the bot directly — remove the records of exactly one
message, following the same target rules as `/undo`: first the message being replied to (deleting its
records, or closing its still-open Да/Нет question), and otherwise your own most recently recorded
message in that chat or topic from the last 24 hours, or your most recent still-open question if that
is more recent. Recognizing this kind of request is entirely the model's job — the code never removes
more than the one targeted message and never retracts an alert that was already sent. The bot answers
with "Удалено: …", "Не записано." or "Нечего отменять.". This only works when you post in the
tracking group under your own name rather than anonymously as the group, because an anonymous sender
can't be matched to their records; posting anonymously gets "Не могу определить автора — ответьте на
сообщение командой /del." instead.

Extraction makes one LLM call per text message in the health bot's chats. These calls count toward
`LLM_CALLS_PER_DAY` and `LLM_CALLS_PER_MINUTE`, which are per family, per UTC day, and shared with
the General bot — raise `LLM_CALLS_PER_DAY` accordingly. An answered question makes one more call
(`smart` tier) that counts the same way.

| Command | Who | What |
|---|---|---|
| `/start` | anyone, private chat | What the bot does and its commands. |
| `/week` | any approved member | Current stage week and day ("3 нед. 2 дн."), counted from the stage start date in the profile's time zone. |
| `/profile` | any approved member | Stage start date, stage week, time zone, emergency phone, context note, conditions, prescribed medicines, allergies, doctor plan/contacts and thresholds summary; long output is split. |
| `/thresholds` | any approved member | Every safety rule with its values and source: "врач" (entered with `/threshold`) or "не подтверждено врачом" (published-guideline defaults). |
| `/today` | any approved member | Today's records (the profile's local day), oldest first, each with its number (`#12`). |
| `/notes` or `/notes <tag>` | any approved member | Last 10 notes across all dates, or up to 20 notes with the exact tag from the past 90 days, newest first; dates use the profile's time zone. |
| `/docs` | any approved member | Ten newest active documents for this Health profile, with posted date/year, filename, bounded caption and reading status; no download or model call. |
| `/undo` | any approved member | Deletes the records of your latest recorded message in this chat or topic (up to 24 hours old). |
| `/del` | any approved member | As a reply: deletes that source's document, caption records and open confirmations together. `/del 12` deletes only record #12. |
| `/setstart ДД.ММ.ГГГГ` | owners | Sets the stage start date (not in the future, at most 300 days ago). |
| `/settz Area/City` | owners | Sets the profile's time zone (IANA id such as `Europe/Berlin`; default `UTC`). |
| `/setphone <text>` | owners | Emergency number text for alerts (default "103 или 112"; up to 100 characters). |
| `/setnote <text>` | owners | Context note for answering questions (up to 500 characters); `/setnote -` clears it. |
| `/setprofile <field> <text>` | owners | Fields: `состояние`, `лекарства`, `аллергии`, `план`, `врач` (case-insensitive); up to 1,000 characters each. Exact `-` clears that field. Internal spaces/newlines are preserved; other fields and thresholds are unchanged. |
| `/threshold <rule> <field> <value>` | owners | Sets one value of a rule (only the fields `/thresholds` shows for it); the rule's source becomes "врач". Enter the doctor's values. |
| `/threshold <rule> default` | owners | Restores that rule's default values. |
| `/version` | anyone | Running version. |

Other members get "Только владелец семьи может менять профиль." for owner commands. Voice messages
and photos in a private chat get a note directing the sender to text or supported documents.

**Health documents.** Approved members can upload PDFs with readable text, UTF-8 text or Markdown.
The bot retains the initial filename, caption, original posted date and reading status before
downloading, then up to 200,000 extracted characters from at most 20,000,000 bytes. Original file
bytes are not archived. Unsupported, oversized, encrypted, corrupt or scan-only files retain their
metadata and a fixed explanation; this path has no OCR. Readable files receive the normal record
reaction without another generic reply; partial text is marked. `/docs` retrieves retained state.

An initial nonempty caption follows the ordinary recording/safety/eligible-answer policy, while
the document body is reference material only. Caption commands cannot change the profile or undo
records, and body instructions cannot perform actions. An eligible caption answer can use the
just-read document. Duplicate delivery/recovery resumes file work without replaying the caption,
model answer or alerts; the ordinary post-offset caption crash limitation still applies.

Consultations include an all-age active document inventory and up to 20,000 characters of the newest
readable text, within the existing shared input budget. Coverage and partial-text markers describe
omissions; answers attribute material to its document posted date rather than treating that date
as a measurement date. Reply `/del` removes the source from this library and caption diary together;
deleting an individual record or using `/undo` retains its document. Upload edits are ignored: delete
and repost to replace a document.

Durable admission protects a valid document source before the polling offset advances, including
unsupported formats. Recovery handles at most ten due sources per bot once per minute and rechecks
current authorization. Supported downloads consume at most three attempts, with one- then five-minute
delays after transient failures. Processing leases renew independently; original metadata survives
interruption. Attempted or uncertain Telegram acknowledgements are not automatically resent.

**Vet assistant** (`/newbot vet`): one cat profile and a separate confirmed diary for each bot.
Turn off Group Privacy before using its approved tracking group/topic. Owners set the initially
unset timezone and units once, using `/settz UTC`, `/setunit mmol/L` and `/setinsulin <product> U`,
or an explicit natural-language profile change. No care facts are built into the prompt.

Ordinary approved text makes one `fast` interpretation with independent reply intent and intent
for every glucose/insulin fact. Actual measured glucose and insulin already administered are
recorded, including exact fractional decimals; hypothetical doses remain discussion. Missing
historical time, unsupported units and ambiguous intent get a review. Clear siblings can save
while others remain pending. Current reports without a clock use the original message time;
historical records require a reliable date/time and can be from any year. Glucose uses mmol/L,
insulin uses U; there is no automatic mg/dL conversion or numeric veterinary alert threshold.

Consultation requires both the interpretation's `needs_reply` and an approved DM, mention/reply,
or the owner's enabled setting for that exact place. Passive off still records clear reports.
An eligible request can receive one subsequent `smart` answer using the runtime profile and
confirmed diary. It acknowledges missing coverage; ordinary questions use the last seven local
days, explicit historical requests use their chosen period. At most 200 recent facts from that
period fit in the bounded prompt, with omitted counts. Pending facts are excluded. Answers can
help prepare questions for a veterinarian; they do not verify owner-reported guidance.

Confirmed history is available across this bot's authorized places, with source/event handles
withheld for facts from another place. Reviews and mutations remain in the original place.
Any approved member can confirm a shown review; only the first valid acceptance/decline counts.
Clarification creates a new reviewed version. Source authors remain separate from actors who
confirm/correct/delete. Text reviews expire after 24 hours. Photo batches have no automatic expiry. Text edits within 24 hours preserve stable
event IDs, increase revisions only for changed facts, and protect independent corrections/deletes;
ambiguous mapping requires explicit IDs. Older edits need an explicit correction.

| Command | Who | What |
|---|---|---|
| `/start`, `/version` | approved members | DM introduction or running version. |
| `/profile` | approved members | Profile/defaults and separately attributed owner/veterinary notes. |
| `/setname <text>`, `/settz <IANA>` | owners | Name/timezone for future records; `-` clears a field. |
| `/setunit mmol/L`, `/setinsulin <product> U` | owners | Runtime unit/product defaults; `-` clears the product. |
| `/setnote [owner\|vet] <text>` | owners | Owner context or owner-reported veterinary guidance; `-` clears the selected note. |
| `/today`, `/more` | approved members | Today's local diary or the next history page (50 facts per page). Natural requests can choose older periods. |
| `/del <ID>` or `/del` as a reply | approved members | Soft-delete exact events in this place, preserving sources. Natural multi-record changes require review. |
| `/undo` | approved members | Reverse your latest unreversed action here within 24 hours of its action time; later independent changes are protected. |
| `/retry` as a reply | approved members | Explicit retry of a paused/failed text source, at most three retries. Saved successful results are reused. |

Natural corrections use an exact ID, a replied source or one unambiguous date/type match.
Writes are atomic and revision checked. The source is durably admitted before its transport
offset advances; recovery resumes exact saved work without another interpretation. Unknown
provider outcomes pause for an explicit retry. A failed acknowledgement cannot duplicate an action.

Vet requires a configured, active ChatGPT subscription model chain containing only `codex-cli`
entries (`LLM_MODELS` and any `LLM_FAST_MODELS`). Other providers cause a visible refusal for Vet;
profile/history commands remain available. Calls use the shared gateway limits and actual attempt
accounting with the original stored-message trigger; subscription cost is zero.

**Vet meter photos.** Approved members can import JPEG/PNG meter images as Telegram photos or image
files. Use `/photos_start` or `/import readings`, upload across albums or individual messages,
then `/photos_close`.
Intake closes explicitly; an album or elapsed time never closes it. A photo outside an open
collection gets a one-item review. `/photos` lists this place's batches; `/photos <batch-id>`
shows an exact batch. A late album/reply/upload-window item remains identified for explicit
addition; a genuinely new image after a terminal batch gets a separate review.

Metadata is admitted before the Telegram offset commits. Downloading and image extraction run
in an independent background loop, so polling can receive edits and decisions during processing.
Status distinguishes received, retained, processed, unresolved and saved items. Metadata alone
is not an archived original. A failed or interrupted call keeps its durable status; a successful
stored result is reused, and an uncertain dispatch is never automatically repeated.
Unresolved or protected evidence can be shown in a complete notice scoped to the current place,
without acceptance buttons or authority to save facts. A failed or uncertain notice send waits
for an explicit review retry; a fully shown notice does not keep recovery occupied.

The complete review orders clear readings by measurement time and displays unresolved items,
shared year/unit/timezone assumptions, duplicate choices and source numbers. Meter dates never
use upload time as a substitute. Missing year requires a batch decision; missing clock/date,
`HI`/`LO`, multiple displays and conflicting units stay unresolved. Confirming a year alone does
not supply a missing month, day or clock. Only positive exact decimal mmol/L readings can save;
mg/dL remains source evidence without conversion. Synthetic rendered-panel checks do not prove
real-camera accuracy; check the displayed proposal before confirming it.

Save the fully shown clear subset once; it becomes one atomic diary action. Unresolved items
remain reviewable. Edits, changed assumptions, duplicate discoveries or another member's decision
invalidate stale previews. Repeated acceptance returns the same action. Cancellation first closes
collecting intake and shows the exact remainder; it stops new calls only after the full cancellation
review is accepted. Saved facts and originals remain. Original authors remain
separate from the member who saves, corrects or reverses it. Explicit actual insulin in the
caption follows the separate text diary once; planned doses do not record, and image reprocessing
cannot repeat an administration or create a second caption glucose reading.

The same operations are available conversationally, using exact displayed batch/source/item/action
handles when needed. Ambiguous references ask for a specific target. Shortcuts are:

| Operation | Shortcut or selection | Result |
|---|---|---|
| Start intake | `/photos_start` or `/import readings` | Opens one collecting batch in this exact place. |
| Close intake | `/photos_close [batch-id]` | Ends uploads explicitly; duplicate close is safe. |
| Show batch/history | `/photos [offset]` or `/photos <batch-id>` | Lists stored batches or one exact batch. |
| Review item/subset | `/photos_review [batch-id]` or exact item numbers in a request | Shows current evidence and exceptions. |
| Correct item | Exact source/item and supplied value/date/time | Prepares a revised proposal; saved facts need a fresh review. |
| Exclude item | Exact item request or `/photos_exclude [batch-id]` | Reviews the selected exclusion; no invented measurement. |
| Save clear readings | `/photos_save [batch-id]` | Shows the exact clear subset for explicit confirmation. |
| Cancel remainder | `/photos_cancel [batch-id]` or an exact run request | Closes collecting intake and shows its remainder; accepting that review stops new image calls. A run cancellation stops new windows. |
| Change batch assumptions | Explicit year, unit or timezone request | Refreshes proposals and review without rewriting confirmed facts. |
| Undo latest action | `/undo` | Reverses your latest applicable action here within 24 hours; protects later edits. |
| Reverse older save | `/photos_reverse <action-id>` or an exact selected subset | Shows up to 50 exact rows and protected changes before confirmation. |
| Reprocess originals | `/photos_reprocess current\|all_originals [batch-id]` or scoped date/source selection | Shows a fixed selection and call policy before work. |
| Delete originals | `/photos_delete_originals current\|all_originals [batch-id]` | Owner-only preview of exact references and reclaimable bytes. |
| Accept shown review | Acceptance button or unambiguous explicit decision | Applies only that fully delivered, current review. |
| Decline shown review | Decline button or explicit decision | Applies no proposed diary changes. |
| Add late item | Exact late source and identified batch request | Adds explicitly and invalidates the prior batch review. |
| Resolve duplicate | Same reading/link, separate measurement or exclude | Records the reviewed choice; equal value alone is not deduplication. |
| Continue run | `/photos_continue <run-id>` | Explicitly releases the next fixed window; never retries unknown calls silently. |

Reprocessing defaults to current inputs. `all_originals` includes retained superseded and
unknown-date inputs, split into sequential windows of at most 50 without truncating the selection.
Measurement-date selection uses local dates; upload-date selection can recover a wrongly read
meter date. Compare stored results with current facts before confirming each window. Multiple
historical results for one source do not create multiple events. Use
`/photos_result <run-id> <window-id> <source-id> <input-id> <result-id> [restore]` to choose one
exact stored alternative for a fresh review. `restore` explicitly requests restoration; a normal
comparison cannot revive a deleted, excluded, cancelled or manually protected identity. Selecting
historical evidence leaves the current input pointer intact. Unknown attempts retain their charged
slots; explicitly reprocessing them warns about possible additional subscription usage.

Originals are retained for saved, cancelled, excluded, duplicate and superseded inputs. Source
edits append evidence; caption-only changes can share bytes and reuse display extraction only
when prompt/model/schema match. Diary undo, cancellation and trace cleanup never delete originals.
Owner-confirmed byte deletion preserves facts and provenance; shared references and active readers
protect content, and deleted originals require reupload. This deletes application-retained copies,
not Telegram messages or owner-managed backups.

Limits are 50 logical sources per batch, 10 MiB (10,485,760 bytes) per original and 25,000,000 decoded
pixels. Actual bytes/format are checked; images are not resized or re-encoded for archival. Stored
Telegram photo bytes may differ from the camera original; an image document avoids Telegram's photo
compression. Across the application, unique retained content plus active reservations is limited
to 1 GiB (1,073,741,824 bytes), with separate 10,000 input and 10,000 result/attempt slot limits.
These are admission counters, not disk-size or subscription-quota estimates. Full capacity pauses
new bytes/calls without eviction. No automatic capacity increase or retention expiry occurs.

Image extraction requires the pinned Codex CLI `0.160.1`, eligible subscription model
`gpt-6.1-sol` in `LLM_MODELS`, subscription-only model chains and enabled native image capability. Existing
`CODEX_IMAGE_INPUT_ENABLED=false` disables that local capability. There is no separate Vet-photo
environment switch, API fallback or model substitution; archive/status/diary operations remain
available when image extraction is unavailable. The image request carries only source/input IDs,
the selected image and its immutable caption, with tools disabled and no profile/history.

## Reminders

General, Health and Vet support durable reminders in the current approved chat/topic:

- `/remind in 10m synthetic task`
- `/remind at 2026-10-09T14:30 synthetic task`
- `/remind daily 14:30 synthetic task`
- `напомни через 10 минут synthetic task`
- `напомни 09.10.2026 в 14:30 synthetic task`
- `напоминай каждый день в 14:30 synthetic task`
- `/reminders` lists your own current-place reminders and recent outcomes with Cancel buttons.
- `/reminder_settings` shows your preferences; `/reminder_settings +03:00 22:00 08:00` sets your
  fixed UTC offset and quiet hours, shared across your family's role bots.

Creation shows a preview; Save schedules it, Cancel stops future occurrences. Defaults are
UTC+00:00 and quiet 22:00–08:00. Fixed offsets do not adjust for daylight saving time. Schedule
offset/time is frozen per reminder; current settings govern quiet hours. Offsets range from −12:00
to +14:00; quiet-hour endpoints must differ. Only the creator can list or cancel their reminders.
Reminders and expected-event checks share limits of 20 live entries per creator/bot/place and 200
per bot, and a family-wide limit of 10 dispatch attempts per role per UTC day, including unknown
outcomes. Paused checks count toward capacity. Drafts expire after 24h; Save rejects passed
times. Jobs 24h late are skipped; daily jobs advance to the next future occurrence. Delivery with
an unknown result is never automatically repeated; cancellation cannot recall a started send.
Supported conversational forms work in private chat, when replying to the bot or in places with
reply-to-all enabled. Other phrasing beginning with напомни/напоминай shows help. Arbitrary language
interpretation and medical schedule automation are not supported.

## Daily expected-event checks

Health and Vet can check whether a confirmed diary record exists in the current approved chat/topic.
These deterministic commands make no model call and never create a profile or a diary record:

| Command | What |
|---|---|
| `/expect glucose daily 09:00 grace 30` | Previews a daily check for an existing subject profile. |
| `/expectations` | Lists your checks here, their IDs, schedule, status, latest outcome and skipped dates. |
| `/expect_edit <id> daily 09:00 grace 30` | Previews a replacement schedule for an active check. |
| `/expect_pause <id>` | Pauses an active check immediately. |
| `/expect_resume <id>` | Previews restarting a paused check. |
| `/expect_cancel <id>` | Cancels future checks or a creation proposal. |

Use the displayed 32-character ID. Health supports `glucose`, `insulin`, `meal`, `symptom`, `weight`
and `blood_pressure`; Vet supports `glucose` and `insulin` and requires a named cat profile.
Grace is 0–180 minutes, and deadline plus grace must remain before midnight. One live check of a
type is allowed per profile/place, including drafts and paused checks, regardless of creator.
Management and Save are creator-only in the exact bot/chat/topic; an owner's status does not grant
access to someone else's checks. Editing the original command does not change its schedule; use
the management commands. `/expectations` shows up to 20 live or recent terminal entries here.

Creation, editing and resuming change nothing until Save on the delivered preview. They take effect
tomorrow in the check's saved fixed UTC offset; previews expire at that local midnight or after
24 hours, whichever comes first. Editing keeps today's version. Resume requires fresh confirmation
and does not replay paused days. Changing reminder preferences or the profile's IANA timezone does
not move an existing check; current reminder preferences still govern quiet hours.

At deadline plus grace, the check looks for an active confirmed record of that type for the exact
family/profile/bot/chat/topic, with occurrence time from local midnight through deadline plus grace
inclusive. The record can have been entered by another approved member. Pending or hypothetical
values do not count. A confirmed meter-photo glucose import can count in its source place. There is
no dose, product or unit matching, and a missing record does not prove an action was not performed.
The notice reports missing documentation; it gives no treatment advice.

A matched day becomes `satisfied` and never reopens after correcting or deleting its record.
Quiet hours or the shared dispatch cap defer an unmatched check only until the next local midnight.
Older missed dates are marked skipped, without a burst of catch-up notices. Every started send,
including `dispatch-unknown`, consumes the shared cap and is never automatically repeated.
Pause/cancel cannot recall a started send. Revoking a creator's approval, disabling/removing a place
or removing its bot retires affected checks; reapproval does not restore them. A disabled bot cannot
send, and stale dates are skipped when it resumes. Terminal detail is retained for bounded cleanup
for 60 days; latest outcomes remain on live checks.

## Privacy

This project is designed to later handle personal and health data at runtime, in the database. **No
personal data lives in this repository** — not in code, tests, commit history or this README. See
`AGENTS.md` for the full rules.

## Private debug traces

Detailed diagnostics are off by default. Explicit local `DEBUG_TRACES_ENABLED=true` enables
continuous rolling capture for authorized role-bot interactions: the source, assembled model
request, parsed final response, application decisions, and attempted/sent text. Rejected or
replaced final answer text is private and labeled **not sent**; the actual replacement and its
delivery are recorded separately. Traces are separate from conversation history and never replayed.
Manager interactions and unauthorized updates are excluded. Credentials, hidden reasoning,
headers and raw process streams are excluded; known application credentials are redacted before
persistence. Redaction cannot identify every secret typed into ordinary message text, so protect
the database and local exports as sensitive data. Captured content never enters ordinary logs.

Retention is at most 60 days from interaction creation. The default cumulative detail allowance is
256 KiB per interaction, with 256 events, and total accounted storage is capped at
100 MiB. Oldest interactions may be evicted earlier; exports report actual retained coverage,
truncation and cap eviction counts. Accounting counts serialized detail plus a 1,024-byte metadata
reserve per trace and event, rather than PostgreSQL physical file size. Cleanup runs at startup
and hourly, including while capture is off.
Diagnostic write failures are best effort and do not change normal processing or delivery.

Optional local settings can reduce the limits: `DEBUG_TRACES_RETENTION_DAYS` (1–60),
`DEBUG_TRACES_MAX_DETAIL_BYTES` (1024–262144), `DEBUG_TRACES_MAX_STORAGE_BYTES`
(1048576–104857600), and `DEBUG_TRACES_MAX_EVENTS` (16–512). Invalid configuration disables
capture with a sanitized warning. Production compose defaults remain off; applying new compose
variables requires an explicitly authorized container recreation.

Use the separate read-only local export tool with `ConnectionStrings__Assistant` set privately.
Choose an explicit absolute output path outside repositories; the tool refuses existing files.
It starts no bot or host and applies no migrations:

```powershell
dotnet run --project src/Assistant.TraceExport -- --message-id 123 --out C:/private/trace.json
dotnet run --project src/Assistant.TraceExport -- --bot-id 111 --chat-id 222 --telegram-message-id 333 --out C:/private/trace.json
```

The second form resolves a Telegram source reference. An export contains only linked original,
edit and captured confirmation timelines, actual LLM-attempt summaries and delivery results.
Text edits are distinguished from new sends, including their target message IDs. Timeouts have
**unknown** delivery; generated or attempted text is never labeled delivered. Attempt links and
bounded delivery metadata survive exhaustion of the text allowance, subject to the event/storage cap.
Disabled periods, expiry, eviction and failed writes can leave gaps; callback/reaction capture
is not exhaustive. Coverage also lists up to 64 timestamp-only observations of incoming originals
or latest edits without a retained trace in the selected family/bot/chat/topic window, with a total
count and omitted count. Their cause and any continuous disabled period are unknown; no message
content is reconstructed. The tool reports not found when no linked trace remains and prints no content.

## Prerequisites

- A Windows PC (or any Docker host) with **Docker Desktop** installed, WSL2 backend enabled, and
  **set to start automatically with Windows** (Settings → General) — the bot only runs while Docker
  is running.
- A Telegram account to create the bot.

## 1. Turn your bot into the manager bot

This milestone's manager bot creates every other bot for you through Telegram's **Managed Bots**
feature — you no longer create bots by hand in BotFather except this first one.

1. Open a chat with [@BotFather](https://t.me/BotFather) in Telegram.
2. Send `/newbot`, follow the prompts, and choose a name and username. BotFather replies with a
   token like `123456789:AAExampleTokenValueDoNotUseThisOne`. Copy it — this is
   `TELEGRAM_MANAGER_BOT_TOKEN`.
3. Open BotFather's own Mini App (or send `/mybots` → your bot → Bot Settings) and enable
   **"Allow bot to manage other bots"** for this bot. Without this, `/newbot` in the running
   assistant will fail — Telegram will reject the creation link.

## 2. Generate a token encryption key

Role-bot tokens (fetched automatically when `/newbot` completes) are encrypted at rest. Generate a
random 32-byte key, base64-encoded:

```bash
openssl rand -base64 32
```

This is `TOKEN_ENCRYPTION_KEY`.

## 3. Configure `.env`

```bash
cp deploy/.env.example deploy/.env
```

Edit `deploy/.env`:

```
TELEGRAM_MANAGER_BOT_TOKEN=<the token from BotFather>
TOKEN_ENCRYPTION_KEY=<the base64 key from step 2>
POSTGRES_PASSWORD=<pick a password>
POSTGRES_DB=assistant
IMAGE_TAG=latest
```

To also enable the General assistant now (optional, can be done later — step 6): set
`LLM_MODELS`, `LLM_FAST_MODELS`, and complete supported provider login. Every other `LLM_*` limit already has a working default
from `deploy/docker-compose.yml` — leave them unset unless you have a reason to override one in
`.env` (see `deploy/.env.example`).

`POSTGRES_PASSWORD` must contain **only letters and digits** — it's interpolated directly into a
Postgres connection string in `deploy/docker-compose.yml`, and punctuation there (`:`, `@`, `/`,
etc.) can break parsing.

`deploy/.env` is gitignored — never commit it.

## 4. Run it

```bash
docker compose -f deploy/docker-compose.yml up -d
```

This starts three containers: `postgres` (with a named volume, so data survives restarts),
`app` (the bot, pulled from `ghcr.io/sergey-bulavskiy/assistant`), and `watchtower` (checks for a
new `app` image every 5 minutes and restarts it automatically — Postgres is never touched).

> **Note:** the first image the CD workflow publishes to GHCR is **private** by default, and
> Watchtower has no credentials to pull a private image. Make the package public once: GitHub →
> the repo's **Packages** tab → `assistant` → **Package settings** → **Change visibility** →
> **Public**.

## 5. For every new role bot: turn off Group Privacy

Bots created with `/newbot` have Telegram's Group Privacy mode on, so in groups they only see
commands and replies, not ordinary messages. Before adding a role bot to a group: in BotFather,
`/mybots` → the role bot → Bot Settings → **Group Privacy** → **Turn off**. (Making the bot a group
admin also works.) If the bot was already in the group when you changed this setting,
remove it and add it again for the change to take effect.

An ordinary `@mention` is not a documented delivery path for a non-admin bot with privacy
mode enabled: the application cannot answer a message Telegram never delivers. Targeted
commands such as `/version@<bot-username>` and replies to that bot's actual messages work
with privacy mode enabled. Those checks establish command and reply handling; they do not
verify ordinary mentions or passive recording. See [Telegram's privacy-mode documentation](https://core.telegram.org/bots/features#privacy-mode).

## 6. Set up the General assistant (optional)

Empty `LLM_MODELS` keeps model access off. General replies and Health text extraction use the same
gateway, with separate smart and fast chains. The Linux amd64 image includes pinned Codex CLI
`0.160.1` at `/usr/local/bin/codex`; its private authentication volume is `codex-home`, mounted at
`/home/app/.codex`. Packaging does not prove account access, disabled tools or extraction quality.
Local runs can set absolute `CODEX_CLI_PATH` and `CODEX_HOME`; the CLI must still be exactly the
pinned native binary. Production compose uses the image paths and private auth volume.
Confirm these before switching an existing deployment; changing the home deployment requires the
owner's explicit approval.

1. Create a General bot with `/newbot general`; turn off Group Privacy for group use.
2. After deploying the reviewed image and compose changes, complete supported ChatGPT login in
   a one-off container with the application entrypoint overridden. The existing desktop session
   does not establish login inside this volume. Enable device authentication for the account if
   needed, complete the browser approval privately, then check status without reading credentials:

   ```bash
   docker compose -f deploy/docker-compose.yml run --rm --no-deps --entrypoint /usr/local/bin/codex app login --device-auth -c 'cli_auth_credentials_store="file"' -c 'forced_login_method="chatgpt"'
   docker compose -f deploy/docker-compose.yml run --rm --no-deps --entrypoint /usr/local/bin/codex app login status
   ```

   These supported pinned-release commands still require actual deployment account access.
   Keep the displayed device authorization code private. Do not inspect or print credentials,
   use API-key login for this switch, or start another app poller.
3. Prove one synthetic ordinary reply and structured extraction with effective tool/context
   isolation, through the existing gateway. Verify the selected fast model with the live extraction
   evals: at least 90% of all cases and every critical case must pass. See
   [the eval guide](tests/Assistant.Evals/README.md). Select an available model from this evidence;
   no identifier is implied by this example:

   ```text
   LLM_MODELS=codex-cli:<verified-model>
   LLM_FAST_MODELS=codex-cli:<verified-model>
   ```

   Both chains must contain only ChatGPT subscription entries. A Codex entry in either raw chain
   disables Claude/API entries; remove Codex from both chains for deliberate manual rollback.
   General preferences outside the active catalog are ignored. Health has no preferred-model
   override. Do not mistake a stale saved preference for an active fallback.
4. Recreate compose after approved configuration/login preparation:

   ```bash
   docker compose -f deploy/docker-compose.yml up -d
   ```

   Watchtower replaces the existing image; it cannot apply new environment variables or volume
   mounts. Never run two pollers against the same bot token. Verify the running image, one actual
   Telegram reply and provider/model/outcome metadata before calling the deployment switched.

Subscription calls retain call/token/attempt accounting with zero monetary `Cost`; this does not
mean unlimited quota or represent subscription charges. Auth, process or quota failure reports
unavailability and waits. It never automatically invokes Claude or a paid API. Raw CLI event
streams can contain private content and must never enter ordinary logs or traces.
Native image requests support one JPEG or PNG up to 20,000,000 bytes on the verified combination
of Codex CLI `0.160.1` and `gpt-6.1-sol`. The adapter passes the image directly with `--image`
while keeping tools disabled. This path was verified with synthetic inputs on Windows and
nonroot Linux. Image attempts use the same call/token counters and zero subscription monetary
cost. A durable attempt identity prevents automatic redispatch after an uncertain result or
restart. Local hosts can explicitly disable this capability with `CODEX_IMAGE_INPUT_ENABLED=false`;
the production compose uses the enabled default. This option does not select or change model chains.
For Codex, `LLM_MAX_OUTPUT_TOKENS` is an instruction target rather than a verified hard generation
cap. The adapter rejects final text above eight characters per configured token and bounds final
file bytes/process streams; timeout and cancellation limit execution separately.

Role import handlers share Telegram document/photo metadata and bounded attachment downloads.
Captions remain ordinary message text, and photo size variants describe one source image.
Downloads enforce the actual-byte limit, a 60-second deadline and caller cancellation.
Document text extraction accepts UTF-8 plain text/Markdown and PDFs with a readable text layer,
retaining at most 200,000 characters from at most 20,000,000 bytes. Truncation and unreadable,
encrypted, malformed or unsupported files have explicit results. PDF extraction preserves page
boundaries and marks its text-only coverage. Scan-only PDFs have no readable text in this path.
PdfPig page parsing is synchronous, so cancellation takes effect between page operations.
Health runs parsing on a worker task while renewing its lease from independent scopes. The per-bot
processing gate remains held until that task actually finishes; cancellation cannot hard-preempt a
PdfPig open/page operation. Results after cancellation, lease loss or source deletion are discarded.

For a count-only audit of General preferences, privately query `chat_settings` for the number of
non-null `preferred_model` values outside the final configured model list. Do not return rows,
chat/topic identifiers or settings contents. No reset is required for safe routing; an optional
runtime reset needs approval and an operator-managed private backup if exact rollback is desired.

Claude remains available for deliberate manual rollback. Generate its subscription token with
`claude setup-token`, set `CLAUDE_CODE_OAUTH_TOKEN` and legacy model entries in both chains, then
recreate compose. Its proprietary CLI is installed at startup into `claude-home` only when active;
`CLAUDE_CLI_VERSION` retains the reviewed pinned default. Paid rollback prerequisites follow below.

## 7. Set up API providers and budgets (optional)

These providers are for deliberate manual rollback from subscription-only access; every variable is optional
and empty by default.

1. **Before putting a key in `.env`:** create a dedicated API key used only by this app (not a key
   shared with anything else), and set a monthly spend limit at the provider — a project budget in
   the OpenAI dashboard, or a monthly spend limit in the Anthropic Console. This app's own
   `LLM_BUDGET_*` variables are a second, independent guard on top of that provider-side cap, not a
   replacement for it: the app cannot verify the provider-side cap exists, so set it first.
2. Get an API key from Anthropic (`ANTHROPIC_API_KEY`) and/or OpenAI (`OPENAI_API_KEY`). Optionally
   `OPENAI_BASE_URL` (an OpenAI-compatible endpoint other than `api.openai.com`) and
   `ANTHROPIC_PROXY`/`OPENAI_PROXY` (`http://`, `https://` or `socks5://[user:pass@]host:port`, if
   this provider needs to be reached through a proxy from this network).
3. Add `anthropic:<model>`/`openai:<model>` entries to `LLM_MODELS` (alongside or instead of
   `claude-cli:` ones — every entry, regardless of provider, falls back to the next one in the order
   listed) and a matching price in `LLM_PRICES` for each one: comma-separated
   `name=input/output` entries, prices in USD per million tokens (e.g.
   `claude-haiku-4-5=1/5,gpt-6-luna=0.1/0.5` — see `deploy/.env.example` for current example model
   ids; check current provider docs for current ids and prices). A `claude-cli:` entry always costs 0
   toward budgets and needs no price entry. An `anthropic:`/`openai:` entry missing its API key or its
   price is dropped individually (one logged error) without stopping any other entry, including other
   `claude-cli:` models.
4. Set `LLM_BUDGET_DAILY_USD` and `LLM_BUDGET_MONTHLY_USD` (both required together — a paid entry
   stays disabled until both are set). Optionally `LLM_BUDGET_WARN_PERCENT` (default 80) and
   `LLM_BUDGET_HARD_PERCENT` (default 120). **The real ceiling this app will ever spend in a
   day/month is that number × `LLM_BUDGET_HARD_PERCENT` / 100, plus possible overshoot of a few calls
   already in flight when the cap is crossed** (so a $2 daily budget with the default hard% can in
   practice reach a bit over $2.40) — size the provider-side cap from step 1 with that real ceiling in
   mind, not the `LLM_BUDGET_*` number alone.
5. Optional: `LLM_FAST_MODELS` — a cheaper, ordered `provider:model` chain (every entry must already
   appear in `LLM_MODELS`). The health bot's extraction (`fast` tier) tries these first, then the rest
   of `LLM_MODELS`; without it the `fast` tier uses `LLM_MODELS` as is. The same list is used
   automatically once spend crosses 100% of either period, before the hard cutoff.
6. `docker compose -f deploy/docker-compose.yml up -d` (same note as the `claude-cli` section:
   Watchtower alone does not pick up new environment variables).

Budget state, checked after every call and before each new one:
- **Below warn%:** normal — every configured paid and subscription model stays usable.
- **Warn (≥ warn%, < 100%):** nothing is restricted yet; platform admins (owners of the first family)
  get a one-time DM through the manager bot the first time spend crosses this line for the day or the
  month.
- **Soft (≥ 100%, < hard%):** platform admins get another DM; new calls restrict to `LLM_FAST_MODELS`
  entries and zero-price (`claude-cli`) entries only — other paid models are skipped as unavailable.
- **Hard (≥ hard%):** platform admins get a third DM; only zero-price (`claude-cli`) entries still
  answer. If none is configured, the assistant refuses with a message naming when the budget resets.

`/usage` (manager bot, owners only, private chat) shows current spend and state for today and this
month (when budgets are configured) plus a per-bot/per-model call/token/cost breakdown for your own
family, covering both today and this calendar month.

## 8. Smoke test

When the supervised smoke owner tests reminders in an isolated synthetic environment, verify
create → preview → Save → `/reminders` → Cancel in one exact place. Test a short due reminder
outside configured quiet hours and confirm one delivery. Unknown sends need separate controlled
failure evidence; registry promotion alone proves neither reminder delivery nor home runtime.

For expected-event checks, use an isolated synthetic Health/Vet profile and exact approved place.
Verify create → preview → Save → `/expectations`, then edit, pause, fresh-confirmed resume and cancel.
Confirm a matching active record suppresses a notice and another topic's record does not; verify
pending values do not satisfy a check. Check a missing-record notice, quiet-hour deferral, the shared
reminder/check dispatch cap and restart behavior without repeating an unknown send. Verify deletion
after `satisfied` does not reopen the day. These checks require dedicated evidence before claiming
live delivery; they do not authorize production data changes or share another smoke owner's session.

An automated smoke test drives real Telegram through one throwaway account: once the repository
variable `SMOKE_ENABLED` is `true`, CD runs it on every merge to `main` and moves `latest` (what
Watchtower pulls) only if it passes; until then CD promotes without it. One-time setup, running it
locally and enabling the gate: `tests/Assistant.SmokeTests/README.md`. It does not cover creating a
bot through `/newbot`, an unknown user's approval or promoting a second owner; the manual
checklist below does.

Private tracing is checked separately with synthetic inputs and explicit local authorization:
confirm default-off writes no trace, then opt in in an isolated runtime, send a synthetic message,
and export it to a private absolute path. Check request/attempt/source linkage and each final text
delivery result; a replaced answer must show its original as `not sent`. Check an edit and a
confirmation remain distinct. Disable capture afterward and confirm cleanup still runs. This
check is not part of ordinary Telegram smoke and does not authorize capture in the home app.

Optional [supervised local Telegram MCP checks](tests/Assistant.SmokeTests/TELEGRAM-MCP.md)
use an existing account with dedicated test bots/chats and synthetic messages; they do not change
the automated suite or CD gate. Follow the linked runbook for shared session ownership,
bounded runs, and required connector cleanup when multiple agents work in parallel.
They include settings/topic isolation, restart observations and optional Health record/removal
checks; Health inputs require proof of an isolated disposable database and dedicated bot/profile.
Explicitly authorized document/photo checks can use the runbook's synthetic upload extension:
one private fixture directory, exact file/destination manifests and the same bounded attempt
budget. The ordinary connector keeps its eight-tool filter and file uploads disabled.

Manual checklist, for after setup and after any release that changes bot behaviour. It creates
real bots and a real family.

- On startup with no family yet, the log prints a claim code (new on every start) — send
  `/claim <code>` to the manager bot in a DM. It replies confirming you're the platform owner.
- Send `/newbot <role>` (e.g. `/newbot general`) → the manager replies with a Telegram creation
  link. Tap it, confirm creation in Telegram's own UI → the manager DMs you that the bot is
  running, with no restart needed.
- Turn off Group Privacy for it (step 5), add it to a group → every family owner gets a DM with
  Yes/No buttons; tapping Yes lets it start recording messages there. The group stays silent;
  a private chat with the bot gets `Получил ✅ #<id>`.
- In a forum group, post in a topic the bot hasn't seen → a separate Yes/No DM for that topic.
- Have an unrecognized Telegram account message the role bot → every owner gets an Allow/Deny DM
  for that user. After Allow, that account can use every bot of the family.
- `/settings` on the manager bot lists bots/places/users matching reality. Disable a bot → it
  stops replying and storing; Enable → it resumes. Remove a place → a new message from it starts
  a fresh approval. "Сделать владельцем" on a user → they can use `/settings` and get owner DMs.
- With a General assistant in an approved group: `/settings` → tap "Отвечать на все: выкл" on that
  group's place → an ordinary message without a mention gets an answer; `/tokens` in the group then
  shows one answered call. Tap "Отвечать на все: вкл" → plain messages are ignored again.
- In a disposable General chat/topic, save an invented fact with `/remember`, verify `/memory`
  and an ordinary answer can use it, then retire its displayed ID with `/forget`. Search an invented
  older keyword; after `/new` it is no longer found, while a separately saved fact survives until
  retired. Editing a summarized source invalidates affected derived memory. This checklist does
  not claim live provider or Telegram verification.
- With a Health assistant in an approved forum topic: `/settings` → find that bot and topic, tap
  "Отвечать без упоминания: выкл", then send a synthetic question without a mention in
  that topic. It gets an answer. An unrelated approved topic stays off. Tap the matching "вкл"
  control to stop passive answers; explicitly addressed questions still work.
- With an isolated synthetic Vet profile: create `/newbot vet`, disable Group Privacy and approve
  one topic; set `/settz UTC`, `/setunit mmol/L`, `/setinsulin synthetic-product U`. Send an actual
  synthetic glucose/insulin report and check both exact values in `/today`. Edit one value and check
  its ID stays stable; `/undo` restores that change. A hypothetical dose must not become a record.
  Enable that topic's "Отвечать без упоминания" and check an unmentioned request gets an answer,
  while plain reports do not require one. An older-month list and `/more` must use stored history.
  Keep these checks synthetic and isolated; no existing diary or live Telegram session is implied.
- For an explicitly authorized synthetic meter-photo check, follow the existing
  [upload runbook](tests/Assistant.SmokeTests/TELEGRAM-MCP.md) and its exact send budget in an isolated
  disposable database. Check explicit close, full sorted review, one confirmed action, stale/replayed
  decisions, retained originals after cancellation and owner-only deletion. This checklist is not
  evidence that a live Telegram check has run.

- `/newbot health`, turn off its Group Privacy, then in a private chat with it: `/thresholds` lists
  the defaults, each "не подтверждено врачом"; `/setstart` with a date exactly three weeks ago, then
  `/week` → "Неделя: 3 нед. 0 дн."; `/threshold glucose.any low_alert 4.0` → `/thresholds` shows that
  rule as "врач"; `/threshold glucose.any default` restores it.
- In a private chat with the health bot: "вес 70.5" → ✍ on the message and `/today` shows
  "вес 70.5 кг"; "сахар 400" → "Не понял «400» — уточните единицы (нужно в ммоль/л)."; `/undo` →
  "Удалено: …" and the ✍ disappears.
- In a private Health chat, send "После прогулки легче сосредоточиться"; check ✍ and a "заметка:"
  line in `/today`. `/notes` shows the note; `/notes прогулка` finds it if the model chose that tag.
- In a private chat with the health bot: "сахар 2.5" → ✍ and "🚨 Глюкоза: 2.5. … Порог 3.0 — не
  подтверждено врачом. …"; "давление 150/95" → "⚠️ Верхнее давление: 150 — выше порога 140 (не
  подтверждено врачом). …"; `/threshold glucose.any low_alert 4.0`, then "сахар 3.9" → "⚠️ Глюкоза:
  3.9 — ниже порога 4.0 (порог от врача). …". With the model off, "сахар 2.5" → the alert followed by
  "Ничего не записано — повторите сообщение позже."
- In a private chat with the health bot: "вес 70.5", then edit it to "вес 71.5" → ✍ stays and
  `/today` shows only "вес 71.5 кг"; edit it to "просто текст" → the ✍ disappears and `/today` says
  "Сегодня записей нет.". "сахар 5.5", edited to "сахар 2.5" → the 🚨 alert once; edit it again to
  "Сахар 2.5" → no second alert.
- In a private chat with the health bot (stage start set, one reading posted): "какой сахар считается
  нормой натощак?" → an answer that can refer to the stage week or the thresholds, ending with "Не
  заменяю врача."; an eligible greeting can also receive a model reply. In a tracking group with the Health reply setting off, the
  same question without a mention gets no answer; with `@<health bot username>` it gets an answer.
  With the setting on for that place, the unmentioned question gets an answer too.
- `/setprofile план synthetic plan`, then `/profile` shows it; `/setprofile план -` clears it.
  A dangerous clear report plus consultation request gets its fixed alert before the answer.
  An unclear value plus consultation request gets one model clarification, with no fixed duplicate.
- In a private chat with the health bot: "сахар 10 - высокий?" → an answer, followed by "Записать
  глюкоза 10.0 ммоль/л?" with Да/Нет buttons; tapping Да → "Записано: …" and ✍ on the original
  question; tapping Да again → "Уже решено.". `/today` shows the value exactly once. "а если сахар
  2.5, что делать?" → the 🚨 alert fires immediately together with an answer, with no buttons and
  nothing added to `/today`. "сахар 9 - высокий?" followed by "нет, я только спросил" → "Не
  записано." and the buttons are removed. In the tracking group, a tap from an account that is not
  an approved family member → "У вас нет прав.".
- Send `/version` to the role bot → it replies with the running version.
- Restart the process (or container) and resend a message you already sent before restarting to
  any bot → no duplicate row, no duplicate reply, for every bot independently.

## Rollback

With the smoke gate enabled, `latest` only moves after the smoke test passes, so pinning is only
needed to go back to an older release. A `sha-` tag of a build that failed smoke exists in GHCR but
was never promoted; don't pin it.

Pin a previous image by setting `IMAGE_TAG` in `deploy/.env` to a known-good short SHA (from a past
successful build, e.g. `sha-abc1234`), then:

```bash
docker compose -f deploy/docker-compose.yml up -d
```

Watchtower then leaves `app` alone as long as `IMAGE_TAG` is pinned to that specific tag (it only
auto-updates `latest`).

## Running tests locally

```bash
dotnet test
```

Integration tests need Docker (they start short-lived Postgres + IntegreSQL containers via
Testcontainers automatically). For a faster local loop, start `docker-compose.tests.yml` once and
point the tests at it instead:

```bash
docker compose -f docker-compose.tests.yml up -d
INTEGRESQL_URL=http://localhost:15000/ TEST_PG_HOST=localhost TEST_PG_PORT=15432 dotnet test
docker compose -f docker-compose.tests.yml down
```

Extraction evals (`tests/Assistant.Evals`) run with every `dotnet test`: invented health messages
with recorded model answers are checked against the expected records and alerts, without calling a
model. A live run against a real model is opt-in and local only (`EVALS_LIVE=1`); see
`tests/Assistant.Evals/README.md`.

## Privacy note

No personal data is stored in this repository. All data the bot collects at runtime lives in the
`postgres` container's Docker volume, on your own machine.
