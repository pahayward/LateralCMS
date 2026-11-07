# Evidence

What was run, what it showed, and what is **not** proven. Each result has its date, machine and command so it can be repeated;
a number here comes from a recorded run, never from memory. The reasoning is in [DESIGN](DESIGN.md), the requirements and their
tests in [REQUIREMENTS](REQUIREMENTS.md).

**Environment of every run below:** a Windows 11 Pro development machine with 16 logical cores, Windows 10.0.26200, .NET SDK 10.0.403 building `net9.0` and running on the 9.0.22 runtime
(`RollForward=Major`), Node 22.22.0, 6 and 7 November 2026 (the photo library and its tests on the 7th). One machine, one person; the numbers are evidence of an order of magnitude.

## 1. Build and tests

| Check | Command | Result |
|---|---|---|
| Strict build (warnings are errors, as CI) | `CI=true dotnet build Lateral.sln -c Release` | 0 warnings, 0 errors |
| Service suite | `dotnet test tests/Lateral.Cms.Tests -c Release` | 559 passed, 0 failed, 12 to 18 s |
| Test console suite | `dotnet test tests/Lateral.Cms.TestConsole.Tests -c Release` | 124 passed, 0 failed |
| Flake gate | the service suite, repeated with no rebuild | 40 of 40 passed (559 tests each, 12 to 18 s per run), no failures. Earlier gates on this code each stopped at a failure that was then traced and fixed: an error logged when the app stopped mid-way through opening a connection, a race in the Microsoft.Data.Sqlite connection pool that gave a rare HTTP 500 "database is locked" (DESIGN K21), and a scan test that raced a 400 ms timeout |

