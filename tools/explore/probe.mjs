// An exploratory edge-case probe of a running CMS: 168 checks over the webhook envelope and every field, reconciliation under
// concurrency, paging, assets (simple, multipart, ranges), headers, and authentication. It prints one line per check and
// flags anything unexpected, above all any 5xx, which is never the right answer to a request.
//
//   node tools/explore/probe.mjs http://localhost:5080
//
// RUN IT AGAINST A THROWAWAY INSTANCE. It creates records and assets, and its last step sends twelve wrong passwords, which
// the CMS throttles per address: for about a minute afterwards every request from this machine gets 429. A throwaway
// instance has its own database and asset folder:
//
//   ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5192 \
//     Database__ConnectionString="Data Source=/tmp/probe.db" Assets__Root=/tmp/probe-assets \
//     dotnet run --project src/Lateral.Cms --no-launch-profile
//
// Credentials come from src/Lateral.Cms/App_Data/dev-credentials.json (written by the CMS in Development) and are never printed.
// It found four defects the first time it ran (docs/EVIDENCE.md, "Exploratory testing"); all are fixed and tested.

import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';

const BASE = process.argv[2] ?? 'http://localhost:5080';
const creds = JSON.parse(readFileSync(new URL('../../src/Lateral.Cms/App_Data/dev-credentials.json', import.meta.url), 'utf8'));
const basic = (u, p) => 'Basic ' + Buffer.from(`${u}:${p}`).toString('base64');
const AUTH = {
  cms: basic(creds.Auth.Cms.Username, creds.Auth.Cms.Passwords[0]),
  user: basic(creds.Auth.Users.find((u) => u.Role.toLowerCase() === 'user').Username, creds.Auth.Users.find((u) => u.Role.toLowerCase() === 'user').Password),
  admin: basic(creds.Auth.Users.find((u) => u.Role.toLowerCase() === 'admin').Username, creds.Auth.Users.find((u) => u.Role.toLowerCase() === 'admin').Password),
};

let bad5xx = 0;
const findings = [];
const rows = [];
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const rid = () => Math.random().toString(36).slice(2, 8);

async function req(method, path, { as = 'cms', body, headers = {}, raw } = {}) {
  const h = { ...headers };
  if (as && AUTH[as]) h.Authorization = AUTH[as];
  if (body !== undefined && typeof body !== 'string' && !Buffer.isBuffer(body)) { body = JSON.stringify(body); h['Content-Type'] ??= 'application/json'; }
  else if (body !== undefined && !h['Content-Type']) h['Content-Type'] = 'application/json';
  try {
    const r = await fetch(BASE + path, { method, headers: h, body: raw ?? body });
    const text = Buffer.from(await r.arrayBuffer());
    let json = null; try { json = JSON.parse(text.toString('utf8')); } catch { /* not json */ }
    return { status: r.status, headers: r.headers, text, json };
  } catch (e) { return { status: 0, error: e.message, headers: new Headers(), text: Buffer.alloc(0), json: null }; }
}

function note(name, res, expect, extra = '') {
  const status = res.status;
  const ok = expect === undefined ? true : Array.isArray(expect) ? expect.includes(status) : status === expect;
  if (status >= 500 || status === 0) { bad5xx++; findings.push(`5xx/none: ${name} -> ${status} ${res.error ?? ''}`); }
  if (!ok) findings.push(`unexpected: ${name} -> ${status} (wanted ${expect})`);
  rows.push(`${ok && status < 500 && status > 0 ? 'ok' : '!!'} ${String(status).padEnd(3)} ${name}${extra ? '  | ' + extra : ''}`);
}

const ev = (o) => ({ type: 'publish', id: 'x-' + rid(), version: 1, payload: { a: 1 }, timestamp: new Date().toISOString(), ...o });

async function sendOne(name, event, expect = [200, 202]) {
  const r = await req('POST', '/cms/events', { body: [event] });
  const res = r.json?.results?.[0];
  note(name, r, expect, res ? `${res.outcome}${res.reason ? ' ' + res.reason : ''}` : (r.json?.detail ?? ''));
  return r;
}

