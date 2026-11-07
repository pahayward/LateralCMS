// Draws the design diagrams as SVG files, and a JSON file per diagram with the same shapes' positions and what each one is,
// so the engineering console can lay clickable hotspots over the picture and explain every box.
//
//   node tools/diagrams/build.mjs
//
// The SVGs carry their own light and dark colours, so they look right on GitHub, in a document, and in the console.

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'Lateral.Cms.TestConsole', 'wwwroot', 'diagrams');
mkdirSync(out, { recursive: true });

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const STYLE = `
  <style>
    :root { --bg:#ffffff; --box:#f3f5fb; --stroke:#c4cce0; --text:#1a2133; --muted:#5b6579;
            --ingest:#3552d6; --asset:#1f9d63; --read:#d4761c; --admin:#8a4fd8; --store:#66708a; --bad:#c23a3f; }
    @media (prefers-color-scheme: dark) {
      :root { --bg:#171b26; --box:#212737; --stroke:#3a4560; --text:#e8ebf4; --muted:#9aa4ba;
              --ingest:#8aa0ff; --asset:#58d39b; --read:#f0a35a; --admin:#c19bff; --store:#9aa4ba; --bad:#ff8f93; }
    }
    text { font-family: system-ui, -apple-system, "Segoe UI", Roboto, Arial, sans-serif; fill: var(--text); }
    .bg { fill: var(--bg); }
    .title { font-size: 20px; font-weight: 700; }
    .lede { font-size: 13px; fill: var(--muted); }
    .h { font-size: 14px; font-weight: 650; }
    .s { font-size: 11.5px; fill: var(--muted); }
    .lab { font-size: 11.5px; font-weight: 600; paint-order: stroke; stroke: var(--bg); stroke-width: 5px; stroke-linejoin: round; }
    .halo { paint-order: stroke; stroke: var(--bg); stroke-width: 6px; stroke-linejoin: round; }
    .group { fill: none; stroke: var(--stroke); stroke-width: 1.5; stroke-dasharray: 6 5; }
    .box { fill: var(--box); stroke: var(--stroke); stroke-width: 1.6; }
    .ingest .box, .ingest.box { stroke: var(--ingest); } .ingest .h { fill: var(--ingest); }
    .asset .box, .asset.box { stroke: var(--asset); } .asset .h { fill: var(--asset); }
    .read .box, .read.box { stroke: var(--read); } .read .h { fill: var(--read); }
    .admin .box, .admin.box { stroke: var(--admin); } .admin .h { fill: var(--admin); }
    .store .box, .store.box { stroke: var(--store); } .store .h { fill: var(--text); }
    .bad .box { stroke: var(--bad); } .bad .h { fill: var(--bad); }
    .a { fill: none; stroke-width: 2.2; }
    .a.ingest { stroke: var(--ingest); } .a.asset { stroke: var(--asset); } .a.read { stroke: var(--read); } .a.admin { stroke: var(--admin); }
    .a.bad { stroke: var(--bad); } .a.store { stroke: var(--store); }
    .dash { stroke-dasharray: 7 5; }
    .life { stroke: var(--stroke); stroke-width: 1.5; stroke-dasharray: 4 5; }
    .dot { stroke: none; }
    .dot.ingest { fill: var(--ingest); } .dot.asset { fill: var(--asset); } .dot.read { fill: var(--read); } .dot.admin { fill: var(--admin); } .dot.bad { fill: var(--bad); }
    .num { font-size: 12px; font-weight: 700; fill: #fff; text-anchor: middle; }
    .mid { text-anchor: middle; }
  </style>`;

const defs = (lanes) => `<defs>${lanes.map((l) => `<marker id="m-${l}" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" class="dot ${l}"/></marker>`).join('')}</defs>`;
const LANES = ['ingest', 'asset', 'read', 'admin', 'bad', 'store'];

// ---------------------------------------------------------------- tiny drawing kit