How the service suite is built: real HTTP pipeline (`WebApplicationFactory`) against a real SQLite file per test class, assertions
on responses **and** on stored rows through raw SQL, a manual clock, real faults produced with database triggers (no test hooks in
production code), and the order-independence checks below. Per area: reducer rules and properties, parsing and limits, checksum,
persistence and constraints, authentication and the role matrix, the pipeline (accept, apply, retry, set-aside, takedown, cap,
restart), the read API, observability, **the asset store** (single and multipart upload, ranges, parallel reads, quota, expiry and
orphans, hard delete, a 64 MiB streamed round trip), **the mock scanner** (every rule in memory, then end to end: clean, rejected and
wiped, fail closed, re-scan), **the security hardening** (generated credentials, the accept limit, forwarded headers, the reset
allow-list, the audit log), and the documentation checks. The console project covers its proxy, guards and the new asset paths.

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
| M01 | Rules | ignore the tombstone | killed | `Domain › ReducerTests.Replaying_a_whole_stream_changes_nothing` (+11 more) |
| M02 | Rules | remove the unPublish tie-break | killed | `Domain › ReducerTests.An_unpublish_beats_a_publish_at_the_same_version_and_instant_in_either_arrival_order` (+5 more) |
| M03 | Rules | remove the stale-version check | killed | `Domain › ReducerTests.The_unpublish_of_an_unseen_id_wins_over_a_late_older_publish_in_either_order` (+14 more) |
| M04 | Rules | decide content conflicts by arrival order | killed | `Domain › ReducerPropertyTests.Every_shuffle_of_twenty_thousand_random_timelines_ends_where_the_oracle_says` (+8 more) |
| M05 | Rules | a stale delete does not raise the tombstone | killed | `Domain › ReducerTests.A_delete_older_than_the_stored_state_is_stale_but_still_raises_the_tombstone` (+5 more) |
| M06 | Rules | re-create keeps the old admin override (same-batch delete and re-create) | killed | `Api › OverrideAndPolicyTests.A_publish_never_clears_the_admin_override_but_a_delete_and_re_create_in_one_batch_does` |
| M07 | Parsing | hash the wrong bytes | killed | `Api › ChecksumEndpointTests.Publishing_returns_the_checksum_of_the_payload_that_was_stored` (+7 more) |
| M08 | Parsing | remove the declared-length check | killed | `Api › IngestionLimitsTests.A_declared_ten_gigabyte_body_is_refused_without_being_read` |
| M09 | Parsing | accept far-future timestamps | killed | `Ingestion › EventParserTests.Invalid_events_are_rejected_with_a_specific_reason` (+1 more) |
| M10 | Storage | open the read side read-write | killed | `Persistence › PersistenceGuaranteeTests.The_read_context_refuses_to_save_and_its_connection_is_read_only` |
| M11 | Storage | stop renewing the concurrency token | killed | `Persistence › PersistenceGuaranteeTests.A_save_based_on_a_stale_read_is_refused` (+1 more) |
| M12 | Storage | make the listing index filter unmatched by the query | killed | `Persistence › ListingIndexTests.The_user_listing_is_served_from_an_index_in_id_order_without_a_sort` |
| M13 | Storage | turn secure_delete off | killed | `Persistence › StorageGuaranteeTests.After_a_delete_and_a_checkpoint_the_deleted_entitys_content_is_in_neither_the_file_nor_the_wal` (+1 more) |
| M14 | Storage | drop the payload CHECK | killed | `Persistence › StorageGuaranteeTests.A_decided_event_cannot_hold_a_payload_the_schema_refuses_it` |
| M15 | Pipeline | commit the apply and the batch completion separately | killed | `Api › InboxFailureTests.Applying_the_state_the_outcomes_and_completing_the_batch_succeed_or_fail_together` |
| M16 | Pipeline | leave the payload in the row when a batch is applied | killed | `Api › InboxBackpressureTests.Applying_batches_frees_the_space_they_used` (+95 more) |
| M17 | Pipeline | remove takedown priority | killed | `Persistence › InboxIndexTests.Picking_the_next_batch_walks_the_pick_index_in_order_without_a_sort` (+2 more) |
| M18 | Pipeline | never set a batch aside | killed | `Api › InboxFailureTests.The_wait_between_attempts_doubles_up_to_a_ceiling` (+5 more) |
| M19 | Pipeline | no back-off between attempts | killed | `Api › InboxFailureTests.The_wait_between_attempts_doubles_up_to_a_ceiling` (+3 more) |
| M20 | Pipeline | queue size cap not enforced | killed | `Api › InboxBackpressureTests.A_full_inbox_refuses_with_429_and_stores_nothing_of_the_refused_batch` (+2 more) |
| M21 | Pipeline | queue size cap not judged under the write gate | killed | `Api › InboxBackpressureTests.Senders_arriving_together_cannot_squeeze_past_the_limit` (+1 more) |
| M22 | Pipeline | let the worker die on an exception | killed | `Api › InboxWorkerTests.A_storage_outage_is_logged_and_the_worker_resumes_when_storage_returns` |
| M23 | Pipeline | pick set-aside batches again | killed | `Api › InboxWorkerTests.A_batch_the_worker_cannot_apply_is_set_aside_and_the_worker_carries_on` (+2 more) |
| M24 | Pipeline | report a failed batch as queued | killed | `Api › InboxFailureTests.The_wait_between_attempts_doubles_up_to_a_ceiling` (+2 more) |
| M25 | Pipeline | acknowledge even though nothing was stored | killed | `Api › IngestionEndpointTests.When_storing_a_batch_fails_nothing_from_it_is_kept_and_the_sender_is_told_to_retry` |
| M26 | Pipeline | a batch holding a takedown may be set aside like any other | killed | `Api › InboxFailureTests.A_batch_holding_a_takedown_is_never_set_aside_it_keeps_retrying_at_the_longest_delay_until_it_succeeds` |
| M27 | Security | let users read batch status | killed | `Assets › UploadTests.The_status_endpoint_reports_state_and_never_the_bytes` (+1 more) |
| M28 | Security | let admins ingest | killed | `Auth › BasicAuthTests.Users_and_admins_cannot_post_cms_events` (+1 more) |
| M29 | Security | remove the fallback policy | killed | `Api › OverrideAndPolicyTests.Deny_by_default_a_fallback_policy_requires_a_signed_in_caller_for_anything_without_its_own_policy` |
| M30 | Security | log a payload | killed | `Api › IngestionEndpointTests.Processed_and_failing_events_are_logged_without_leaking_payloads_or_credentials` (+1 more) |
| M31 | Rules | an event exactly at the tombstone time is applied | killed | `Domain › ReducerTests.Events_at_or_before_the_tombstone_are_stale_so_a_deleted_entity_cannot_come_back` (+6 more) |
| M32 | Rules | a delete at the entity's own instant is treated as stale | killed | `Domain › ReducerPropertyTests.Every_shuffle_of_twenty_thousand_random_timelines_ends_where_the_oracle_says` (+3 more) |
| M33 | Read API | users also see entities an admin disabled | killed | `Api › EntitiesEndpointTests.Users_see_only_published_and_not_disabled_entities_while_admins_see_everything` (+3 more) |
| M34 | Read API | users also see unpublished entities | killed | `Api › EntitiesEndpointTests.A_query_string_cannot_widen_what_a_user_can_see` (+6 more) |
| M35 | Parsing | allow control characters in ids | killed | `Ingestion › EventParserTests.Invalid_events_are_rejected_with_a_specific_reason` |
| M36 | Rules | an unPublish for an id never seen is ignored instead of creating it | killed | `Domain › ReducerTests.The_unpublish_of_an_unseen_id_wins_over_a_late_older_publish_in_either_order` (+17 more) |
| M37 | Rules | a delete keeps the entity row and its payload | killed | `Domain › ReducerPropertyTests.The_pinned_counterexample_arrival_order_matters_when_a_higher_version_carries_an_earlier_timestamp` (+15 more) |
| M38 | Observability | a stale, rejected or conflicting event is logged at Information | killed | `Api › ObservabilityTests.Applied_and_duplicate_are_information_while_stale_unsupported_rejected_and_conflict_winners_are_warnings` (+1 more) |
| M39 | Observability | a rejected or unsupported event is logged at Information | killed | `Api › ObservabilityTests.Applied_and_duplicate_are_information_while_stale_unsupported_rejected_and_conflict_winners_are_warnings` (+1 more) |
| M40 | Pipeline | a batch is never flagged as holding a takedown | killed | `Api › InboxFailureTests.A_batch_holding_a_takedown_is_never_set_aside_it_keeps_retrying_at_the_longest_delay_until_it_succeeds` (+2 more) |
| M41 | Developer tooling | the reset runs without the confirmation phrase | killed | `Api › ResetEndpointTests.Without_the_exact_confirmation_nothing_is_erased` (+1 more) |
| M42 | Developer tooling | the reset route is mapped even when it is switched off | killed | `Api › ResetDisabledByDefaultTests.An_admin_gets_404_because_the_route_is_not_mapped` (+4 more) |
| M43 | Assets | serve an asset that is not clean | killed | `Assets › ScanPipelineTests.An_admin_can_re_scan_one_clean_asset_and_not_one_that_is_not_clean` (+1 more) |
| M44 | Assets | skip the checksum check on uploads | killed | `Assets › UploadTests.A_matching_sha256_header_is_accepted_and_a_wrong_one_stores_nothing` (+1 more) |
| M45 | Assets | do not check the assembled size against the declared size | killed | `Assets › UploadTests.Completing_with_a_bad_part_list_is_refused_and_assembles_nothing` |
| M46 | Assets | allow a part below the minimum that is not the last | killed | `Assets › UploadTests.Completing_with_a_bad_part_list_is_refused_and_assembles_nothing` |
| M47 | Assets | no range support on downloads | killed | `Assets › DownloadTests.Eight_parallel_ranged_downloads_reassemble_to_exactly_the_original_bytes` (+4 more) |
| M48 | Assets | serve the type the sender claimed instead of the detected one | killed | `Assets › DownloadTests.A_download_is_an_attachment_typed_by_what_the_bytes_are_never_by_what_the_sender_claimed` |
| M49 | Assets | store size cap not enforced before an upload starts | killed | `Assets › HousekeepingTests.Past_the_store_cap_uploads_get_429_with_retry_after_and_nothing_is_stored` (+3 more) |
| M50 | Assets | the sweeper takes files that are still young | killed | `Assets › HousekeepingTests.The_sweeper_leaves_young_files_alone_so_it_cannot_take_one_that_is_still_being_registered` |
| M51 | Assets | a delete leaves the content on disk | killed | `Assets › HousekeepingTests.An_admin_delete_removes_the_row_and_wipes_the_bytes_and_a_user_cannot` (+1 more) |
| M52 | Assets | a delete removes content another asset still uses | killed | `Assets › HousekeepingTests.Identical_content_is_stored_once_and_survives_until_the_last_asset_that_uses_it_is_deleted` |
| M53 | Assets | the wipe does not overwrite before deleting | killed | `Assets › HousekeepingTests.Deleted_content_is_overwritten_with_zeros_even_while_someone_has_the_file_open` |
| M54 | Scanner | a scanner exception is treated as clean | killed | `Assets › ScanPipelineTests.A_scanner_error_keeps_the_asset_quarantined_retries_with_back_off_and_sets_it_aside_without_ever_serving_it` (+2 more) |
| M55 | Scanner | a scan that keeps failing is never set aside | killed | `Assets › ScanPipelineTests.A_scanner_error_keeps_the_asset_quarantined_retries_with_back_off_and_sets_it_aside_without_ever_serving_it` (+1 more) |
| M56 | Scanner | a rejected asset keeps its quarantined bytes | killed | `Assets › ScanPipelineTests.A_flagged_asset_is_rejected_its_bytes_are_wiped_from_disk_and_only_the_verdict_remains` (+1 more) |
| M57 | Scanner | re-scanning outdated assets leaves them served meanwhile | killed | `Assets › ScanPipelineTests.Assets_cleared_by_an_older_scanner_version_can_be_re_queued_and_are_not_served_until_cleared_again` |
| M58 | Scanner | active content in svg, html and pdf is not flagged | killed | `Assets › ScannerTests.Script_in_svg_or_html_is_suspicious` (+1 more) |
| M59 | Scanner | archive compression ratio is not checked | killed | `Assets › ScannerTests.An_archive_that_declares_a_huge_expansion_is_a_bomb_judged_from_its_directory_alone` |
| M60 | Scanner | an executable dressed as another type passes | killed | `Assets › ScannerTests.An_executable_dressed_up_as_an_image_is_suspicious` (+1 more) |
| M61 | Assets | downloads are buffered whole in memory | killed | `Assets › DownloadTests.The_etag_makes_repeat_downloads_free_and_if_range_makes_resuming_safe` (+2 more) |
| M62 | Hardening | the audit log can be updated | killed | `Assets › ScanPipelineTests.Uploads_verdicts_and_admin_actions_are_written_to_an_audit_log_that_cannot_be_edited` |
| M63 | Hardening | disabling an entity is not audited | killed | `Api › HardeningTests.A_reset_is_audited_and_never_erases_the_audit_log` (+1 more) |
| M64 | Hardening | the reset is allowed in any environment but Production | killed | `Api › HardeningTests.The_reset_is_refused_in_every_environment_that_has_not_been_named_on_purpose_and_always_in_production` |
| M65 | Hardening | X-Forwarded-For is believed from anyone | killed | `Api › HardeningTests.Without_a_trusted_proxy_a_forged_forwarded_for_header_cannot_dodge_the_throttle` |
| M66 | Hardening | no limit on batches read into memory at once | killed | `Api › HardeningTests.Only_as_many_batches_are_read_into_memory_at_once_as_configured_and_the_rest_get_429_with_retry_after` |
| M67 | Hardening | development credentials are generated in every environment | killed | `Api › HardeningTests.Outside_development_nothing_is_generated_so_missing_credentials_still_stop_startup` |
| M68 | Hardening | the test console starts in any environment | killed | `Lateral › Cms.TestConsole.Tests.EnvironmentGuardTests.Outside_development_the_console_refuses_to_start_and_says_how_to_override` |
| M69 | Hardening | failed batch bytes are not reported | killed | `Api › InboxWorkerTests.Stopping_the_app_stops_the_worker_cleanly_with_no_errors` (+1 more) |
| M70 | Hardening | a connection open cancelled by shutdown is logged as an error | killed | `Api › ShutdownTests.Many_apps_stopped_at_once_log_no_errors` |
| M71 | Hardening | connections are opened in parallel (the pool race returns) | killed | `Assets › ScanPipelineTests.A_slow_scan_does_not_hold_up_ingestion_because_it_runs_outside_the_write_gate` (+1 more) |
| M72 | Assets | the write gate is held for the whole scan | killed | `Assets › ScanPipelineTests.A_slow_scan_does_not_hold_up_ingestion_because_it_runs_outside_the_write_gate` |
| M73 | Photos | a refused download is not asked for again | killed | `Lateral › Cms.TestConsole.Tests.MediaProxyTests.A_refused_download_because_too_many_are_running_is_asked_for_again` |
| M74 | Photos | every picture is offered as a download | killed | `Lateral › Cms.TestConsole.Tests.MediaProxyTests.It_is_shown_in_the_page_unless_the_page_asks_for_a_download` |
| M75 | Photos | the console API no longer needs its header (the relaxed check leaks to /api) | killed | `Lateral › Cms.TestConsole.Tests.ConsoleApiTests.A_request_without_the_console_header_is_refused_before_anything_is_proxied` |

