# Evidence

What was run, what it showed, and what is **not** proven. Each result has its date, machine and command so it can be repeated;
a number here comes from a recorded run, never from memory. The reasoning is in [DESIGN](DESIGN.md), the requirements and their
tests in [REQUIREMENTS](REQUIREMENTS.md).

**Environment of every run below:** a Windows 11 Pro development machine with 16 logical cores, Windows 10.0.26200, .NET SDK 10.0.403 building `net9.0` and running on the 9.0.22 runtime
(`RollForward=Major`), Node 22.22.0, 2026-11-05. One machine, one person; the numbers are evidence of an order of magnitude.

## 1. Build and tests

| Check | Command | Result |
|---|---|---|
| Strict build (warnings are errors, as CI) | `CI=true dotnet build Lateral.sln -c Release` | 0 warnings, 0 errors |
| Service suite | `dotnet test tests/Lateral.Cms.Tests -c Release` | 377 passed, 0 failed, 8 to 11 s |
| Test console suite | `dotnet test tests/Lateral.Cms.TestConsole.Tests -c Release` | 100 passed, 0 failed |
| Flake gate | the service suite, 20 times in a row, no rebuild | 20 of 20 passed (377 tests each, 8 to 11 s per run), no failures |

How the service suite is built: real HTTP pipeline (`WebApplicationFactory`) against a real SQLite file per test class, assertions
on responses **and** on stored rows through raw SQL, a manual clock, real faults produced with database triggers (no test hooks in
production code), and the order-independence checks below. Per area: reducer rules and properties, parsing and limits, checksum,
persistence and constraints, authentication and the role matrix, the pipeline (accept, apply, retry, set-aside, takedown, cap,
restart), the read API, observability, and the documentation checks.

### Order independence

The reducer was compared with an independent order-free oracle (design section 5; code in `ReducerPropertyTests.Oracle`):

| Check | Size | Result |
|---|---|---|
| Random timelines, each shuffled three times (duplicates, ties, contradicting content and deletes included) | 20,000 timelines, 60,000 folds | 0 mismatches |
| Slow category: one shuffle each | 1,000,000 timelines | 0 mismatches |
| Every arrival order of each curated timeline | 11 timelines of up to 7 events (up to 5,040 orders each) | 0 mismatches |
| Streams split into batches that are applied in the queue's own order, through the real processor | 60 ids per run | final state equals the oracle for every id |
| **Outside the assumption** (a higher version with an earlier timestamp) | 20,000 timelines | **some shuffles diverge**, as documented (limit K8, two cases pinned by tests) |

## 2. Deliberate defects (mutation log)

Each row breaks one rule in the source, runs the service suite, and records whether a test failed. The runner
(`node tools/mutation-check/run.mjs`) restores every file and checks a hash; the results are in
`tools/mutation-check/last-run.json`, and a test (`DeliveryTests`) fails if a requirement that needs a mutation names one that
was not caught. "(+N more)" means N further tests also failed. Manual, not an automated mutation tool (limit K16).

