# Design guidelines

The rules this service is built to. They are the checklist for reviewing a change: if a change breaks one,
either fix the change or update the guideline and say why. [DESIGN.md](DESIGN.md) explains the decisions;
this file is the standard they are held to.

## 1. Correctness first

1. **Rules are pure.** Reconciliation logic lives in `Domain/` with no database, clock, HTTP or logging, so it can
   be tested exhaustively.
2. **Idempotent.** Replaying any event, or the whole stream, changes nothing.
3. **Order-independent.** The final state depends on which events arrived, not the order. Any claim of this is
   backed by permutation tests, including awkward cases (ties, duplicates, conflicting content).
4. **Deterministic ties.** Where two events cannot be ordered, a fixed rule picks the winner, never arrival order.
5. **Every ambiguity gets a written decision** (event names, delete vs unpublish, invalid sample JSON), in DESIGN.md.
6. **State assumptions and the case that breaks them.** E.g. one CMS, versions and timestamps rising together,
   ids not reused.

## 2. Reliability and trust

1. **Acknowledge only after the data is durable.** `200` means every event was decided and committed, `202` means the batch
   is stored in the queue and committed, `503` and a `429` (`inbox_full`) mean nothing was kept. Never acknowledge from
   memory.
2. **One transaction per batch, at each step.** Accepting a batch is one transaction (the batch row plus one event row per
   event; those already decided carry their outcome). Applying it is one transaction (entity and tombstone changes, every
   outcome, the payload removed, the batch completed). Each is whole or not at all, so a failure leaves the batch exactly
   where it was.
3. **No event is silently dropped.** Every event read from a request (applied, ignored, unsupported or rejected) is
   reported in the response, logged, and recorded in the `Events` table (the queue while it waits, the ledger once decided).
   A request refused as a whole (invalid JSON, over the size limit) yields no events, so it has no ledger rows; it is
   still logged and answered with an error.
4. **A bad event never blocks its neighbours.**
5. **Fail closed.** Ties go to unpublish, hidden entities look missing, and missing configuration stops startup.
6. **Retry is always safe.** Every operation a sender may repeat must be idempotent.
7. **A queue is bounded, visible and cannot block itself.** It has a size cap that pushes back (`429`) rather than
   growing, its depth and the age of its oldest item are metrics and an admin report, a failing item is retried with
   back-off and then set aside so it cannot hold up the rest, and its worker catches every error and carries on.
8. **Reordering is allowed only where the rules are order-independent.** Takedowns go first because the outcome does not
   depend on arrival order; anything that would break that property must not be reordered.

## 3. Security (all data is confidential)

1. **Deny by default.** A fallback policy requires sign-in; the only anonymous endpoint is `/health`, which
   reveals nothing.
2. **Least privilege per account.** The CMS account can ingest and nothing else; users and admins cannot ingest;
   only admins can disable, and only an admin can use the test-only reset where it has been switched on.
3. **Visibility is decided on the server from the caller's role,** never from a request parameter.
4. **Hidden looks missing.** A restricted entity returns the same `404` as a nonexistent one.
5. **Credentials:** validated at startup, compared in constant time, rotatable, with a brute-force throttle.
   Development credentials exist only in the development config.
6. **Never log payloads, passwords or Authorization headers,** and keep a test that proves it.
7. **Treat all input as hostile.** Cap body size, batch size, payload size and nesting depth; parse strictly;
   reject control characters and far-future timestamps.
8. **Say what each control does not do.** A checksum detects corruption, not tampering by someone who can rewrite
   both values; Basic auth is only safe over TLS.
9. **A developer tool that holds credentials is as sensitive as the service.** It listens on loopback only, keeps the
   credentials on its server, refuses cross-site and rebinding requests, reaches only the service's own routes, and builds
   no markup from text it was given.

## 4. Data and integrity

1. **Store the latest state only;** the `Events` table is the history of events, holding metadata and checksums, never a
   payload once the event is decided (the schema enforces it with a `CHECK`).
2. **Hard delete means the content is gone,** from the live database file too: `secure_delete` is on for every connection,
   so deleted and overwritten values are wiped rather than left in free pages. Only a payload-free tombstone remains. A
   queued payload is removed from its event row in the transaction that applies it, so a delete cannot leave an earlier
   publish's content behind. (Backups, replicas and WAL archives are outside this; see the design's limits.)
3. **Every payload has a checksum,** computed once at ingestion over the canonical form and stored with it. The
   stored checksum always describes exactly the stored payload text.
4. **Checksums are self-describing** (`sha256:…`) so the algorithm can change without ambiguity. SHA-256 is the
   default; MD5 is available by configuration for compatibility, not for security.
5. **The canonical form is documented** precisely enough for a client to reproduce and verify it.
6. **The admin override is its own column** that CMS events never write.
7. **All timestamps are UTC,** normalised on the way in.
8. **Sanitising means normalising,** not altering content. Escaping for display belongs to the consumer.
9. **Payloads are bounded entity fields; files are never carried in an event.** A payload names a file (address, type, size,
   checksum); the file itself goes through the asset store, where it is streamed to disk, scanned, and served only once clean.