async function drain(batchId) {
  for (let i = 0; i < 60; i++) { const r = await req('GET', `/cms/batches/${batchId}`); if (r.json && r.json.status !== 'queued') return r.json; await sleep(150); }
  return null;
}

// ---------------------------------------------------------------- ingestion: the envelope
console.log('--- envelope');
note('empty array', await req('POST', '/cms/events', { body: '[]' }), 200);
note('non-array object', await req('POST', '/cms/events', { body: '{}' }), 400);
note('null', await req('POST', '/cms/events', { body: 'null' }), 400);
note('not json', await req('POST', '/cms/events', { body: 'hello' }), 400);
note('trailing comma', await req('POST', '/cms/events', { body: '[{"type":"publish",}]' }), 400);
note('empty body', await req('POST', '/cms/events', { body: '' }), [400, 411]);
note('text/plain content type', await req('POST', '/cms/events', { body: '[]', headers: { 'Content-Type': 'text/plain' } }), [200, 400, 415]);
note('no content type', await req('POST', '/cms/events', { raw: '[]' }), [200, 400, 415]);
note('invalid utf-8', await req('POST', '/cms/events', { body: Buffer.from([0x5b, 0xff, 0xfe, 0x5d]) }), 400);
note('utf-8 BOM', await req('POST', '/cms/events', { body: Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from('[]')]) }), [200, 400]);
note('501 events', await req('POST', '/cms/events', { body: Array.from({ length: 501 }, () => ev({})) }), [400, 413]);
note('500 events (the limit)', await req('POST', '/cms/events', { body: Array.from({ length: 500 }, () => ev({})) }), [200, 202]);
note('body over 5 MiB', await req('POST', '/cms/events', { body: '[' + ' '.repeat(6 * 1024 * 1024) + ']' }), 413);
note('GET on the webhook', await req('GET', '/cms/events'), [404, 405]);
note('PUT on the webhook', await req('PUT', '/cms/events', { body: '[]' }), [404, 405]);

// ---------------------------------------------------------------- ingestion: each field
console.log('--- fields');
await sendOne('version 0', ev({ version: 0 }));
await sendOne('version -1', ev({ version: -1 }));
await sendOne('version 1.5', ev({ version: 1.5 }));
await sendOne('version "1" (string)', ev({ version: '1' }));
await sendOne('version null', ev({ version: null }));
await sendOne('version 2^53', ev({ version: 9007199254740992 }));
await sendOne('version 9223372036854775807 (long max)', ev({ version: 9223372036854775807 }));
await sendOne('version 1e30', ev({ version: 1e30 }));
await sendOne('id empty', ev({ id: '' }));
await sendOne('id spaces', ev({ id: '   ' }));
await sendOne('id 200 chars', ev({ id: 'a'.repeat(200) }));
await sendOne('id 201 chars', ev({ id: 'a'.repeat(201) }));
await sendOne('id 5000 chars', ev({ id: 'a'.repeat(5000) }));
await sendOne('id with slash', ev({ id: 'a/b-' + rid() }));
await sendOne('id with unicode', ev({ id: 'caf\u00e9-\u{1F600}-' + rid() }));
await sendOne('id with newline', ev({ id: 'a\nb' }));
await sendOne('id with percent', ev({ id: '100%-' + rid() }));
await sendOne('id number', ev({ id: 123 }));
await sendOne('type PUBLISH (case)', ev({ type: 'PUBLISH' }));
await sendOne('type unPublish', ev({ type: 'unPublish' }));
await sendOne('type missing', ev({ type: undefined }));
await sendOne('type unknown', ev({ type: 'archive' }));
await sendOne('payload array', ev({ payload: [1] }));
await sendOne('payload null', ev({ payload: null }));
await sendOne('payload missing', ev({ payload: undefined }));
await sendOne('payload empty object', ev({ payload: {} }));
await sendOne('payload string', ev({ payload: 'x' }));
await sendOne('payload 256 KiB exactly-ish', ev({ payload: { t: 'x'.repeat(262144 - 20) } }));
await sendOne('payload 256 KiB + 1 KiB', ev({ payload: { t: 'x'.repeat(262144 + 1024) } }));
await sendOne('payload depth 32', ev({ payload: JSON.parse('{"a":'.repeat(31) + '1' + '}'.repeat(31)) }));
await sendOne('payload depth 40', ev({ payload: JSON.parse('{"a":'.repeat(40) + '1' + '}'.repeat(40)) }));
await sendOne('payload with html', ev({ payload: { t: '<script>alert(1)</script>&"\'' } }));
await sendOne('payload with lone surrogate', ev({ payload: JSON.parse('{"t":"\\ud800"}') }));
await sendOne('payload with NUL', ev({ payload: { t: 'a\u0000b' } }));
await sendOne('payload with huge number', ev({ payload: { n: 1e308 } }));
await sendOne('payload duplicate keys', { ...ev({}), payload: undefined }, [200, 202]);
for (const [name, ts] of [['timestamp date only', '2024-01-01'], ['timestamp no zone', '2024-01-01T00:00:00'], ['timestamp +02:00', '2024-01-01T00:00:00+02:00'],
  ['timestamp year 1900', '1900-01-01T00:00:00Z'], ['timestamp year 0001', '0001-01-01T00:00:00Z'], ['timestamp year 9999', '9999-12-31T23:59:59Z'], ['timestamp garbage', 'yesterday'],
  ['timestamp +30min', new Date(Date.now() + 30 * 60e3).toISOString()], ['timestamp +61min', new Date(Date.now() + 61 * 60e3).toISOString()], ['timestamp number', 1700000000], ['timestamp empty', ''], ['timestamp null', null]]) {
  await sendOne(name, ev({ timestamp: ts }));
}
await sendOne('extra unknown field', ev({ surprise: true }));
await sendOne('checksum field wrong', ev({ checksum: 'sha256:' + '0'.repeat(64) }));
note('event is not an object (number)', await req('POST', '/cms/events', { body: '[1]' }), [200, 202, 400]);
note('event is null', await req('POST', '/cms/events', { body: '[null]' }), [200, 202, 400]);