| ID | Area | Deliberate defect | Result | A test that failed (first of N) |
|---|---|---|---|---|
| M01 | Rules | ignore the tombstone | killed | `Domain › ReducerTests.Replaying_a_whole_stream_changes_nothing` (+10 more) |
| M02 | Rules | remove the unPublish tie-break | killed | `Domain › ReducerPropertyTests.Every_shuffle_of_twenty_thousand_random_timelines_ends_where_the_oracle_says` (+4 more) |
| M03 | Rules | remove the stale-version check | killed | `Domain › ReducerTests.The_unpublish_of_an_unseen_id_wins_over_a_late_older_publish_in_either_order` (+13 more) |
| M04 | Rules | decide content conflicts by arrival order | killed | `Domain › ReducerTests.Contradicting_content_at_the_same_version_instant_and_kind_is_decided_by_the_greater_text_not_by_arrival` (+7 more) |
| M05 | Rules | a stale delete does not raise the tombstone | killed | `Domain › ReducerTests.A_delete_older_than_the_stored_state_is_stale_but_still_raises_the_tombstone` (+4 more) |
| M06 | Rules | re-create keeps the old admin override (same-batch delete and re-create) | killed | `Api › OverrideAndPolicyTests.A_publish_never_clears_the_admin_override_but_a_delete_and_re_create_in_one_batch_does` |
| M07 | Parsing | hash the wrong bytes | killed | `Ingestion › EventParserTests.The_checksum_is_computed_over_the_canonical_payload_text_that_is_stored` (+7 more) |
| M08 | Parsing | remove the declared-length check | killed | `Api › IngestionLimitsTests.A_declared_ten_gigabyte_body_is_refused_without_being_read` |
| M09 | Parsing | accept far-future timestamps | killed | `Ingestion › EventParserTests.Invalid_events_are_rejected_with_a_specific_reason` (+1 more) |
| M10 | Storage | open the read side read-write | killed | `Persistence › PersistenceGuaranteeTests.The_read_context_refuses_to_save_and_its_connection_is_read_only` |
| M11 | Storage | stop renewing the concurrency token | killed | `Persistence › PersistenceGuaranteeTests.A_save_based_on_a_stale_read_is_refused` (+1 more) |
| M12 | Storage | make the listing index filter unmatched by the query | killed | `Persistence › ListingIndexTests.The_user_listing_is_served_from_an_index_in_id_order_without_a_sort` |
| M13 | Storage | turn secure_delete off | killed | `Persistence › StorageGuaranteeTests.After_a_delete_and_a_checkpoint_the_deleted_entitys_content_is_in_neither_the_file_nor_the_wal` (+1 more) |
| M14 | Storage | drop the payload CHECK | killed | `Persistence › StorageGuaranteeTests.A_decided_event_cannot_hold_a_payload_the_schema_refuses_it` |
| M15 | Pipeline | commit the apply and the batch completion separately | killed | `Api › InboxFailureTests.Applying_the_state_the_outcomes_and_completing_the_batch_succeed_or_fail_together` |
| M16 | Pipeline | leave the payload in the row when a batch is applied | killed | `Api › InboxBackpressureTests.Applying_batches_frees_the_space_they_used` (+93 more) |
| M17 | Pipeline | remove takedown priority | killed | `Persistence › InboxIndexTests.Picking_the_next_batch_walks_the_pick_index_in_order_without_a_sort` (+2 more) |
| M18 | Pipeline | never set a batch aside | killed | `Api › InboxFailureTests.The_wait_between_attempts_doubles_up_to_a_ceiling` (+4 more) |
| M19 | Pipeline | no back-off between attempts | killed | `Api › InboxFailureTests.The_wait_between_attempts_doubles_up_to_a_ceiling` (+3 more) |
| M20 | Pipeline | queue size cap not enforced | killed | `Api › InboxBackpressureTests.A_full_inbox_refuses_with_429_and_stores_nothing_of_the_refused_batch` (+2 more) |
| M21 | Pipeline | queue size cap not judged under the write gate | killed | `Api › InboxBackpressureTests.Senders_arriving_together_cannot_squeeze_past_the_limit` |
| M22 | Pipeline | let the worker die on an exception | killed | `Api › InboxWorkerTests.A_storage_outage_is_logged_and_the_worker_resumes_when_storage_returns` |
| M23 | Pipeline | pick set-aside batches again | killed | `Persistence › InboxIndexTests.Picking_the_next_batch_walks_the_pick_index_in_order_without_a_sort` (+2 more) |
| M24 | Pipeline | report a failed batch as queued | killed | `Api › InboxFailureTests.The_wait_between_attempts_doubles_up_to_a_ceiling` (+2 more) |
| M25 | Pipeline | acknowledge even though nothing was stored | killed | `Api › IngestionEndpointTests.When_storing_a_batch_fails_nothing_from_it_is_kept_and_the_sender_is_told_to_retry` |
| M26 | Pipeline | a batch holding a takedown may be set aside like any other | killed | `Api › InboxFailureTests.A_batch_holding_a_takedown_is_never_set_aside_it_keeps_retrying_at_the_longest_delay_until_it_succeeds` |
| M27 | Security | let users read batch status | killed | `Auth › RoleMatrixTests.Each_caller_gets_exactly_the_access_its_role_allows` |
| M28 | Security | let admins ingest | killed | `Auth › BasicAuthTests.Users_and_admins_cannot_post_cms_events` (+1 more) |
| M29 | Security | remove the fallback policy | killed | `Api › OverrideAndPolicyTests.Deny_by_default_a_fallback_policy_requires_a_signed_in_caller_for_anything_without_its_own_policy` |
| M30 | Security | log a payload | killed | `Api › IngestionEndpointTests.Processed_and_failing_events_are_logged_without_leaking_payloads_or_credentials` (+1 more) |
| M31 | Rules | an event exactly at the tombstone time is applied | killed | `Domain › ReducerPropertyTests.Replaying_any_random_stream_a_second_time_changes_nothing` (+5 more) |
| M32 | Rules | a delete at the entity's own instant is treated as stale | killed | `Domain › ReducerPropertyTests.Every_shuffle_of_twenty_thousand_random_timelines_ends_where_the_oracle_says` (+2 more) |
| M33 | Read API | users also see entities an admin disabled | killed | `Api › OverrideAndPolicyTests.A_publish_never_clears_the_admin_override_but_a_delete_and_re_create_in_one_batch_does` (+3 more) |
| M34 | Read API | users also see unpublished entities | killed | `Api › EntitiesEndpointTests.A_query_string_cannot_widen_what_a_user_can_see` (+6 more) |
| M35 | Parsing | allow control characters in ids | killed | `Ingestion › EventParserTests.Invalid_events_are_rejected_with_a_specific_reason` |
| M36 | Rules | an unPublish for an id never seen is ignored instead of creating it | killed | `Domain › ReducerTests.The_unpublish_of_an_unseen_id_wins_over_a_late_older_publish_in_either_order` (+15 more) |
| M37 | Rules | a delete keeps the entity row and its payload | killed | `Domain › ReducerPropertyTests.The_pinned_counterexample_arrival_order_matters_when_a_higher_version_carries_an_earlier_timestamp` (+14 more) |
| M38 | Observability | a stale, rejected or conflicting event is logged at Information | killed | `Api › ObservabilityTests.Applied_and_duplicate_are_information_while_stale_unsupported_rejected_and_conflict_winners_are_warnings` (+1 more) |
| M39 | Observability | a rejected or unsupported event is logged at Information | killed | `Api › ObservabilityTests.Applied_and_duplicate_are_information_while_stale_unsupported_rejected_and_conflict_winners_are_warnings` (+1 more) |
| M40 | Pipeline | a batch is never flagged as holding a takedown | killed | `Api › InboxFailureTests.A_batch_holding_a_takedown_is_never_set_aside_it_keeps_retrying_at_the_longest_delay_until_it_succeeds` (+2 more) |
| M41 | Developer tooling | the reset runs without the confirmation phrase | killed | `Api › ResetEndpointTests.Without_the_exact_confirmation_nothing_is_erased` |
| M42 | Developer tooling | the reset route is mapped even when it is switched off | killed | `Api › RouteAndTransportTests.No_route_updates_replaces_or_deletes_an_entity_the_only_writes_are_ingestion_the_admin_override_and_the_queue` (+4 more) |