function kit() {
  const parts = { back: [], arrows: [], boxes: [], text: [] };
  const nodes = [];
  return {
    parts, nodes,
    group(x, y, w, h, label) {
      parts.back.push(`<rect class="group" x="${x}" y="${y}" width="${w}" height="${h}" rx="14"/>`);
      if (label) parts.text.push(`<text class="s halo" x="${x + 16}" y="${y + 20}">${esc(label)}</text>`);
    },
    /** A labelled box. lines are the small lines under the title. */
    box(def) {
      const { id, x, y, w, h, title, lines = [], lane = 'store', rx = 10 } = def;
      parts.boxes.push(`<g class="${lane}" data-id="${id}"><rect class="box" x="${x}" y="${y}" width="${w}" height="${h}" rx="${rx}"/></g>`);
      const top = y + 22;
      parts.text.push(`<g class="${lane}"><text class="h" x="${x + w / 2}" y="${top}" text-anchor="middle">${esc(title)}</text></g>`);
      lines.forEach((line, i) => parts.text.push(`<text class="s" x="${x + w / 2}" y="${top + 17 + i * 14}" text-anchor="middle">${esc(line)}</text>`));
      nodes.push({ id, x, y, w, h, title, lane, text: def.text ?? '', points: def.points ?? [], code: def.code ?? [], tests: def.tests ?? [] });
    },
    arrow(lane, d, { dashed = false, both = false } = {}) {
      parts.arrows.push(`<path class="a ${lane}${dashed ? ' dash' : ''}" d="${d}" marker-end="url(#m-${lane})"${both ? ` marker-start="url(#m-${lane})"` : ''}/>`);
    },
    label(x, y, text, anchor = 'start') { parts.text.push(`<text class="lab" x="${x}" y="${y}" text-anchor="${anchor}">${esc(text)}</text>`); },
    raw(svg) { parts.text.push(svg); },
    svg(width, height, heading, lede) {
      return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${height}" width="${width}" height="${height}" role="img" aria-label="${esc(heading)}">${STYLE}${defs(LANES)}
<rect class="bg" width="${width}" height="${height}"/>
<text class="title" x="24" y="32">${esc(heading)}</text><text class="lede" x="24" y="52">${esc(lede)}</text>
${parts.back.join('\n')}
${parts.arrows.join('\n')}
${parts.boxes.join('\n')}
${parts.text.join('\n')}
</svg>\n`;
    },
  };
}

function write(name, doc, svg) {
  writeFileSync(join(out, `${name}.svg`), svg);
  writeFileSync(join(out, `${name}.json`), JSON.stringify(doc, null, 2) + '\n');
  console.log(`${name}.svg  ${doc.nodes.length} shapes`);
}

// ================================================================ 1. the architecture

(() => {
  const k = kit();
  const W = 1200;
  const col = (i) => 24 + i * 240; // five columns, 192 wide
  const cw = 192;
  const mid = (i) => col(i) + cw / 2;

  // who talks to it
  k.box({ id: 'cms', x: 24, y: 66, w: 432, h: 54, lane: 'ingest', title: 'The CMS', lines: ['the source of truth: publishes, unpublishes, deletes, uploads files'],
    text: 'The content-management system that owns the content. It is the only caller allowed to write. It tells this service what changed by POSTing events, and sends files as assets.',
    points: ['One account (the "CMS account"), several passwords may be live so one can be rotated without downtime.', 'It never reads the API: the CMS account gets 403 on every read route.', 'This service is a read model: if it were wiped, the CMS could replay its events and rebuild it.'],
    code: ['src/Lateral.Cms/Auth/AuthExtensions.cs'], tests: ['RoleMatrixTests'] });
  k.box({ id: 'users', x: 504, y: 66, w: 432, h: 54, lane: 'read', title: 'Users and apps', lines: ['read published records and download clean files (the photo library is one)'],
    text: 'Anyone who consumes the content: websites, apps, the photo library in this repository. Read-only. They see only published records that no admin has disabled, and only files the scanner has cleared.',
    points: ['A hidden record answers exactly like one that never existed (404), so the API never confirms a restricted record is there.', 'The photo library keeps everything in this service: photos are records, pictures are assets.'],
    code: ['src/Lateral.Cms/Entities/EntityQueries.cs'], tests: ['EntitiesEndpointTests', 'OverrideAndPolicyTests'] });
  k.box({ id: 'admins', x: 984, y: 66, w: 192, h: 54, lane: 'admin', title: 'Admins', lines: ['override, requeue, re-scan'],
    text: 'People who operate the service. They see everything (with a status), can hide a record locally without touching the CMS, and run the operational tools.',
    points: ['Every admin action is written to an append-only audit log in the same transaction as the change.', 'There is a test-only "erase everything" route. It exists only where it was switched on, never in Production.'],
    code: ['src/Lateral.Cms/Admin/AdminEndpoints.cs'], tests: ['HardeningTests', 'ResetEndpointTests'] });

  // the HTTP edge
  k.group(24, 138, 1152, 124, 'HTTP edge (ASP.NET Core): Basic authentication, three roles, a throttle on failed sign-ins, body and rate limits, a correlation id on every acknowledgement');
  const route = (i, id, title, lane, lines, def) => k.box({ id, x: col(i), y: 174, w: cw, h: 70, lane, title, lines, ...def });
  route(0, 'r-webhook', 'POST /cms/events', 'ingest', ['the webhook', 'array of up to 500 events'], {
    text: 'The one door for content changes. The declared length is checked before a byte is read, so an oversized body is refused (413) without being transferred or held in memory.',
    points: ['Only the CMS account may call it.', 'A bounded number of batches are read at once; overflow gets 429 with Retry-After.', 'The answer is 202: stored, not yet applied.'],
    code: ['src/Lateral.Cms/Ingestion/IngestionEndpoints.cs'], tests: ['IngestionEndpointTests', 'BasicAuthTests', 'RouteAndTransportTests'] });
  route(1, 'r-upload', 'PUT /cms/assets/…', 'asset', ['simple upload', 'or multipart, in parts'], {
    text: 'How the CMS sends files. One PUT up to a limit, or an S3-style multipart upload: start, send parts in parallel, complete.',
    points: ['Each part may carry a SHA-256 that is checked.', 'Parts can be re-sent; the last one wins.', 'Everything goes to quarantine first.'],
    code: ['src/Lateral.Cms/Assets/AssetEndpoints.cs'], tests: ['UploadTests'] });
  route(2, 'r-read', 'GET /api/entities', 'read', ['list (by cursor)', 'and read one record'], {
    text: 'The read API. Records come back in id order, a page at a time, by an opaque cursor, so the cost of a page does not grow with how deep you are.',
    points: ['There is deliberately no route that changes a record: the CMS is the only source of truth.', 'Users and admins only; the CMS account gets 403.'],
    code: ['src/Lateral.Cms/Entities/EntityEndpoints.cs'], tests: ['EntitiesEndpointTests', 'RouteAndTransportTests'] });
  route(3, 'r-download', 'GET /api/assets/{id}', 'asset', ['Range, ETag, HEAD', 'parallel downloads'], {
    text: 'Downloads stream with byte ranges, so many ranges of one large file can be fetched at once and an interrupted download can resume. The ETag is the file\'s SHA-256.',
    points: ['Always an attachment, never sniffed or framed.', 'A per-caller cap on concurrent downloads; beyond it, 429.'],
    code: ['src/Lateral.Cms/Assets/AssetEndpoints.cs'], tests: ['DownloadTests'] });
  route(4, 'r-admin', '/api/admin/*', 'admin', ['disable, enable, requeue', 're-scan, delete, queue report'], {
    text: 'The operational routes. Admin role only.',
    points: ['Disable hides a record from users without touching the CMS.', 'Requeue puts a failed batch (or a set-aside asset) back in line.', 'The queue report is what an operator watches.'],
    code: ['src/Lateral.Cms/Admin/AdminEndpoints.cs', 'src/Lateral.Cms/Ingestion/BatchEndpoints.cs'], tests: ['RoleMatrixTests', 'HardeningTests'] });

  // services
  const service = (i, id, title, lane, lines, def) => k.box({ id, x: col(i), y: 294, w: cw, h: 72, lane, title, lines, ...def });
  service(0, 's-accept', 'Accept', 'ingest', ['validate every event,', 'checksum, store in one', 'transaction'], {
    text: 'Validates each event on its own, so one bad event never spoils the rest, then stores the whole batch and every accepted event in a single transaction before answering.',
    points: ['Each rule has a stable reason code (invalid_version, payload_too_large, timestamp_in_future…).', 'The payload is checksummed in a canonical form so the same content always has the same checksum.', 'Answered only after the commit: an acknowledgement means it is safely on disk.'],
    code: ['src/Lateral.Cms/Ingestion/AcceptService.cs', 'src/Lateral.Cms/Ingestion/EventParser.cs'], tests: ['AcceptanceTests', 'EventParserTests', 'PayloadChecksumTests'] });
  service(1, 's-intake', 'Asset intake', 'asset', ['stream to disk, assemble', 'parts, verify SHA-256,', 'enforce the quota'], {
    text: 'Streams the bytes straight to disk (never held in memory), assembles multipart parts, verifies sizes and checksums, and enforces the store\'s quota before accepting anything.',
    points: ['A file is not served until it has been scanned.', 'Identical content is stored once.'],
    code: ['src/Lateral.Cms/Assets/AssetService.cs', 'src/Lateral.Cms/Assets/FileAssetStore.cs'], tests: ['UploadTests', 'LargeAssetTests'] });
  service(2, 's-read', 'Read queries', 'read', ['a read-only database', 'connection; the caller\'s', 'role decides visibility'], {
    text: 'Reads through a separate, read-only database context. What a caller may see is decided here, from their role, never from a request parameter.',
    points: ['Users: published and not disabled. Admins: everything, with a status block.', 'Keyset pagination: stable under inserts, constant cost per page.'],
    code: ['src/Lateral.Cms/Entities/EntityQueries.cs'], tests: ['EntitiesEndpointTests', 'EntityPagingTests', 'ListingIndexTests'] });
  service(3, 's-serve', 'Asset serving', 'asset', ['only clean files;', 'ranges and ETags;', 'caller limits'], {
    text: 'Looks the asset up and serves it only if its state is clean. Quarantined, rejected and set-aside assets are the same 404 as a missing one.',
    points: ['Range requests are handled by the framework\'s file streaming (206, 416, If-Range).', 'A range in an unknown unit is ignored (RFC 9110).'],
    code: ['src/Lateral.Cms/Assets/AssetEndpoints.cs'], tests: ['DownloadTests', 'ScanPipelineTests'] });
  service(4, 's-admin', 'Admin services', 'admin', ['disable, requeue, re-scan,', 'delete; every one audited'], {
    text: 'The operational actions. Each writes an audit row in the same transaction as the change.',
    points: ['Delete of an asset overwrites the bytes with zeros before removing the file (best effort on SSDs and copy-on-write filesystems).'],
    code: ['src/Lateral.Cms/Entities/EntityAdminService.cs', 'src/Lateral.Cms/Assets/AssetService.cs'], tests: ['HardeningTests', 'HousekeepingTests'] });

  // stores
  const store = (i, id, title, lane, lines, def) => k.box({ id, x: col(i), y: 404, w: cw, h: 72, lane, title, lines, ...def });
  store(0, 'db-queue', 'Queue and ledger', 'ingest', ['Batches + Events', 'SQLite table'], {
    text: 'One table is both the work queue and the permanent record of every event received, so what was received is never in doubt.',
    points: ['Indexed so the worker\'s "next batch" is a seek, not a scan.', 'The queue has a size cap; a full queue answers 429 and stores nothing.', 'A failed batch is set aside (never retried forever, never dropped) until an admin requeues it.'],
    code: ['src/Lateral.Cms/Persistence/CmsDbContext.cs'], tests: ['InboxIndexTests', 'InboxFailureTests'] });
  store(1, 'disk-quarantine', 'Quarantine', 'asset', ['files not yet cleared', 'plain folder on disk'], {
    text: 'Where every upload waits. Nothing here can be downloaded. A flagged file is wiped from here and only its verdict is kept.',
    points: ['Parts of unfinished multipart uploads live beside it and expire.'],
    code: ['src/Lateral.Cms/Assets/FileAssetStore.cs'], tests: ['ScanPipelineTests', 'HousekeepingTests'] });
  store(2, 'db-entities', 'Current state', 'read', ['Entities + Tombstones', 'SQLite tables'], {
    text: 'The latest state of every record, plus a payload-free tombstone for each deleted id so an older event can never bring it back.',
    points: ['secure_delete is on for every connection, so a deleted payload is overwritten in the file, not just unlinked.', 'WAL mode: readers never block the single writer.'],
    code: ['src/Lateral.Cms/Persistence/CmsDbContext.cs'], tests: ['StorageGuaranteeTests', 'PersistenceGuaranteeTests'] });
  store(3, 'disk-blobs', 'Clean files', 'asset', ['content-addressed blobs', 'blobs/ab/cd/<sha256>'], {
    text: 'Cleared files, stored by the SHA-256 of their content, so identical files are stored once. A file reaches here only by an atomic move after a clean verdict.',
    points: ['Deleting the last asset that uses a blob removes the blob.'],
    code: ['src/Lateral.Cms/Assets/FileAssetStore.cs'], tests: ['DownloadTests', 'HousekeepingTests'] });
  store(4, 'db-audit', 'Audit log', 'admin', ['append-only SQLite table'], {
    text: 'Who did what to what, and when. Database triggers refuse any update or delete of a row, so history cannot be rewritten through this service.',
    points: ['Written in the same transaction as the action it records.', 'The test-only reset erases records but keeps the audit log.'],
    code: ['src/Lateral.Cms/Persistence/AuditExtensions.cs', 'src/Lateral.Cms/Persistence/Migrations/20261106151459_Assets.cs'], tests: ['HardeningTests'] });

  // workers
  k.box({ id: 'w-batch', x: col(0), y: 534, w: cw, h: 64, lane: 'ingest', title: 'Batch worker', lines: ['applies each batch with', 'a pure reducer'],
    text: 'A background worker that takes the oldest due batch and applies each event with a pure function: an event changes the stored state only if it sorts after it. The result does not depend on arrival order.',
    points: ['Replays and old versions are ignored, not applied twice.', 'Same version with different content: a deterministic winner, flagged as a conflict.', 'On failure it retries with back-off, then sets the batch aside; a poisoned batch never blocks the ones behind it.', 'A takedown (unpublish or delete) batch is retried forever, never set aside: the item stays visible until it applies, so it must apply.'],
    code: ['src/Lateral.Cms/Ingestion/BatchWorker.cs', 'src/Lateral.Cms/Ingestion/BatchProcessor.cs', 'src/Lateral.Cms/Domain/Reducer.cs'], tests: ['ReducerTests', 'ReducerPropertyTests', 'InboxWorkerTests'] });
  k.box({ id: 'w-scan', x: col(3), y: 534, w: cw, h: 64, lane: 'asset', title: 'Scan worker', lines: ['IAssetScanner (a mock)', 'clean, reject or retry'],
    text: 'Scans quarantined files outside the write lock, then in one transaction records the verdict. Clean: atomic move into the blob store. Infected or suspicious: wipe and keep only the verdict.',
    points: ['Fails closed: a scanner error or timeout leaves the file quarantined, retries with back-off, then sets it aside (still unserved).', 'The scanner sits behind an interface: a real engine such as ClamAV replaces the mock without changing the pipeline.', 'The mock is not malware protection; it proves the pipeline.'],
    code: ['src/Lateral.Cms/Assets/AssetWorkers.cs', 'src/Lateral.Cms/Assets/ScanProcessor.cs', 'src/Lateral.Cms/Assets/MockAssetScanner.cs'], tests: ['ScanPipelineTests', 'ScannerTests'] });
  k.box({ id: 'w-sweep', x: col(4), y: 534, w: cw, h: 64, lane: 'asset', title: 'Sweeper', lines: ['expires uploads, removes', 'files nothing accounts for'],
    text: 'Housekeeping: abandoned multipart uploads expire, and files no row accounts for (stray parts, temp files, orphan blobs) are removed after a grace period.',
    points: ['It never takes a young file or one that is accounted for.'],
    code: ['src/Lateral.Cms/Assets/AssetWorkers.cs'], tests: ['HousekeepingTests'] });

  // the cross-cutting bar
  k.box({ id: 'cross', x: 24, y: 636, w: 1152, h: 44, lane: 'store', title: 'Everywhere: structured logs (never payloads or passwords) · metrics (queue depth, oldest age, failed bytes) · correlation ids · every call repeatable as curl', lines: [],
    text: 'What lets an operator trust and debug the service.',
    points: ['Logs carry batch ids, counts and reason codes, never a payload, a password or an Authorization header.', 'Metrics include the queue depth and the age of the oldest waiting batch: a queue that quietly falls behind is the main risk of asynchronous processing.'],
    code: ['src/Lateral.Cms/Ingestion/CmsMetrics.cs', 'src/Lateral.Cms/Ingestion/IngestionLog.cs'], tests: ['ObservabilityTests'] });

  // arrows, one lane each
  const down = (lane, x, y1, y2) => k.arrow(lane, `M${x} ${y1} V${y2}`);
  down('ingest', mid(0), 120, 174); down('ingest', mid(0), 244, 294); down('ingest', mid(0), 366, 404);
  k.arrow('ingest', `M${mid(0)} 534 V476`, { both: true });
  k.label(mid(0) + 8, 396, 'one transaction');
  k.label(mid(0) + 8, 512, 'reads, then writes outcome');
  k.arrow('ingest', `M${col(0) + cw} 566 H${mid(2)} V476`);
  k.label(col(0) + cw + 14, 556, 'applies', 'start');
  down('asset', mid(1), 120, 174); down('asset', mid(1), 244, 294); down('asset', mid(1), 366, 404);
  down('read', mid(2), 120, 174); down('read', mid(2), 244, 294); down('read', mid(2), 366, 404);
  k.arrow('asset', `M${mid(3)} 120 V174`); down('asset', mid(3), 244, 294); down('asset', mid(3), 366, 404);
  k.label(mid(3) + 8, 398, 'reads');
  k.arrow('asset', `M${mid(3)} 534 V476`);
  k.label(mid(3) + 8, 516, 'clean: move');
  k.arrow('asset', `M${col(3)} 582 H${mid(1)} V476`);
  k.label(col(3) - 14, 574, 'reads', 'end');
  down('admin', mid(4), 120, 174); down('admin', mid(4), 244, 294); down('admin', mid(4), 366, 404);

  const nodes = k.nodes;
  const walks = [
    { id: 'event', title: 'An event arrives', steps: [
      { nodes: ['cms'], text: 'The CMS publishes, unpublishes or deletes something and POSTs a batch of events to the webhook.' },
      { nodes: ['r-webhook'], text: 'The webhook checks who is calling (only the CMS account may) and the declared size, before reading any of the body.' },
      { nodes: ['s-accept'], text: 'Each event is validated on its own with a stable reason code, and the payload is checksummed in a canonical form.' },
      { nodes: ['db-queue'], text: 'The whole batch and every accepted event are stored in one transaction. Only then does the CMS get its answer: 202, with where to follow it.' },
      { nodes: ['w-batch'], text: 'A background worker takes the batch and applies each event with a pure function. Replays, old versions and late events change nothing.' },
      { nodes: ['db-entities'], text: 'The record\'s latest state (or a tombstone, for a delete) is written in the same transaction that marks the batch completed.' },
      { nodes: ['users', 'r-read', 's-read'], text: 'Later, a user reads the record. What they may see is decided by their role: users see published and enabled records; admins see everything.' },
    ] },
    { id: 'file', title: 'A file arrives', steps: [
      { nodes: ['cms', 'r-upload'], text: 'The CMS uploads a file: one PUT, or multipart parts sent in parallel. Each part can carry a SHA-256 that is checked.' },
      { nodes: ['s-intake'], text: 'The bytes are streamed to disk, never held in memory. Sizes, checksums and the quota are checked.' },
      { nodes: ['disk-quarantine'], text: 'The file waits in quarantine. Nothing here can be downloaded: a user asking for it gets the same 404 as for a file that does not exist.' },
      { nodes: ['w-scan'], text: 'The scan worker asks the scanner. If it errors or times out the file stays quarantined and is retried: fail closed.' },
      { nodes: ['disk-blobs'], text: 'Clean: the file is moved atomically into the content-addressed store. Infected: wiped, and only the verdict is kept.' },
      { nodes: ['users', 'r-download', 's-serve'], text: 'Only now can a user download it, with byte ranges, so many ranges of a big file can be fetched in parallel and a download resumed.' },
    ] },
    { id: 'admin', title: 'An admin steps in', steps: [
      { nodes: ['admins', 'r-admin'], text: 'An admin signs in. Admin routes are closed to every other role.' },
      { nodes: ['s-admin'], text: 'They hide a record, requeue a failed batch, re-scan an asset or delete one. Each action does one thing and checks its own preconditions.' },
      { nodes: ['db-audit'], text: 'Every action is recorded in the audit log in the same transaction as the change. The log cannot be updated or deleted through this service.' },
    ] },
  ];

  const doc = { id: 'architecture', title: 'Architecture', width: W, height: 706, nodes, walks };
  write('architecture', doc, k.svg(W, 706, 'Lateral CMS: architecture', 'Blue: events in. Green: files. Orange: reads. Purple: admin. Boxes are clickable in the engineering console.'));
})();

// ================================================================ 2. the life of an event

(() => {
  const k = kit();
  const W = 1180;
  const lifelines = [
    ['cms', 150, 'The CMS', 'ingest'], ['api', 400, 'Service API', 'ingest'], ['db', 650, 'Database', 'store'], ['worker', 890, 'Batch worker', 'ingest'], ['reader', 1090, 'A reader', 'read'],
  ];
  const X = Object.fromEntries(lifelines.map(([id, x]) => [id, x]));
  const top = 70;
  const bottom = 700;
  for (const [id, x, title, lane] of lifelines) {
    k.parts.back.push(`<line class="life" x1="${x}" y1="${top + 44}" x2="${x}" y2="${bottom}"/>`);
    k.box({ id: `p-${id}`, x: x - 70, y: top, w: 140, h: 44, lane, title, lines: [], text: '' });
  }

  let n = 0;
  const step = (id, y, from, to, label, lane, def = {}) => {
    n++;
    const x1 = X[from];
    const x2 = X[to];
    if (from === to) {
      k.arrow(lane, `M${x1} ${y} h46 v26 h-46`, { dashed: def.dashed });
      k.label(x1 + 58, y + 18, label, 'start');
    } else {
      k.arrow(lane, `M${x1 + (x2 > x1 ? 6 : -6)} ${y} H${x2 + (x2 > x1 ? -6 : 6)}`, { dashed: def.dashed });
      k.label((x1 + x2) / 2, y - 8, label, 'middle');
    }

    // The number sits just outside the sending end of the arrow, so it never covers the words on it.
    const cx = x2 > x1 || from === to ? x1 - 24 : x1 + 24;
    k.parts.text.push(`<circle class="dot ${lane}" cx="${cx}" cy="${y}" r="11"/><text class="num" x="${cx}" y="${y + 4}">${n}</text>`);
    k.nodes.push({ id, x: Math.min(x1, x2) - 38, y: y - 24, w: Math.abs(x2 - x1) + 76 + (from === to ? 250 : 0), h: from === to ? 56 : 36, title: `${n}. ${def.title}`, lane, text: def.text, points: def.points ?? [], code: def.code ?? [], tests: def.tests ?? [] });
  };

  step('e1', 150, 'cms', 'api', 'POST /cms/events  [ events ]', 'ingest', { title: 'The CMS posts a batch', text: 'A JSON array of up to 500 events: publish, unPublish or delete, each with an id, a timestamp and (except delete) a version and payload.', points: ['The declared length is checked first: over 5 MiB is a 413 before any body is read.', 'Only the CMS account may call this.'], code: ['src/Lateral.Cms/Ingestion/IngestionEndpoints.cs'], tests: ['IngestionEndpointTests'] });
  step('e2', 210, 'api', 'api', 'validate each event, checksum the payload', 'ingest', { title: 'Validate, one event at a time', text: 'Every event is checked on its own, so a bad one is rejected with its own reason and never spoils the rest of the batch.', points: ['Stable reason codes: invalid_version, missing_id, payload_too_large, timestamp_in_future…', 'The payload is hashed in a canonical form, so the same content always has the same checksum.'], code: ['src/Lateral.Cms/Ingestion/EventParser.cs'], tests: ['EventParserTests', 'PayloadChecksumTests'] });
  step('e3', 300, 'api', 'db', 'one transaction: batch + accepted events', 'ingest', { title: 'Commit before answering', text: 'The batch and every accepted event are written in a single transaction. Nothing is acknowledged until it is durably on disk.', points: ['A crash before the commit leaves nothing; a crash after leaves a queued batch that the worker will find on restart.'], code: ['src/Lateral.Cms/Ingestion/AcceptService.cs'], tests: ['AcceptanceTests', 'InboxWorkerTests'] });
  step('e4', 370, 'api', 'cms', '202 Accepted  { batchId, results: queued … }', 'ingest', { title: 'Acknowledge (202)', dashed: true, text: 'The CMS learns that its batch is safe and where to follow it (the Location header). Events that were rejected already carry their reason; accepted ones say queued.', points: ['202 means stored, not applied. That is the honest answer for asynchronous processing.', 'The answer also carries a correlation id for support.'], code: ['src/Lateral.Cms/Ingestion/IngestionEndpoints.cs'], tests: ['AcceptanceTests', 'ObservabilityTests'] });
  step('e5', 450, 'worker', 'db', 'take the oldest due batch', 'ingest', { title: 'The worker picks it up', text: 'Soon after (a wake signal, or its next poll), the worker takes the oldest batch that is due.', points: ['The pick is an index seek, so cost does not grow with the queue.'], code: ['src/Lateral.Cms/Ingestion/BatchProcessor.cs'], tests: ['InboxIndexTests'] });
  step('e6', 510, 'worker', 'worker', 'reduce each event: a pure function', 'ingest', { title: 'Decide each event', text: 'For each event the reducer compares it with the stored state and decides: applied, ignored (a replay, or older than what is stored) or a conflict with a deterministic winner.', points: ['The result does not depend on arrival order: the same events in any order end in the same state.', 'This is tested against an independent oracle on random shuffles and on every permutation of curated timelines.'], code: ['src/Lateral.Cms/Domain/Reducer.cs'], tests: ['ReducerTests', 'ReducerPropertyTests'] });
  step('e7', 590, 'worker', 'db', 'one transaction: state, outcomes, completed', 'ingest', { title: 'Commit the result', text: 'The record\'s new state (or tombstone), every event\'s final outcome and the batch\'s completion are written in one transaction, so a reader can never see half a batch.', points: ['If this fails, the batch is retried with back-off, then set aside after too many attempts.', 'Except takedowns (unpublish, delete): those are retried forever, because the item stays visible until they apply.'], code: ['src/Lateral.Cms/Ingestion/BatchProcessor.cs'], tests: ['InboxFailureTests'] });
  step('e8', 650, 'cms', 'api', 'GET /cms/batches/{id}  →  completed, final outcomes', 'ingest', { title: 'The CMS follows up', text: 'The CMS (or an admin) can ask where its batch got to and sees each event\'s final outcome. Payloads are never returned here.', code: ['src/Lateral.Cms/Ingestion/BatchEndpoints.cs'], tests: ['AcceptanceTests'] });
  step('e9', 690, 'reader', 'api', 'GET /api/entities/{id}', 'read', { title: 'A reader sees the result', text: 'Reads go through a separate read-only database connection. A user sees the record only if it is published and not disabled; otherwise the answer is the same 404 as for a record that never existed.', code: ['src/Lateral.Cms/Entities/EntityEndpoints.cs'], tests: ['EntitiesEndpointTests'] });

  const nodes = k.nodes;
  const walks = [{ id: 'life', title: 'Follow one batch', steps: nodes.filter((x) => /^e\d$/.test(x.id)).map((x) => ({ nodes: [x.id], text: x.text })) }];
  write('event-lifecycle', { id: 'event-lifecycle', title: 'The life of an event', width: W, height: 740, nodes, walks }, k.svg(W, 740, 'The life of an event', 'Numbered steps are clickable in the engineering console. Dashed lines are answers.'));
})();

// ================================================================ 3. the life of an asset

(() => {
  const k = kit();
  const W = 1180;
  const st = (id, x, y, w, h, title, lines, lane, def) => k.box({ id, x, y, w, h, title, lines, lane, ...def });

  st('uploading', 24, 90, 190, 80, 'uploading', ['multipart parts on disk', 'not yet an asset'], 'asset', { text: 'A multipart upload in progress: parts are on disk and can be re-sent. Not yet an asset. It expires if abandoned, and the sweeper removes its parts.', code: ['src/Lateral.Cms/Assets/AssetService.cs'], tests: ['UploadTests', 'HousekeepingTests'] });
  st('quarantined', 374, 90, 220, 80, 'quarantined', ['on disk, never served', 'waiting for the scanner'], 'ingest', { text: 'Every asset starts here, whether it came in one PUT or in parts. It cannot be downloaded: asking for it gets the same 404 as for a file that does not exist.', points: ['The scan runs outside the write lock, so a slow scan never holds up ingestion.'], code: ['src/Lateral.Cms/Assets/ScanProcessor.cs'], tests: ['ScanPipelineTests'] });
  st('clean', 730, 40, 220, 80, 'clean', ['served: ranges, ETag', 'content-addressed blob'], 'asset', { text: 'The scanner cleared it. The file was moved atomically into the blob store and is now served with byte ranges. Its ETag is its SHA-256.', points: ['The verdict, the scanner version and the time are recorded, so a later signature update can re-queue old assets.'], code: ['src/Lateral.Cms/Assets/AssetEndpoints.cs'], tests: ['DownloadTests'] });
  st('rejected', 730, 190, 220, 80, 'rejected', ['bytes wiped', 'only the verdict is kept'], 'bad', { text: 'The scanner flagged it (a signature, a disguised executable, active content in an SVG, a zip bomb…). The bytes are overwritten and removed; only the verdict, the checksum and the times remain.', points: ['Never served, to anyone.'], code: ['src/Lateral.Cms/Assets/MockAssetScanner.cs'], tests: ['ScannerTests'] });
  st('failed', 374, 280, 220, 80, 'set aside', ['the scan kept failing', 'still never served'], 'admin', { text: 'The scanner kept erroring or timing out. Failing closed means the file stays unserved. It is set aside so it stops consuming attempts, and an admin can requeue it once the cause is fixed.', code: ['src/Lateral.Cms/Assets/ScanProcessor.cs'], tests: ['ScanPipelineTests'] });
  st('deleted', 990, 115, 170, 80, 'deleted', ['row removed', 'bytes overwritten, removed'], 'store', { text: 'An admin deleted it. The row goes, the bytes are overwritten with zeros even while a reader has them open, then removed. Identical content survives until the last asset using it is deleted.', points: ['Best effort on SSDs and copy-on-write filesystems; snapshots are out of scope.'], code: ['src/Lateral.Cms/Assets/FileAssetStore.cs'], tests: ['HousekeepingTests'] });

  k.arrow('asset', 'M214 130 H374'); k.label(294, 116, 'complete', 'middle'); k.label(294, 152, 'SHA-256 checked', 'middle');
  k.arrow('ingest', 'M594 118 C660 118 670 85 730 85'); k.label(664, 74, 'scan: clean', 'middle');
  k.arrow('bad', 'M594 150 C670 150 670 225 730 225'); k.label(672, 214, 'scan: flagged', 'middle');
  k.arrow('ingest', 'M514 90 C514 40 464 40 464 90'); k.label(489, 28, 'error or timeout:', 'middle'); k.label(489, 42, 'retry with back-off', 'middle');
  k.arrow('admin', 'M454 170 V280'); k.label(444, 232, 'after N failed attempts', 'end');
  k.arrow('admin', 'M524 280 V170', { dashed: true }); k.label(534, 232, 'admin: requeue', 'start');
  k.arrow('admin', 'M800 120 C800 146 610 146 574 170', { dashed: true }); k.label(720, 160, 'admin: re-scan', 'middle');
  k.arrow('store', 'M950 80 C972 80 972 150 990 155', { dashed: true }); k.label(985, 112, 'admin: delete', 'middle');
  k.parts.text.push(`<text class="s" x="30" y="420">Any asset in any state can be deleted by an admin. Everything an admin does is audited.</text>`);
  k.parts.text.push(`<text class="s" x="30" y="440">The scan runs in the background: a quarantined asset becomes clean (or not) within a second or two, depending on its size.</text>`);

  const nodes = k.nodes;
  const walks = [{ id: 'states', title: 'Follow a file', steps: [
    { nodes: ['uploading'], text: 'A large file is sent as parts, in parallel. Nothing is an asset yet.' },
    { nodes: ['quarantined'], text: 'Complete (or a single PUT) creates the asset in quarantine: on disk, never served.' },
    { nodes: ['clean'], text: 'Clean: moved into the blob store and served with byte ranges.' },
    { nodes: ['rejected'], text: 'Flagged: wiped. Only the verdict survives.' },
    { nodes: ['failed'], text: 'If the scanner itself keeps failing, the file is set aside, unserved, until an admin requeues it.' },
    { nodes: ['deleted'], text: 'An admin can delete any asset for good.' },
  ] }];
  write('asset-lifecycle', { id: 'asset-lifecycle', title: 'The life of an asset', width: W, height: 470, nodes, walks }, k.svg(W, 470, 'The life of an asset', 'Dashed lines are admin actions.'));
})();