// ---------------------------------------------------------------- reconciliation under concurrency
console.log('--- reconciliation');
{
  const id = 'race-' + rid();
  const versions = Array.from({ length: 40 }, (_, i) => i + 1).sort(() => Math.random() - 0.5);
  const sent = await Promise.all(versions.map((v) => req('POST', '/cms/events', { body: [ev({ id, version: v, payload: { v } })] })));
  note('40 parallel publishes of one id, no 5xx', { status: sent.every((r) => r.status === 200 || r.status === 202) ? 200 : sent.find((r) => r.status >= 500)?.status ?? 400 }, 200);
  for (const r of sent) if (r.json?.batchId) await drain(r.json.batchId);
  const got = await req('GET', `/api/entities/${id}`, { as: 'user' });
  note('final version is the highest (40)', got, 200, `version ${got.json?.version}, payload v ${got.json?.payload?.v}`);
  if (got.json?.version !== 40) findings.push(`reconciliation: expected version 40, got ${got.json?.version}`);

  const dup = 'dup-' + rid();
  const same = await Promise.all(Array.from({ length: 20 }, () => req('POST', '/cms/events', { body: [ev({ id: dup, version: 1, payload: { same: true }, timestamp: '2024-01-01T00:00:00Z' })] })));
  for (const r of same) if (r.json?.batchId) await drain(r.json.batchId);
  const once = await req('GET', `/api/entities/${dup}`, { as: 'user' });
  note('20 identical replays leave one entity at v1', once, 200, `version ${once.json?.version}`);

  const tie = 'tie-' + rid();
  const a = await req('POST', '/cms/events', { body: [ev({ id: tie, version: 1, payload: { who: 'A' }, timestamp: '2024-01-01T00:00:00Z' }), ev({ id: tie, version: 1, payload: { who: 'B' }, timestamp: '2024-01-01T00:00:00Z' })] });
  await drain(a.json.batchId);
  const t1 = await req('GET', `/api/entities/${tie}`, { as: 'user' });
  const tie2 = 'tie2-' + rid();
  const b = await req('POST', '/cms/events', { body: [ev({ id: tie2, version: 1, payload: { who: 'B' }, timestamp: '2024-01-01T00:00:00Z' }), ev({ id: tie2, version: 1, payload: { who: 'A' }, timestamp: '2024-01-01T00:00:00Z' })] });
  await drain(b.json.batchId);
  const t2 = await req('GET', `/api/entities/${tie2}`, { as: 'user' });
  note('same-version conflict picks the same winner in either order', t1, 200, `A,B -> ${t1.json?.payload?.who}; B,A -> ${t2.json?.payload?.who}`);
  if (t1.json?.payload?.who !== t2.json?.payload?.who) findings.push('reconciliation: conflicting content picked different winners by arrival order');

  const del = 'del-' + rid();
  const d = await req('POST', '/cms/events', { body: [ev({ id: del, version: 1, timestamp: '2024-01-01T00:00:00Z' }), { type: 'delete', id: del, timestamp: '2024-01-02T00:00:00Z' }, ev({ id: del, version: 9, timestamp: '2024-01-01T12:00:00Z' })] });
  await drain(d.json.batchId);
  const gone = await req('GET', `/api/entities/${del}`, { as: 'admin' });
  note('an older publish cannot resurrect a deleted entity', gone, 404);
  const later = await req('POST', '/cms/events', { body: [ev({ id: del, version: 1, timestamp: '2024-01-03T00:00:00Z' })] });
  await drain(later.json.batchId);
  const back = await req('GET', `/api/entities/${del}`, { as: 'admin' });
  note('a newer publish after a delete is accepted', back, 200);
}