42 of 42 defects were caught.

## 3. Measurements

Load smoke: `dotnet run -c Release --project tools/Lateral.Cms.LoadSmoke -- --senders S --batches B --events E --payload 1024 --log L`.
It starts the built service on a loopback port (environment `Staging`, generated credentials, a temporary SQLite file), drives
it, waits for the admin report to show an empty queue, then pages through every entity as a user. ~1 KB payloads.

| Load | Accepted | Request latency | Applied end to end |
|---|---|---|---|
| 1 event per request, 1 sender, 300 requests | 93 events/s | p50 10 ms, p95 14 ms, p99 22 ms | 91 events/s; queue empty 3.3 s after the first request; 905 log lines |
| 100 events per request, 1 sender, 3,000 events | 1,181 events/s | p50 66 ms, p95 130 ms, p99 345 ms | 1,111 events/s; queue empty 2.7 s after the first request; 3,065 log lines |
| 500 events per request (the maximum), 1 sender, 4,000 events | 1,390 events/s | p50 277 ms, p95 638 ms, p99 638 ms | 1,227 events/s; queue empty 3.3 s after the first request; 4,021 log lines |
| 100 events per request, 8 senders, 20,000 events, logging at Information | 2,264 events/s | p50 350 ms, p95 463 ms, p99 501 ms | 1,498 events/s; queue empty 13.4 s after the first request; 20,405 log lines |
| the same with logging at Warning | 2,194 events/s | p50 390 ms, p95 520 ms, p99 546 ms | 1,499 events/s; queue empty 13.3 s after the first request; 0 log lines |
| Reading it back as a user (after the 8-sender run) | | 20,000 entities in 200 pages, 0.49 s; page p50 2.0 ms, p95 3.3 ms | |