10. **Uploaded content is untrusted until a scanner clears it, and every failure to scan fails closed.** Store it apart (quarantine),
    judge its type from its bytes, never serve anything but a clean asset, serve it as an attachment with `nosniff` and a sandbox
    CSP, and wipe what is rejected or deleted.

## 5. API behaviour

1. **Each caller gets what it needs.** The CMS gets an actionable status code; humans get per-event outcomes
   and a correlation id.
2. **Outcomes and reasons are stable, machine-readable codes.** Errors never echo payloads.
3. **The ingest response reports a checksum for every publish or unPublish event that passed validation,** queued or
   decided. An event rejected for another reason (bad id, version or timestamp) is not given one, because its payload is
   never checksummed.
4. **No endpoint changes entity content.** The CMS is the only source of truth. The one destructive endpoint is the
   test-only reset, which erases rather than edits, and is fenced: off by default, not mapped unless switched on,
   refused at startup in Production, admin-only, behind an exact confirmation, and logged with who and how much.
5. **Keyset pagination with capped limits;** the page cost does not grow with depth.
6. **Status codes mean something to a retrying sender:** 2xx stop, 4xx fix the sender, 5xx/429 retry.
7. **Size is judged from the declared length first,** before any of the body is read or buffered, and the limits are small
   enough to bound memory per request. Senders are told to declare a length and to ask first (`Expect: 100-continue`).

## 6. Performance

1. **A constant number of queries per batch,** however many events it holds.
2. **Separate read and write contexts;** the read side tracks nothing, cannot save, and is opened read-only.
3. **Read queries project to what they serve,** avoid `Include`, and rely on an index chosen for the real filter.
4. **Choose sync or async deliberately and write down why.** This service is durable async only: validate, store, acknowledge,
   apply in the background. One pipeline, not two; a second mode would be a second implementation to keep proven.
5. **Async all the way for I/O,** with cancellation passed through.

## 7. Structure and dependency injection

1. **Feature folders in one web project;** no more layering than the size justifies.
2. **Inject every collaborator** (`TimeProvider`, options, `IPayloadChecksum`) and bind
   configuration through validated options. Never read configuration eagerly at startup, so tests can override it.
3. **Add an interface only where a second implementation is plausible.** Do not wrap EF Core in a repository;
   tests use the real database instead.
4. **Settings fail fast.** Invalid configuration (credentials, checksum algorithm, limits) stops startup with a
   clear message.
5. **Storage details stay behind the persistence boundary.** If payloads move out of the database, the swap
   happens there; see DESIGN.md.

## 8. Observability

1. **Structured, source-generated logs** carrying a batch id that also appears in the response header and the stored rows.
2. **One log line per event** with id, type, version, outcome, reason and checksum. Applied and duplicate at
   Information; stale, unsupported and rejected at Warning; a failed batch at Error. An applied event that won a
   content conflict (`same_version_different_content`) also logs at Warning, so the level does not depend on
   arrival order.
3. **Metrics tags are bounded** (never untrusted text).
4. **Document the alerts operators need,** including "the CMS has gone quiet".
5. **Admin actions are logged with who and what.**
6. **Asynchronous work reports its own health:** a log line when a batch is applied (with how long it waited), a
   warning for each failed attempt, an error when a batch is set aside or the worker faults, and gauges for backlog,
   oldest age and failures.

## 9. Testing

1. **Test the claims, not just the code:** order independence, atomic rollback, no leaks into logs, role
   separation, integrity of stored checksums.
2. **Use real dependencies where cheap:** real SQLite, the real HTTP pipeline, a temp database per test class.
3. **Cover valid and invalid credential combinations,** and every role on every endpoint.
4. **Check the tests bite:** deliberately break a rule and confirm a named test fails. `tools/mutation-check` does it for
   a recorded list of defects and the results are kept in [EVIDENCE.md](EVIDENCE.md); a rule worth a MUST gets one.
5. **Tests are deterministic:** inject the clock, avoid sleeps, give each class its own data, and share no process-wide
   state (a factory closes only its own database's pooled connections, never all of them). Background work is driven
   by calling the processor, with the worker off. Tests of the running worker wait for a condition, with a time limit,
   never for a fixed time.
6. **Inject real failures, not test hooks.** A database trigger makes one entity fail, so rollback, retry and
   setting aside are exercised against the real code and no production path carries a test switch.
7. **Tests carry the requirement they prove** (`[Trait("req", "EV-05")]`), and a test fails when a MUST requirement in
   [REQUIREMENTS.md](REQUIREMENTS.md) has none, or when a test names a requirement that does not exist.
8. **A claim is tested, measured, or labelled.** Numbers come from a recorded run in EVIDENCE.md; anything neither
   tested nor measured is written down as reasoned or assumed.

## 10. Delivery

1. **Runs on Windows, macOS and Linux with nothing to install;** CI builds and tests on all three.
2. **Warnings are errors in CI.**
3. **README is for running it, DESIGN.md is for the reasoning,** REQUIREMENTS.md maps every requirement to its
   tests, and EVIDENCE.md holds what was measured. A fact lives in one place and is linked from the others.
4. **Known limitations are listed openly,** in order of importance, with the next step for each.
5. **A change is not done until** the build is warning-free, tests pass, and the docs match the behaviour.