75 of 75 defects were caught.

## 3. Measurements

Load smoke: `dotnet run -c Release --project tools/Lateral.Cms.LoadSmoke -- --senders S --batches B --events E --payload 1024 --log L`.
It starts the built service on a loopback port (environment `Staging`, generated credentials, a temporary SQLite file), drives
it, waits for the admin report to show an empty queue, then pages through every entity as a user. ~1 KB payloads.

| Load | Accepted | Request latency | Applied end to end |
|---|---|---|---|
| 1 event per request, 1 sender, 300 requests | 114 events/s | p50 8 ms, p95 10 ms, p99 22 ms | 113 events/s; queue empty 2.7 s after the first request; 906 log lines |
| 100 events per request, 1 sender, 3,000 events | 1,519 events/s | p50 56 ms, p95 92 ms, p99 240 ms | 1,419 events/s; queue empty 2.1 s after the first request; 3,066 log lines |
| 500 events per request (the maximum), 1 sender, 4,000 events | 1,735 events/s | p50 275 ms, p95 325 ms, p99 325 ms | 1,560 events/s; queue empty 2.6 s after the first request; 4,022 log lines |
| 100 events per request, 8 senders, 20,000 events, logging at Information | 3,479 events/s | p50 228 ms, p95 269 ms, p99 333 ms | 2,154 events/s; queue empty 9.3 s after the first request; 20,406 log lines |
| the same with logging at Warning | 3,502 events/s | p50 221 ms, p95 271 ms, p99 326 ms | 2,213 events/s; queue empty 9.0 s after the first request; 0 log lines |
| Reading it back as a user (after the 8-sender run) | | 20,000 entities in 200 pages, 0.29 s; page p50 1.2 ms, p95 1.9 ms | |

