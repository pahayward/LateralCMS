# Rebuild brief: Lateral CMS ingestion service

Written 5 Nov 2026, after the first build (`main` at `2238384`). It is the one input a fresh session needs to build this
solution again, cleanly. It holds the original brief word for word, the decisions already settled with the user, the target
design, a requirements matrix, the standard of evidence every claim must meet, what is knowingly left at demo quality, and a
phased plan with exit gates.

**Contents:** [0 Using this brief](#0-using-this-brief) · [1 The original brief](#1-the-original-brief-verbatim) ·
[2 Situation](#2-situation) · [3 Decisions](#3-decisions) · [4 Target design](#4-target-design) ·
[5 Requirements matrix](#5-requirements-matrix) · [6 Standards of evidence](#6-standards-of-evidence) ·
[7 Demo versus production](#7-demo-versus-production) · [8 Build plan](#8-build-plan) ·
[9 Working agreements](#9-working-agreements) · [10 Risks](#10-risks) · [Appendices](#appendix-a-the-order-free-oracle)

## At a glance

- **What it is:** a .NET 9 service that receives a CMS's `publish`, `unPublish` and `delete` events in batches, reconciles
  them into the latest state of each entity whatever order they arrive in, and serves that state through an authenticated,
  role-filtered REST API. Everything is confidential, and correct event processing matters most.
- **Why rebuild:** the first build works (360 + 100 tests) but grew sideways: synchronous first with asynchronous bolted on,
  two event tables, four migrations, six overlapping docs, and a console and generators wrapped around the core.
- **The thesis:** the same behaviour, one asynchronous pipeline from the start, a smaller core (about 2,500 lines against
  3,374), and no claim without evidence.
- **What the evidence standard adds:** every MUST requirement has a test that was seen to fail when the behaviour was
  broken; performance claims are measured and recorded; the README is executed in CI; the matrix is checked against the tests.
- **Found while writing this brief (§2.4):** an independent oracle agrees with the first build's rules on 600,000 shuffled
  timelines, but only while a higher version never carries an earlier timestamp; "hard delete" left payload bytes in the
  SQLite file; applied throughput here is about 1,000 to 1,400 events/s and a one-event request takes about 10 ms.
- **Plan:** eight phases, each ending at an evidence gate (§8); the last, developer tooling, is optional.
- **Decisions the user may veto (defaults in §3.2):** asynchronous only, with no sync switch; one `Events` table as queue and
  ledger; `secure_delete` on; tooling last.

## 0. Using this brief

- Read all of it before writing code. Where parts conflict, the order of authority is: the original brief (§1), decisions
  settled with the user (§3.1), the target design (§4), the matrix (§5), everything else. If something here looks wrong,
  say so and propose the fix. Do not deviate silently.
- A working reference implementation exists at commit `2238384`. Make it a read-only worktree next to the repo:
  `git worktree add ../Lateral-v1 2238384`. Port its leaf components and their tests (list in §8.2). Do not copy its
  ingestion and persistence structure; that is the part being redesigned (§2.2, §3.2).
- Plan before building. The user reviews plans first and decides when to commit. Open with one short message that confirms
  the defaults in §3.2 (carry on with them if there is no objection), then do Phase 0 and stop at each phase gate (§8).
- Kickoff prompt to give the new session:

  > Read docs/REBUILD-BRIEF.md in full. Rebuild the service on a new branch, following its phases and evidence gates.
  > Your first reply: the defaults you will use from section 3.2, and your Phase 0 plan. Then start Phase 0. Stop at each
  > phase gate and show me the evidence before anything is committed.

## 1. The original brief (verbatim)

Everything below is the assignment as the user pasted it, character for character. Its sample JSON is not valid JSON
(typographic quotes, a trailing comma); it is reproduced as given, and how to treat it is settled in §3.1 (S4). The brief
itself says what it is judged on: *"Focus on the correctness, structure, and clarity of the system. Correct event
processing is the most important part."*

```text
Scenario: A CMS sends live updates via a webhook/event pipeline.
Events include, but are not limited to:
- Add → new entities are being created.
- Update → an existing entity was successfully changed.
- Delete → an entity was removed or unpublished. Please note that unpublishing the entity does not remove it from the CMS, but disables it.
Once an entity is updated (new fields, new values etc), a new version is set for the entity in the CMS. New data is only available upon publishing the event! This will send an event containing the latest entity version. An “unpublish” event will contain the entity fields, as well as the version that is being unpublished.
Please keep in mind that an entity with version X can be modified → resulting in version X+1. Then, this gets unpublished. However, since there was no published version before that, you do no longer have the latest version in your database. Please also treat this corner case appropriately!
The CMS also handles confidential data, and must not be served publicly. Since the system does not provide any information about which data is of the said tier, you have to assume all data is restricted. The entities should not be publicly available. Please authenticate your endpoints so that only allowed users can access the REST API. You can use the same basic auth used for the CMS, but the users should be different. One is an organization request (the CMS sending updates), the other is user requests.
Task: Build a .NET 9 service that ingests those events, processes them, stores them in a database, and exposes them via a clean API.
Requirements:

1. Data ingestion layer
   * Expose a webhook endpoint: /cms/events
   * Secure your endpoint using Basic Authentication.
      * Username: feel free to use any string with a length of 10 to 20 characters.
      * Password: random guid.
   * Accept batch events. The first version added is version 1, and you can get multiple revisions per entity. Schema example below:

[
{ "type": "publish", "id": "X", "payload": {...}, "version": 2, "timestamp”: ”2024-01-01T00:00:00Z”},
{ "type": "delete", "id": "Y", "timestamp”: ”2024-01-01T00:00:00Z” },
{ "type": "unPublish", "id": "Z", "payload": {...}, "version": 4, "timestamp”: ”2024-01-01T00:00:00Z”},
]

2. Application Layer
   * Incoming data must be validated and sanitized.
   * Deleted entities should be removed (hard-delete), while unpublish should still keep the data in your persistence layer.
3. Data storage
   * EF Core + Relational database of your choice.
   * Entities must keep track of the latest data version.
4. REST API
   * Provide endpoints that support listing entities for consumers.
      * If the user is an admin (feel free to decide which userId/email corresponds to that of an admin), it will receive all entities that a normal user can see, including the ones that were disabled. No need to implement separate endpoints, feel free to decide how to approach this!
   * Data can not be updated by any kind of users, but an admin can disable them from the API - this will not affect the CMS, it’s an overwrite that does not affect CMS data!
5. Performance
   * Feel free to use any asynchronous mechanisms to process incoming requests. This can also be synchronous, but we would like to understand why either decision was taken.
   * Use a read-only/writer configuration for your application context and optimize your EF read queries.
6. Observability
   * Log processed events, including failing ones.
7. Testing
   * Test how events are processed and whether the ingestion constraints are successfully followed by the service.
   * Test your basic authentication mechanism with valid/invalid user-password combinations.
8. Deliverable
   * .NET Core 9 or above solution.
   * Provide a README with setup/running instructions.
   * The solution should work on either Mac or Windows, so build it platform-agnostic.
   * Github repo with all the above.

Notes to consider: This assignment is intentionally open-ended. Focus on the correctness, structure, and clarity of the system. Correct event processing is the most important part. Feel free to make use of available LLMs to help you achieve the goal proposed in this assignment!
```

Reading it for what it leans towards, not only what it requires (a lesson from the first build): requirement 5 *permits*
synchronous processing but *leans* asynchronous; the whole scenario is about events that arrive late, twice and out of
order; and "confidential" means every default must fail closed.

## 2. Situation

### 2.1 What exists (the first build)

| Item | Value |
|---|---|
| Commits | `ea9e9aa` v1, synchronous (3 Nov) · `c3f7392` checksums, console, guidelines, diagrams (4 Nov) · `5e82c2f` matrix and story (4 Nov) · `5c0bab3` reset, endpoints tab, VS launch (5 Nov) · `2238384` durable async (5 Nov) |
| Stack | .NET 9 (`RollForward=Major`), ASP.NET Core minimal APIs, EF Core 9.0.22 on SQLite, xUnit 2.9.3, `WebApplicationFactory` |
| Size | Production C# 3,374 lines (plus 1,240 of migrations). Service tests 4,546 lines, 360 tests. Console tool about 2,200 lines, 100 tests. Six docs, 1,916 lines. Diagram generator 459 lines |
| Verified | Strict Release build clean. 360 + 100 tests pass; the service suite 35 times in a row. About 30 deliberate-defect checks (7 on the rules, 12 on the async pipeline, 12 on the console), recorded only in prose. CI (Windows, macOS, Linux) passed at `c3f7392` |
| Not verified | CI on the last four commits (nothing was pushed). Load behaviour (until the measurements in §2.4). Physical deletion (disproved in §2.4). The Visual Studio launch profile |

### 2.2 Why rebuild, and what each problem becomes

| Symptom in the first build | Cause | Response in the rebuild |
|---|---|---|
| Two ingestors, a mode switch, a "same result in both modes" test, docs rewritten twice | Built synchronously first; asynchronous retrofitted | One asynchronous pipeline from day one (D1). The brief's lean towards async is decided up front |
| Event rows live in two tables (inbox and ledger) and move between them | The inbox was added after the ledger | One `Events` table is both queue and ledger (D2) |
| Four migrations, a checksum backfill and a startup repair | Checksums, an unused index and the inbox all arrived after v1 | The cross-cutting checklist (§3.3) is answered before design; one migration (D4) |
| Reducer mutates EF-tracked objects and keeps a spare instance to dodge an EF limitation | Domain code shaped by persistence | Immutable reducer; the persistence layer turns before/after states into inserts, updates and deletes (D3) |
| Console, diagram generator, S3 comparison, VS profile, reset endpoint crowd the core | Scope grew sideways | Phase 8, ported not rewritten, only after the core is green (D6) |
| Tables repeated across six docs; counts typed by hand | No single home for each fact | Five docs, one job each; requirement ids on tests; a meta-test keeps the matrix honest (D7, D8) |
| Random 500s in about one test run in ten | Test harness cleared every SQLite pool in the process when any factory was disposed | Harness rules (§6.2) and a 20-run flake gate |
| Claims without evidence: throughput, "hard delete means gone", mutation checks | Evidence was not a gate | Evidence levels and gates (§6) |

What to keep: the pure reducer, permutation tests, guidelines used as an audit, deliberate-defect checks, measuring instead
of assuming, ranked honest limits.

### 2.3 Requirements added after the brief (all from the user)

| Asked for | Where it lands |
|---|---|
| Payload checksum "e.g. MD5", stored with the object and returned to the publisher | IN-07. SHA-256 default, MD5 by setting, self-describing |
| Where payloads are stored; dependency injection of storage and database | §4.3 (inline column), §4.9 (seams), DESIGN section "Payload storage" |
| A written set of design guidelines, used as an audit | `docs/GUIDELINES.md`, ported (§8.2) |
| "What if someone uploads a 10 GB video?" | IN-06, ST-07: refuse by declared length, media by reference |
| A test console, an endpoints tab, a one-click Visual Studio launch, a fenced test-only reset | DV-01 to DV-03, Phase 8 |
| A line-by-line requirements matrix, a development story, generated diagrams | `docs/REQUIREMENTS.md` (this matrix, kept true), DV-04 |
| S3 comparison, framed as "for a CMS demo we are not going to pursue an S3 alternative" | DV-04 (copy the doc) |
| "They are really looking for a robust async solution" | D1, AS-01 to AS-11 |

### 2.4 Facts established while preparing this brief

These were measured or computed against commit `2238384`, not recalled. Re-measure on the new build; the numbers are the
bar it must not fall below by more than 20% (§6.5).

1. **The order-free oracle matches the reducer.** An independent model that sees only the *set* of events (tombstone is
   the newest delete; live events are those strictly after it; the winner is the maximum of version, timestamp,
   unPublish-beats-publish, then payload text; code in Appendix A) agreed with the first build's reducer on 200,000 seeded
   random timelines, each shuffled 3 times (600,000 folds): **0 mismatches**, with duplicates, ties, contradicting
   content and deletes in the mix.
2. **The order-independence guarantee holds only under a precise assumption.** If a higher version can carry an
   *earlier* timestamp than a lower one, arrival order matters: 3,376 of 600,000 shuffles (0.56%) of timelines built that
   way disagreed with the oracle. Smallest case: `publish v2 @t5`, `publish v3 @t1`, `delete @t3`. Order
   (v2, v3, delete) leaves **no entity**; order (delete, v2, v3) leaves **entity v2**. The first build's docs described
   the assumption loosely ("versions and timestamps rise together; ids not reused"). State it precisely, pin this case
   with a test, and list it as a limitation (K8).
3. **"Hard delete" is logical, not physical, by default.** With SQLite's defaults the payload bytes stay in the database
   file after a `DELETE`, and after `UPDATE ... SET Payload = NULL`, for small and large (overflow-page) values alike, even
   after `PRAGMA wal_checkpoint(TRUNCATE)`. With `PRAGMA secure_delete=ON` on the connection that does the delete, none
   remained, and the WAL held none. `secure_delete` is per connection, so it needs a connection interceptor (ST-06). This was measured on a plain table
   through `Microsoft.Data.Sqlite`, the provider EF Core uses, and the first build never sets it; ST-06 repeats the test
   against the real schema.
4. **Throughput baseline** (Windows dev machine, Release build, localhost, SQLite file, ~1 KB payloads; the worker is on):

   | Load | Accepted | Request latency | Applied end to end |
   |---|---|---|---|
   | 1 event per request, 1 sender (editor-style webhook), 300 requests | 98 events/s (sender-bound) | p50 10 ms, p95 12 ms, p99 26 ms | 97 events/s (sender-bound, no backlog) |
   | 100 events per request, 1 sender, 3,000 events | 1,046 events/s | p50 78 ms, p95 152 ms, p99 222 ms | 947 events/s |
   | 500 events per request (the maximum), 1 sender, 4,000 events | 1,036 events/s | p50 409 ms, p95 738 ms | 918 events/s |
   | 100 events per request, 8 senders, 20,000 events, per-event logging at Information | 1,991 events/s | p50 406 ms, p95 505 ms | 1,224 events/s; queue empty 16.3 s after the first request |
   | the same with logging at Warning | 2,536 events/s | p50 322 ms, p95 398 ms | 1,425 events/s; queue empty 14.0 s after the first request |
   | Reading: 200 pages of 100 entities as a user | | p50 1.8 ms, p95 3.0 ms per page; 0.5 s for 20,000 entities | |

   Reading the table: accepting scales with CPU (parse, canonicalise, hash) until senders queue on the single write gate;
   applying is capped by SQLite's single writer at roughly 1,000 to 1,400 events/s here; per-event Information logging
   costs roughly 15 to 20% of throughput and wrote 40,605 lines for 20,000 events. One machine, one run each: evidence of order of magnitude,
   not a guarantee.

## 3. Decisions

### 3.1 Settled with the user (carry over unchanged)

| # | Ambiguity | Decision |
|---|---|---|
| S1 | Prose says Add / Update / Delete; the schema says `publish`, `unPublish`, `delete` | The schema is the wire format. Add and update are both `publish` (version 1 is an add). Types match case-insensitively |
| S2 | "Delete → removed or unpublished" | Requirement 2 separates them: `delete` is a hard delete; `unPublish` keeps the data and hides it from users |
| S3 | Events "include, but are not limited to" three types | An unknown type is reported per event as `ignored_unsupported_type`; the batch is not failed |
| S4 | The sample JSON is invalid | Strict parsing; invalid JSON gets `400`. Real traffic is assumed to be valid JSON |
| S5 | "New data is only available upon publishing"; unPublish "contains the entity fields" | A publish is a full snapshot: the payload is replaced, never merged. An unPublish carries the whole entity, so it can *create* an entity (the X → X+1 → unpublish corner case) |
| S6 | Versions: X modified to X+1 | Unpublished drafts are never sent, so published versions have gaps. Contiguity is not required |
| S7 | "Assume all data is restricted" | Deny by default; the CMS and the readers are different accounts and roles; a hidden entity returns the same `404` as a missing one |
| S8 | Admin disable "does not affect CMS data" | A separate `IsDisabledByAdmin` column that CMS events never write. A later publish does not clear it. A delete followed by a re-create does |
| S9 | Admin listing "including the ones that were disabled" | Same endpoints; the caller's role picks the filter on the server. Admins also see unpublished entities and get a `status` block |
| S10 | Two events that cannot be ordered | `unPublish` beats `publish` at the same version and instant (fail closed). Same version, instant and kind but different content: the greater payload text wins, whichever arrived first, reported as `same_version_different_content` |
| S11 | Hard delete vs late events | The entity row and payload are removed. A payload-free tombstone (id, newest delete time) remains so a late older event cannot bring it back; a newer publish legitimately recreates it |
| S12 | One bad event in a batch | The request still succeeds (`2xx`) with a per-event outcome and reason, because `4xx` makes a webhook sender retry the whole batch forever. A batch of only rejected events is also `200`. Only an unreadable request (not JSON, not an array, too big) is refused |
| S13 | Ids and timestamps | Ids are 1 to 200 characters, trimmed, no control characters, no `/` or `\` (they must fit one URL path segment; refused loudly). Timestamps are ISO-8601 *with* an explicit offset, normalised to UTC, not more than an hour in the future |
| S14 | "Sanitised" | Normalise, never alter content: trim ids, UTC timestamps, compact canonical JSON, reject control characters in ids. Payload values are not HTML-escaped or stripped; escaping belongs to whoever renders them |
| S15 | Checksums | Every payload, computed once at ingestion over the canonical text; `sha256:<hex>` by default, `md5:<hex>` by setting; stored, returned to the publisher, served by the API, recorded in the ledger and logs. Integrity, not authenticity |
| S16 | Large content | Out of scope. The webhook refuses bodies over the limit by *declared* length before reading; media is referenced (address, type, size, checksum), never carried |
| S17 | Ordering assumption | One CMS. For one id, a higher version never carries an earlier timestamp, and ids are not reused with restarting versions. Outside it, arrival order can matter (§2.4 item 2, K8) |
| S18 | Sync or async | Durable asynchronous, because acknowledging before storing loses events and a slow synchronous answer is treated as a failure by webhook senders. The brief leans this way |

### 3.2 Changes proposed for the rebuild (these are defaults; the user may veto any)

| # | Change | Why | Effect on what a reviewer or the CMS sees |
|---|---|---|---|
| D1 | **Asynchronous only.** No `Ingestion:Mode`, no second ingestor | Removes a whole implementation, a switch, a differential test and a duplicated doc section. Rule-level tests use a helper that posts, drives the processor, then reads batch status | `202` + `Location` when anything is queued, `200` with final outcomes when nothing is. Same bodies as before |
| D2 | **One `Events` table is queue and ledger**, plus a `Batches` table. A payload exists only while an event is queued; a `CHECK` makes any other state impossible | No rows copied between tables; the status query reads one table; "no event silently dropped" is true by construction; "payload gone once decided" is enforced by the schema | None. Status and admin endpoints keep their shapes |
| D3 | **Immutable reducer** returning a new state and an outcome. The persistence layer compares before and after and issues insert, update or delete | No `_spare` workaround for EF tracking; the rules stay testable with no EF types | None |
| D4 | **One initial migration** holding the final schema | History of three follow-up migrations was noise | None |
| D5 | **`secure_delete=ON` on every connection**, with a test that scans the file (§2.4 item 3) | Makes "hard delete" physically true for the live file | None |
| D6 | **Developer tooling last (Phase 8):** console, reset endpoint, VS launch profile, S3 doc, generated diagrams. Copied from `2238384`, updated for D1 and D2 | Keeps the core small and reviewable; these were requested, so they stay in scope | The reset and console exist again at the end |
| D7 | **Five docs, one job each:** README (run it), DESIGN (why, contract, limits), GUIDELINES (rules), REQUIREMENTS (this matrix, with test names), EVIDENCE (results: counts, mutation log, measurements). A fact lives in one place and is linked elsewhere | The first build repeated tables in two docs and drifted | Less to read |
| D8 | **Requirement ids on tests** (`[Trait("req","EV-05")]`) and a meta-test that fails when a MUST row has no test | Keeps the matrix true without hand-counting | None |
| D9 | **No inner save-retry loop** (`Ingestion:MaxSaveAttempts` goes). A concurrency conflict fails the attempt; the batch-level back-off retries it | One retry mechanism instead of two | None |
| D10 | **State the ordering assumption exactly (S17), pin its counterexample with a test, and (could) log `timestamp_regression`** when an applied higher version has an earlier timestamp | Turns a hidden limit into an observable one | One new optional reason code |
| D11 | **Work on a new branch** (`rebuild`) from `2238384`; the old tree stays reachable and is mounted as the read-only worktree | Safe to rewrite; easy to diff | None |

Questions to settle with the user in the first message, with the default if they say nothing: keep a synchronous option
(default **no**, D1); build the console, reset and VS profile (default **yes, in Phase 8**); keep the S3 document and the
generated diagrams (default **yes, copied in Phase 8**); keep the .NET-encoder canonical form (default **yes**, Appendix B).

### 3.3 Cross-cutting checklist: answer all of it before designing

The first build learned these late. Each has its answer here; if the user changes one, revisit the design first.

| Concern | Answer |
|---|---|
| Integrity | Checksum over the canonical payload, self-describing, SHA-256 default (S15) |
| Payload storage | Inline `TEXT`, at most 256 KiB, in the same transaction as state. Media by reference. Move to a content-addressed object store above about 1 MB (K11) |
| Injection and configuration | `TimeProvider`, `IPayloadChecksum`, validated options, no eager configuration reads; EF Core is not wrapped (§4.9) |
| Large content | Declared-length refusal; no upload or download endpoint (S16) |
| Retention and deletion | Physical wipe of the live file (D5); no retention job; erasure beyond the live file is out of scope (K6) |
| Acknowledgement | Only after a durable commit (AS-01); `synchronous` stays FULL |
| Credential rotation | Several active CMS passwords (IN-02) |
| Resync and replay | None; the ledger shows gaps (K7) |
| Observability | One log line per event, batch id as correlation id, a meter, gauges for queue health (OB-01 to OB-06) |
| Ordering | Reducer proven order-free under S17; known failure pinned (D10) |
| Capacity | Measured baseline (§2.4 item 4), a load smoke in the repo, a regression bar |
| Evidence | §6 |

## 4. Target design

### 4.1 Shape

```
 CMS ──POST /cms/events (Basic, CmsIngest)──► ACCEPT  auth · limits · per event: validate, canonicalise, checksum (no DB)
                                                │      ONE transaction under the write gate:
                                                │        Batches row + Events rows (queued, with payload; rejected and
                                                │        unsupported ones already decided, without payload)
                                                ▼
                                       202 + Location  (200 with final outcomes if nothing was left to queue)
                                                │ wake
                                  WORKER ► PROCESSOR  next due batch (takedowns first, then oldest)
                                                │      2 reads (entities, tombstones) · pure reducer · ONE transaction:
                                                │        entity/tombstone changes + event outcomes + payload nulled + batch completed
                                                ▼
                 Entities · Tombstones · Events (outcome, reason, checksum, times) · Batches (state, attempts, back-off)

 CMS/admin ──GET /cms/batches/{id}──────────────► queued | completed | failed, per-event outcomes, never payloads
 user/admin ─GET /api/entities[/{id}]───────────► read-only context · projection · keyset paging · role decides visibility
 admin ──────POST /api/entities/{id}/disable|enable ─► one UPDATE of the override columns
 admin ──────GET /api/admin/ingestion · POST …/{batchId}/requeue ─► queue depth, oldest age, failed batches, put one back
```

One web project with feature folders. The rules in `Domain/` reference nothing but the BCL.

### 4.2 The pipeline in detail

**Accept** (on the request):

1. Authenticate; require role `CmsIngest`.
2. If the declared `Content-Length` exceeds `Ingestion:MaxBodyBytes`, answer `413 payload_too_large` before reading any
   body. Otherwise read with a hard cap (a chunked body has no declared length).
3. Parse strictly (depth at most 32). Not JSON: `400 invalid_json`. Not an array: `400 invalid_batch`. More than
   `MaxBatchSize` events: `413 batch_too_large`.
4. For each element: validate, normalise, canonicalise, checksum (§4.5, §4.7). No database access.
5. If no element is valid: one transaction writes the decided events (rejected, unsupported) into a completed batch, and
   the answer is `200` with the outcomes. An empty array is the same.
6. Otherwise, under the write gate, in **one transaction**: refuse with `429 inbox_full` + `Retry-After: 10` and store
   nothing if the bytes of work still queued plus this batch would pass `Inbox:MaxBytes`; else insert the `Batches` row
   (`HasTakedown` = any `unPublish` or `delete`) and one `Events` row per element. Commit, wake the worker, answer
   `202` + `Location: /cms/batches/{batchId}` with each event's outcome so far (`queued`, `rejected`,
   `ignored_unsupported_type`) and its checksum.
7. A storage failure answers `503` + `Retry-After` with nothing stored. Every response carries `X-Correlation-Id` = batch id.

**Apply** (the worker, one batch per turn):

1. Wait for the wake signal, or `Inbox:PollInterval` when idle.
2. Under the write gate, pick the next due batch: state `queued` and `NextAttemptAt` not later than now, ordered by
   `HasTakedown` descending, then `Id`. Reordering is safe only because the reducer is order-free (EV-09, EV-12).
3. Load its queued events in sequence order. Load the `Entities` and `Tombstones` rows for the distinct ids: two queries
   however many events there are.
4. Fold the events per id with the reducer. Events for different ids are independent.
5. **One transaction:** write the entity and tombstone changes; set each event's outcome, reason and `ProcessedAt` and
   null its payload; mark the batch completed. Commit.
6. On any failure, shutdown included: roll back (the batch is exactly as it was), then record the attempt in a separate
   small statement: `Attempts + 1`, `NextAttemptAt = now + min(RetryMaxDelay, RetryBaseDelay × 2^(attempt−1))`,
   `LastError` = exception type and SQLite code, never a message. At `MaxAttempts` the batch becomes `failed` (set aside)
   and the queue moves on.
7. The loop catches every exception except cancellation, logs it, waits `FaultDelay`, and carries on. An unhandled
   exception in a hosted service would stop the whole host.

**Requeue** (admin): a `failed` batch goes back to `queued` with `Attempts = 0`; unknown id `404`; not failed `409`.

**Read:** as AP-01 to AP-08. Nothing on the read side can write.

### 4.3 Data model (SQLite via EF Core, WAL, one migration)

| Table | Columns | Notes |
|---|---|---|
| `Entities` | `Id` (PK, ≤200), `Version`, `Payload` (canonical JSON text), `PayloadChecksum`, `IsPublished`, `IsDisabledByAdmin`, `DisabledBy`, `DisabledAt`, `LastEventTimestamp`, `CreatedAt`, `UpdatedAt`, `ConcurrencyStamp` | Partial index on `Id` where `IsPublished` and not `IsDisabledByAdmin`: serves the user listing as an ordered range read. A composite index on the two flags plus `Id` is **never used** by EF's boolean filter on SQLite (found in v1 by reading the query plan); test the plan |
| `Tombstones` | `Id` (PK), `DeletedAt` (newest delete time seen, a high-water mark), `RecordedAt` | No entity data, so a hard delete leaves no content |
| `Batches` | `Id` (long, FIFO key), `BatchId` (guid, unique), `ReceivedAt`, `EventCount`, `PayloadBytes`, `HasTakedown`, `Status` (`queued`, `completed`, `failed`), `Attempts`, `NextAttemptAt`, `LastError` (≤200), `CompletedAt` | Index on (`Status`, `HasTakedown` descending, `Id`) is exactly the pick order. Bytes in use = sum of `PayloadBytes` where `Status <> completed` |
| `Events` | `Id`, `BatchId`, `Sequence`, `EntityId`, `RawType` (masked, ≤64), `Type`, `Version`, `Timestamp`, `Payload` (nullable), `PayloadChecksum`, `Outcome`, `Reason`, `ReceivedAt`, `ProcessedAt` (null while queued) | Unique (`BatchId`, `Sequence`). **`CHECK (Outcome = 'queued' OR Payload IS NULL)`**: a decided event can never hold a payload. Index on `EntityId` for "what happened to this entity" |

Only the latest version is stored; `Events` holds the history of *events*, never payloads once decided. All timestamps are
UTC (a value converter restores `DateTimeKind.Utc` on read; SQLite returns `Unspecified`).

### 4.4 Reconciliation rules (the part that must be right)

An entity's events are ordered by `(version, timestamp)`; an event changes state only if it sorts after what is stored.
Deletes carry no version and compare by timestamp. The reducer takes the current entity (or none), the current tombstone
(or none) and one event, and returns the new entity, the new tombstone, an outcome and a reason.

| Event | Situation | Result |
|---|---|---|
| publish | nothing stored | create, published |
| publish | version > stored | replace payload and version, published |
| publish | same version, newer timestamp | state changes (e.g. republish after unpublish) |
| publish | same version, same timestamp, already published, same content | `ignored_duplicate` |
| publish | version < stored, or same version and older timestamp | `ignored_stale` (`older_version` or `older_timestamp`) |
| unPublish | nothing stored (X → X+1, never published) | **create from the event's payload**, unpublished |
| unPublish | version ≥ stored and newer | overwrite payload and version, unpublished |
| unPublish | older than stored | `ignored_stale` |
| publish vs unPublish | same version **and** same timestamp | **unPublish wins** in either arrival order (`tie_unpublish_wins` for the loser) |
| same kind | same version, timestamp and kind, different content | the **greater payload text** (ordinal) wins; `same_version_different_content` (`applied` for the winner, `ignored_stale` for the loser) |
| delete | entity exists, delete timestamp ≥ its last event | hard delete; tombstone recorded or raised |
| delete | nothing stored | tombstone recorded (`tombstone_only`) |
| delete | older than the stored entity's last event | `ignored_stale` (`newer_state_exists`), but the tombstone high-water mark is still raised |
| delete | at or below the existing tombstone | `ignored_duplicate` (equal) or `ignored_stale` (older) |
| publish / unPublish | timestamp ≤ tombstone | `ignored_stale` (`superseded_by_delete`) |

Properties, each with a test: **idempotent**; **order-free** under S17 (§2.4 items 1 and 2); **deterministic ties**
(never arrival order); **pure** (no I/O, no clock, no randomness; the concurrency token is renewed by the write context at
save time); **no resurrection**; **the admin override is never touched** by CMS events, and a re-create after a delete
starts with it cleared.

### 4.5 Ingestion contract

| Field | Rule | Failure reason |
|---|---|---|
| `type` | required string; `publish`, `unPublish`, `delete`, case-insensitive | `missing_type`; other strings give `ignored_unsupported_type` (reason `unsupported_type`) |
| `id` | required string, trimmed, 1 to 200 characters, no control characters, no `/` or `\` | `missing_id`, `invalid_id` |
| `timestamp` | required; matches `^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z\|[+-]\d{2}:\d{2})$`; converted to UTC; not later than now + `MaxFutureSkew` | `missing_timestamp`, `invalid_timestamp`, `timestamp_in_future` |
| `version` | publish and unPublish: required integer ≥ 1. Ignored on delete | `missing_version`, `invalid_version` |
| `payload` | publish and unPublish: required JSON object, at most `MaxPayloadBytes` once canonicalised. Ignored on delete | `missing_payload`, `invalid_payload`, `payload_too_large` |
| element | must be a JSON object; text that cannot be decoded (e.g. a lone surrogate escape) | `event_not_object`, `malformed_event` |

Outcomes: `applied`, `ignored_stale`, `ignored_duplicate`, `ignored_unsupported_type`, `rejected`, and `queued` (accepted,
not decided yet: appears in a `202` and in the status of a waiting batch). Reasons are stable machine-readable constants,
never reworded. Reducer reasons: `older_version`, `older_timestamp`, `tie_unpublish_wins`, `superseded_by_delete`,
`newer_state_exists`, `tombstone_only`, `same_version_different_content`. Whole-request error codes (problem+json
`code`): `invalid_json`, `invalid_batch`, `payload_too_large`, `batch_too_large`, `inbox_full`, `storage_unavailable`,
`invalid_cursor`, `batch_not_found`, `batch_not_failed`. Errors never echo payloads.

| Situation | Status | Sender should |
|---|---|---|
| Stored; events queued | `202` + `Location: /cms/batches/{batchId}` | not retry; follow the `Location` |
| Nothing left to queue (all rejected, or empty) | `200` with final outcomes | not retry |
| Not JSON, not an array | `400` | fix the sender |
| Body or batch too large | `413` | fix the sender |
| Wrong or missing credentials; wrong account | `401` (+ `WWW-Authenticate: Basic`); `403` | fix credentials |
| Too many failed sign-ins from this address | `429` + `Retry-After` | wait |
| Queue full (`inbox_full`); nothing stored | `429` + `Retry-After: 10` | retry the same batch |
| Storage failure; nothing stored | `503` + `Retry-After` | retry the same batch |

Response body (ingest and `GET /cms/batches/{id}` share it): `batchId`, `status` (`queued`, `completed`, `failed`),
`received`, `queued`, `applied`, `ignored`, `rejected`, `results[]` of `{index, id, type, version, outcome, reason?,
checksum?}`; plus `receivedAt`, `completedAt`, and for a failed batch `attempts` and `lastError`. `checksum` is present for
every publish or unPublish that passed validation, whatever its outcome (for `ignored_stale` it identifies what that event
carried, not what is stored); absent for deletes, rejected events and unsupported types. Outcomes serialise as
snake_case words. Keep these shapes identical to the first build so its console and black-box tests port unchanged.

### 4.6 API and authorisation

| Route | Anonymous | CMS | User | Admin |
|---|---|---|---|---|
| `GET /health` (status and database check only) | 200 | 200 | 200 | 200 |
| `POST /cms/events` | 401 | 200 / 202 | 403 | 403 |
| `GET /cms/batches/{batchId}` | 401 | 200 / 404 | 403 | 200 / 404 |
| `GET /api/entities?limit=&cursor=` (default 50, max 100) and `GET /api/entities/{id}` | 401 | 403 | 200 | 200 |
| `POST /api/entities/{id}/disable` and `/enable` (idempotent) | 401 | 403 | 403 | 200 / 404 |
| `GET /api/admin/ingestion` | 401 | 403 | 403 | 200 |
| `POST /api/admin/ingestion/{batchId}/requeue` | 401 | 403 | 403 | 200 / 404 / 409 |
| `POST /api/admin/reset` (Phase 8 only; absent unless `Admin:AllowReset`) | 401 | 403 | 403 | 200 / 400 |

Entity body: `id`, `version`, `checksum`, `updatedAt`, `payload` (the stored text written raw, not re-serialised), and for
admins `status`: `isPublished`, `isDisabledByAdmin`, `disabledBy`, `disabledAt`. A page is `{items, nextCursor}`.
Accounts: one CMS account (role `CmsIngest`; username 10 to 20 characters; one or more GUID passwords), users with role
`User` or `Admin` (password at least 12 characters). Development usernames: `cms-ingest-svc`, `user@lateral.example`,
`admin@lateral.example`; their passwords are random GUIDs in `appsettings.Development.json` and are never repeated in chat
or docs.

### 4.7 Canonical form and checksum

The checksum covers the UTF-8 bytes of the payload re-serialised by `Utf8JsonWriter` from the parsed element (compact,
member order and duplicates kept, number text as sent, strings escaped by the .NET default encoder). That text is exactly
what is stored and what the API serves, so a consumer can verify from the served text without re-serialising. The full rule
table is Appendix B. Pin it with a golden test; if it must ever change, introduce a new checksum prefix instead of
altering the old one. `{"title":"Hello"}` gives
`sha256:5c924960820d681cb4ac7c1dd76c1b56b963dbbf718faba003691dd26cce3e38`.

### 4.8 Configuration

| Key | Default | Notes |
|---|---|---|
| `Database:ConnectionString` | `Data Source=App_Data/cms.db` | relative to the project folder |
| `Database:ReadConnectionString` | the writer's file, opened read-only | could point at a replica |
| `Database:ApplyMigrationsOnStartup` | `true` | |
| `Auth:Cms:Username`, `Auth:Cms:Passwords`, `Auth:Users` | required, not in `appsettings.json` | startup fails without them, naming the setting |
| `Auth:MaxFailuresPerMinute` | `10` | `0` disables |
| `Ingestion:MaxBodyBytes` / `MaxBatchSize` / `MaxPayloadBytes` / `MaxJsonDepth` / `MaxFutureSkew` | 5242880 / 500 / 262144 / 32 / `01:00:00` | |
| `Ingestion:ChecksumAlgorithm` | `sha256` | `sha256` or `md5`; checked at startup (MD5 may be forbidden by FIPS) |
| `Inbox:WorkerEnabled` | `true` | tests turn it off and call the processor |
| `Inbox:PollInterval` / `MaxAttempts` / `RetryBaseDelay` / `RetryMaxDelay` / `FaultDelay` | `00:00:01` / 5 / `00:00:02` / `00:01:00` / `00:00:05` | |
| `Inbox:MaxBytes` | `268435456` | must be at least `Ingestion:MaxBodyBytes` |
| `Api:DefaultPageSize` / `MaxPageSize` | 50 / 100 | |
| `Admin:AllowReset` | `false` | Phase 8; refused at startup in Production |

Removed from the first build: `Ingestion:Mode`, `Ingestion:MaxSaveAttempts`.

### 4.9 Module map and size budget

| Folder | Contents | Budget (lines) |
|---|---|---|
| `Domain/` | event, entity state, tombstone, outcomes and reason constants, the reducer | 350 |
| `Ingestion/` | `EventParser`, canonical writer and `IPayloadChecksum`, accept service, batch processor, worker, endpoints (ingest, status, admin queue), options, log, metrics | 1,000 |
| `Entities/` | queries, endpoints, admin override | 300 |
| `Auth/` | Basic handler, credential store, options and validator, throttle, policies | 450 |
| `Persistence/` | write and read contexts, model and converters, write gate, connection setup (WAL, `secure_delete`), the one migration | 350 |
| `Program.cs` | composition only | 70 |

About 2,500 lines of production code against 3,374 in the first build; tests at most 4,000 lines. A budget guards against
sprawl; it is not a target to game.

Injected and configured through validated options, never read eagerly: `TimeProvider`, `IPayloadChecksum`, all options.
`EntityQueries`, `EntityAdminService`, the processor and the accept service are scoped; the write gate, credential store,
throttle, metrics and wake signal are singletons. **EF Core is not wrapped in a repository interface** and tests use real
SQLite. Add an interface only where a second implementation is plausible; the one real candidate is a future
`IPayloadStore` inside `Persistence/` (K11).

### 4.10 Pitfalls the first build already paid for

1. EF Core cannot track a delete and an insert of the same key in one `SaveChanges`. With an immutable reducer the
   persistence layer compares the entity before and after the batch: none to some is an insert, some to none a delete,
   some to some an update.
2. EF stores `Guid` as upper-case `TEXT` in SQLite. Raw-SQL assertions need `COLLATE NOCASE` or an upper-cased value.
3. `Microsoft.Data.Sqlite` pools connections per connection string. Disposing a test factory must clear **only its own**
   pools (`SqliteConnection.ClearPool`). `ClearAllPools()` is process-wide and caused random 500s in other running tests.
4. SQLite returns `DateTime` with `Kind = Unspecified`; apply a value converter for every `DateTime`.
5. `PRAGMA secure_delete` is per connection; `journal_mode=WAL` persists once set. Set the former in a connection
   interceptor for both contexts. Leave `synchronous` at its default (FULL) so a `202` means durable.
6. Open the read-only connection (`Mode=ReadOnly`) only after the database exists and is migrated.
7. A read-then-write transaction on SQLite can fail with `SQLITE_BUSY` if another writer commits between. Serialise
   writers with one in-process gate; this is also why the design is single-instance (K1).
8. An unhandled exception in a `BackgroundService` stops the host. Catch everything in the loop.
9. Kestrel enforces its body limit, but judge the *declared* length first and answer `413` before reading. A client that
   streams while the server answers may see a connection reset instead of the `413` (observed with .NET's `HttpClient`
   after 0.5 to 1.6 MiB). Document it; do not pretend it away.
10. `JsonDocument` keeps duplicate member names; the canonical form keeps them too. A lone surrogate escape throws on
    `GetString()`: map it to `malformed_event`. Use `JsonDocumentOptions { MaxDepth = 32 }`.
11. Basic auth: split at the **first** colon (passwords may contain colons; usernames may not, so validate that at startup).
    Scheme matching is case-insensitive; bad base64 is a `401`, not a `500`.
12. Tests: host with environment `Testing` so the development credentials file is not loaded; read options lazily so tests
    can override them after the host is built; `WebApplicationFactory<Program>` needs `public partial class Program`.
13. Windows may hold a pooled SQLite file open; clear the pool, then delete the temp files best-effort.
14. xUnit runs classes in parallel. Give every class its own database and share no mutable static state.

## 5. Requirements matrix

**Source:** B1 to B8 = the numbered sections of the original brief (§1: 1 ingestion, 2 application, 3 storage, 4 REST API,
5 performance, 6 observability, 7 testing, 8 deliverable) · Sc = the scenario text · Add = asked for later (§2.3) ·
Note = the brief's closing "Notes to consider" · Eng = an engineering rule (GUIDELINES) · Der = a logical consequence.
**Pri:** MUST = the brief or correctness; SHOULD = cheap robustness the build is judged better for; COULD = nice to have.
**Evidence** codes are defined in §6.1. `M` means the requirement must also be mutation-checked (§6.4).
**Compromise** points at §7. Statuses live in `docs/REQUIREMENTS.md`, not here.

### Event semantics (the most important group)

| ID | Requirement (acceptance criterion) | Src | Pri | Evidence | Comp. |
|---|---|---|---|---|---|
| EV-01 | A publish for an unknown id creates it: published, the event's version, payload stored whole and never merged | Sc | MUST | U I | |
| EV-02 | A publish with a higher version replaces version, payload and checksum. A lower version is `ignored_stale` (`older_version`). Equal version and newer timestamp applies (state can flip back to published). Equal version and older timestamp is `ignored_stale` (`older_timestamp`) | Sc | MUST | U I M | |
| EV-03 | An exact duplicate (version, instant, kind, content) is `ignored_duplicate`; replaying a whole stream changes nothing | Sc, Eng | MUST | U P I | |
| EV-04 | An unPublish keeps the row and payload and marks it unpublished; users stop seeing it, admins still do | B2 | MUST | U I | |
| EV-05 | An unPublish for an id never seen (X, then X+1 unpublished with no publish between) creates the entity from the event's payload, unpublished | Sc | MUST | U I M | |
| EV-06 | A delete removes the entity row and payload and leaves a payload-free tombstone (id, newest delete time). A raw-SQL check finds no `Entities` row and no content in the tombstone | B2 | MUST | U I M | |
| EV-07 | Events at or before the tombstone are `ignored_stale` (`superseded_by_delete`); a newer publish recreates the entity with the admin override cleared | Der | MUST | U P I M | |
| EV-08 | Delete edge cases: nothing stored gives `tombstone_only`; older than the stored state gives `newer_state_exists` but still raises the tombstone; at or below the tombstone is duplicate or stale | Der | MUST | U P M | |
| EV-09 | Order independence: every arrival order of a timeline ends in the same state. Evidence: the set-based oracle (Appendix A) on at least 20,000 seeded random timelines per run, and every permutation of at least 10 curated timelines of up to 7 events | Note | MUST | P M | K8 |
| EV-10 | Deterministic ties: unPublish beats publish at equal version and instant; contradicting content at equal version, instant and kind resolves to the greater payload text with `same_version_different_content` | Der | MUST | U P M | |
| EV-11 | Version is an integer ≥ 1; gaps are allowed; the first version is 1 | B1 | MUST | U | |
| EV-12 | Batch-partition independence: splitting a stream into batches any way, and applying the batches in any order, gives the same final state | Der | MUST | P I | |
| EV-13 | An unknown event type is reported per event (`ignored_unsupported_type`), gets an `Events` row, and never fails the batch | Sc | MUST | I | |
| EV-14 | The entity keeps the latest version; a lower or equal version never overwrites; a stale concurrent write is refused (concurrency token) and the attempt fails and retries | B3 | MUST | U I F | |
| EV-15 | The admin override is its own column. CMS events never write it. A later publish does not clear it; only delete then re-create does | B4 | MUST | U I | |
| EV-16 | The ordering assumption (S17) is stated exactly in DESIGN, and the counterexample in §2.4 item 2 is pinned by a test that asserts the divergence. (Could) an applied higher version with an earlier timestamp is logged with reason `timestamp_regression` | Eng | MUST / COULD | P D | K8 |

### Ingestion

| ID | Requirement (acceptance criterion) | Src | Pri | Evidence | Comp. |
|---|---|---|---|---|---|
| IN-01 | `POST /cms/events` takes a JSON array; only the CMS account may call it | B1 | MUST | I A | |
| IN-02 | Basic auth. The CMS username is 10 to 20 characters and each CMS password parses as a GUID, both checked at startup (the service refuses to start otherwise). Several passwords may be active so one can be rotated without downtime | B1 | MUST | I | K4 |
| IN-03 | Per-event validation gives the stable reason codes in §4.5, with a boundary-pass and a boundary-fail input for every rule (id 200 and 201 characters, version 0 and 1, 7 and 8 fractional digits, the skew limit) | B2 | MUST | U V | |
| IN-04 | One bad event never blocks its neighbours. The response lists every event's outcome, reason and checksum. A batch holding only rejected events is `200` | B1, Eng | MUST | I | |
| IN-05 | Sanitising is normalising: trim ids, UTC timestamps, compact canonical payload, control characters rejected in ids, untrusted type text masked and bounded. Values are never altered or HTML-escaped | B2 | MUST | U V | |
| IN-06 | Limits: a declared `Content-Length` over the cap is `413` *before any body is read* (a stalled declared 10 GiB body is refused without waiting); batch of 501 is `413`; payload over 256 KiB rejects that event; depth over 32 rejects the request; invalid JSON or a non-array is `400` | Eng, Add | MUST | I M | |
| IN-07 | Every validated publish and unPublish gets a checksum over its canonical text. It is returned in the response, stored with the entity, served by the API and recorded in the ledger. A test recomputes every stored row after a mixed workload and finds no mismatch | Add | MUST | U V I M | K10 |
| IN-08 | Status codes and `Retry-After` exactly as the §4.5 table; errors are problem+json with a stable `code` and never echo payloads | Eng | MUST | I V | |

### Asynchronous processing

| ID | Requirement (acceptance criterion) | Src | Pri | Evidence | Comp. |
|---|---|---|---|---|---|
| AS-01 | The answer follows a durable commit. A database failure at accept gives `503` and stores nothing; a `202` is never sent first | B5, Eng | MUST | F I M | K1 |
| AS-02 | Accepting is one transaction; applying is one transaction (state, outcomes, payload purge, batch completion). A failure, crash or shutdown leaves the batch exactly as it was; nothing is half applied | Eng | MUST | F M | |
| AS-03 | A background worker applies batches, takedown batches first then oldest; it wakes on accept and polls when idle | B5 | MUST | I M | K3 |
| AS-04 | A failing batch is retried with doubling back-off (2 s up to 60 s), set aside as `failed` after 5 attempts without blocking later batches, and can be requeued by an admin. The worker survives any exception and a storage outage | Eng | MUST | F I M | |
| AS-05 | The queue is bounded by `Inbox:MaxBytes`: past it, `429 inbox_full` + `Retry-After` and nothing stored; concurrent senders cannot get past the cap | Eng | MUST | C I M | |
| AS-06 | `GET /cms/batches/{id}` (CMS and admin) returns `queued`, `completed` or `failed` with per-event outcomes; never payloads; unknown id is `404` | Eng | MUST | I A L | |
| AS-07 | `GET /api/admin/ingestion` reports pending batches and events, oldest age, bytes used and failed batches; requeue follows §4.2 | Eng | SHOULD | I A | |
| AS-08 | Batches accepted before a restart are applied after it, once | Eng | MUST | F | |
| AS-09 | A queued payload exists only in its `Events` row and is nulled in the apply transaction; the `CHECK` makes any other state impossible; a delete leaves no copy of an earlier publish in the queue or the ledger | B2, Eng | MUST | I V M | K6 |
| AS-10 | Accepting reads no entity state; applying reads it in two queries however large the batch; the pick query uses its index with no sort | B5 | MUST | Q | |
| AS-11 | DESIGN states the choice and why: alternatives (synchronous, in-memory async, durable async), trade-offs, who calls and what each expects, the measured throughput, and what the queue does *not* do (it does not raise the single-writer ceiling) | B5 | MUST | D | K1 K2 |

### Storage

| ID | Requirement (acceptance criterion) | Src | Pri | Evidence | Comp. |
|---|---|---|---|---|---|
| ST-01 | EF Core on SQLite; one migration creates the final schema; applied at startup unless switched off | B3 | MUST | I | K15 |
| ST-02 | Separate write and read contexts. The read context is no-tracking, opened read-only (`Mode=ReadOnly`) and throws on `SaveChanges`. The write context is used by ingestion and the admin override only | B5 | MUST | I M | |
| ST-03 | WAL mode; writers serialised by one in-process gate; `synchronous` left at FULL; concurrency token renewed on every save | Eng | MUST | I C M | K1 |
| ST-04 | All timestamps are UTC and come back with `Kind = Utc` | Eng | MUST | U I | |
| ST-05 | The schema enforces what code assumes: the payload `CHECK`, unique (`BatchId`, `Sequence`), unique `BatchId`. A raw-SQL insert that breaks one fails | Eng | SHOULD | I | |
| ST-06 | `secure_delete=ON` on every connection. After a delete (and after a payload is nulled) plus `wal_checkpoint(TRUNCATE)`, a unique marker string is absent from the database file and the WAL | B2, Eng | SHOULD | I M | K6 |
| ST-07 | Payload stored inline, at most `MaxPayloadBytes`; media is referenced (address, type, size, checksum), never carried | Add | MUST | I | K11 |

### Read API and admin override

| ID | Requirement (acceptance criterion) | Src | Pri | Evidence | Comp. |
|---|---|---|---|---|---|
| AP-01 | `GET /api/entities` (keyset cursor, default 50, max 100, bad cursor is `400`) and `GET /api/entities/{id}` | B4 | MUST | I | K13 |
| AP-02 | Users see published and not-disabled entities. Admins see everything, with a `status` block. Same endpoints; the server decides from the caller's role only; no query parameter widens it | B4 | MUST | I A M | |
| AP-03 | A hidden entity returns the same `404` as a missing one | Sc | MUST | I | |
| AP-04 | No route updates, replaces or deletes an entity. The only entity writes are admin disable and enable (idempotent, logged with who). A route-inventory test proves it | B4 | MUST | I A | |
| AP-05 | Disable changes only the override columns; payload, version and published flag are untouched and the CMS is not involved | B4 | MUST | I | |
| AP-06 | Reads project to a DTO, track nothing, use no `Include`, write the stored payload text raw, use the partial index with no sort, and cost a constant number of queries per page | B5 | MUST | Q M | |
| AP-07 | `/health` is anonymous and returns only `{"status":"ok"}` (or 503) after a database check | Eng | MUST | I | |
| AP-08 | Responses carry `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and a restrictive CSP | Eng | SHOULD | I | |

### Security

| ID | Requirement (acceptance criterion) | Src | Pri | Evidence | Comp. |
|---|---|---|---|---|---|
| SE-01 | Deny by default: a fallback policy requires sign-in; only `/health` is anonymous. A test enumerates every mapped route and fails if one has no row in the role matrix | Sc | MUST | A M | |
| SE-02 | Least privilege exactly as the §4.6 table: the CMS cannot read entities; users and admins cannot ingest; only admins disable, enable and administer the queue | Sc | MUST | A M | |
| SE-03 | Credentials are compared in constant time (hash both sides, `FixedTimeEquals`, every account always evaluated). `401` carries `WWW-Authenticate: Basic` | Eng | MUST | I U | K4 |
| SE-04 | Basic-auth valid and invalid combinations are covered (§6.3, suite T9) | B7 | MUST | I | |
| SE-05 | A failed-sign-in throttle per client address answers `429` + `Retry-After`, with an injected clock | Eng | SHOULD | I | K4 |
| SE-06 | No credentials in `appsettings.json`; development credentials only in `appsettings.Development.json`; missing or malformed credentials stop startup and name the setting | Eng | MUST | I | K4 |
| SE-07 | No payload, password or `Authorization` header in any log, on success, rejection, 401 and failure paths; the login-failure log omits the submitted username | Eng | MUST | L M | |
| SE-08 | Production enables HTTPS redirection and HSTS; the docs say Basic auth is only safe over TLS | Eng | SHOULD | I D | K5 |
| SE-09 | Hostile input is bounded: size, count, depth, strict UTF-8, control characters, far-future timestamps, lone surrogates | Eng | MUST | U I | |
| SE-10 | Admin actions are logged with who and what | Eng | MUST | L | K14 |

### Observability

| ID | Requirement (acceptance criterion) | Src | Pri | Evidence | Comp. |
|---|---|---|---|---|---|
| OB-01 | Every event is logged, failing ones included, with batch id, index, id, type, version, outcome, reason and checksum. Applied and duplicate: Information. Stale, unsupported, rejected and applied-by-conflict: Warning. A failed batch: Error | B6 | MUST | L M | |
| OB-02 | The batch id is the `X-Correlation-Id` header, a property of every log line and the key in `Batches` and `Events` | Eng | MUST | I L | |
| OB-03 | `Events` is the ledger: a row per event received, rejected ones included, with `ReceivedAt` and `ProcessedAt`; no payload once decided | Eng | MUST | I | K6 |
| OB-04 | Meter `Lateral.Cms` with bounded tags: events processed (outcome, type), queued, apply and accept duration, queue lag, retries, set-asides, and gauges pending, oldest age and failed | Eng | SHOULD | I | K12 |
| OB-05 | Worker health is visible: a log when a batch is applied (with its wait), a Warning per failed attempt, an Error when a batch is set aside or the worker faults | Eng | MUST | L | |
| OB-06 | DESIGN lists the alerts an operator needs, including "the CMS has gone quiet" | Eng | SHOULD | D | K12 |

### Testing and delivery

| ID | Requirement (acceptance criterion) | Src | Pri | Evidence | Comp. |
|---|---|---|---|---|---|
| TE-01 | Event-processing tests as §6.3 suites T1, T2, T6 | B7 | MUST | | |
| TE-02 | Ingestion-constraint tests as §6.3 suites T3, T5 | B7 | MUST | | |
| TE-03 | Basic-auth tests as §6.3 suite T9 | B7 | MUST | | |
| TE-04 | The suite is hermetic, finishes in under 60 s, and passes 20 consecutive full runs | Eng | MUST | O | |
| TE-05 | Every MUST row names at least one test through `[Trait("req","ID")]`; a meta-test fails if one does not | Eng | SHOULD | D | |
| TE-06 | A mutation log in `docs/EVIDENCE.md`: at least 25 deliberate defects (§6.4), each caught by a named test | Eng | MUST | M | K16 |
| TE-07 | A load smoke (§6.5) is in the repo and its results are recorded with machine and date | Eng | SHOULD | B | K2 |
| DL-01 | A .NET 9 solution (`net9.0`, `RollForward=Major` so it runs where only a newer runtime is installed) | B8 | MUST | S | |
| DL-02 | README: prerequisites, run, a try-it flow that includes following a batch, tests, configuration, running outside Development, database and migrations, ranked limitations | B8 | MUST | D O | |
| DL-03 | A CI job runs the README's try-it commands against the built service | Eng | SHOULD | O | |
| DL-04 | CI builds and tests on Windows, macOS and Linux with warnings as errors; no OS-specific paths | B8 | MUST | O | |
| DL-05 | A GitHub repository holds all of the above. The remote is the user's decision; never push unasked | B8 | MUST | | |
| DL-06 | Five docs with one job each (D7); relative links resolve (a link test); no number typed by hand that a test or script can produce | Eng | SHOULD | D | |

### Developer tooling (Phase 8, COULD)

| ID | Requirement | Src | Pri | Evidence | Comp. |
|---|---|---|---|---|---|
| DV-01 | A test console that listens on loopback only, keeps credentials on its server, refuses cross-site and DNS-rebinding requests, reaches only an allow-list of the service's routes and builds no markup from text it was given | Add | COULD | I (its own tests) | K17 |
| DV-02 | A fenced test-only reset: off by default, not mapped unless switched on, refused at startup in Production, admin only, exact confirmation body, runs in one transaction, logged with who and counts. It also empties the queue | Add | COULD | I M | K17 |
| DV-03 | A one-click Visual Studio launch profile for the service and the console | Add | COULD | O | |
| DV-04 | The S3 comparison and the generated diagrams, updated for the new design | Add | COULD | D | |

## 6. Standards of evidence

A claim is only as strong as the weakest thing behind it. This section says what must exist before a requirement counts as
done, what a test must look like, which suites are required, and what gates a phase and the release.

### 6.1 Evidence levels and codes

| Level | Meaning | Allowed for |
|---|---|---|
| E1 Reasoned | An argument and a code review | Only the production items in §7 that the demo cannot test (scale-out, secrets management) |
| E2 Tested | An automated test passes, **and** it was seen to fail when the behaviour was broken (mutation log) | Every MUST row |
| E3 Measured | A repeatable measurement with numbers, environment and date, recorded in `docs/EVIDENCE.md` | Every performance or capacity claim |
| E4 Operated | Observed in a real run: CI on three operating systems, and the README's commands executed against the built service | The README, the build, portability |

A row marked `M` needs E2 with a logged mutation. A claim in README or DESIGN is either tested (name the test), measured
(point at EVIDENCE) or labelled *reasoned* or *assumed*. Never state a number nobody measured.

| Code | Kind of evidence |
|---|---|
| S | Static: compiles with warnings as errors, nullable on, analysers on |
| U | Unit: pure rule or parser test, no I/O |
| P | Property: exhaustive permutations, seeded random against the oracle, idempotence, partition independence |
| I | Integration: real HTTP pipeline (`WebApplicationFactory`), real SQLite file per test class, real auth, assertions on responses **and** on stored rows through raw SQL |
| F | Fault injection: a real failure, produced by a SQLite trigger (`RAISE(ABORT)`), a dropped table, a second host on the same file after a "crash"; never a test hook in production code |
| C | Concurrency: many simultaneous senders, no loss, no duplicate, cap respected |
| Q | Query: SQL command count per operation, and `EXPLAIN QUERY PLAN` of the SQL EF actually generates |
| A | Authorisation matrix: every caller against every mapped route, generated from the endpoint list |
| L | Log: captured log lines checked for required fields, levels and the absence of secrets |
| V | Golden or contract: pinned bytes (canonical form, checksum vectors), reason-code constants, status table, schema constraints |
| M | Mutation: the rule was deliberately broken and a named test failed |
| B | Benchmark: the load smoke |
| O | Operated: CI matrix, README smoke job, the 20-run flake gate |
| D | Docs: the claim is written, linked, and where possible checked by a test or script |

### 6.2 Rules every test follows

1. **Real dependencies.** Real SQLite and the real HTTP pipeline. The EF in-memory provider is not used: it enforces no
   constraints, triggers or query plans.
2. **Deterministic.** Inject `TimeProvider` (a manual clock for back-off, throttle windows, queue age). No sleeps. Drive
   the worker by calling the processor with the worker switched off. A test of the *running* worker waits for a condition
   with a time limit, never for a fixed time.
3. **Isolated.** One temp database per test class; classes run in parallel. A factory clears only its own pooled
   connections. No shared mutable static state.
4. **Assert effects, not just answers.** Check the stored rows, the log lines and the metrics a behaviour should have
   produced, not only the status code.
5. **Fail for the right reason.** Each test names the behaviour as a sentence
   (`A_failed_attempt_applies_nothing_keeps_the_batch_and_waits_before_trying_again`) and fails when that behaviour is
   removed.
6. **Real faults.** A trigger refuses one entity so rollback, retry and set-aside run against the real code.
7. **Fast.** Whole suite under 60 s; a single test under 5 s.
8. **Trace.** Each test carries `[Trait("req","ID")]` for the requirement it proves.

### 6.3 Required suites and their minimum content

| Suite | Must contain at least |
|---|---|
| T1 Rules | One test per row of the §4.4 table; both arrival orders for every tie and every contradiction; the X → X+1 → unpublish case; no resurrection; recreate clears the override |
| T2 Properties | Exhaustive permutations of ≥10 curated timelines (≤7 events; include ties, duplicates, contradicting content, deletes); the seeded oracle test (≥20,000 timelines × 3 shuffles in the normal run; a slower category runs ≥1,000,000); replay-twice idempotence; partition independence through the real applier; the pinned counterexample from §2.4 item 2; an architecture test that `Domain` references no EF or ASP.NET type |
| T3 Parsing | Every reason code with a failing input and a boundary-passing input; every row of the canonical-form table as a golden vector; hostile input (invalid UTF-8, lone surrogate escape, duplicate members, depth 32 and 33, huge number tokens, a BOM, a 4 MiB string); type text masked and bounded |
| T4 Checksum | Published SHA-256 and MD5 vectors; verification under the algorithm a value names, not the configured one; every stored row re-verifies after a mixed workload; MD5 only by configuration; a forbidden algorithm stops startup |
| T5 Contract | Each row of the §4.5 status table through HTTP; the response shape; `Location`; `X-Correlation-Id`; partial failure; all-rejected is `200`; replay; unknown type; batch of 500 and 501; body exactly at the limit and one over, judged by declared length; a stalled declared-10-GiB body refused without being read |
| T6 Pipeline | Acknowledgement follows commit (a trigger on the insert gives `503` and an empty database); apply is atomic (a trigger on the last statement leaves state, events and batch untouched); retry delays 2, 4, 8, 16 s capped at 60 s on a manual clock; set-aside after the maximum without blocking later batches; requeue (`200`, `404`, `409`); the worker survives a failing batch and a storage outage; restart on the same file applies what was queued, once; cap and concurrent senders; takedown first; batch reordering cannot change the result; a delete leaves no copy of an earlier publish in `Events` or `Batches`; status for each state, never a payload; clean shutdown |
| T7 Storage | Read context cannot save and its connection is read-only; WAL on; migration builds the schema from nothing; the payload `CHECK` and unique keys reject bad rows; UTC kinds round-trip; stale write refused and token renewed; `secure_delete` marker-absence test (ST-06) |
| T8 Read API | User and admin visibility for every state (published, unpublished, disabled, both); `404` indistinguishable from missing; no parameter widens visibility; paging (cursor, limit caps, bad cursor); disable and enable idempotent and logged; override survives a publish and is cleared by delete then re-create; route inventory shows no write verb on entities |
| T9 Security | Credential matrix: unknown user, wrong password, right password on the wrong account, empty parts, no colon, colon inside the password, bad base64, wrong scheme and scheme case, very long header, non-ASCII, rotated and old password. Startup validation: CMS username 9, 10, 20 and 21 characters; non-GUID, empty and duplicate passwords; short user password; unknown role; missing credentials. Throttle on a manual clock including expiry. The role-by-route matrix generated from the endpoint list. Headers. No secret in any log |
| T10 Observability | One line per event with the required fields; the level for each outcome; batch id in header, logs and rows; meter values through a `MeterListener`; an unknown type string cannot create a new metric series |
| T11 Performance | SQL command counts: accepting reads no entity state, applying reads it in two queries for 1 and for 60 events; the pick query and the listing query use their indexes with no sort; the load smoke (§6.5) |
| T12 Delivery | The README smoke job; relative links resolve; the requirement-id meta-test |

### 6.4 Mutation log (the baseline to repeat and extend)

Break each rule on purpose, confirm a named test fails, restore it, and record it in `docs/EVIDENCE.md` as *rule, mutation,
failing test*. At least 25, covering at minimum:

| Area | Mutations |
|---|---|
| Rules | ignore the tombstone · remove the unPublish tie-break · remove the stale-version check · decide content conflicts by arrival order · a stale delete does not raise the tombstone · recreate keeps the old override |
| Parsing | hash the wrong bytes · remove the declared-length check · accept far-future timestamps |
| Storage | open the read side read-write · stop renewing the concurrency token · make the index filter unmatched · turn `secure_delete` off · drop the payload `CHECK` |
| Pipeline | commit apply and queue removal separately · leave the payload when a batch is applied · remove takedown priority · never set a batch aside · no back-off · cap not enforced, or not under the gate · let the worker die on an exception · pick set-aside batches again · report a failed batch as queued · acknowledge before the commit |
| Security | let users read batch status · let admins ingest · remove the fallback policy · log a payload |

The first build did about 30 of these and kept no record. The log is part of the deliverable.

### 6.5 Measured claims: the load smoke

Keep a small console tool in `tools/` (it starts the built service on a loopback port with generated credentials and a
temp database, then drives it) with these parameters: log level, senders, batches per sender, events per batch, payload
size. It prints accepted events per second, request latency percentiles, the time until the queue is empty (read from the
admin report), and the time to page through every entity as a user. Record machine, runtime, date and the commands in
`docs/EVIDENCE.md`.

**Regression bar:** on the same machine and the same loads as §2.4 item 4, accepted throughput and end-to-end applied
throughput must be within 20% of the first build's, and the 1-event request p95 at most 25 ms. If the rebuild is
slower, find out why before accepting it. Throughput is a measurement, not a requirement; no figure goes in the README as a
promise.

### 6.6 Gates

**Phase gate** (before asking the user to commit):

1. The phase's matrix rows each name their tests, and `docs/REQUIREMENTS.md` shows the evidence level reached.
2. `CI=true dotnet build -c Release` has zero warnings; the full suite passes; it finishes in time.
3. The mutations listed for the phase are done and logged.
4. `docs/EVIDENCE.md` has this phase's results (counts, measurements).
5. Anything claimed and not proven is written down as such, in the same change.

**Release gate** (Phase 7):

1. Every MUST row at its target evidence; every SHOULD row done or moved to §7 with a reason.
2. CI green on Windows, macOS and Linux with warnings as errors; the README smoke job passes.
3. The full service suite passes 20 consecutive runs locally with no failure.
4. Mutation log of at least 25, each caught.
5. Load smoke recorded and within the regression bar.
6. `docs/REQUIREMENTS.md` and `docs/EVIDENCE.md` agree with the code and the tests; the meta-test and the link test pass.
7. A sweep: no dead configuration key, no unused code, no TODO, no secret in the repo.

## 7. Demo versus production

### 7.1 What a demo is allowed to leave, and what production needs

| ID | Area | The demo does | Why that is acceptable here | Production needs | Evidence now |
|---|---|---|---|---|---|
| K1 | Scale-out | SQLite file; one writer; an in-process gate; one instance | Runs on any OS with nothing to install; correctness does not depend on scale | A server database; several workers claiming batches (`SELECT … FOR UPDATE SKIP LOCKED`); drop the gate; shared throttle | E3 on one machine |
| K2 | Capacity | Throughput measured on one machine; no soak, no burst-size limit, no capacity model | The brief asks for reasoning, not numbers | Load, soak and chaos tests against production-like hardware; SLOs | E3, narrow |
| K3 | Queue | A table in the same database; one worker; FIFO with takedown priority per batch (a continuous takedown flood could starve publish-only batches) | One transaction covers state and queue; no second system to run | A broker or partitioned queue keyed by entity id; every Nth batch taken oldest-first | E2 |
| K4 | Credentials | Plain text in configuration; Basic auth as the brief requires; an in-memory per-instance throttle; no sign-in audit | The brief asks for Basic auth; credentials are fenced to the development file | A secret store, stored hashes, request signing (HMAC) for the CMS, OIDC for people, gateway-level rate limiting and lockout | E2 / E1 |
| K5 | Transport | TLS assumed at the edge; redirect and HSTS only in Production | Local runs are plain HTTP | Enforced TLS, forwarded-header configuration, certificate management | E1 |
| K6 | Deletion and lifecycle | The live file is wiped on delete (`secure_delete`). No encryption at rest. No retention job for the ledger, tombstones, completed batches or failed batches. Ledger rows keep an erased entity's id and its payload checksum | "Hard delete" holds for the live database file | Backups, replicas, WAL archives and snapshots covered by the erasure policy; encryption at rest; per-entity keys (crypto-shredding); retention periods set as policy; a purge job | E2 (live file only) |
| K7 | Recovery | No resync or replay; no CMS-supplied sequence numbers; a dropped batch is visible only as a gap in the ledger | Out of the brief | A sequence or offset from the CMS, a replay endpoint, a periodic reconciliation (compare checksums with the CMS) | E1 |
| K8 | Ordering | Correct only while a higher version never carries an earlier timestamp (S17). The counterexample is pinned by a test | One CMS emits the events | A CMS-assigned monotonic sequence per entity (or deletes that carry a version) | E2 (the limit itself is tested) |
| K9 | Idempotency and callbacks | A retried batch is stored and processed again (each event reports `ignored_duplicate`); the CMS is not called back when a batch completes | Safe, merely wasteful | An idempotency key per batch; a completion callback or event | E2 |
| K10 | Integrity | Checksums detect corruption, not tampering; the canonical form follows the .NET JSON encoder, not a standard | Meets the request; documented with golden vectors | An HMAC or signatures; RFC 8785 (JCS) so other languages can reproduce the form | E2 |
| K11 | Payload storage | Inline `TEXT`, at most 256 KiB; no blob store; media by reference | State, payload, checksum and event row commit atomically | Content-addressed object store above about 1 MB, written before the row commits, with a guaranteed deletion queue | E2 |
| K12 | Operations | A meter and structured logs; alerts only listed | The platform owns exporters and dashboards | OpenTelemetry export, dashboards, alert rules as code, tracing, runbooks | E2 (signals) / E1 |
| K13 | API surface | Keyset paging by id only; no filtering, sorting, search, ETags, conditional GET, versioning or per-reader rate limits | Out of the brief | Those, plus an API version policy | E1 |
| K14 | Admin audit | Columns on the row and log lines; no history table | Adequate for a demo | An append-only audit table written in the same save | E2 |
| K15 | Database portability | Only SQLite exercised; read-only connection mode and the partial-index filter are SQLite specifics; no rollback or zero-downtime migration plan | Brief allows any relational database | A second provider in CI; migration strategy | E1 |
| K16 | Verification depth | No browser tests, soak, chaos, dependency audit, SAST or DAST, formal threat model; mutation checks are manual | Proportionate to a take-home | Automated mutation testing (Stryker.NET scoped to `Domain` and the parser), dependency and container scanning, a threat model, a security review | E2 |
| K17 | Developer tooling | The console and the reset endpoint exist for hand-testing and are fenced | Requested by the user | Not shipped to production | E2 |
| K18 | Tenancy | One CMS, one tenant | Brief describes one | Tenant isolation, per-tenant keys and quotas | E1 |

### 7.2 If this went to production, in this order

1. Server database and several workers (K1, K3). 2. Secrets, signed requests, TLS (K4, K5). 3. Erasure and retention policy
including backups (K6). 4. Sequence numbers and resync from the CMS (K7, K8). 5. Observability stack and alerts (K12).
6. Load, soak and chaos tests, then a security review (K2, K16). 7. Idempotency keys and callbacks (K9).

### 7.3 Explicit non-goals

Search, filtering and sorting; outbound webhooks; a blob or media store; SSO and user management; multi-tenancy; an event
replay API; a user interface beyond the developer console.

## 8. Build plan

### 8.1 Phases

Size is relative: S small, M medium, L large. Each phase ends at its gate (§6.6); show the evidence and wait for the
user's word before committing.

| Phase | Deliverable | Matrix rows | Exit evidence | Size |
|---|---|---|---|---|
| 0 Foundations | Branch `rebuild`; solution; `Directory.Build.props`, `.editorconfig`, `.gitattributes`, `global.json`, tool manifest; CI on three operating systems; GUIDELINES ported and updated; `REQUIREMENTS.md` generated from §5 (all "not started"); `EVIDENCE.md` skeleton; test harness (factory with per-database pool clearing, capturing logger, manual clock); a walking skeleton (`/health`, fallback deny, one context and migration, one HTTP test) | DL-01, DL-04, SE-01 (start) | CI green on three operating systems with warnings as errors | S |
| 1 Reducer | Pure domain and reducer; suites T1 and T2 including the oracle and the pinned counterexample; architecture test | EV-01 to EV-16 | T1, T2 pass in under 5 s; mutations: ignore tombstone, remove tie-break, remove stale check, conflict by arrival order, stale delete, recreate keeps override | M |
| 2 Parse, canonicalise, checksum | `EventParser`, canonical writer, `IPayloadChecksum`, limits constants; suites T3, T4 | IN-03, IN-05, IN-07, SE-09 | Golden vectors pinned; boundary tests; mutations: wrong bytes, far-future accepted | M |
| 3 Persistence | Model with the `CHECK`, contexts, interceptor (`secure_delete`), WAL, write gate, converter, one migration; suite T7 | ST-01 to ST-07 | Marker-absence test passes; read context cannot write; mutations: read side read-write, token not renewed, `secure_delete` off, `CHECK` dropped | M |
| 4 Auth | Handler, store, options and validator, policies, throttle, route-inventory and matrix harness; suite T9 | IN-02, SE-01 to SE-06, TE-03 | Credential and startup matrices pass; the matrix fails when a route has no row; mutations: fallback policy removed, admins may ingest | M |
| 5 Pipeline | Accept, worker, processor, status and queue endpoints, logs and metrics; suites T5, T6, T10 and the pipeline half of T11 | IN-01, IN-04, IN-06, IN-08, AS-01 to AS-10, OB-01 to OB-05, EV-12 to EV-14 | Faults, restart, cap and concurrency tests pass; query counts and plans pass; the pipeline mutations are logged | L |
| 6 Read API | Queries, endpoints, admin override; suite T8 and the read half of T11 | AP-01 to AP-08, SE-10, EV-15 | Visibility matrix and plan tests pass; mutations: filter widened, listing index unmatched | M |
| 7 Evidence and docs | Load smoke; 20-run loop; mutation log completed; README smoke job in CI; README, DESIGN, REQUIREMENTS, EVIDENCE finished; meta-test and link test; final sweep | TE-04 to TE-07, DL-02, DL-03, DL-06, AS-11, OB-06 | The release gate in §6.6 | M |
| 8 Developer tooling (optional, after Phase 7) | Console, reset endpoint, VS launch profile, S3 doc, diagrams, copied from `2238384` and updated for D1 and D2 | DV-01 to DV-04 | Console tests pass; reset mutations logged | M |

Working rule inside a phase: write the failing test first for rules and parsing; build the thinnest slice that makes a
test pass; add the mutation immediately, not at the end. At each gate update `REQUIREMENTS.md` and `EVIDENCE.md` in the
same change as the code.

### 8.2 What to take from the first build (the reference worktree)

| Take as it is, with its tests (adjust namespaces) | Take with changes | Rewrite from §4 (reuse test ideas and names) |
|---|---|---|
| `Ingestion/EventParser.cs`, `ParsedEvent.cs`; `Integrity/PayloadChecksum.cs`; `Auth/*` (handler, credential store, options, throttle, extensions); `Entities/*` (queries, endpoints, admin service, models). Tests: `EventParserTests`, `PayloadChecksumTests`, `ChecksumEndpointTests`, `BasicAuthTests`, `AuthOptionsTests`, `AuthThrottleTests`, `RoleMatrixTests`, `StartupValidationTests`, `EntitiesEndpointTests`, `EntityPagingTests`, `QueryCountTests`, `ListingIndexTests`, `IngestionLimitsTests` | `Domain/EntityAggregate.cs` and its two test classes (keep the rules and every case; reshape to the immutable reducer, D3). `Persistence/` contexts, `WriteGate`, `UtcDateTimeConverter`, `PersistenceExtensions` (new model, interceptor). Test infrastructure: `CmsFactory` (with the per-database `ClearPool` fix), `AsyncCmsFactory`, `CapturingLoggerProvider`, `ManualTimeProvider`, `Events` helper, `QueryPlan`. Config files, CI workflow, `requests.http`, `GUIDELINES.md`, `S3-COMPARISON.md` | `Ingestion/EventIngestor.cs`, `InboxEventIngestor.cs`, `BatchApplier.cs`, `InboxProcessor.cs`, `InboxWorker.cs`, `InboxEndpoints.cs`, `InboxSupport.cs`, `IngestionEndpoints.cs`; the migrations; the Admin reset service. Test ideas from `IngestionEndpointTests`, `AsyncIngestionTests`, `InboxFailureTests`, `InboxWorkerTests`, `InboxBackpressureTests`, `InboxIndexTests`, `AsyncQueryCountTests`, `AsyncResetTests` |

Phase 8 copies wholesale: `tools/Lateral.Cms.TestConsole`, `tests/Lateral.Cms.TestConsole.Tests`, the reset endpoint and
`ResetEndpointTests`, `Lateral.slnLaunch`, `.vsconfig`, `tools/diagrams`.

## 9. Working agreements

- **Style of work.** The user reviews a plan before anything is built, asks pointed design questions, and values measured
  claims and honest limits over polish. Say what is unverified, in the same message that reports the result. Ask only about
  decisions that change behaviour; otherwise pick the sensible default and say so.
- **Source control.** Work on a new branch. Commit only when asked, one commit per phase gate, message ending with
  `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. **Never push.** `origin` points at a closed repository
  (`pahayward/CMS`); ignore any suggestion of a new remote. Stage files by name; write the message to a file and use
  `git commit -F`.
- **Secrets.** Development credentials live only in `appsettings.Development.json`, as random GUIDs, and are never echoed
  in chat, docs or logs. Never log a payload, a password or an `Authorization` header.
- **The user's own things.** Do not touch `src/Lateral.Cms/App_Data/cms.db` or stop anything on ports 5080 (service) and
  5100 (console): those are the user's running copies. Use other ports and temp databases for your own runs.
- **Environment.** Windows 11. Only the .NET 10 SDK is installed: projects target `net9.0` with `RollForward=Major`, and
  `dotnet ef` (local tool, 9.0.22) needs `DOTNET_ROLL_FORWARD=Major`. `CI=true` turns warnings into errors (use it for the
  strict build). No Python; Perl and Node are available. Windows PowerShell 5.1 runs the diagram script. Git prints
  line-ending warnings (`autocrlf`); they are expected.
- **People.** Refer to the user as they/them.

## 10. Risks

| Risk | Mitigation |
|---|---|
| A rewrite loses a subtle rule | Port the rule tests first; the oracle; permutation tests; the first build as an executable reference |
| Scope grows sideways again | Phase 8 is last and optional; size budgets; a gate per phase |
| Docs drift from code | Five docs, one job each; ids on tests; the meta-test; numbers produced by scripts |
| Flaky tests hide real races | Harness rules; the 20-run gate; no sleeps |
| EF or SQLite surprises | The pitfall list (§4.10); plan and command-count tests |
| Reviewers misread `202` as "applied" | README try-it follows the batch; DESIGN states it; the response says `queued` |
| Throughput claims creep in | Evidence level E3 only; the README promises none |
| Dropping the synchronous option disappoints | It is a default the user can veto (D1); inline apply is a small addition if wanted |

## Appendix A: the order-free oracle

The reducer is correct under S17 if, for any set of events for one id, every arrival order ends in the state this function
computes from the set alone. It matched the first build's reducer on 600,000 shuffles (§2.4).

```csharp
static Snap Oracle(IReadOnlyList<CmsEvent> events)
{
    DateTime? tomb = events.Where(e => e.Type == CmsEventType.Delete)
                           .Select(e => (DateTime?)e.Timestamp).Max();            // newest delete
    var live = events.Where(e => e.Type != CmsEventType.Delete
                              && (tomb is null || e.Timestamp > tomb)).ToList();   // strictly after it
    if (live.Count == 0) return new Snap(Exists: false, Tombstone: tomb);
    var w = live.OrderBy(e => e.Version!.Value)
                .ThenBy(e => e.Timestamp)
                .ThenBy(e => e.Type == CmsEventType.UnPublish ? 1 : 0)           // unPublish beats publish
                .ThenBy(e => e.Payload, StringComparer.Ordinal)                   // greater text wins
                .Last();
    return new Snap(true, w.Version!.Value, w.Type == CmsEventType.Publish, w.Payload, w.Timestamp, tomb);
}
```

Generator used for the 200,000 timelines (fixed seed, one entity id): 1 to 8 steps; the minute advances by 1 or 2, or
stays the same 1 time in 5 (timestamp ties); each step is a publish (40%), an unPublish (30%) or a delete (30%); the
version never decreases and rises by 0 to 2 per publish or unPublish; with probability 1/6 each, add a same-kind twin with
different payload and an opposite-kind twin at the same version and minute; then add 0 to 2 exact duplicates; shuffle
three times. Compare `(exists, version, published, payload, last event time, tombstone time)`. To demonstrate the limit,
let the version fall behind by 0 to 2 relative to its running value and watch roughly half a percent of shuffles diverge.

## Appendix B: canonical form of a payload

The text stored in `Entities.Payload`, served as `payload`, and hashed. It is what `Utf8JsonWriter` produces with default
options from the parsed `JsonElement`. Pin every row with a golden test.

| Aspect | Rule |
|---|---|
| Whitespace | None between tokens |
| Members | In the order sent; duplicate names kept; nothing sorted or merged |
| Numbers | Exactly as sent (`1.50`, `1E3` and `-0` stay as they are) |
| `true`, `false`, `null` | As sent |
| Strings: short escapes | `"` is `"`; `\` is `\\`; newline `\n`; tab `\t`; carriage return `\r`; backspace `\b`; form feed `\f` |
| Strings: other control characters | `\uXXXX`, including DEL (`\u007F`) and U+0001 (`\u0001`) |
| Strings: HTML-sensitive ASCII | `<` `>` `&` `'` `+` and the backtick are `<` `>` `&` `'` `+` ``` |
| Strings: non-ASCII | Every UTF-16 code unit above U+007F is `\uXXXX` with upper-case hex; an astral character is a surrogate pair (`😀` is `😀`) |
| Strings: left as they are | `/` and every other printable ASCII character |

To verify from what the API served: hash the UTF-8 bytes of the `payload` member's raw text, without re-serialising it, using
the algorithm the checksum names. To verify from the source object: reproduce the form above (for plain ASCII without the
HTML-sensitive characters it is just the compact JSON). Checksums are integrity only: there is no secret, so someone who can
rewrite a payload and its checksum together is not detected.

## Appendix C: where to look in the reference worktree

| To learn | Read |
|---|---|
| The rules and their edge cases | `src/Lateral.Cms/Domain/EntityAggregate.cs`; `tests/Lateral.Cms.Tests/Domain/` |
| Validation, canonical form, reason codes | `Ingestion/EventParser.cs`; `Domain/EventOutcome.cs`; `tests/.../Ingestion/EventParserTests.cs` |
| Auth | `src/Lateral.Cms/Auth/`; `tests/.../Auth/` |
| Read side and plan tests | `Entities/EntityQueries.cs`; `tests/.../Persistence/ListingIndexTests.cs`; `Infrastructure/QueryPlan.cs` |
| How faults are injected, how time is controlled | `tests/.../Api/InboxFailureTests.cs`; `Infrastructure/AsyncCmsFactory.cs` |
| The reasoning behind the first build | `docs/DESIGN.md`, `docs/DEVELOPMENT-STORY.md`, `docs/GUIDELINES.md` |
| The console and the reset | `tools/Lateral.Cms.TestConsole/`; `src/Lateral.Cms/Admin/AdminEndpoints.cs` |
