# Lateral CMS ingestion service

A .NET 9 service that receives a CMS's `publish`, `unPublish` and `delete` events in batches, reconciles them into the
latest state of each entity whatever order they arrive in, and serves that state through an authenticated, role-filtered
REST API. Everything is treated as confidential, and correct event processing matters most.

**How it works in one paragraph.** `POST /cms/events` validates the batch, stores it durably in one transaction and
answers `202` with a link to follow it. A background worker then applies each batch with a pure reducer: an event only
changes state if it sorts after what is stored, so replays do nothing and late or duplicated events cannot undo newer
ones. Users read published entities; admins read everything and can disable an entity locally without touching the CMS.

**Contents:** [Prerequisites](#prerequisites) · [Run it](#run-it) · [Try it](#try-it) · [Test console](#test-console) ·
[Tests](#tests) · [Configuration](#configuration) · [Outside Development](#running-outside-development) ·
[Database](#database-and-migrations) · [API at a glance](#api-at-a-glance) · [Known limits](#known-limits) · [Docs](#docs)

## Prerequisites

- The **.NET 9 SDK** or newer (the projects target `net9.0` and roll forward, so a machine with only .NET 10 also works).
- Windows, macOS or Linux. Nothing else to install: the database is a SQLite file.
- Optional, for the try-it commands below: `curl` and a POSIX shell (Git Bash on Windows).

## Run it

```bash
dotnet run --project src/Lateral.Cms
```

In the `Development` environment (the default for `dotnet run`) the service listens on <http://localhost:5080>, creates
`src/Lateral.Cms/App_Data/cms.db` and applies the migration. The accounts below come from
[appsettings.Development.json](src/Lateral.Cms/appsettings.Development.json), which holds random development-only passwords:

| Account | Username | Role | May |
|---|---|---|---|
| CMS (the sender) | `cms-ingest-svc` | `CmsIngest` | post events, follow its batches |
| User | `user@lateral.example` | `User` | list and read published entities |
| Admin | `admin@lateral.example` | `Admin` | read everything, disable/enable, watch the queue |

In Visual Studio, open `Lateral.sln` and pick the **CMS and test console** startup profile.

## Try it

These commands are run by CI against the built service (the `README try-it` job), so they stay true. They need only `curl`
and a POSIX shell (bash, zsh, Git Bash); on Windows without one, use the [test console](#test-console) or
[requests.http](requests.http) instead. Pipe any response through `jq .` if you have it.

Set up the accounts. The development settings file lists three passwords in order (CMS, user, admin) and the usernames are in
the table above, so nothing is typed here:

```bash try-it
BASE=http://localhost:5080
PASSWORDS=($(grep -oE "[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}" src/Lateral.Cms/appsettings.Development.json))
CMS="cms-ingest-svc:${PASSWORDS[0]}"
READER="user@lateral.example:${PASSWORDS[1]}"
ADMIN="admin@lateral.example:${PASSWORDS[2]}"
ID="demo-$(date +%s)"
```

Send a batch as the CMS: a publish, and a publish whose version is invalid. The answer is `202`, **not** "applied": the
batch is stored and queued, and the response (and its `Location` header) tells you where to follow it.

```bash try-it
HEADERS="$(mktemp)"
RESPONSE="$(curl -sf -u "$CMS" -H "Content-Type: application/json" -D "$HEADERS" "$BASE/cms/events" -d "[
  {\"type\":\"publish\",\"id\":\"$ID\",\"version\":1,\"payload\":{\"title\":\"Hello\"},\"timestamp\":\"2024-01-01T00:00:00Z\"},
  {\"type\":\"publish\",\"id\":\"$ID-bad\",\"version\":0,\"payload\":{},\"timestamp\":\"2024-01-01T00:00:00Z\"}
]")"
grep -iE "^(HTTP|location|x-correlation-id)" "$HEADERS"
echo "$RESPONSE" | sed 's/},{/},\n  {/g'
BATCH="$(echo "$RESPONSE" | sed -E 's/.*"batchId":"([^"]+)".*/\1/')"
```

Follow the batch until it is no longer `queued`. Each event shows its final outcome, reason and checksum, never its payload:

```bash try-it
while curl -sf -u "$CMS" "$BASE/cms/batches/$BATCH" | grep -q '"status":"queued"'; do sleep 0.2; done
curl -sf -u "$CMS" "$BASE/cms/batches/$BATCH" | sed 's/},{/},\n  {/g'
```

Read it as a user, then show that nothing is served without credentials and that users cannot post events:

```bash try-it
curl -sf -u "$READER" "$BASE/api/entities/$ID"; echo
test "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/entities")" = 401
test "$(curl -s -o /dev/null -w '%{http_code}' -u "$READER" -X POST "$BASE/cms/events" -H "Content-Type: application/json" -d "[]")" = 403
```

Unpublish it. Users stop seeing it, with the same `404` as for an id that never existed; admins still can, with a `status` block:

```bash try-it
curl -sf -u "$CMS" -H "Content-Type: application/json" "$BASE/cms/events" -d "[
  {\"type\":\"unPublish\",\"id\":\"$ID\",\"version\":1,\"payload\":{\"title\":\"Hello\"},\"timestamp\":\"2024-01-01T00:01:00Z\"}
]" > /dev/null
until [ "$(curl -s -o /dev/null -w '%{http_code}' -u "$READER" "$BASE/api/entities/$ID")" = 404 ]; do sleep 0.2; done
curl -sf -u "$ADMIN" "$BASE/api/entities/$ID"; echo
```

Publish again and disable it as an admin. The disable is local: a later publish from the CMS does not undo it.

```bash try-it
curl -sf -u "$CMS" -H "Content-Type: application/json" "$BASE/cms/events" -d "[
  {\"type\":\"publish\",\"id\":\"$ID\",\"version\":2,\"payload\":{\"title\":\"Hello again\"},\"timestamp\":\"2024-01-01T00:02:00Z\"}
]" > /dev/null
until curl -sf -u "$ADMIN" "$BASE/api/entities/$ID" | grep -q '"version":2'; do sleep 0.2; done
curl -sf -u "$ADMIN" -X POST "$BASE/api/entities/$ID/disable"; echo
test "$(curl -s -o /dev/null -w '%{http_code}' -u "$READER" "$BASE/api/entities/$ID")" = 404
```

Finally hard-delete it. The row and its content are removed (the file is wiped, see [DESIGN](docs/DESIGN.md)); only a
payload-free marker remains so that a late, older event cannot bring it back:

```bash try-it
curl -sf -u "$CMS" -H "Content-Type: application/json" "$BASE/cms/events" -d "[
  {\"type\":\"delete\",\"id\":\"$ID\",\"timestamp\":\"2024-01-01T00:03:00Z\"}
]" > /dev/null
until [ "$(curl -s -o /dev/null -w '%{http_code}' -u "$ADMIN" "$BASE/api/entities/$ID")" = 404 ]; do sleep 0.2; done
curl -sf -u "$ADMIN" "$BASE/api/admin/ingestion"; echo
```

## Test console

A browser page for driving all of this by hand: send batches from ready-made scenarios (update and replay, out of order,
same version with different content, bad events, an oversized payload, a video by reference), act as the CMS, a user, an
admin, or with wrong credentials, browse entities, and watch the queue. Credentials stay on the console's server; the
browser only ever names an account.

```bash
dotnet run --project src/Lateral.Cms            # terminal 1: the service on :5080
dotnet run --project tools/Lateral.Cms.TestConsole   # terminal 2: the console on http://localhost:5100
```

It listens on loopback only and reaches only an allow-list of the service's routes. In Development it reads the accounts
from the service's development settings. Tabs: **Send events**, **Browse and admin**, **Auth matrix**, **Large files**,
**Queue**, **Endpoints**. The **Browse and admin** tab has a *Reset* button that erases all data; it only exists while
`Admin:AllowReset` is on, which is refused in Production.

## Tests

```bash
dotnet test                                  # everything; the suites finish in well under a minute
CI=true dotnet build -c Release              # the strict build CI runs: warnings are errors
```

The suite uses the real HTTP pipeline and a real SQLite file per test class (no in-memory fakes), a manual clock, and real
faults produced with database triggers. It covers the reconciliation rules (one test per row of the rule table, both
arrival orders for every tie), order independence against an independent oracle on random shuffles and on every
permutation of the curated timelines, parsing and its limits, the checksum, the acknowledgement-after-commit and
atomic-apply guarantees, retry/back-off/set-aside, the queue cap under concurrent senders, restarts, authentication with
valid and invalid combinations, every role on every route, the log contents, and what a hard delete leaves in the file.

- [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) maps every requirement to the tests that prove it, and a test fails if a
  MUST requirement has none.
- [docs/EVIDENCE.md](docs/EVIDENCE.md) records the results: counts, the deliberate-defect log
  (`node tools/mutation-check/run.mjs`), and measurements.
- Load smoke: `dotnet run -c Release --project tools/Lateral.Cms.LoadSmoke -- --senders 8 --batches 25 --events 100`
  starts the built service with generated credentials and prints throughput and latency. It measures; it does not assert.

## Configuration

Set in `appsettings.json` or as environment variables (`Ingestion__MaxBatchSize=100`). Invalid values stop startup and
name the setting.

| Key | Default | Meaning |
|---|---|---|
| `Database:ConnectionString` | `Data Source=App_Data/cms.db` | SQLite file, relative to the project folder |
| `Database:ReadConnectionString` | the same file, read-only | could point at a replica |
| `Database:ApplyMigrationsOnStartup` | `true` | |
| `Auth:Cms:Username` / `Passwords` | none | username 10 to 20 characters; each password a GUID; several may be active to rotate |
| `Auth:Users` | none | list of `{Username, Password (12+ chars), Role: User or Admin}` |
| `Auth:MaxFailuresPerMinute` | `10` | failed sign-ins per address before `429`; `0` disables |
| `Ingestion:MaxBodyBytes` / `MaxBatchSize` / `MaxPayloadBytes` | 5 MiB / 500 / 256 KiB | refused with `413`, `413`, and a rejected event |
| `Ingestion:MaxJsonDepth` / `MaxFutureSkew` | 32 / 1 hour | |
| `Ingestion:ChecksumAlgorithm` | `sha256` | `sha256` or `md5` |
| `Inbox:WorkerEnabled` | `true` | tests turn it off and drive the processor |
| `Inbox:PollInterval` / `MaxAttempts` | 1 s / 5 | |
| `Inbox:RetryBaseDelay` / `RetryMaxDelay` / `FaultDelay` | 2 s / 60 s / 5 s | back-off doubles up to the maximum |
| `Inbox:MaxBytes` | 256 MiB | queue size cap; beyond it `429` and nothing is stored |
| `Api:DefaultPageSize` / `MaxPageSize` | 50 / 100 | |
| `Admin:AllowReset` | `false` | test-only erase endpoint; refused at startup in Production |

## Running outside Development

There are no credentials in `appsettings.json`: the service refuses to start without them. Supply them as environment
variables (or any configuration source), for example:

```bash
export ASPNETCORE_ENVIRONMENT=Production
export Auth__Cms__Username=cms-ingest-svc
export Auth__Cms__Passwords__0=$(uuidgen)
export Auth__Users__0__Username=reader@example.org
export Auth__Users__0__Password='at-least-12-characters'
export Auth__Users__0__Role=User
export Auth__Users__1__Username=admin@example.org
export Auth__Users__1__Password='another-long-password'
export Auth__Users__1__Role=Admin
export Database__ConnectionString='Data Source=/var/lib/lateral/cms.db'
dotnet run --project src/Lateral.Cms -c Release
```

Production turns on HTTPS redirection and HSTS. **Basic authentication sends the password with every request, so run it
behind TLS only.** The service is a single instance with a single writer; see [Known limits](#known-limits).

## Database and migrations

SQLite in WAL mode, accessed through EF Core with two contexts: a write context (tracking, the only one that saves) and a
read context (no tracking, opened `Mode=ReadOnly`, throws on save). `secure_delete` is on for every connection, so a hard
delete wipes the bytes from the file rather than only unlinking them. There is one migration, applied at startup. To
change the model:

```bash
dotnet tool restore
DOTNET_ROLL_FORWARD=Major dotnet ef migrations add <Name> --project src/Lateral.Cms --context CmsWriteDbContext --output-dir Persistence/Migrations
```

(`DOTNET_ROLL_FORWARD=Major` is only needed on a machine that has just a newer SDK than the 9.0 tool.)

## API at a glance

| Route | Who | |
|---|---|---|
| `GET /health` | anyone | `{"status":"ok"}` after a database check |
| `POST /cms/events` | CMS | a JSON array of events; `202` + `Location`, or `200` when nothing was left to queue |
| `GET /cms/batches/{id}` | CMS, admin | `queued`, `completed` or `failed`, with each event's outcome |
| `GET /api/entities?limit=&cursor=` and `GET /api/entities/{id}` | user, admin | keyset paging; admins also get a `status` block |
| `POST /api/entities/{id}/disable` and `/enable` | admin | local override, idempotent |
| `GET /api/admin/ingestion`, `POST /api/admin/ingestion/{batchId}/requeue` | admin | queue depth and age; put a failed batch back |

Whole-request errors are problem+json with a stable `code` (`invalid_json`, `invalid_batch`, `payload_too_large`,
`batch_too_large`, `inbox_full`, `storage_unavailable`, ...); per-event outcomes are `applied`, `ignored_stale`,
`ignored_duplicate`, `ignored_unsupported_type`, `rejected` (and `queued`) with a reason code. A bad event never fails
the batch, because a webhook sender retries a failed batch forever. The full contract is in [DESIGN](docs/DESIGN.md).

## Known limits

In order of importance (the reasoning and the next step for each is in [DESIGN](docs/DESIGN.md#limits)):

1. **One instance, one writer** (SQLite). Throughput is bounded by it; see EVIDENCE for what was measured. Scale-out needs a
   server database and several workers claiming batches.
2. **Ordering holds under one assumption:** for one id, a higher version never carries an earlier timestamp, and ids are not
   reused with restarting versions. Outside it, arrival order can matter; both counterexamples are pinned by tests.
3. **Basic auth with secrets in configuration.** Fine for this brief, not for production: use a secret store, signed
   requests for the CMS, and TLS at the edge.
4. **"Hard delete" covers the live database file only.** Backups, replicas and WAL archives need their own erasure policy;
   there is no encryption at rest and no retention job for the event history.
5. **No resync or replay.** A batch that never arrives is visible only as a gap; the CMS would need to send sequence numbers.
6. **.NET 9 leaves support on 10 November 2026.** The projects target `net9.0` and roll forward, so moving to `net10.0` is a
   one-line change in `Directory.Build.props`.

## Docs

| | |
|---|---|
| [docs/DESIGN.md](docs/DESIGN.md) | the decisions, the contract, the rules, the limits |
| [docs/GUIDELINES.md](docs/GUIDELINES.md) | the rules the code is held to, used as a review checklist |
| [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) | every requirement, its priority and evidence, and the tests that prove it |
| [docs/EVIDENCE.md](docs/EVIDENCE.md) | test counts, the deliberate-defect log, measurements |
| [docs/REBUILD-BRIEF.md](docs/REBUILD-BRIEF.md) | the brief this rebuild was built from |