### The asset store

`dotnet run -c Release --project tools/Lateral.Cms.LoadSmoke -- --asset-mib N --part-mib 16 --uploaders 4 --ranges 8` uploads one object
in 16 MiB parts (4 at a time), waits for the mock scanner to clear it, then reads it back once serially and once as 8 parallel range
requests, verifying the SHA-256 both times. Loopback, one machine: the client holds the object in memory and there is no network or
disk-bound storage tier, so this shows the service's own path, not what a real network or a slow disk would allow. Single runs.

| Object | Upload (parts, 4 at a time) | Scan until clean | Download, one stream | Download, 8 parallel ranges |
|---|---|---|---|---|
| 64 MiB | 0.48 s = 133 MiB/s (4 parts) | 0.19 s (337 MiB/s) | 0.10 s = 620 MiB/s | 0.04 s = 1636 MiB/s |
| 256 MiB | 0.95 s = 270 MiB/s (8 parts) | 0.82 s (314 MiB/s) | 0.57 s = 450 MiB/s | 0.17 s = 1489 MiB/s |
| 1024 MiB | 2.91 s = 351 MiB/s (16 parts) | 2.89 s (354 MiB/s) | 1.68 s = 611 MiB/s | 0.93 s = 1095 MiB/s |

Reading it: parallel ranges are consistently faster than one stream, which is the point of range support; the scanner reads the
object once at several hundred MiB/s, so it is not the bottleneck for a mock, and a real engine would be.

