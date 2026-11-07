# Design

Why the service is built the way it is, the contract it keeps, and where it stops. [README](../README.md) says how to run
it; [GUIDELINES](GUIDELINES.md) is the standard it is held to; [REQUIREMENTS](REQUIREMENTS.md) maps each requirement to its
tests; [EVIDENCE](EVIDENCE.md) holds the measurements and the deliberate-defect log. A claim here is either tested (a test is
named), measured (EVIDENCE has the run) or labelled *reasoned* or *assumed*.

**Contents:** [1 Decisions](#1-decisions-on-ambiguities) · [2 Shape](#2-shape) · [3 Sync or async](#3-why-asynchronous) ·
[4 The pipeline](#4-the-pipeline) · [5 The rules](#5-the-reconciliation-rules) · [6 The ordering assumption](#6-the-ordering-assumption) ·
[7 Contract](#7-ingestion-contract) · [8 Storage](#8-storage) · [9 API and access](#9-api-and-access) ·
[10 Observability](#10-observability-and-alerts) · [11 Assets and scanning](#11-assets-and-scanning) · [12 Security review](#12-security-review) · [Limits](#limits)

## 1. Decisions on ambiguities

The brief is open-ended on purpose. Each ambiguity got a written decision (tested where it is behaviour).

| # | Ambiguity | Decision |
|---|---|---|
| S1 | Prose says Add / Update / Delete; the schema says `publish`, `unPublish`, `delete` | The schema is the wire format. Add and update are both `publish` (version 1 is an add). Type names match case-insensitively |
| S2 | "Delete → removed or unpublished" | `delete` is a hard delete; `unPublish` keeps the data and hides it from users |
| S3 | Events "include, but are not limited to" three types | An unknown type is reported per event as `ignored_unsupported_type`; the batch is not failed |
| S4 | The sample JSON is invalid (typographic quotes, trailing comma) | Strict parsing: invalid JSON gets `400`. Real traffic is assumed to be valid JSON |
| S5 | "New data is only available upon publishing"; an unPublish "contains the entity fields" | A publish is a full snapshot: the payload is replaced, never merged. An unPublish carries the whole entity, so it can *create* one (the X → X+1 → unpublish corner case) |
| S6 | Versions: X modified to X+1 | Unpublished drafts are never sent, so published versions have gaps. Contiguity is not required |
| S7 | "Assume all data is restricted" | Deny by default; the CMS and the readers are different accounts and roles; a hidden entity returns the same `404` as a missing one |
| S8 | Admin disable "does not affect CMS data" | A separate `IsDisabledByAdmin` column that CMS events never write. A later publish does not clear it. A delete followed by a re-create does |
| S9 | Admin listing "including the ones that were disabled" | Same endpoints; the caller's role picks the filter on the server. Admins also see unpublished entities and get a `status` block |
| S10 | Two events that cannot be ordered | `unPublish` beats `publish` at the same version and instant (fail closed). Same version, instant and kind but different content: the greater payload text wins, whichever arrived first, reported as `same_version_different_content` |
| S11 | Hard delete vs late events | The entity row and payload are removed. A payload-free tombstone (id, newest delete time) remains so a late older event cannot bring it back; a newer publish legitimately recreates it |
| S12 | One bad event in a batch | The request still succeeds (`2xx`) with a per-event outcome and reason, because `4xx` makes a webhook sender retry the whole batch forever. A batch of only rejected events is also `200`. Only an unreadable request (not JSON, not an array, too big) is refused |
| S13 | Ids and timestamps | Ids are 1 to 200 characters, trimmed, no control characters, no `/` or `\` (they must fit one URL path segment). Timestamps are ISO-8601 *with* an explicit offset, normalised to UTC, not more than an hour in the future. Versions are 64-bit integers ≥ 1 |
| S14 | "Sanitised" | Normalise, never alter content: trim ids, UTC timestamps, compact canonical JSON, reject control characters in ids. Payload values are not HTML-escaped or stripped; escaping belongs to whoever renders them |
| S15 | Checksums (asked for later) | Every payload, computed once at ingestion over the canonical text; `sha256:<hex>` by default, `md5:<hex>` by setting; stored, returned to the publisher, served by the API, recorded with the event and in the logs. Integrity, not authenticity |
| S16 | Large content ("what if someone uploads a 10 GB video?") | Never in an event: the webhook refuses bodies over the limit by their *declared* length before reading, and a payload may only *name* a file. Files have their own path, the asset store ([section 11](#11-assets-and-scanning)): multipart upload, scanning, ranged download |
| S17 | Ordering assumption | One CMS. For one id, a higher version never carries an earlier timestamp, and ids are not reused with restarting versions. [Section 6](#6-the-ordering-assumption) says what happens outside it |
| S18 | Sync or async | Durable asynchronous ([section 3](#3-why-asynchronous)) |
| S19 | A takedown that keeps failing | A batch holding an `unPublish` or `delete` is never set aside: it keeps retrying at the longest delay, loudly, because a parked takedown would leave a confidential entity visible. Other batches are set aside after the maximum attempts |
| S20 | Assets and entities | Standalone. An event's payload may name an asset id, but reconciliation and the entity API do not look at assets, so the rules of section 5 are unchanged |
| S21 | An upload the scanner cannot judge | Fail closed: it stays quarantined and unserved, retries with back-off, and is set aside (still unserved) for an admin. Only a `clean` verdict can end quarantine |
| S22 | An upload the scanner flags | Rejected: the bytes are overwritten and removed; the verdict, rule, checksum and times stay, never the content |

## 2. Shape

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

One web project with feature folders. The rules in `Domain/` reference nothing but the BCL (a test enforces it).

| Folder | Holds |
|---|---|
| `Domain/` | event, entity and tombstone records, outcomes and reason codes, the reducer |
| `Ingestion/` | parser and canonical writer, accept service, batch processor, worker, endpoints, options, log, metrics |
| `Entities/` | the read queries, the endpoints, the admin override |
| `Auth/` | Basic handler, credential store, options and their validation, failure throttle, policies |
| `Persistence/` | the two contexts, the model and converters, the write gate, connection setup (WAL, `secure_delete`), the migration |
| `Admin/` | the test-only reset (off unless `Admin:AllowReset`) |

EF Core is not wrapped in a repository: tests use the real database. An interface is added only where a second
implementation is plausible (the one candidate is a future payload store, see Limits).

## 3. Why asynchronous

The brief permits either and asks why. **Chosen: durable asynchronous.** Accept validates and stores in one transaction,
answers `202`, and a worker applies the batch.

| Option | Good | Bad |
|---|---|---|
| **Synchronous** (apply, then answer) | The sender learns the final outcome at once; one step | The sender waits for the whole batch (a 500-event batch is hundreds of milliseconds, and a slow database makes it seconds); a webhook sender often treats a slow answer as a failure and **retries a batch that was in fact applied**; no buffer, so a burst and a slow database turn into timeouts |
| **Asynchronous, in memory** (answer, then apply from a queue in RAM) | Fast answer | Acknowledging before the data is stored **loses events** on a crash: the CMS believes they were delivered |
| **Durable asynchronous** (store, answer, apply from the stored queue) | An acknowledged event survives a crash; the answer time does not depend on how long applying takes; bursts become queue depth, which is visible and bounded; a bad batch can be retried or set aside without losing it | A `202` is "stored", not "applied"; the sender must follow the batch to learn the outcome; one more moving part, and the queue must be watched |

**Who calls and what they expect.** The caller is a CMS webhook sender: it wants a quick answer, retries on anything that
is not `2xx`, and cannot do anything useful with a `4xx` for one bad event. So: `2xx` means "do not retry"; `202` says
where to look; a refusal (`413`, `429`, `503`) always means "nothing was stored, retry the same batch".

**What the queue does not do.** It does not raise the ceiling. Applying is still one writer on one SQLite file, so the
sustained rate is the applier's rate ([EVIDENCE](EVIDENCE.md) has the measurement); the queue turns a burst above that rate
into delay instead of failure, up to a cap (`Inbox:MaxBytes`), after which senders get `429` and retry. Accepting is
cheap and parallel (parse, canonicalise, hash) until senders queue on the single write gate.

## 4. The pipeline

**Accept** (on the request).
1. Authenticate; require role `CmsIngest`.
2. If the *declared* `Content-Length` exceeds `Ingestion:MaxBodyBytes`, answer `413 payload_too_large` before reading any
   body; otherwise read with a hard cap (a chunked body has no declared length).
3. Parse strictly (depth ≤ 32). Not JSON: `400 invalid_json`. Not an array: `400 invalid_batch`. More than `MaxBatchSize`
   events: `413 batch_too_large`.
4. Per element: validate, normalise, canonicalise, checksum. No database access: accepting reads no entity state.
5. Under the write gate, **one transaction**: if the bytes already queued (queued and failed batches) plus this batch would
   pass `Inbox:MaxBytes`, refuse with `429 inbox_full` + `Retry-After: 10` and store nothing; otherwise insert the `Batches`
   row (`HasTakedown` = any `unPublish` or `delete`) and one `Events` row per element. Queued events carry their payload;
   rejected and unsupported ones are already decided and carry none. A batch with nothing left to queue, and an empty one,
   is stored already `completed` and answered `200`.
6. After the commit: wake the worker; answer `202` with `Location: /cms/batches/{batchId}` and each event's outcome so far.
   A storage failure answers `503` + `Retry-After` with nothing stored. `X-Correlation-Id` is the batch id.

**Apply** (the worker, one batch per turn).
1. Wait for the wake signal, or `Inbox:PollInterval` when idle.
2. Under the write gate, take the next due batch: `queued` and not waiting for a retry, takedowns first, then oldest.
   Reordering is safe only because the reducer does not depend on arrival order ([section 6](#6-the-ordering-assumption)).
3. Load its queued events, then the `Entities` and `Tombstones` rows for the distinct ids: two queries however many events.
4. Fold each id's events, in sequence, with the pure reducer. Ids are independent.
5. **One transaction:** entity and tombstone changes (the persistence layer compares each id's state before and after:
   none to some is an insert, some to none a delete, some to some an update), every event's outcome and reason, its payload
   removed, the batch marked completed.
6. On any failure, shutdown included: the transaction rolls back and the batch is exactly as it was. The attempt is then
   recorded separately (attempts + 1, the next time, the *kind* of failure, never a message). Delay before attempt *n* is
   `min(RetryMaxDelay, RetryBaseDelay × 2^(n-2))`: 2, 4, 8, 16, 32, 60 s by default. After `MaxAttempts` (5) a batch is set
   aside as `failed` and the queue moves on. **A batch holding a takedown is never set aside**: it keeps retrying at the
   longest delay, with an Error log each time, until it succeeds (S19).
7. The loop catches every exception except cancellation, logs it and carries on: an unhandled exception in a hosted service
   would stop the whole host.

**Requeue** (admin): a `failed` batch goes back to `queued` with its attempts reset. Unknown id `404`; not failed `409`.

**Why one table for the queue and the ledger.** An `Events` row is the queue entry while its outcome is `queued` and the
ledger entry afterwards, so nothing is copied between tables, "no event is silently dropped" is true by construction, and the
schema enforces `CHECK (Outcome = 'queued' OR Payload IS NULL)`: once an event is decided its confidential content cannot be
there. (Tested in `StorageGuaranteeTests` by trying to break the constraint.)

## 5. The reconciliation rules

An entity's events are ordered by `(version, timestamp)`; an event changes state only if it sorts after what is stored. Deletes
carry no version and compare by timestamp. The reducer takes the current entity (or none), the current tombstone (or none) and
one event, and returns the new entity, the new tombstone, an outcome and a reason. It is pure and immutable.

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
| same kind | same version, timestamp and kind, different content | the **greater payload text** (ordinal) wins; `same_version_different_content` |
| delete | entity exists, delete timestamp ≥ its last event | hard delete; tombstone recorded or raised |
| delete | nothing stored | tombstone recorded (`tombstone_only`) |
| delete | older than the stored entity's last event | `ignored_stale` (`newer_state_exists`), but the tombstone mark is still raised |
| delete | at or below the existing tombstone | `ignored_duplicate` (equal) or `ignored_stale` (older) |
| publish / unPublish | timestamp ≤ tombstone | `ignored_stale` (`superseded_by_delete`) |

Properties, each with tests (`ReducerTests`, `ReducerPropertyTests`): **idempotent**; **order-free** under S17; **deterministic
ties** (never arrival order); **pure** (no I/O, clock or randomness; the concurrency token is renewed by the write context at
save time); **no resurrection**; **the admin override is never touched** by CMS events, and a re-create after a delete starts
with it cleared.

**How order independence is checked.** An independent *oracle* sees only the *set* of events for an id: the tombstone is the
newest delete, live events are those strictly after it, and the winner is the maximum of (version, timestamp, unPublish beats
publish, payload text). The tests compare the incremental reducer with it on tens of thousands of seeded random timelines
(duplicates, ties, contradicting content and deletes included), each shuffled, on every permutation of the curated timelines,
and through the real processor with batches split and reordered.

## 6. The ordering assumption

Order independence holds **only if, for one id, a higher version never carries an earlier timestamp, and ids are not reused
with restarting versions.** One CMS emitting its own events satisfies this. Outside it, arrival order can matter, and the
limit is not hidden: each case is pinned by a test that asserts the divergence (`The_pinned_counterexample…`,
`The_second_pinned_limit…`).

- *Version higher, timestamp earlier:* `publish v2 @t5`, `publish v3 @t1`, `delete @t3`. In the order (v2, v3, delete) no entity
  remains; in (delete, v2, v3) entity v2 remains. The set-based oracle says v2.
- *Id reused:* `publish v5 @t0`, `delete @t1`, `publish v1 @t2`, `unPublish v1 @t3`. If the unPublish arrives while v5 still
  exists it is discarded as stale and cannot be recovered after the delete and re-create.

This is not a bug a cleverer reducer could fix. In the first case v3 beats v2 by version, the delete beats v3 by time, and v2
beats the delete by time: a cycle. Two orderings disagree, and one stored row per entity cannot remember enough to settle it.
The fix belongs at the source: a CMS-assigned monotonic sequence per entity, or deletes that carry a version. The takedown-first
reordering and any reordering of batches rely on the same assumption.

## 7. Ingestion contract

| Field | Rule | Failure reason |
|---|---|---|
| `type` | required string; `publish`, `unPublish`, `delete`, case-insensitive | `missing_type`; other strings give `ignored_unsupported_type` (reason `unsupported_type`) |
| `id` | required string, trimmed, 1 to 200 characters, no control characters, no `/` or `\` | `missing_id`, `invalid_id` |
| `timestamp` | required; ISO-8601 with an explicit `Z` or offset and 0 to 7 fractional digits; converted to UTC; not later than now + `MaxFutureSkew` | `missing_timestamp`, `invalid_timestamp`, `timestamp_in_future` |
| `version` | publish and unPublish: required integer ≥ 1 (64-bit). Ignored on delete | `missing_version`, `invalid_version` |
| `payload` | publish and unPublish: required JSON object, at most `MaxPayloadBytes` once canonicalised. Ignored on delete | `missing_payload`, `invalid_payload`, `payload_too_large` |
| element | must be a JSON object; text that cannot be decoded (e.g. a lone surrogate escape) | `event_not_object`, `malformed_event` |

Outcomes: `applied`, `ignored_stale`, `ignored_duplicate`, `ignored_unsupported_type`, `rejected`, and `queued` (accepted, not
decided yet). Reasons are stable machine-readable constants, never reworded. Whole-request error codes (problem+json `code`):
`invalid_json`, `invalid_batch`, `payload_too_large`, `batch_too_large`, `inbox_full`, `storage_unavailable`, `invalid_cursor`,
`batch_not_found`, `batch_not_failed`. Errors never echo payloads.

| Situation | Status | The sender should |
|---|---|---|
| Stored; events queued | `202` + `Location: /cms/batches/{batchId}` | not retry; follow the `Location` |
| Nothing left to queue (all rejected, or empty) | `200` with final outcomes | not retry |
| Not JSON, not an array | `400` | fix the sender |
| Body or batch too large | `413` | fix the sender |
| Wrong or missing credentials; wrong account | `401` (+ `WWW-Authenticate: Basic`); `403` | fix credentials |
| Too many failed sign-ins from this address | `429` + `Retry-After` | wait |
| Queue full (`inbox_full`); nothing stored | `429` + `Retry-After: 10` | retry the same batch |
| Storage failure; nothing stored | `503` + `Retry-After` | retry the same batch |

The body of an ingest response and of `GET /cms/batches/{id}` is the same: `batchId`, `status`, `received`, `queued`, `applied`,
`ignored`, `rejected`, `results[]` of `{index, id, type, version, outcome, reason?, checksum?}`, plus `receivedAt`,
`completedAt`, and `attempts`/`lastError` once a batch has been retried. `checksum` is present for every publish or unPublish
that passed validation, whatever its outcome (for `ignored_stale` it identifies what that event carried, not what is stored).
**A `202` is not "applied"**: that is why the README's try-it follows the batch.

**Payload storage and canonical form.** A payload is stored inline as `TEXT` (at most 256 KiB) in the same transaction as the
state, so the state, its checksum and its event record cannot disagree; files never travel inside an event (a payload may name an
asset, see section 11). The stored text is exactly what `Utf8JsonWriter` writes from the parsed element with
default options: no whitespace; members in the order sent, duplicate names kept; numbers as sent; `"` `\` and the control
characters as short escapes (`\n \t \r \b \f`) or `\u00XX`; the HTML-sensitive characters `< > & ' + `` ` `` as `< >
& ' + ``; every non-ASCII UTF-16 unit as `\uXXXX` with upper-case hex; `/` and other printable ASCII
untouched. The API serves that stored text raw and the checksum covers its UTF-8 bytes, so a consumer verifies by hashing what
it received, with the algorithm the checksum names. The form is pinned by golden tests; if it must ever change, introduce a
new checksum prefix. It follows the .NET encoder, not a standard (RFC 8785 would let other languages reproduce it). Checksums
detect corruption; they are not signatures.

## 8. Storage

SQLite in WAL mode through EF Core, `synchronous` left at FULL so an acknowledged batch is on disk. Writers are serialised by
one in-process gate (SQLite allows one, and a read-then-write transaction can be refused if another writer commits between);
that is also why the design is a single instance.

| Table | Holds |
|---|---|
| `Entities` | `Id`, `Version`, `Payload`, `PayloadChecksum`, `IsPublished`, the override columns, timestamps, a concurrency token. A **partial index** on `Id` where published and not disabled serves the user listing as an ordered range read (a composite index on the two flags is never used by EF's boolean filter; the plan is tested) |
| `Tombstones` | `Id`, the newest delete time: a high-water mark, with no entity data |
| `Batches` | one row per request: state, attempts, next attempt, last error kind, takedown flag, queued payload bytes. The index (`Status`, `HasTakedown` descending, `Id`) is exactly the pick order and serves the queue-size sums as index seeks |
| `Events` | one row per event received, rejected ones included: outcome, reason, checksum, times, and the payload **only while queued** |

- **Read and write contexts.** The read context is no-tracking, opened `Mode=ReadOnly`, and throws on save; the write context
  is the only one that saves. `Database:ReadConnectionString` could point at a replica.
- **Hard delete is physical (for the live file).** SQLite by default leaves the bytes of a deleted or overwritten value in the
  file, even after a checkpoint (measured, and reproduced as a control in `StorageGuaranteeTests`). `secure_delete=ON`, set on
  every connection by an interceptor, wipes them; a test then deletes, replaces and nulls payloads and scans the file and its
  WAL for a marker.
- Only the latest version of an entity is stored. All timestamps are UTC (a converter restores the kind on read).

## 9. API and access

| Route | Anonymous | CMS | User | Admin |
|---|---|---|---|---|
| `GET /health` | 200 | 200 | 200 | 200 |
| `POST /cms/events` | 401 | 200 / 202 | 403 | 403 |
| `GET /cms/batches/{id}` | 401 | 200 / 404 | 403 | 200 / 404 |
| `GET /api/entities`, `GET /api/entities/{id}` | 401 | 403 | 200 | 200 |
| `POST /api/entities/{id}/disable` and `/enable` | 401 | 403 | 403 | 200 / 404 |
| `GET /api/admin/ingestion` | 401 | 403 | 403 | 200 |
| `POST /api/admin/ingestion/{id}/requeue` | 401 | 403 | 403 | 200 / 404 / 409 |

The asset routes (section 11) follow the same pattern: `PUT`/`POST`/`DELETE` under `/cms/assets` for the CMS only; `GET /cms/assets/{id}` for the CMS and admins; `GET`/`HEAD /api/assets/{id}` for users and admins (clean assets only); `DELETE /api/assets/{id}` and `/api/admin/assets/*` for admins only; each has a row in the generated matrix. (The reset endpoint exists only in Development, or where an environment is named on purpose.) A test generates the role-by-route matrix from the mapped endpoints and
fails if a route has no row; another proves no route updates or deletes an entity. Users see published, non-disabled entities;
admins see everything plus a `status` block; the server decides from the caller's role only, and no query parameter widens it.
A hidden entity gives the same `404` as a missing one. Listing is keyset-paged (default 50, max 100), projects to a DTO, tracks
nothing, uses no `Include`, writes the stored payload text raw, and costs a constant number of queries per page.

**Security measures.** Deny by default (a fallback policy requires sign-in; only `/health` is anonymous). Credentials are
compared in constant time (both sides hashed, every account always evaluated). Passwords may contain colons (split at the first).
A per-address failed-sign-in throttle answers `429` with an injected clock in tests. Credentials live only in the development
settings file or the environment; missing or malformed ones stop startup, naming the setting. No payload, password or
`Authorization` header is logged (tested across success, rejection, 401 and failure paths), and the failed-sign-in log omits the
submitted username. Responses carry `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and a restrictive CSP.
Production enables HTTPS redirection and HSTS; Basic auth is only safe over TLS.

## 10. Observability and alerts

One log line per event with batch id, index, id, type, version, outcome, reason and checksum, written once the outcome is
final: applied and duplicate at Information; stale, unsupported, rejected and applied-by-conflict at Warning; a failed batch at
Error. The batch id is the `X-Correlation-Id` header, a property of every log line and the key in `Batches` and `Events`. The
meter `Lateral.Cms` has counters (events processed by outcome and type, queued, retries, set-asides), histograms (accept and
apply duration, queue lag) and gauges (pending batches, oldest pending age, failed batches); tags are bounded, so an unknown
type string cannot create a new series.

**Alerts an operator needs.**

| Signal | Why |
|---|---|
| `cms.queue.oldest_age` growing, or `cms.queue.pending` not draining | the worker is stuck or too slow: a queue that quietly falls behind is the main risk of asynchronous processing |
| `cms.queue.failed` > 0 | a batch was set aside and an admin must look and requeue it |
| Error log "holds an unPublish or delete and failed" | a takedown is not applying: **its entity stays visible to users until it does** |
| `429 inbox_full` responses | senders are being pushed back; the queue is at its cap (failed batches count against it) |
| No batches accepted for longer than the CMS's normal quiet period | **the CMS has gone quiet** (webhook broken, credentials rotated, network): nothing in this service can detect it, so it needs an alert on `cms.events.queued` |
| Worker Error "hit an unexpected error" | the database is unreachable or full |
| Error "The database connection failed." | a connection could not be opened (the line carries the exception; EF's own line for this event is switched off because it omits it). A connection open cancelled by the app stopping is not logged: that is a normal stop |

## 11. Assets and scanning

The brief's events carry fields, never files (S16), so a large file needs its own path. The asset store is that path: a local
approximation of what an object store such as S3 gives you (several object sizes, multipart upload, ranged and parallel
download, checksums and ETags, hard delete), with one rule above all: **nothing that was uploaded is served until a scanner has
cleared it.** It is standalone: an event's payload may *name* an asset id, but the reconciliation rules and the entity API do not
look at assets (S20).

**Model.** `Assets` (id, the client's key, claimed type, detected type, size, SHA-256, state, verdict, signature, scanner name and
version, attempts, next attempt), `Uploads` and `UploadParts` for multipart uploads in progress, and `AuditLog`. **Bytes live on
disk, never in a table**, behind one seam, `IAssetStore`: `parts/<upload>/<n>` while an upload is in progress,
`quarantine/<asset>` until the scan, `blobs/ab/cd/<sha256>` once clean. Content-addressed means identical content is stored once;
it is removed only when the last asset using it goes. Files are written to a temp name, flushed to disk and renamed into place, so
a crash never leaves a half-written object under its real name.

| State | Meaning | Served? |
|---|---|---|
| `quarantined` | stored, waiting for (or being retried by) the scanner | never |
| `clean` | cleared by the scanner; bytes are under their content address | yes, to users and admins |
| `rejected` | flagged; bytes overwritten and removed; only the verdict remains | never |
| `failed` | scanning kept failing and was set aside; bytes stay in quarantine | never |

**Upload.** `PUT /cms/assets/{key}` takes one object up to `Assets:MaxSimpleBytes` (8 MiB), streamed to disk and never buffered.
Larger objects use the S3 shape: `POST /cms/assets/uploads` (declared size), `PUT .../parts/{n}` (5 to 64 MiB, any order, any
number in parallel, each checked against its `X-Content-SHA256`, sending a part again replaces it), `POST .../complete` with the
part list. Completion verifies the parts, the declared total and the assembled hash, assembles by streaming, and quarantines the
result. Declared lengths over a limit are refused with `413` before a byte is read; chunked bodies are cut off as they are read.
A total cap (`Assets:MaxBytes`, counting parts in flight) answers `429` with nothing stored. Unfinished uploads expire; a sweeper
removes expired uploads and any file no row accounts for, but never a file younger than `Assets:SweepGrace`. File names are only
ever ids and digests, so a client-supplied key cannot steer a path (tested with traversal and encoded separators).

**Download.** `GET`/`HEAD /api/assets/{id}` streams the file (async, sequential, never loaded whole: a test serves 64 MiB while
the process allocates a small fraction of that) with `Range` (single, suffix and open-ended), `If-Range`, a strong `ETag` that is
the SHA-256, and `If-None-Match` for `304`. That is what makes parallel download work: a client opens many range requests at once
and reassembles them, and can resume after a failure. A per-caller cap on concurrent downloads stops one client holding every
file handle. Responses are always an attachment, typed by what the scanner **detected** (never what the sender claimed), with
`nosniff`, `Content-Security-Policy: default-src 'none'; sandbox` and `no-store`. A missing asset and one that is not clean are the
same `404` (S7).

**Scanning.** `IAssetScanner` is the seam; the built-in `MockAssetScanner` is a deterministic, harmless stand-in (it is **not**
protection, K19). It reads the bytes in chunks and recognises: its own test signature (and, if switched on, the industry EICAR
string, which is off by default because antivirus on a developer machine deletes it); executables by magic bytes, flagged when
bare or when dressed as another type; a declared image or PDF type that the bytes contradict; script, event handlers and
`foreignObject` in SVG and HTML, and JavaScript, `OpenAction` and `Launch` in PDF; ZIP bombs, judged from the central directory
alone without decompressing anything; and, only when `Assets:Mock:EnableFailureMarkers` is on, content that simulates a scanner
that errors or hangs, so the failure paths run for real.

A worker takes the oldest due quarantined asset, scans it **outside the write gate** (a slow scan never holds up ingestion), then
applies the verdict in one transaction under the gate:

- **Clean:** the file moves to its content address and the asset becomes servable.
- **Infected or suspicious:** the verdict is recorded first (nothing is marked servable while its bytes go), then the quarantined
  bytes and the blob are overwritten with zeros and removed. Other assets with the same content are rejected with it. The row keeps
  the verdict, rule name, checksum and times: no content.
- **Anything else fails closed (S21).** A scanner error, a timeout (`Assets:ScanTimeout`), a missing file or an exception leaves the
  asset quarantined and unserved, counts as a failed attempt, and retries with doubling back-off; after `Assets:ScanMaxAttempts` it
  is set aside as `failed` (still unserved, bytes kept for inspection) until an admin requeues it, and a requeue goes through the
  scan again. Nothing in these paths can produce `clean`.

The scanner stamps its version on every verdict, so when its rules change `POST /api/admin/assets/rescan-outdated` puts every
asset cleared by an older version back in quarantine (and out of service) until it is cleared again; an admin can also re-scan one
asset. To use a real engine, implement `IAssetScanner` (for example ClamAV over `clamd` INSTREAM) and register it; upload,
quarantine, wipe, retry and download do not change.

**Hard delete.** `DELETE /api/assets/{id}` removes the row and overwrites the bytes with zeros before removing them, **even if a
reader has the file open** (some platforms refuse to delete an open file; the zeros are already written and the sweeper removes
the file). Limits: overwriting a file is best effort on SSDs and copy-on-write file systems, and says nothing of backups or
snapshots (K6). Unlike the SQLite file, asset bytes are not covered by `secure_delete`; the explicit overwrite is the mechanism.

**Audit.** Uploads, verdicts, deletes, re-scans and the admin actions on entities and the queue write an `AuditLog` row in the
same transaction as the change: who, what, which target, never content. The schema refuses `UPDATE` and `DELETE` on the table
(triggers), and the test-only reset keeps it (SH-05).

## 12. Security review

What a reviewer would conclude about the design as built, from a deliberate attempt to break it.

**Secure by design (and tested):** deny by default (a fallback policy; a role-by-route matrix generated from the mapped endpoints,
so an unprotected route fails a test); least privilege and separate accounts (the CMS cannot read, users cannot write, only
admins administer); constant-time credential comparison with every account evaluated; a failed-sign-in throttle; fail-closed
`404`s for anything hidden; no payload, password or `Authorization` header in any log; strict input bounds everywhere (declared
length before reading, depth, batch, payload, ids, timestamps, parts, objects, concurrent accepts and downloads); only
parameterised EF queries; a read-only read context; physical delete in the database (`secure_delete`) and for asset bytes; startup
refusing missing or malformed credentials, proxy settings and asset limits; HSTS and HTTPS redirection in Production;
`no-store`, `nosniff` and a CSP everywhere, and a sandbox CSP and attachment disposition on every download; payload canonical form
that escapes HTML-sensitive characters; uploaded content never served until scanned, and every failure of the scan leaves it
unserved.

**Weaknesses the review found and this build closed** (each has a requirement and a test that fails if it is undone):

| Finding | Closed by |
|---|---|
| Development passwords were committed to the repository (SH-01) | Settings files hold none; Development generates random ones into a git-ignored file on first run, only when none were supplied. *The earlier commit's history still contains the old development passwords; they no longer work.* |
| An authenticated sender could make the service buffer many 5 MiB bodies at once (SH-02) | Only `Ingestion:MaxConcurrentAccepts` batches are read at once; a bounded queue absorbs bursts; overflow gets `429` |
| Behind a proxy every client shared one address, so the throttle either locked everyone out or could be dodged with a forged header (SH-03) | `X-Forwarded-For` is believed only from configured proxies and ignored otherwise |
| The reset endpoint was refused only in `Production`, so any other environment name could enable it (SH-04) | An allow-list: Development, or an environment named on purpose |
| Admin actions were only log lines (SH-05) | An append-only, same-transaction audit log |
| Set-aside batches could quietly fill the queue (SH-06) | Their bytes are in the report, a gauge and the refusal log |
| The test console holds live credentials and could run anywhere (SH-07) | It refuses to start outside Development unless told to |

**Still open, in order of importance** (needs decisions or infrastructure beyond a local build; the next steps are in the limits
table): (1) **Basic authentication with secrets in configuration** over whatever transport the deployment provides (K4, K5): move to
a secret store, hashed credentials, TLS at the edge and signed requests for the CMS, which would also give replay protection;
(2) **a stolen CMS credential can do real damage**: a delete timestamped up to an hour ahead blocks that id until then, and
checksums are integrity, not authenticity (K10); (3) **no encryption at rest** for the database or asset files, and the SQLite
file and asset folder rely on the operating system's permissions (K6); (4) **a mock scanner is not malware protection** (K19):
real protection needs a real engine, and defence in depth for whoever consumes the files; (5) dependency checking is only a vulnerable-package check in CI, with
no SAST, SBOM or signed builds (K16); (6) payload content is served as JSON for consumers that must escape it before rendering (S14).

**Verdict.** Secure by design in its principles (deny by default, fail closed, least privilege, keep and serve as little as
possible), and the review's concrete weaknesses are closed. **Not production-ready as shipped:** the authentication, secret
handling and scanner are demo-grade, which is stated rather than hidden.

## Limits

What this build knowingly leaves at demo quality, in order of importance, with what production needs.

| | Area | The demo does | Production needs |
|---|---|---|---|
| K1 | Scale-out | SQLite file; one writer; an in-process gate; one instance | A server database; several workers claiming batches (`SELECT … FOR UPDATE SKIP LOCKED`); no gate; a shared throttle |
| K8 | Ordering | Correct only under S17; both counterexamples pinned | A CMS-assigned monotonic sequence per entity, or deletes that carry a version |
| K4 | Credentials | Plain text in configuration; Basic auth as the brief requires; per-instance throttle | A secret store, stored hashes, request signing for the CMS, OIDC for people, gateway rate limiting |
| K6 | Deletion | The live file is wiped on delete. No encryption at rest, no retention for events, tombstones or batches. Event rows keep an erased entity's id and its payload checksum | Backups, replicas and WAL archives under the erasure policy; encryption at rest; crypto-shredding; retention and a purge job |
| K7 | Recovery | No resync or replay; a dropped batch is visible only as a gap | A sequence or offset from the CMS, a replay endpoint, periodic reconciliation by checksum |
| K3 | Queue | A table in the same database; one worker; takedown priority per batch (a flood of takedowns could starve publish-only batches) | A broker or partitioned queue keyed by entity id; every Nth batch oldest-first |
| K2 | Capacity | Throughput measured on one machine, no soak | Load, soak and chaos tests on production-like hardware; SLOs |
| K5 | Transport | TLS assumed at the edge; redirect and HSTS only in Production | Enforced TLS, forwarded-header handling, certificate management |
| K9 | Idempotency | A retried batch is stored and processed again (each event reports `ignored_duplicate`); no completion callback | An idempotency key per batch; a callback or event |
| K10 | Integrity | Checksums detect corruption, not tampering; the form follows the .NET encoder | An HMAC or signatures; RFC 8785 canonical JSON |
| K11 | Payload storage | Inline `TEXT` up to 256 KiB; larger content goes to the asset store, which is a folder on local disk behind `IAssetStore` | An object store behind the same interface, with the durability and lifecycle features a folder does not have |
| K12 | Operations | A meter and structured logs; alerts only listed here | OpenTelemetry export, dashboards, alert rules as code, runbooks |
| K13 | API surface | Keyset paging by id only; no filtering, sorting, search, ETags or versioning | Those, and an API version policy |
| K14 | Admin audit | An append-only `AuditLog` written in the same transaction as each admin, asset and verdict change; the schema refuses updates and deletes. No retention policy, no tamper evidence beyond the schema, sign-ins not recorded | A hash-chained or externally shipped audit trail, sign-in and failure events, retention and access rules |
| K15 | Portability | Only SQLite exercised; the read-only mode and partial index are SQLite specifics | A second provider in CI and a migration strategy |
| K16 | Verification | The deliberate-defect log is manual; a CI job fails on known-vulnerable packages (run once locally: none); no soak, chaos, SAST or DAST | Automated mutation testing scoped to `Domain` and the parser, dependency scanning, a threat model |
| K17 | Developer tooling | The console and reset endpoint exist for hand-testing and are fenced | Not shipped to production |
| K18 | Tenancy | One CMS, one tenant | Isolation, per-tenant keys and quotas |
| K19 | Content scanning | A mock scanner that recognises obvious test patterns, to prove the pipeline (quarantine, verdict, wipe, retry, fail closed). It is **not** malware protection | A real engine behind `IAssetScanner` (ClamAV or a vendor service), signature updates with re-scan of old assets (supported), sandboxed rendering and a policy on which types are accepted at all |
| K20 | Asset store | One folder on one disk: no replication, erasure coding, versioning, lifecycle rules, pre-signed URLs or server-side encryption; overwrite-before-delete is best effort on SSDs and copy-on-write systems; assets are not covered by `secure_delete` | An object store (S3 or similar) with encryption and per-object keys; crypto-shredding for erasure |
| K21 | SQLite connection pool | Microsoft.Data.Sqlite 9 has a race in its pool: a connection is marked active before its owner is recorded, so a second thread that finds the pool empty can take it as "leaked" and fail the first with `database is locked` (seen as a rare HTTP 500 under many parallel requests). Worked around by opening every connection under one lock (`SerialOpenInterceptor`, tests `ConnectionPoolTests`) | Remove the workaround once the library fixes it (re-check on each upgrade of the SQLite package), or move to a server database where the pool is not the library's |

**.NET 9** leaves support on 10 November 2026. The projects target `net9.0` with `RollForward=Major`, so they build with a 9 SDK
and run on a 9 or newer runtime; `net10.0` is a one-line change in `Directory.Build.props` once the 9 SDK need goes away.

**If this went to production, in this order:** server database and several workers (K1, K3); secrets, signed requests, TLS (K4,
K5); an erasure and retention policy including backups (K6); sequence numbers and resync from the CMS (K7, K8); the observability
stack and alerts (K12); load, soak and chaos tests, then a security review (K2, K16); idempotency keys and callbacks (K9).

**Explicit non-goals:** search, filtering and sorting; outbound webhooks; pre-signed URLs and the rest of S3's feature set beyond what
section 11 lists; SSO and user management; multi-tenancy; an event replay API; a user interface beyond the developer console.