### Against the first build

The first build (`2238384`) was measured the same way on this machine (its figures are in the rebuild brief, section 2.4). The
bar for the rebuild: accepted and applied throughput within 20% of those, and the 1-event request p95 at most 25 ms.

| Load | First build accepted → applied (events/s) | This build accepted → applied (events/s) | Within the bar? |
|---|---|---|---|
| 1 event per request, 1 sender, 300 requests | 98 → 97 | 93 → 91 (−5%, −6%); p95 14 ms | yes |
| 100 events per request, 1 sender, 3,000 events | 1,046 → 947 | 1,181 → 1,111 (+13%, +17%) | yes |
| 500 events per request (the maximum), 1 sender, 4,000 events | 1,036 → 918 | 1,390 → 1,227 (+34%, +34%) | yes |
| 100 events per request, 8 senders, 20,000 events, logging at Information | 1,991 → 1,224 | 2,264 → 1,498 (+14%, +22%) | yes |
| the same with logging at Warning | 2,536 → 1,425 | 2,194 → 1,499 (−13%, +5%) | yes |

Reading it: accepting scales with CPU (parse, canonicalise, hash) until senders queue on the single write gate; applying is capped
by SQLite's single writer at roughly 1,000 to 1,500 events/s here. This build writes one log line per event (the first wrote two),
so Information logging now costs little: 20,405 lines for 20,000 events against 40,605. These are single runs, so the small
differences (for example the Warning run) are within noise. **No figure here is a promise;** the README states none.

## 4. Operated

| Claim | How it was checked | Result |
|---|---|---|
| The service runs and serves the documented flow | Started `dotnet run` on a spare port; a script posted a batch, followed it, read as user and admin, disabled, re-enabled, hard-deleted | Worked as documented |
| The test console drives the service | Ran the console against that service in the browser pane: sent the "Publish an article" scenario, followed the batch to completion, opened the Queue tab | Worked; no browser console errors |
| The README's try-it commands | The fenced blocks tagged `bash try-it`, extracted with the same `awk` as CI, run against the built service on a spare port | Passed (exit 0): 202 with Location and X-Correlation-Id, batch followed to completed, user and admin reads, 401 and 403 checks, unpublish, re-publish, disable, hard delete |
| `secure_delete` makes a hard delete physical | `StorageGuaranteeTests`: a control shows SQLite's default leaves a deleted value in the file after a checkpoint; with it on, a marker is absent from the file and WAL after delete, replace and null | Passed |

## 5. Not proven, and how each is labelled

| Item | Status |
|---|---|
| **CI on Windows, macOS and Linux** | The workflow is written (`.github/workflows/ci.yml`) and **has not been run**: nothing was pushed (the remote is the owner's decision). Everything above ran on Windows only. Not shown: that the suite passes on macOS and Linux |
| **README try-it job in CI** | Written, not run in CI (same reason). The commands themselves were run locally, see section 4 |
| **Visual Studio launch profile** (`Lateral.slnLaunch`, `.vsconfig`) | Copied from the first build; not opened in Visual Studio here |
| **S3 comparison and generated diagrams** | Not ported from the first build; the design diagram in DESIGN is hand-drawn text |
| **Size budget** (brief section 4.9: about 2,500 production lines, tests at most 4,000) | Production: 3,242 raw lines (the first build had 3,374), **2,326 excluding blanks and comments**, so within the budget by that measure; the gap is mostly XML doc comments. Service tests: 5,827 raw, **4,548 excluding blanks and comments: over the 4,000 budget**, because most of the first build's tests were ported and new suites were added. The budget is a guard against sprawl, so this is reported rather than hidden. Measured on 2026-11-05 |
| **Throughput, capacity, soak** | One machine, a few minutes: an order of magnitude only (limit K2) |
| **Erasure beyond the live file** | Backups, replicas, WAL archives and snapshots are out of scope (limit K6) |
| **Ordering outside the assumption** | Known and pinned, not fixed (section 6 of DESIGN) |
| *Assumed* | One CMS emits the events; credentials are only ever sent over TLS in production; the .NET JSON encoder's output is stable across the patch versions in use (pinned by golden vectors, which would fail if it changed) |