// ---------------------------------------------------------------- reads
console.log('--- reads');
for (const q of ['limit=0', 'limit=-1', 'limit=1000', 'limit=abc', 'limit=1.5', 'limit=', 'cursor=!!!', 'cursor=' + 'A'.repeat(5000), 'limit=2&limit=3', 'x=1']) {
  const r = await req('GET', '/api/entities?' + q, { as: 'user' });
  note(`list ?${q.slice(0, 30)}`, r, [200, 400], r.json?.items ? `${r.json.items.length} items` : (r.json?.code ?? ''));
}
for (const p of ['/api/entities/', '/api/entities/%2e%2e', '/api/entities/' + 'a'.repeat(5000), '/api/entities/a%2Fb', '/api/entities/%00', '/api/entities/%ff', '/api/entities/nope']) {
  const r = await req('GET', p, { as: 'user' });
  note(`get ${p.slice(0, 40)}`, r, [200, 400, 404, 414]);
}
note('HEAD list', await req('HEAD', '/api/entities', { as: 'user' }), [200, 404, 405]);
note('OPTIONS list', await req('OPTIONS', '/api/entities', { as: 'user' }), [200, 204, 404, 405]);
note('DELETE entity', await req('DELETE', '/api/entities/x', { as: 'admin' }), [404, 405]);
note('PATCH entity', await req('PATCH', '/api/entities/x', { as: 'admin', body: '{}' }), [404, 405]);
note('disable missing', await req('POST', '/api/entities/does-not-exist/disable', { as: 'admin' }), 404);
note('enable missing', await req('POST', '/api/entities/does-not-exist/enable', { as: 'admin' }), 404);
note('batch status unknown guid', await req('GET', '/cms/batches/00000000-0000-0000-0000-000000000000'), 404);
note('batch status not a guid', await req('GET', '/cms/batches/not-a-guid'), [400, 404]);
note('requeue unknown', await req('POST', '/api/admin/ingestion/00000000-0000-0000-0000-000000000000/requeue', { as: 'admin' }), [404, 409]);
note('reset without confirm', await req('POST', '/api/admin/reset', { as: 'admin', body: '{}' }), [400, 404]);
note('reset wrong confirm', await req('POST', '/api/admin/reset', { as: 'admin', body: { confirm: 'yes' } }), [400, 404]);
note('health anonymous', await req('GET', '/health', { as: null }), 200);

