# Requirements

Every requirement the service answers to, its priority, the kind of evidence it needs, and the tests that prove it. The matrix
comes from the rebuild brief ([REBUILD-BRIEF](REBUILD-BRIEF.md), section 5); this file is where it is kept true.

- **Pri:** MUST = the brief or correctness; SHOULD = cheap robustness; COULD = nice to have.
- **Evidence** codes: S static (warnings as errors) · U unit · P property (permutations, oracle) · I integration (real HTTP and
  SQLite, rows checked with raw SQL) · F fault injection (a real fault, no test hook) · C concurrency · Q query counts and plans ·
  A authorisation matrix · L log content · V golden or contract · M mutation (a deliberate defect was caught) · B benchmark ·
  O operated (CI, README run) · D documented.
- **Limit:** the known limitation in [DESIGN](DESIGN.md#limits) that bounds the requirement.
- **Mutations:** deliberate defects, from [tools/mutation-check](../tools/mutation-check/mutations.json), that a test caught; the
  results are in [EVIDENCE](EVIDENCE.md).
- **Tests:** generated. Each test names the requirements it proves with `[Trait("req", "EV-05")]`. `DeliveryTests` fails if the
  column is stale (regenerate it with `UPDATE_REQUIREMENTS=1 dotnet test --filter DeliveryTests`, then run `node tools/requirements/build.mjs` so the console's Requirements page follows), if a MUST requirement that
  needs tests has none, if a test names an unknown requirement, or if an `M` requirement names no caught mutation.
- **Status:** `Done` means built, tested to the evidence level, and the claim in the row is true today. Anything else says what is missing.

### Event semantics

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| EV-01 | A publish for an unknown id creates it: published, the event's version, payload stored whole and never merged | MUST | U I | - | Done | - | `IngestionEndpointTests` (31), `ReducerTests` (2) |
| EV-02 | A publish with a higher version replaces version, payload and checksum. A lower version is `ignored_stale` (`older_version`). Equal version and newer timestamp applies (state can flip back to published). Equal version and older timestamp is `ignored_stale` (`older_timestamp`) | MUST | U I M | - | Done | M03 | `IngestionEndpointTests` (31), `ReducerTests` (4) |
| EV-03 | An exact duplicate (version, instant, kind, content) is `ignored_duplicate`; replaying a whole stream changes nothing | MUST | U P I | - | Done | - | `AcceptanceTests` (1), `ReducerPropertyTests` (1), `ReducerTests` (2) |
| EV-04 | An unPublish keeps the row and payload and marks it unpublished; users stop seeing it, admins still do | MUST | U I | - | Done | - | `IngestionEndpointTests` (31), `ReducerTests` (1) |
| EV-05 | An unPublish for an id never seen (X, then X+1 unpublished with no publish between) creates the entity from the event's payload, unpublished | MUST | U I M | - | Done | M36 | `IngestionEndpointTests` (31), `ReducerTests` (2) |
| EV-06 | A delete removes the entity row and payload and leaves a payload-free tombstone (id, newest delete time). A raw-SQL check finds no `Entities` row and no content in the tombstone | MUST | U I M | - | Done | M37 | `AcceptanceTests` (1), `IngestionEndpointTests` (31), `ReducerTests` (1), `StorageGuaranteeTests` (1) |
| EV-07 | Events at or before the tombstone are `ignored_stale` (`superseded_by_delete`); a newer publish recreates the entity with the admin override cleared | MUST | U P I M | - | Done | M01 M31 | `IngestionEndpointTests` (31), `ReducerTests` (2) |
| EV-08 | Delete edge cases: nothing stored gives `tombstone_only`; older than the stored state gives `newer_state_exists` but still raises the tombstone; at or below the tombstone is duplicate or stale | MUST | U P M | - | Done | M05 M32 | `ReducerTests` (4) |
| EV-09 | Order independence: every arrival order of a timeline ends in the same state. Evidence: the set-based oracle (Appendix A) on at least 20,000 seeded random timelines per run, and every permutation of at least 10 curated timelines of up to 7 events | MUST | P M | K8 | Done | M02 | `DomainArchitectureTests` (2), `ReducerPropertyTests` (4), `ReducerTests` (1) |
| EV-10 | Deterministic ties: unPublish beats publish at equal version and instant; contradicting content at equal version, instant and kind resolves to the greater payload text with `same_version_different_content` | MUST | U P M | - | Done | M02 M04 | `ReducerTests` (3) |
| EV-11 | Version is an integer ≥ 1; gaps are allowed; the first version is 1 | MUST | U | - | Done | - | `EventParserTests` (23) |
| EV-12 | Batch-partition independence: splitting a stream into batches any way, and applying the batches in any order, gives the same final state | MUST | P I | - | Done | - | `AcceptanceTests` (2), `ReducerPropertyTests` (1) |
| EV-13 | An unknown event type is reported per event (`ignored_unsupported_type`), gets an `Events` row, and never fails the batch | MUST | I | - | Done | - | `AcceptanceTests` (1), `IngestionEndpointTests` (31) |
| EV-14 | The entity keeps the latest version; a lower or equal version never overwrites; a stale concurrent write is refused (concurrency token) and the attempt fails and retries | MUST | U I F | - | Done | M11 | `PersistenceGuaranteeTests` (4), `ReducerTests` (1) |
| EV-15 | The admin override is its own column. CMS events never write it. A later publish does not clear it; only delete then re-create does | MUST | U I | - | Done | M06 | `EntitiesEndpointTests` (11), `OverrideAndPolicyTests` (2), `ReducerTests` (2) |
| EV-16 | The ordering assumption (S17) is stated exactly in DESIGN, and the counterexample in §2.4 item 2 is pinned by a test that asserts the divergence. (Could) an applied higher version with an earlier timestamp is logged with reason `timestamp_regression` | MUST / COULD | P D | K8 | Done; the optional `timestamp_regression` log is not built | - | `ReducerPropertyTests` (3) |

### Ingestion

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| IN-01 | `POST /cms/events` takes a JSON array; only the CMS account may call it | MUST | I A | - | Done | - | `BasicAuthTests` (18), `IngestionEndpointTests` (31), `RoleMatrixTests` (2) |
| IN-02 | Basic auth. The CMS username is 10 to 20 characters and each CMS password parses as a GUID, both checked at startup (the service refuses to start otherwise). Several passwords may be active so one can be rotated without downtime | MUST | I | K4 | Done | - | `AuthOptionsTests` (12), `StartupValidationTests` (3) |
| IN-03 | Per-event validation gives the stable reason codes in §4.5, with a boundary-pass and a boundary-fail input for every rule (id 200 and 201 characters, version 0 and 1, 7 and 8 fractional digits, the skew limit) | MUST | U V | - | Done | - | `EventParserTests` (23) |
| IN-04 | One bad event never blocks its neighbours. The response lists every event's outcome, reason and checksum. A batch holding only rejected events is `200` | MUST | I | - | Done | - | `AcceptanceTests` (3), `IngestionEndpointTests` (31) |
| IN-05 | Sanitising is normalising: trim ids, UTC timestamps, compact canonical payload, control characters rejected in ids, untrusted type text masked and bounded. Values are never altered or HTML-escaped | MUST | U V | - | Done | - | `EventParserTests` (23) |
| IN-06 | Limits: a declared `Content-Length` over the cap is `413` *before any body is read* (a stalled declared 10 GiB body is refused without waiting); batch of 501 is `413`; payload over 256 KiB rejects that event; depth over 32 rejects the request; invalid JSON or a non-array is `400` | MUST | I M | - | Done | M08 | `AcceptanceTests` (1), `IngestionLimitsTests` (5) |
| IN-07 | Every validated publish and unPublish gets a checksum over its canonical text. It is returned in the response, stored with the entity, served by the API and recorded in the ledger. A test recomputes every stored row after a mixed workload and finds no mismatch | MUST | U V I M | K10 | Done | M07 | `AcceptanceTests` (1), `ChecksumEndpointTests` (12), `PayloadChecksumTests` (7) |
| IN-08 | Status codes and `Retry-After` exactly as the §4.5 table; errors are problem+json with a stable `code` and never echo payloads | MUST | I V | - | Done | - | `IngestionEndpointTests` (31) |

### Asynchronous processing

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| AS-01 | The answer follows a durable commit. A database failure at accept gives `503` and stores nothing; a `202` is never sent first | MUST | F I M | K1 | Done | M25 | `AcceptanceTests` (1), `IngestionEndpointTests` (1) |
| AS-02 | Accepting is one transaction; applying is one transaction (state, outcomes, payload purge, batch completion). A failure, crash or shutdown leaves the batch exactly as it was; nothing is half applied | MUST | F M | - | Done | M15 | `InboxFailureTests` (1) |
| AS-03 | A background worker applies batches, takedown batches first then oldest; it wakes on accept and polls when idle | MUST | I M | K3 | Done | M17 M40 | `AcceptanceTests` (2), `InboxWorkerTests` (7) |
| AS-04 | A failing batch is retried with doubling back-off (2 s up to 60 s), set aside as `failed` after 5 attempts without blocking later batches, and can be requeued by an admin. The worker survives any exception and a storage outage | MUST | F I M | - | Done | M18 M19 M22 M23 M24 M26 | `InboxFailureTests` (9) |
| AS-05 | The queue is bounded by `Inbox:MaxBytes`: past it, `429 inbox_full` + `Retry-After` and nothing stored; concurrent senders cannot get past the cap | MUST | C I M | - | Done | M20 M21 | `AcceptanceTests` (1), `InboxBackpressureTests` (4) |
| AS-06 | `GET /cms/batches/{id}` (CMS and admin) returns `queued`, `completed` or `failed` with per-event outcomes; never payloads; unknown id is `404` | MUST | I A L | - | Done | - | `AcceptanceTests` (4) |
| AS-07 | `GET /api/admin/ingestion` reports pending batches and events, oldest age, bytes used and failed batches; requeue follows §4.2 | SHOULD | I A | - | Done | - | `InboxFailureTests` (9) |
| AS-08 | Batches accepted before a restart are applied after it, once | MUST | F | - | Done | - | `InboxWorkerTests` (7) |
| AS-09 | A queued payload exists only in its `Events` row and is nulled in the apply transaction; the `CHECK` makes any other state impossible; a delete leaves no copy of an earlier publish in the queue or the ledger | MUST | I V M | K6 | Done | M14 M16 | `AcceptanceTests` (3), `StorageGuaranteeTests` (1) |
| AS-10 | Accepting reads no entity state; applying reads it in two queries however large the batch; the pick query uses its index with no sort | MUST | Q | - | Done | - | `AsyncQueryCountTests` (2), `InboxIndexTests` (2) |
| AS-11 | DESIGN states the choice and why: alternatives (synchronous, in-memory async, durable async), trade-offs, who calls and what each expects, the measured throughput, and what the queue does *not* do (it does not raise the single-writer ceiling) | MUST | D | K1 K2 | Done | - | - |

### Storage

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| ST-01 | EF Core on SQLite; one migration creates the final schema; applied at startup unless switched off | MUST | I | K15 | Done | - | `StartupValidationTests` (3), `StorageGuaranteeTests` (1) |
| ST-02 | Separate write and read contexts. The read context is no-tracking, opened read-only (`Mode=ReadOnly`) and throws on `SaveChanges`. The write context is used by ingestion and the admin override only | MUST | I M | - | Done | M10 | `PersistenceGuaranteeTests` (4) |
| ST-03 | WAL mode; writers serialised by one in-process gate; `synchronous` left at FULL; concurrency token renewed on every save | MUST | I C M | K1 | Done | M11 | `ConnectionPoolTests` (2), `StorageGuaranteeTests` (1) |
| ST-04 | All timestamps are UTC and come back with `Kind = Utc` | MUST | U I | - | Done | - | `StorageGuaranteeTests` (1) |
| ST-05 | The schema enforces what code assumes: the payload `CHECK`, unique (`BatchId`, `Sequence`), unique `BatchId`. A raw-SQL insert that breaks one fails | SHOULD | I | - | Done | - | `AcceptanceTests` (1), `StorageGuaranteeTests` (2) |
| ST-06 | `secure_delete=ON` on every connection. After a delete (and after a payload is nulled) plus `wal_checkpoint(TRUNCATE)`, a unique marker string is absent from the database file and the WAL | SHOULD | I M | K6 | Done | M13 | `StorageGuaranteeTests` (3) |
| ST-07 | Payload stored inline, at most `MaxPayloadBytes`; media is referenced (address, type, size, checksum), never carried | MUST | I | K11 | Done | - | `IngestionLimitsTests` (5) |

### Read API and admin override

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| AP-01 | `GET /api/entities` (keyset cursor, default 50, max 100, bad cursor is `400`) and `GET /api/entities/{id}` | MUST | I | K13 | Done | - | `EntitiesEndpointTests` (11) |
| AP-02 | Users see published and not-disabled entities. Admins see everything, with a `status` block. Same endpoints; the server decides from the caller's role only; no query parameter widens it | MUST | I A M | - | Done | M33 M34 | `EntitiesEndpointTests` (11) |
| AP-03 | A hidden entity returns the same `404` as a missing one | MUST | I | - | Done | - | `EntitiesEndpointTests` (11) |
| AP-04 | No route updates, replaces or deletes an entity. The only entity writes are admin disable and enable (idempotent, logged with who). A route-inventory test proves it | MUST | I A | - | Done | - | `RouteAndTransportTests` (2) |
| AP-05 | Disable changes only the override columns; payload, version and published flag are untouched and the CMS is not involved | MUST | I | - | Done | - | `EntitiesEndpointTests` (11) |
| AP-06 | Reads project to a DTO, track nothing, use no `Include`, write the stored payload text raw, use the partial index with no sort, and cost a constant number of queries per page | MUST | Q M | - | Done | M12 | `ListingIndexTests` (3), `QueryCountTests` (3) |
| AP-07 | `/health` is anonymous and returns only `{"status":"ok"}` (or 503) after a database check | MUST | I | - | Done | - | `BasicAuthTests` (1) |
| AP-08 | Responses carry `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and a restrictive CSP | SHOULD | I | - | Done | - | `BasicAuthTests` (1) |

### Security

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| SE-01 | Deny by default: a fallback policy requires sign-in; only `/health` is anonymous. A test enumerates every mapped route and fails if one has no row in the role matrix | MUST | A M | - | Done | M29 | `OverrideAndPolicyTests` (1), `RoleMatrixTests` (2) |
| SE-02 | Least privilege exactly as the §4.6 table: the CMS cannot read entities; users and admins cannot ingest; only admins disable, enable and administer the queue | MUST | A M | - | Done | M27 M28 | `BasicAuthTests` (18), `RoleMatrixTests` (2) |
| SE-03 | Credentials are compared in constant time (hash both sides, `FixedTimeEquals`, every account always evaluated). `401` carries `WWW-Authenticate: Basic` | MUST | I U | K4 | Done | - | `BasicAuthTests` (18) |
| SE-04 | Basic-auth valid and invalid combinations are covered (§6.3, suite T9) | MUST | I | - | Done | - | `BasicAuthTests` (18) |
| SE-05 | A failed-sign-in throttle per client address answers `429` + `Retry-After`, with an injected clock | SHOULD | I | K4 | Done | - | `AuthThrottleTests` (3) |
| SE-06 | No credentials in `appsettings.json`; development credentials only in `appsettings.Development.json`; missing or malformed credentials stop startup and name the setting | MUST | I | K4 | Done | - | `AuthOptionsTests` (12), `DeliveryTests` (1), `StartupValidationTests` (3) |
| SE-07 | No payload, password or `Authorization` header in any log, on success, rejection, 401 and failure paths; the login-failure log omits the submitted username | MUST | L M | - | Done | M30 | `AcceptanceTests` (1) |
| SE-08 | Production enables HTTPS redirection and HSTS; the docs say Basic auth is only safe over TLS | SHOULD | I D | K5 | Done | - | `RouteAndTransportTests` (2) |
| SE-09 | Hostile input is bounded: size, count, depth, strict UTF-8, control characters, far-future timestamps, lone surrogates | MUST | U I | - | Done | - | `EventParserTests` (23) |
| SE-10 | Admin actions are logged with who and what | MUST | L | K14 | Done | - | `EntitiesEndpointTests` (11) |

### Observability

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| OB-01 | Every event is logged, failing ones included, with batch id, index, id, type, version, outcome, reason and checksum. Applied and duplicate: Information. Stale, unsupported, rejected and applied-by-conflict: Warning. A failed batch: Error | MUST | L M | - | Done | M38 M39 | `AcceptanceTests` (1), `ObservabilityTests` (2) |
| OB-02 | The batch id is the `X-Correlation-Id` header, a property of every log line and the key in `Batches` and `Events` | MUST | I L | - | Done | - | `ObservabilityTests` (1) |
| OB-03 | `Events` is the ledger: a row per event received, rejected ones included, with `ReceivedAt` and `ProcessedAt`; no payload once decided | MUST | I | K6 | Done | - | `IngestionEndpointTests` (1) |
| OB-04 | Meter `Lateral.Cms` with bounded tags: events processed (outcome, type), queued, apply and accept duration, queue lag, retries, set-asides, and gauges pending, oldest age and failed | SHOULD | I | K12 | Done | - | `ObservabilityTests` (2) |
| OB-05 | Worker health is visible: a log when a batch is applied (with its wait), a Warning per failed attempt, an Error when a batch is set aside or the worker faults | MUST | L | - | Done | - | `InboxFailureTests` (9), `InboxWorkerTests` (7), `ShutdownTests` (1) |
| OB-06 | DESIGN lists the alerts an operator needs, including "the CMS has gone quiet" | SHOULD | D | K12 | Done | - | - |

### Testing and delivery

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| TE-01 | Event-processing tests as §6.3 suites T1, T2, T6 | MUST | - | - | Done (aggregate of T1, T2, T6) | - | - |
| TE-02 | Ingestion-constraint tests as §6.3 suites T3, T5 | MUST | - | - | Done (aggregate of T3, T5) | - | - |
| TE-03 | Basic-auth tests as §6.3 suite T9 | MUST | - | - | Done (aggregate of T9) | - | - |
| TE-04 | The suite is hermetic, finishes in under 60 s, and passes 20 consecutive full runs | MUST | O | - | Done: 20 consecutive full runs passed, well inside the limit (the count and timings are in [EVIDENCE](EVIDENCE.md#1-build-and-tests)) | - | - |
| TE-05 | Every MUST row names at least one test through `[Trait("req","ID")]`; a meta-test fails if one does not | SHOULD | D | - | Done | - | `DeliveryTests` (3) |
| TE-06 | A mutation log in `docs/EVIDENCE.md`: at least 25 deliberate defects (§6.4), each caught by a named test | MUST | M | K16 | Done: every deliberate defect in the log is caught (the log and its count are in [EVIDENCE](EVIDENCE.md#2-deliberate-defects-mutation-log)) | - | `DeliveryTests` (2) |
| TE-07 | A load smoke (§6.5) is in the repo and its results are recorded with machine and date | SHOULD | B | K2 | Done: [EVIDENCE](EVIDENCE.md#3-measurements) | - | - |
| DL-01 | A .NET 9 solution (`net9.0`, `RollForward=Major` so it runs where only a newer runtime is installed) | MUST | S | - | Done | - | `DeliveryTests` (1) |
| DL-02 | README: prerequisites, run, a try-it flow that includes following a batch, tests, configuration, running outside Development, database and migrations, ranked limitations | MUST | D O | - | Done; the try-it commands were run against the built service ([EVIDENCE](EVIDENCE.md#4-operated))| - | - |
| DL-03 | A CI job runs the README's try-it commands against the built service | SHOULD | O | - | Done: the `README try-it` job passed in CI on all three pushes | - | - |
| DL-04 | CI builds and tests on Windows, macOS and Linux with warnings as errors; no OS-specific paths | MUST | O | - | Partly: CI passes on Windows and Ubuntu, the suites and a smoke test also pass by hand on Debian 13, and macOS passed on CI runs 1 and 2 but failed its Test step on run 3 (cause not yet read) | - | - |
| DL-05 | A GitHub repository holds all of the above. The remote is the user's decision; never push unasked | MUST | - | - | Done: pushed to github.com/pahayward/LateralCMS | - | - |
| DL-06 | Five docs with one job each (D7); relative links resolve (a link test); no number typed by hand that a test or script can produce | SHOULD | D | - | Done | - | `DeliveryTests` (1) |

### Developer tooling

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| DV-01 | A test console that listens on loopback only, keeps credentials on its server, refuses cross-site and DNS-rebinding requests, reaches only an allow-list of the service's routes and builds no markup from text it was given | COULD | I (its own tests) | K17 | Done; its tests are in the separate `Lateral.Cms.TestConsole.Tests` project, which does not use requirement ids| - | - |
| DV-02 | A fenced test-only reset: off by default, not mapped unless switched on, refused at startup in Production, admin only, exact confirmation body, runs in one transaction, logged with who and counts. It also empties the queue | COULD | I M | K17 | Done | M41 M42 | `AsyncResetTests` (1), `ResetEndpointTests` (7) |
| DV-03 | A one-click Visual Studio launch profile for the service and the console | COULD | O | - | Copied from the first build; not verified (needs Visual Studio) | - | - |
| DV-04 | The S3 comparison and the generated diagrams, updated for the new design | COULD | D | - | Done: the diagrams are generated by `tools/diagrams` and shown in the console and in the README and DESIGN; the S3 comparison was deliberately not ported (a CMS demo does not pursue an S3 alternative) | - | - |

### Assets

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| BL-01 | Upload: a single streamed `PUT` up to `Assets:MaxSimpleBytes`; S3-style multipart above it (initiate, parts sent in parallel, each checksummed and replaceable, complete) up to `Assets:MaxObjectBytes`. Every refusal (bad key, checksum, size, part list, expired or unknown upload) stores nothing and leaves no file | MUST | I V M | K1 | Done | M44 M45 M46 | `UploadTests` (18) |
| BL-02 | Only the CMS account uploads; the CMS and admins see an asset's state; users and admins download; only admins delete, re-scan or requeue. Every asset route has a row in the role matrix | MUST | A | - | Done | - | `RoleMatrixTests` (2) |
| BL-03 | Downloads stream with `Range` (single, suffix, open-ended; `206`, `416`), `If-Range`, `ETag` (the SHA-256) and `If-None-Match` (`304`), `HEAD`; many ranges can be read in parallel and reassemble to the original hash. Served as an attachment, typed by what the scanner detected, with `nosniff`, a sandbox CSP and `no-store` | MUST | I M | K1 | Done | M47 M48 | `DownloadTests` (9) |
| BL-04 | Only a **clean** asset is ever served: quarantined, rejected and set-aside assets are the same `404` as a missing one, to users and admins alike | MUST | I M | - | Done | M43 | `ScanPipelineTests` (15) |
| BL-05 | The store is bounded: a total cap (`429` with `Retry-After`, nothing stored, parts in flight counted), a per-object cap, and a per-caller cap on concurrent downloads | MUST | I C M | K1 | Done | M49 | `DownloadTests` (1), `HousekeepingTests` (2) |
| BL-06 | Housekeeping: unfinished uploads expire; the sweeper removes expired uploads and files no row accounts for (stray parts, quarantine files, blobs, temp files) and never takes a young file or one that is accounted for | SHOULD | I M | - | Done | M50 | `HousekeepingTests` (3) |
| BL-07 | Hard delete is physical (best effort): the row goes, the bytes are overwritten with zeros even while a reader has them open, then removed; identical content is stored once and survives until the last asset using it is deleted | MUST | I M | K6 | Done | M51 M52 M53 | `HousekeepingTests` (3) |
| BL-08 | Large objects are streamed, not held in memory: a 64 MiB asset is uploaded in parallel parts, scanned and downloaded while the process allocates a small fraction of its size | SHOULD | I M | - | Done | M61 | `LargeAssetTests` (1) |

### Content scanner

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| SC-01 | The mock scanner recognises, deterministically and from the bytes: a test signature (and, if switched on, EICAR); executables (disguised or bare); a declared type the bytes contradict; active content in SVG, HTML and PDF; archive bombs read from the directory alone; and simulated scanner failures. It is a stand-in that proves the pipeline, **not** protection | MUST | U M | K19 | Done | M58 M59 M60 | `ScannerTests` (20) |
| SC-02 | Pipeline: every upload is quarantined, queued, scanned by a background worker and either cleared (moved to its content address) or rejected; the scan runs outside the write gate, so a slow scan never holds up ingestion | MUST | I F | - | Done | - | `ScanPipelineTests` (15) |
| SC-03 | Fail closed: a scanner error, a timeout or a missing file leaves the asset quarantined and unserved, retries with doubling back-off, and after the maximum sets it aside (still unserved) until an admin requeues it; a requeue never skips the scan | MUST | F I M | - | Done | M54 M55 | `ScanPipelineTests` (4) |
| SC-04 | A rejected asset's bytes are overwritten and removed from disk; only its verdict, signature, checksum and times remain; assets sharing its content are rejected with it | MUST | I M | K6 | Done | M56 | `ScanPipelineTests` (1) |
| SC-05 | Assets cleared by an older scanner version can be re-queued, and are not served until cleared again; an admin can re-scan any clean asset | SHOULD | I M | - | Done | M57 | `ScanPipelineTests` (2) |
| SC-06 | Uploads, verdicts and admin asset actions are recorded in an append-only audit log, in the same transaction as the change, holding no content | MUST | I | K14 | Done | - | `ScanPipelineTests` (1) |

### Security hardening

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| SH-01 | No credential is committed: settings files hold none, and in Development without supplied credentials random ones are generated once into a git-ignored file (never in any other environment, never over supplied ones) | MUST | I M | K4 | Done | M67 | `HardeningTests` (5) |
| SH-02 | The memory a burst of senders can use is bounded: only `Ingestion:MaxConcurrentAccepts` batches are read at once, a queue absorbs bursts, and overflow gets `429` with `Retry-After` | MUST | I C M | K1 | Done | M66 | `HardeningTests` (2) |
| SH-03 | The failed-sign-in throttle keys on the real client: `X-Forwarded-For` is believed only from proxies named in configuration, and ignored (so it cannot be forged to dodge the throttle) otherwise; invalid proxy settings stop startup | MUST | I M | K4 | Done | M65 | `HardeningTests` (3) |
| SH-04 | The test-only reset is an allow-list: it exists in Development, or in another environment only when named on purpose (`Admin:ResetAllowedEnvironment`), and never in Production | MUST | I M | K17 | Done | M64 | `HardeningTests` (2) |
| SH-05 | Admin actions (disable, enable, requeue, reset, asset delete and re-scan) are audited with who and what, in the same transaction; the audit log cannot be updated or deleted, and a reset keeps it | MUST | I M | K14 | Done | M62 M63 | `HardeningTests` (2), `ScanPipelineTests` (1) |
| SH-06 | Failed batches cannot quietly fill the queue: their bytes are reported by the admin report, a gauge and the refusal log | SHOULD | I M | - | Done | M69 | `HardeningTests` (1) |
| SH-08 | Input that cannot be read is a `400` with a problem body in every environment, never a `500`: a bad query value, a body that is not JSON, an unknown enum value (ASP.NET throws for these in Development unless told not to). A range in a unit other than bytes is ignored | MUST | I M | - | Done | M76 | `BadInputTests` (4) |
| SH-07 | The test console, which holds live credentials, refuses to start outside Development unless told to | SHOULD | M | K17 | Done; its tests are in the separate `Lateral.Cms.TestConsole.Tests` project, which does not use requirement ids | M68 | - |
| DX-01 | The engineering console's endpoint catalogue (`endpoints.json`, which drives its API workbench and curl generator) lists exactly the routes the service maps, names the same callers the role matrix proves, and explains the responses each can give | SHOULD | I M | - | Done | M78 | `EndpointCatalogueTests` (4) |
| DX-02 | The engineering console's Requirements page traces every original requirement in the brief to how it was met, with its tests and a live check. The brief's words are verbatim; an item is "met" only if every requirement row it cites is Done; anything partly or not met says what is missing; and the generated requirements data (`requirements.json`) matches this document | SHOULD | I M D | - | Done | M80 M81 | `RequirementsTraceTests` (5) |
| DX-03 | The API is described by an OpenAPI document and a Swagger page (`/swagger`) read from the routes the service really maps: each route has a name, a written summary, a group, the callers the role matrix proves can use it, and the statuses it gives; the upload routes say their body is raw bytes; the example event batch is one the service accepts. Both need a signed-in caller (a browser is asked for a password), the page is served under a policy that allows only its own scripts and no generated code, nothing else is relaxed, and `ApiDocs:Enabled=false` removes both | SHOULD | I A M | - | Done | M94 M95 M96 | `ApiDocsSwitchedOffTests` (1), `ApiDocsTests` (11) |

### Accounts, sign-in and operating it

| ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |
|---|---|---|---|---|---|---|---|
| AC-01 | People who sign in live in the database, not in configuration. A password is stored only as a salted PBKDF2-SHA256 hash (600000 iterations by default), never in clear text, in the log or in the audit log; a hash made with fewer iterations than the setting is upgraded at the next sign-in; a malformed stored value never matches | MUST | U I | K4 | Done | - | `AccountApiTests` (3), `PasswordTests` (4) |
| AC-02 | A person signs in with a session cookie (HttpOnly, SameSite=Strict, signed with keys kept on disk so a restart does not sign anyone out) as well as with Basic credentials. A wrong password, an unknown name and a disabled account get the same answer. A session ends when the password changes, the account is disabled or its sessions are revoked, including a change made straight in the database by the command-line tool | MUST | I M | K4 | Done | M82 M87 M90 M91 | `AccountApiTests` (8) |
| AC-03 | A temporary password (a first-run or administrator-made one) must be replaced before anything else is allowed: every route but sign-in, sign-out, who-am-I and change-password answers 403 `password_change_required`, for a session and for Basic credentials | MUST | I M | - | Done | M83 | `AccountApiTests` (1) |
| AC-04 | An administrator creates, disables, enables, re-roles, resets (a temporary password is shown once) and deletes people. The last enabled administrator and your own account cannot be removed, disabled or demoted; a taken, malformed or weak name, role or password is refused; every change is in the audit log with the actor and no secret | MUST | I A M | - | Done | M84 | `AccountApiTests` (6), `AccountGuardTests` (3) |
| AC-05 | A person changes their own password by proving the current one; the new one must meet the policy (12 to 128 characters, not their name, not a common one) and differ; their other sessions end and the one they are using carries on | MUST | U I M | - | Done | M93 | `AccountApiTests` (1), `PasswordTests` (2) |
| AC-06 | A fresh install, with no accounts anywhere, makes one administrator with a generated temporary password written to a file beside the database (private to the service account) and not to the log; the file goes when the password is replaced; nothing is made when anybody can already sign in | MUST | U I M | - | Done | M85 | `FirstRunTests` (3), `PasswordTests` (1) |
| AC-07 | The operator can manage people from the machine (`dotnet Lateral.Cms.dll admin ...`, for example over SSH): list, create, set or generate a password, change role, disable, enable, sign out, delete, make a service secret, show where data is kept. It works with the service stopped, uses the service's own rules (including the last-administrator guard) and audits as `cli:<user>` | MUST | I M | K4 | Done | M92 | `AdminCliGuardTests` (1), `AdminCliTests` (9) |
| AC-08 | Failed sign-ins are slowed per client address and per username (429 with `Retry-After`); a request that changes something and is authorised only by a cookie must carry a header a hostile page cannot add; a page with its own sign-in form is not given the browser's password box | SHOULD | I M | - | Done | M86 | `AccountApiTests` (2), `LoginThrottleTests` (1) |
| CF-01 | The administrator can see every setting the service reads, its value now, its default, where the value came from (file, environment variable, command line or default), the environment variable that sets it and what it is for. Secrets show only as set or not set, and a password in a connection string is hidden. A setting without a description fails a test | SHOULD | I M | - | Done | M88 M89 | `SettingsAndStorageTests` (4) |
| CF-02 | The administrator can see where the database, asset store and session keys are, how big they are, how much room the disks have, how much of the quota is used, what to back up and where logs go; the report contains no credentials | SHOULD | I | - | Done | - | `SettingsAndStorageTests` (2) |