### Against the first build

The first build (`2238384`) was measured the same way on this machine (its figures are in the rebuild brief, section 2.4). The
bar for the rebuild: accepted and applied throughput within 20% of those, and the 1-event request p95 at most 25 ms.

| Load | First build accepted → applied (events/s) | This build accepted → applied (events/s) | Within the bar? |
|---|---|---|---|
| 1 event per request, 1 sender, 300 requests | 98 → 97 | 114 → 113 (+16%, +16%); p95 10 ms | yes |
| 100 events per request, 1 sender, 3,000 events | 1,046 → 947 | 1,519 → 1,419 (+45%, +50%) | yes |
| 500 events per request (the maximum), 1 sender, 4,000 events | 1,036 → 918 | 1,735 → 1,560 (+67%, +70%) | yes |
| 100 events per request, 8 senders, 20,000 events, logging at Information | 1,991 → 1,224 | 3,479 → 2,154 (+75%, +76%) | yes |
| the same with logging at Warning | 2,536 → 1,425 | 3,502 → 2,213 (+38%, +55%) | yes |

Reading it: accepting scales with CPU (parse, canonicalise, hash) until senders queue on the single write gate; applying is capped
by SQLite's single writer at roughly 1,000 to 1,500 events/s here. This build writes one log line per event (the first wrote two),
so Information logging now costs little: 20,405 lines for 20,000 events against 40,605. These are single runs, so the small
differences (for example the Warning run) are within noise. **No figure here is a promise;** the README states none.