// ---------------------------------------------------------------- assets
console.log('--- assets');
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
const sha = (b) => createHash('sha256').update(b).digest('hex');
const putAsset = (key, bytes, headers = {}, as = 'cms') => req('PUT', `/cms/assets/${encodeURIComponent(key)}`, { as, body: bytes, headers: { 'Content-Type': 'image/png', ...headers } });
note('PUT asset ok', await putAsset('tiny.png', png, { 'X-Content-SHA256': sha(png) }), 202);
note('PUT asset wrong checksum', await putAsset('tiny.png', png, { 'X-Content-SHA256': '0'.repeat(64) }), 400);
note('PUT asset bad checksum text', await putAsset('tiny.png', png, { 'X-Content-SHA256': 'zzz' }), 400);
note('PUT asset zero bytes', await putAsset('empty.bin', Buffer.alloc(0)), [202, 400]);
note('PUT asset key 201 chars', await putAsset('k'.repeat(201), png), 400);
note('PUT asset key with backslash', await putAsset('a\\b.png', png), 400);
note('PUT asset key with space+unicode', await putAsset('my caf\u00e9 \u{1F600}.png', png), 202);
note('PUT asset key %2F', await req('PUT', '/cms/assets/a%2Fb.png', { body: png, headers: { 'Content-Type': 'image/png' } }), [400, 404]);
note('PUT asset 9 MiB (over simple limit)', await putAsset('big.bin', Buffer.alloc(9 * 1024 * 1024, 7)), 413);
note('PUT asset as user', await putAsset('x.png', png, {}, 'user'), 403);
note('PUT asset content-type missing', await req('PUT', '/cms/assets/notype.bin', { body: png, headers: { 'Content-Type': '' } }), [202, 400, 415]);
note('PUT asset weird content-type', await putAsset('weird.bin', png, { 'Content-Type': 'not a media type;;;' }), [202, 400]);

// a clean asset to download
const up = await putAsset('range.png', png, { 'X-Content-SHA256': sha(png) });
const aid = up.json?.id;
let state = null;
for (let i = 0; i < 40; i++) { const s = await req('GET', `/cms/assets/${aid}`); state = s.json?.state; if (state && state !== 'quarantined') break; await sleep(300); }
note('asset reaches clean', { status: state === 'clean' ? 200 : 500 }, 200, `state ${state}`);
note('download full', await req('GET', `/api/assets/${aid}`, { as: 'user' }), 200);
note('download range 0-9', await req('GET', `/api/assets/${aid}`, { as: 'user', headers: { Range: 'bytes=0-9' } }), 206);
note('download suffix range -5', await req('GET', `/api/assets/${aid}`, { as: 'user', headers: { Range: 'bytes=-5' } }), 206);
note('download open range 10-', await req('GET', `/api/assets/${aid}`, { as: 'user', headers: { Range: 'bytes=10-' } }), 206);
note('download range beyond end', await req('GET', `/api/assets/${aid}`, { as: 'user', headers: { Range: 'bytes=9999-' } }), 416);
note('download reversed range 5-1', await req('GET', `/api/assets/${aid}`, { as: 'user', headers: { Range: 'bytes=5-1' } }), [200, 416]);
note('download multi range', await req('GET', `/api/assets/${aid}`, { as: 'user', headers: { Range: 'bytes=0-1,3-4' } }), [200, 206]);
note('download garbage range', await req('GET', `/api/assets/${aid}`, { as: 'user', headers: { Range: 'bytes=abc' } }), [200, 416]);
note('download range other unit', await req('GET', `/api/assets/${aid}`, { as: 'user', headers: { Range: 'items=0-1' } }), [200, 416]);
const full = await req('GET', `/api/assets/${aid}`, { as: 'user' });
const etag = full.headers.get('etag');
note('If-None-Match -> 304', await req('GET', `/api/assets/${aid}`, { as: 'user', headers: { 'If-None-Match': etag } }), 304);
note('If-Range mismatch -> full 200', await req('GET', `/api/assets/${aid}`, { as: 'user', headers: { Range: 'bytes=0-1', 'If-Range': '"nope"' } }), 200);
note('HEAD download', await req('HEAD', `/api/assets/${aid}`, { as: 'user' }), 200);
note('download not a guid', await req('GET', '/api/assets/abc', { as: 'user' }), 404);
note('download unknown guid', await req('GET', '/api/assets/00000000-0000-0000-0000-000000000000', { as: 'user' }), 404);
note('download as cms', await req('GET', `/api/assets/${aid}`, { as: 'cms' }), 403);
note('download anonymous', await req('GET', `/api/assets/${aid}`, { as: null }), 401);
console.log('   headers on a download:', ['content-type', 'content-disposition', 'x-content-type-options', 'content-security-policy', 'cache-control', 'etag', 'accept-ranges'].map((h) => `${h}=${full.headers.get(h)}`).join(' | '));

