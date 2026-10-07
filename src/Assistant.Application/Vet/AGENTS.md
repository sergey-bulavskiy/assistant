# Vet text diary invariants

- Keep Vet profile, events, validation and stores separate from Health clinical semantics. Reuse
  only neutral transport, addressing, gateway, splitting, typing and diagnostic contracts.
- Admission precedes MessageStore offset persistence. Source transport/author/original timestamp
  are immutable; edits append input revisions. Provider calls require original messages.id linkage.
  Persist a successful interpretation and exact work plan before mutation. Unknown dispatches pause;
  only an explicit bounded retry permits a new call. Recovery rechecks current member/place approval.
- Candidate identity includes deleted events. Match unchanged facts before source edits, preserve
  stable IDs, and never guess ambiguous mapping or overwrite an independently advanced revision.
- Confirm only the exact frozen review revision/subset. New clarification evidence gets its own
  input/result provenance and preview; profile changes cannot silently replace reviewed defaults.
  A targetless clarification must match exactly one candidate before revising the proposal. Both
  natural and callback acceptance require a delivered complete preview; oversized partial previews
  remain unresolved, and a failed preview send requires a separate post-delivery confirmation.
- Actor and original author differ. History is bot-wide confirmed facts; source IDs/pending/mutations
  are exact-place. Undo selects actor/action time/place, protects later revisions, and persists the
  attempted subset. Profile updates and pending proposals are not diary actions.
- One fast interpretation supplies independent candidate intent and needs_reply. Clear saves,
  pending review and eligible answers are independent. One smart answer uses bounded raw confirmed
  facts with truthful coverage, never pending facts, diagnostic content or a retrieval loop.