## 4. Operated

| Claim | How it was checked | Result |
|---|---|---|
| The service runs and serves the documented flow | Started `dotnet run` on a spare port; a script posted a batch, followed it, read as user and admin, disabled, re-enabled, hard-deleted | Worked as documented |
| The test console drives the service | Ran the console against that service in the browser pane: sent the "Publish an article" scenario, followed the batch to completion, opened the Queue tab | Worked; no browser console errors |
| The README's try-it commands | The fenced blocks tagged `bash try-it`, extracted with the same `awk` as CI, run against the built service on a spare port | Passed (exit 0) on the final build, run on port 5190 with a fresh database: 202 with Location and X-Correlation-Id, batch followed to completed, user and admin reads, 401 and 403 checks, unpublish, re-publish, disable, hard delete, then an asset uploaded, scanned to clean and downloaded, a flagged file rejected, and three parallel ranges that reassemble to the original; no error lines in the service log |
| The test console's Assets tab | Ran the console and the service in the browser pane: uploaded a clean text file, a file with the mock signature, an SVG with a script, an executable named .png, a scanner-error file, and a 24 MiB picture (3 parts in parallel); downloaded the picture as a user with 8 parallel ranges and the hash matched; the CMS account got 403; the rejected files were 404 and wiped; the scanner-error file ended set aside | Worked as described; the admin list showed every verdict |
| Dependency audit | `dotnet list package --vulnerable --include-transitive` over the solution | No vulnerable packages in any project (the CI job fails if that changes) |
| Generated development credentials | Started the service in Development with no credentials supplied: it wrote the git-ignored file, the console found all three accounts, `git check-ignore` confirmed the file is ignored | Worked |
| `secure_delete` makes a hard delete physical | `StorageGuaranteeTests`: a control shows SQLite's default leaves a deleted value in the file after a checkpoint; with it on, a marker is absent from the file and WAL after delete, replace and null | Passed |

## 5. Not proven, and how each is labelled

| Item | Status |
|---|---|
| **CI on Windows, macOS and Linux** | The workflow is written (`.github/workflows/ci.yml`) and **has not been run**: nothing was pushed (the remote is the owner's decision). Everything above ran on Windows only. Not shown: that the suite passes on macOS and Linux |
| **README try-it job in CI** | Written, not run in CI (same reason). The commands themselves were run locally, see section 4 |
| **A real scanner** | Only the mock was built and tested. It shows the quarantine, verdict, wipe and fail-closed behaviour; it says nothing about detecting real malware (limit K19) |
| **Wipe on SSD or copy-on-write storage** | Overwrite-before-delete was shown to zero the bytes through an open handle and to remove the file; whether the physical blocks are gone depends on the device and file system (limit K20) |
| **Development passwords already in git history** | The first commit contains the old development passwords. They are no longer used, but history was not rewritten |
| **Visual Studio launch profile** (`Lateral.slnLaunch`, `.vsconfig`) | Copied from the first build; not opened in Visual Studio here |
| **S3 comparison and generated diagrams** | Not ported from the first build; the design diagram in DESIGN is hand-drawn text |
| **Size budget** (brief section 4.9: about 2,500 production lines, tests at most 4,000) | Production: 5,523 raw lines (47 files, migrations excluded), **4,053 excluding blanks and comments: over the 2,500 budget**, because the asset store, scanner and hardening work were added after the budget was set (the core without them stayed near 2,300). Service tests: 8,152 raw, **6,379 excluding blanks and comments: over the 4,000 budget**, for the same reason. The budget is a guard against sprawl, so this is reported rather than hidden. Measured on 6 and 7 November 2026 (the photo library and its tests on the 7th) |
| **Throughput, capacity, soak** | One machine, a few minutes: an order of magnitude only (limit K2) |
| **Erasure beyond the live file** | Backups, replicas, WAL archives and snapshots are out of scope (limit K6) |
| **Ordering outside the assumption** | Known and pinned, not fixed (section 6 of DESIGN) |
| *Assumed* | One CMS emits the events; credentials are only ever sent over TLS in production; the .NET JSON encoder's output is stable across the patch versions in use (pinned by golden vectors, which would fail if it changed) |