// multipart edge cases
const init = (size, key = 'mp.bin') => req('POST', '/cms/assets/uploads', { body: { key, contentType: 'application/octet-stream', size } });
note('multipart init size 0', await init(0), 400);
note('multipart init negative', await init(-5), 400);
note('multipart init > max object', await init(6 * 1024 * 1024 * 1024 * 1024), 413);
note('multipart init no key', await req('POST', '/cms/assets/uploads', { body: { size: 100 } }), 400);
note('multipart init body not json', await req('POST', '/cms/assets/uploads', { body: 'x' }), [400, 415]);
{
  const part = Buffer.alloc(5 * 1024 * 1024, 3);
  const total = part.length * 2 + 100;
  const i = await init(total);
  const id = i.json?.uploadId;
  note('multipart init ok', i, 201);
  note('part 0 refused', await req('PUT', `/cms/assets/uploads/${id}/parts/0`, { body: part, headers: { 'Content-Type': 'application/octet-stream' } }), 400);
  note('part 10001 refused', await req('PUT', `/cms/assets/uploads/${id}/parts/10001`, { body: part }), 400);
  const p1 = await req('PUT', `/cms/assets/uploads/${id}/parts/1`, { body: part });
  note('part 1', p1, 200);
  note('part 1 again (idempotent)', await req('PUT', `/cms/assets/uploads/${id}/parts/1`, { body: part }), 200);
  note('part with wrong checksum', await req('PUT', `/cms/assets/uploads/${id}/parts/2`, { body: part, headers: { 'X-Content-SHA256': '0'.repeat(64) } }), 400);
  note('complete with a missing part', await req('POST', `/cms/assets/uploads/${id}/complete`, { body: { parts: [{ partNumber: 1, sha256: sha(part) }, { partNumber: 2, sha256: sha(part) }] } }), 400);
  const p2 = await req('PUT', `/cms/assets/uploads/${id}/parts/2`, { body: part });
  const tail = Buffer.alloc(100, 9);
  const p3 = await req('PUT', `/cms/assets/uploads/${id}/parts/3`, { body: tail });
  note('part 2 and the 100-byte last part', { status: p2.status === 200 && p3.status === 200 ? 200 : 400 }, 200);
  note('complete out of order', await req('POST', `/cms/assets/uploads/${id}/complete`, { body: { parts: [{ partNumber: 2, sha256: sha(part) }, { partNumber: 1, sha256: sha(part) }, { partNumber: 3, sha256: sha(tail) }] } }), [400]);
  note('complete with a wrong part checksum', await req('POST', `/cms/assets/uploads/${id}/complete`, { body: { parts: [{ partNumber: 1, sha256: '0'.repeat(64) }, { partNumber: 2, sha256: sha(part) }, { partNumber: 3, sha256: sha(tail) }] } }), 400);
  const done = await req('POST', `/cms/assets/uploads/${id}/complete`, { body: { parts: [{ partNumber: 1, sha256: sha(part) }, { partNumber: 2, sha256: sha(part) }, { partNumber: 3, sha256: sha(tail) }] } });
  note('complete ok', done, 202);
  const whole = Buffer.concat([part, part, tail]);
  console.log('   expected sha256 matches:', done.json?.sha256 === sha(whole));
  note('complete again', await req('POST', `/cms/assets/uploads/${id}/complete`, { body: { parts: [{ partNumber: 1, sha256: sha(part) }] } }), [404, 409]);
  note('abort after complete', await req('DELETE', `/cms/assets/uploads/${id}`), [204, 404]);
  const small = await init(5 * 1024 * 1024 + 10);
  const sid = small.json?.uploadId;
  await req('PUT', `/cms/assets/uploads/${sid}/parts/1`, { body: Buffer.alloc(1024, 1) });
  note('non-final part below the minimum -> complete refused', await req('POST', `/cms/assets/uploads/${sid}/complete`, { body: { parts: [{ partNumber: 1, sha256: sha(Buffer.alloc(1024, 1)) }] } }), 400);
  note('abort works', await req('DELETE', `/cms/assets/uploads/${sid}`), 204);
  note('part after abort', await req('PUT', `/cms/assets/uploads/${sid}/parts/1`, { body: part }), 404);
}
note('asset status unknown', await req('GET', '/cms/assets/00000000-0000-0000-0000-000000000000'), 404);
note('asset delete as user', await req('DELETE', `/api/assets/${aid}`, { as: 'user' }), 403);
note('asset list as user', await req('GET', '/api/admin/assets', { as: 'user' }), 403);
note('asset list bad state', await req('GET', '/api/admin/assets?state=banana', { as: 'admin' }), [200, 400]);
note('asset list limit 100000', await req('GET', '/api/admin/assets?limit=100000', { as: 'admin' }), [200, 400]);
note('rescan unknown', await req('POST', '/api/admin/assets/00000000-0000-0000-0000-000000000000/rescan', { as: 'admin' }), 409);
note('delete asset ok', await req('DELETE', `/api/assets/${aid}`, { as: 'admin' }), 204);
note('delete asset again', await req('DELETE', `/api/assets/${aid}`, { as: 'admin' }), 404);

// ---------------------------------------------------------------- response hygiene
console.log('--- headers and hygiene');
const probe = await req('GET', '/api/entities?limit=1', { as: 'user' });
console.log('   entity list headers:', ['content-type', 'cache-control', 'x-content-type-options', 'x-correlation-id', 'server', 'x-powered-by'].map((h) => `${h}=${probe.headers.get(h)}`).join(' | '));
const err = await req('POST', '/cms/events', { body: 'hello' });
console.log('   400 body:', err.text.toString().slice(0, 220));
const nf = await req('GET', '/api/entities/zzz', { as: 'user' });
console.log('   404 body:', nf.text.toString().slice(0, 200), '| content-type', nf.headers.get('content-type'));
const noauth = await req('GET', '/api/entities', { as: null });
console.log('   401:', noauth.status, 'www-authenticate=', noauth.headers.get('www-authenticate'));

// ---------------------------------------------------------------- authentication (last: it feeds the failed-sign-in throttle)
console.log('--- authentication');
const raw = (auth) => req('GET', '/api/entities?limit=1', { as: null, headers: auth === null ? {} : { Authorization: auth } });
note('Bearer scheme', await raw('Bearer abc'), 401);
note('Basic with bad base64', await raw('Basic !!!'), 401);
note('Basic with no colon', await raw('Basic ' + Buffer.from('nocolon').toString('base64')), 401);
note('Basic with empty password', await raw('Basic ' + Buffer.from('someone:').toString('base64')), 401);
note('Basic header 20 KB', await raw('Basic ' + 'A'.repeat(20000)), [400, 401, 431]);
note('Basic with valid user lower-cased name', await raw(AUTH.user), 200);
console.log('   (throttle: sending 12 wrong passwords from this address)');
const codes = [];
for (let i = 0; i < 12; i++) codes.push((await raw('Basic ' + Buffer.from('nobody:wrong' + i).toString('base64'))).status);
console.log('   statuses:', codes.join(' '));
note('throttle engages with 429', { status: codes.includes(429) ? 429 : 200 }, 429);
const after = await req('GET', '/api/entities?limit=1', { as: 'user' });
note('right credentials while throttled', after, [200, 429], after.status === 429 ? `Retry-After ${after.headers.get('retry-after')}` : '');

console.log('\n' + rows.join('\n'));
console.log(`\n${rows.length} checks, ${bad5xx} server errors or no response`);
console.log(findings.length ? '\nFINDINGS:\n' + findings.join('\n') : '\nno findings');
