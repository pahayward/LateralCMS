'use strict';

// ---------------------------------------------------------------- helpers
const $ = (selector, root = document) => root.querySelector(selector);

// Every piece of text from the CMS is shown with textContent / text nodes, never as HTML.
function el(tag, props = {}, ...kids) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else if (value === true) node.setAttribute(key, '');
    else if (value !== false && value != null) node.setAttribute(key, value);
  }
  for (const kid of kids.flat()) if (kid != null) node.append(kid);
  return node;
}

// replaceChildren turns a null argument into the text "null", so drop them first.
function fill(node, ...kids) {
  node.replaceChildren(...kids.flat().filter((kid) => kid != null));
}

const GIB = 1024 ** 3;
const UNITS = { KiB: 1024, MiB: 1024 ** 2, GiB: GIB };

function fmtBytes(n) {
  const names = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let i = 0;
  let value = n;
  while (value >= 1024 && i < names.length - 1) { value /= 1024; i++; }
  return (i === 0 ? String(value) : value.toFixed(value >= 100 ? 0 : 1)) + ' ' + names[i];
}

const rand = (n = 6) => Array.from(crypto.getRandomValues(new Uint8Array(n)), (b) => (b % 36).toString(36)).join('');
const isoAt = (offsetMs = 0) => new Date(Date.now() + offsetMs).toISOString();
const b64 = (text) => btoa(String.fromCharCode(...new TextEncoder().encode(text)));
const pretty = (value) => JSON.stringify(value, null, 2);
const entityPath = (id) => 'api/entities/' + encodeURIComponent(id);

function shortChecksum(value) {
  if (!value) return '';
  const [algo, hex = ''] = value.split(':');
  return hex.length > 14 ? `${algo}:${hex.slice(0, 8)}…${hex.slice(-4)}` : value;
}

async function copy(text) {
  try { await navigator.clipboard.writeText(text); } catch { /* clipboard unavailable */ }
}

function statusChip(code) {
  if (!code) return el('span', { class: 'chip bad', text: 'no response' });
  const tone = code < 300 ? 'ok' : code < 500 ? 'warn' : 'bad';
  return el('span', { class: `chip ${tone}`, text: String(code) });
}

function outcomeChip(outcome) {
  const tone = outcome === 'applied' ? 'ok' : outcome === 'rejected' ? 'bad' : outcome === 'queued' ? 'info' : 'warn';
  return el('span', { class: `chip ${tone}`, text: outcome });
}

function checksumCell(value) {
  if (!value) return el('span', { class: 'hint', text: '-' });
  return el('button', { class: 'linkish', type: 'button', title: `${value} (click to copy)`, onclick: () => copy(value), text: shortChecksum(value) });
}

function problemText(json, fallback) {
  if (!json) return fallback;
  return [json.title, json.detail, json.code ? `(${json.code})` : null, json.error].filter(Boolean).join(' ') || fallback;
}

// ---------------------------------------------------------------- state
let config = { cmsBaseUrl: '', accounts: [], limits: { maxBodyBytes: 5 * 1024 * 1024, maxPayloadBytes: 256 * 1024, maxBatchSize: 500 } };

const ACCOUNTS = [
  { id: 'cms', label: 'CMS account (webhook sender)' },
  { id: 'user', label: 'User' },
  { id: 'admin', label: 'Admin' },
  { id: 'anonymous', label: 'No credentials' },
  { id: 'wrong', label: 'Right username, wrong password' },
  { id: 'custom', label: 'Custom credentials' },
];

// ---------------------------------------------------------------- calling the CMS (through the console's proxy)
const logEntries = [];

async function consoleFetch(path, init = {}) {
  const headers = { 'X-Console-Request': '1', ...(init.headers ?? {}) };
  const response = await fetch(path, { ...init, headers });
  return { response, json: await response.json().catch(() => null) };
}

async function call({ method = 'GET', path, as = 'anonymous', custom, body, quiet = false }) {
  const headers = { 'X-Console-Request': '1', 'X-Console-Act-As': as };
  if (as === 'custom' && custom) headers['X-Console-Custom-Credentials'] = custom;
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  const entry = { time: new Date(), method, path, as, status: 0, ms: 0, requestBody: body, responseText: '', correlation: null, retryAfter: null, json: null };
  const started = performance.now();
  try {
    const response = await fetch('/api/cms/' + path, { method, headers, body });
    entry.status = response.status;
    entry.responseText = await response.text();
    entry.correlation = response.headers.get('X-Correlation-Id');
    entry.retryAfter = response.headers.get('Retry-After');
  } catch (error) {
    entry.error = error.message;
  }
  entry.ms = Math.round(performance.now() - started);
  try { entry.json = JSON.parse(entry.responseText); } catch { entry.json = null; }
  if (!quiet) addLog(entry);
  return entry;
}

// ---------------------------------------------------------------- account picker
function accountPicker(initial, only) {
  const choices = ACCOUNTS.filter((a) => !only || only.includes(a.id));
  const select = el('select', { 'aria-label': 'Act as' }, choices.map((a) => {
    const missing = ['cms', 'user', 'admin'].includes(a.id) && !config.accounts.includes(a.id);
    return el('option', { value: a.id, text: a.label + (missing ? ' (not configured)' : ''), disabled: missing });
  }));
  select.value = initial;

  const user = el('input', { placeholder: 'username', autocomplete: 'off', 'aria-label': 'Custom username' });
  const pass = el('input', { placeholder: 'password', type: 'password', autocomplete: 'off', 'aria-label': 'Custom password' });
  const custom = el('span', { class: 'picker', hidden: true }, user, pass);
  select.addEventListener('change', () => { custom.hidden = select.value !== 'custom'; });

  return {
    node: el('span', { class: 'picker' }, select, custom),
    value: () => ({ as: select.value, custom: select.value === 'custom' ? b64(`${user.value}:${pass.value}`) : undefined }),
    select,
  };
}

// ---------------------------------------------------------------- request log
function addLog(entry) {
  logEntries.unshift(entry);
  if (logEntries.length > 80) logEntries.pop();
  renderLog();
}

function renderLog() {
  $('#log-count').textContent = String(logEntries.length);
  const list = $('#log-list');
  list.replaceChildren(...logEntries.map((entry) => {
    const body = el('div', { class: 'log-body', hidden: true });
    const row = el('div', { class: 'log-row' },
      el('div', {
        class: 'log-line', role: 'button', tabindex: '0',
        onclick: () => {
          if (body.childElementCount === 0) fillLogBody(body, entry);
          body.hidden = !body.hidden;
        },
      },
      el('span', { class: 'hint', text: entry.time.toLocaleTimeString() }),
      el('b', { text: entry.method }),
      el('span', { class: 'path', title: entry.path, text: '/' + entry.path }),
      el('span', { class: 'as hint', text: entry.as }),
      statusChip(entry.status),
      el('span', { class: 'ms hint', text: `${entry.ms} ms` })),
      body);
    return row;
  }));
  renderEndpoints();
}

function fillLogBody(body, entry) {
  const clip = (text) => (text.length > 4000 ? text.slice(0, 4000) + `\n... (${text.length - 4000} more characters)` : text);
  const parts = [];
  if (entry.correlation) parts.push(el('p', { class: 'hint', text: `X-Correlation-Id: ${entry.correlation}` }));
  if (entry.retryAfter) parts.push(el('p', { class: 'hint', text: `Retry-After: ${entry.retryAfter}` }));
  if (entry.error) parts.push(el('div', { class: 'banner bad', text: entry.error }));
  if (entry.requestBody) parts.push(el('h3', { text: 'Request body' }), el('pre', { text: clip(entry.requestBody) }));
  if (entry.responseText) parts.push(el('h3', { text: 'Response body' }), el('pre', { text: clip(entry.json ? pretty(entry.json) : entry.responseText) }));
  body.append(...parts);
}

// ---------------------------------------------------------------- tabs and health
const views = ['send', 'browse', 'matrix', 'large', 'assets', 'queue', 'endpoints'];

function showView(name) {
  if (!views.includes(name)) name = 'send';
  for (const view of views) $(`#view-${view}`).hidden = view !== name;
  for (const tab of document.querySelectorAll('#tabs button')) tab.setAttribute('aria-selected', String(tab.dataset.view === name));
  renderStrip(name);
  watchQueue(name === 'queue');
  history.replaceState(null, '', '#' + name);
}

let lastHealth = null;

async function checkHealth() {
  const result = await call({ path: 'health', as: 'anonymous', quiet: true });
  lastHealth = result;
  renderEndpoints();
  const dot = $('#health-dot');
  const up = result.status === 200;
  dot.className = 'dot ' + (up ? 'ok' : 'bad');
  dot.title = up ? 'The CMS answered /health' : `The CMS did not answer /health (${result.status || result.error || 'no response'})`;
  return up;
}

// ---------------------------------------------------------------- send events
const batchBox = $('#batch');
let sendPicker;

function readBatch() {
  if (batchBox.value.trim() === '') return [];
  try {
    const value = JSON.parse(batchBox.value);
    return Array.isArray(value) ? value : null;
  } catch { return null; }
}

function refreshBatchBadge() {
  const events = readBatch();
  const badge = $('#batch-badge');
  if (events === null) { badge.className = 'chip warn'; badge.textContent = 'not a JSON array'; }
  else { badge.className = 'chip'; badge.textContent = events.length === 0 ? 'empty' : `${events.length} event${events.length === 1 ? '' : 's'}`; }
}

function setBatch(events) {
  batchBox.value = pretty(events);
  refreshBatchBadge();
}

function ev(type, id, version, payload, timestamp) {
  const event = { type, id };
  if (version !== undefined) event.version = version;
  if (payload !== undefined) event.payload = payload;
  event.timestamp = timestamp;
  return event;
}

const SCENARIOS = [
  {
    name: 'Publish an article', note: 'One new entity at version 1.',
    build: (s) => [ev('publish', s.id, 1, { title: 'Hello', body: 'First draft' }, s.t(0))],
  },
  {
    name: 'Update, then replay', note: 'Replays come back ignored, not applied twice.',
    build: (s) => [
      ev('publish', s.id, 1, { title: 'Hello' }, s.t(0)),
      ev('publish', s.id, 2, { title: 'Hello again' }, s.t(1)),
      ev('publish', s.id, 1, { title: 'Hello' }, s.t(0)),
      ev('publish', s.id, 2, { title: 'Hello again' }, s.t(1)),
    ],
  },
  {
    name: 'Out-of-order delivery', note: 'Version 3 arrives before 1 and 2.',
    build: (s) => [
      ev('publish', s.id, 3, { title: 'Third' }, s.t(2)),
      ev('publish', s.id, 1, { title: 'First' }, s.t(0)),
      ev('publish', s.id, 2, { title: 'Second' }, s.t(1)),
    ],
  },
  {
    name: 'Unpublish keeps the data', note: 'Users lose it, admins still see it.',
    build: (s) => [
      ev('publish', s.id, 1, { title: 'Going dark' }, s.t(0)),
      ev('unPublish', s.id, 2, { title: 'Going dark' }, s.t(1)),
    ],
  },
  {
    name: 'Hard delete', note: 'Gone for good; older events cannot bring it back.',
    build: (s) => [
      ev('publish', s.id, 1, { title: 'Short lived' }, s.t(0)),
      ev('delete', s.id, undefined, undefined, s.t(1)),
      ev('publish', s.id, 2, { title: 'Too late' }, s.t(0.5)),
    ],
  },
  {
    name: 'Same version, different content', note: 'A deterministic winner, flagged as a conflict.',
    build: (s) => [
      ev('publish', s.id, 1, { title: 'Version A' }, s.t(0)),
      ev('publish', s.id, 1, { title: 'Version B' }, s.t(0)),
    ],
  },
  {
    name: 'A batch with bad events', note: 'Each bad event is rejected with a reason; the good one is applied.',
    build: (s) => [
      { id: s.id + '-a', version: 1, payload: {}, timestamp: s.t(0) },
      { type: 'publish', version: 1, payload: {}, timestamp: s.t(0) },
      { type: 'publish', id: s.id + '-c', version: 'abc', payload: {}, timestamp: s.t(0) },
      { type: 'publish', id: s.id + '-d', version: 1, payload: {}, timestamp: isoAt(24 * 3600e3) },
      { type: 'publish', id: s.id + '-e', version: 1, payload: 'not an object', timestamp: s.t(0) },
      ev('archive', s.id + '-f', 1, {}, s.t(0)),
      ev('publish', s.id + '-ok', 1, { title: 'I am fine' }, s.t(0)),
    ],
  },
  {
    name: 'Payload over the limit', note: 'Only that event is rejected (payload_too_large).',
    build: (s) => [
      ev('publish', s.id + '-big', 1, { text: 'x'.repeat(config.limits.maxPayloadBytes + 1024) }, s.t(0)),
      ev('publish', s.id + '-ok', 1, { title: 'Small and fine' }, s.t(0)),
    ],
  },
  {
    name: 'A video, by reference', note: 'How a 10 GB file belongs in an event: its address, size and checksum.',
    build: (s) => [ev('publish', s.id, 1, {
      title: 'Launch video',
      media: { url: `https://media.example.com/${s.id}/launch.mp4`, contentType: 'video/mp4', sizeBytes: 10 * GIB, sha256: '0'.repeat(64) },
    }, s.t(0))],
  },
];

function scenarioContext() {
  const base = Date.now() - 30 * 60e3;
  return { id: 'demo-' + rand(5), t: (minutes) => new Date(base + minutes * 60e3).toISOString() };
}

function initSend() {
  sendPicker = accountPicker('cms');
  $('#send-as').replaceWith(sendPicker.node);

  $('#scenarios').replaceChildren(...SCENARIOS.map((scenario) =>
    el('button', { type: 'button', class: 'scenario', onclick: () => setBatch(scenario.build(scenarioContext())) },
      el('b', { text: scenario.name }), el('span', { text: scenario.note }))));

  const newId = () => { $('#ev-id').value = 'demo-' + rand(5); };
  const now = () => { $('#ev-ts').value = isoAt(); };
  newId(); now();
  $('#ev-newid').addEventListener('click', newId);
  $('#ev-now').addEventListener('click', now);
  batchBox.addEventListener('input', refreshBatchBadge);

  let attached = null;
  $('#ev-file').addEventListener('change', (event) => {
    const file = event.target.files[0];
    attached = file ? { fileName: file.name, contentType: file.type || 'application/octet-stream', sizeBytes: file.size } : null;
    const note = $('#ev-note');
    note.hidden = !file;
    if (file) {
      const big = file.size > config.limits.maxBodyBytes;
      note.textContent = `Only a reference to "${file.name}" (${fmtBytes(file.size)}) goes into the payload; its bytes are never sent.`
        + (big ? ` It is larger than the ${fmtBytes(config.limits.maxBodyBytes)} the webhook accepts, so a reference is the only option.` : '');
    }
  });

  $('#ev-add').addEventListener('click', () => {
    const error = $('#ev-error');
    error.hidden = true;
    const events = readBatch();
    if (events === null) { error.textContent = 'The batch is not a JSON array yet. Fix it or clear it first.'; error.hidden = false; return; }

    const type = $('#ev-type').value.trim();
    const event = { type, id: $('#ev-id').value };
    if (type.toLowerCase() !== 'delete') {
      event.version = Number($('#ev-version').value);
      try {
        const payload = JSON.parse($('#ev-payload').value || '{}');
        if (attached && payload && typeof payload === 'object' && !Array.isArray(payload)) payload.media = attached;
        event.payload = payload;
      } catch { error.textContent = 'The payload is not valid JSON.'; error.hidden = false; return; }
    }
    event.timestamp = $('#ev-ts').value.trim();
    events.push(event);
    setBatch(events);
    $('#ev-version').value = String(Number($('#ev-version').value) + 1);
    now();
  });

  $('#batch-clear').addEventListener('click', () => { batchBox.value = ''; refreshBatchBadge(); $('#send-result').hidden = true; });
  $('#batch-send').addEventListener('click', async (event) => {
    const button = event.currentTarget;
    button.disabled = true;
    try {
      const identity = sendPicker.value();
      renderIngestResult(await call({ method: 'POST', path: 'cms/events', as: identity.as, custom: identity.custom, body: batchBox.value }), identity);
    } finally { button.disabled = false; }
  });
  refreshBatchBadge();
}

function resultsTable(results) {
  return el('div', { class: 'table-wrap' }, el('table', {},
    el('thead', {}, el('tr', {}, ['#', 'Id', 'Type', 'Ver', 'Outcome', 'Reason', 'Checksum'].map((h) => el('th', { text: h })))),
    el('tbody', {}, results.map((r) => el('tr', {},
      el('td', { text: String(r.index) }),
      el('td', {}, el('code', { text: r.id ?? '-' })),
      el('td', { text: r.type ?? '-' }),
      el('td', { text: r.version ?? '-' }),
      el('td', {}, outcomeChip(r.outcome)),
      el('td', {}, r.reason ? el('code', { text: r.reason }) : el('span', { class: 'hint', text: '-' })),
      el('td', {}, checksumCell(r.checksum)))))));
}

function batchSummary(json) {
  const queued = json.queued ? `${json.queued} queued, ` : '';
  return `Received ${json.received}: ${queued}${json.applied} applied, ${json.ignored} ignored, ${json.rejected} rejected.`;
}

// An unpublish or delete makes an entity vanish from the user list. Say so, and offer the view that still shows it.
function vanishedBanner(results) {
  const vanished = results.filter((r) => r.outcome === 'applied' && ['unpublish', 'delete'].includes(String(r.type).toLowerCase()));
  if (vanished.length === 0) return null;
  return el('div', { class: 'banner info' },
    el('p', { text: 'What users and admins see now:' }),
    el('ul', {}, vanished.map((r) => el('li', {}, el('code', { text: r.id }),
      String(r.type).toLowerCase() === 'delete'
        ? ' was deleted. Nobody can see it; only a payload-free tombstone remains.'
        : ' was unpublished. Users no longer see it. Admins still do, marked unpublished.'))),
    el('button', {
      type: 'button', class: 'btn', text: 'Browse as admin',
      onclick: () => {
        browsePicker.select.value = 'admin';
        browse.selected = null;
        showView('browse');
        listEntities(false);
      },
    }));
}

function renderIngestResult(result, identity = { as: 'cms' }) {
  followToken++; // a new send ends the follow-up of the previous one
  const box = $('#send-result');
  box.hidden = false;
  const head = el('div', { class: 'card-head' },
    el('h2', { text: 'Response' }),
    el('span', { class: 'row' }, statusChip(result.status), el('span', { class: 'hint', text: `${result.ms} ms` }),
      result.correlation ? el('button', { class: 'linkish', type: 'button', title: 'Copy the correlation id', onclick: () => copy(result.correlation), text: result.correlation }) : null));

  const parts = [head];
  if (result.error) parts.push(el('div', { class: 'banner bad', text: `The request did not complete: ${result.error}` }));
  if (result.retryAfter) parts.push(el('div', { class: 'banner', text: `The server asked the sender to wait ${result.retryAfter} s before retrying (Retry-After).` }));

  const json = result.json;
  if (json && Array.isArray(json.results)) {
    const summary = el('p', { text: batchSummary(json) });
    const holder = el('div', {}, resultsTable(json.results));
    const after = el('div', {}, vanishedBanner(json.results));
    parts.push(summary, holder);

    if (json.status === 'queued') {
      // 202: stored safely, not applied yet. Follow it the way the CMS would, through the Location it was given.
      const banner = el('div', { class: 'banner info', text: 'Accepted (202). The batch is stored and waiting for the worker. Following it...' });
      parts.push(banner);
      followBatch(json.batchId, identity, { banner, summary, holder, after });
    }

    parts.push(after);
  } else if (!result.error) {
    parts.push(el('div', { class: `banner ${result.status < 500 ? '' : 'bad'}`, text: problemText(json, result.responseText || 'No body.') }));
  }

  if (result.responseText) parts.push(el('details', {}, el('summary', { text: 'Raw response' }), el('pre', { text: json ? pretty(json) : result.responseText })));
  box.replaceChildren(...parts);
}

// Polls GET /cms/batches/{id} until the batch is decided, then shows the final outcomes in place of "queued".
let followToken = 0;

async function followBatch(batchId, identity, view) {
  const token = ++followToken;
  const started = performance.now();
  for (let poll = 0; token === followToken; poll++) {
    await new Promise((resolve) => setTimeout(resolve, poll === 0 ? 400 : 1000));
    if (token !== followToken) return;

    const status = await call({ path: 'cms/batches/' + batchId, as: identity.as, custom: identity.custom, quiet: true });
    if (token !== followToken) return;
    const json = status.json;
    if (status.status !== 200 || !json || !Array.isArray(json.results)) {
      view.banner.className = 'banner bad';
      view.banner.textContent = `Could not read the batch status (${status.status || status.error || 'no response'}).`;
      return;
    }

    fill(view.holder, resultsTable(json.results));
    view.summary.textContent = batchSummary(json);
    const seconds = ((performance.now() - started) / 1000).toFixed(1);

    if (json.status === 'completed') {
      view.banner.className = 'banner ok';
      view.banner.textContent = `Completed after ${seconds} s. The outcomes above are final.`;
      fill(view.after, vanishedBanner(json.results));
      addLog(status);
      return;
    }
    if (json.status === 'failed') {
      view.banner.className = 'banner bad';
      view.banner.textContent = `The CMS gave up on this batch after ${json.attempts} attempts (${json.lastError}). Nothing from it was applied. An admin can requeue it from the Queue tab.`;
      addLog(status);
      return;
    }

    view.banner.textContent = json.attempts
      ? `Queued: attempt ${json.attempts} failed, so it is waiting to be retried (${seconds} s so far).`
      : `Queued: waiting for the worker to apply it (${seconds} s so far).`;
    if (performance.now() - started > 120000) {
      view.banner.textContent += ' Still waiting after two minutes. Check the Queue tab.';
      return;
    }
  }
}

// ---------------------------------------------------------------- browse and admin
let browsePicker;
const browse = { cursor: null, rows: [], selected: null };

function browseIdentity() { return browsePicker.value(); }

async function listEntities(append) {
  const { as, custom } = browseIdentity();
  const query = new URLSearchParams({ limit: String(Number($('#b-limit').value) || 5) });
  if (append && browse.cursor) query.set('cursor', browse.cursor);
  const result = await call({ path: 'api/entities?' + query, as, custom });

  const message = $('#b-message');
  if (result.status !== 200 || !result.json?.items) {
    message.hidden = false;
    message.className = 'banner ' + (result.status === 0 || result.status >= 500 ? 'bad' : '');
    message.textContent = `${result.status || 'No response'}: ${problemText(result.json, result.error ?? 'Not allowed.')}`;
    $('#b-table').hidden = true;
    $('#b-more').hidden = true;
    return;
  }

  message.hidden = true;
  if (!append) browse.rows = [];
  browse.rows.push(...result.json.items);
  browse.cursor = result.json.nextCursor ?? null;
  renderEntityTable();
}

function renderEntityTable() {
  $('#b-table').hidden = browse.rows.length === 0;
  const viewingAsUser = browseIdentity().as === 'user';
  $('#b-summary').textContent = `${browse.rows.length} loaded${browse.cursor ? ', more available' : ', end of list'}`
    + (viewingAsUser ? '. Users do not see unpublished or disabled entities; view as admin to see them.' : '');
  $('#b-more').hidden = !browse.cursor;
  if (browse.rows.length === 0) {
    const message = $('#b-message');
    message.hidden = false;
    message.className = 'banner info';
    message.textContent = viewingAsUser
      ? 'Nothing is visible to users yet. Users see only published entities that are not disabled. Send some events, or view as admin.'
      : 'Nothing visible to this account yet. Send some events first.';
  }
  $('#b-table tbody').replaceChildren(...browse.rows.map((row) => {
    const status = row.status;
    return el('tr', { class: 'pick' + (row.id === browse.selected ? ' picked' : ''), onclick: () => showEntity(row.id) },
      el('td', {}, el('code', { text: row.id })),
      el('td', { class: 'num', text: String(row.version) }),
      el('td', {}, el('code', { text: shortChecksum(row.checksum) })),
      el('td', {}, status
        ? [el('span', { class: `chip ${status.isPublished ? 'ok' : ''}`, text: status.isPublished ? 'published' : 'unpublished' }), ' ',
          status.isDisabledByAdmin ? el('span', { class: 'chip bad', text: 'disabled' }) : null]
        : el('span', { class: 'hint', text: 'visible' })));
  }));
}

async function showEntity(id, note) {
  browse.selected = id;
  renderEntityTable();
  const { as, custom } = browseIdentity();
  const result = await call({ path: entityPath(id), as, custom });
  const detail = $('#b-detail');

  if (result.status !== 200 || !result.json) {
    const lookAsAdmin = as === 'user' && result.status === 404
      ? [el('p', { class: 'hint', text: 'Just unpublished or disabled it? Users no longer see it, but admins still do.' }),
        el('button', {
          type: 'button', class: 'btn', text: 'Look it up as admin',
          onclick: async () => {
            browsePicker.select.value = 'admin';
            await listEntities(false);
            await showEntity(id);
          },
        })]
      : [];
    detail.replaceChildren(el('h2', { text: 'Entity' }),
      el('div', { class: 'banner', text: `${result.status || 'No response'}: ${problemText(result.json, result.error ?? 'Not available.')}` }),
      el('p', { class: 'hint', text: 'A hidden entity and a missing one look the same, so the API does not confirm that a restricted entity exists.' }),
      ...lookAsAdmin);
    return;
  }

  const entity = result.json;
  const verifyOutput = el('div', { class: 'form' });
  const adminOutput = el('div', { class: 'form' });
  if (note) adminOutput.append(note);

  const facts = el('dl', { class: 'kv' },
    el('dt', { text: 'Id' }), el('dd', {}, el('code', { text: entity.id })),
    el('dt', { text: 'Version' }), el('dd', { text: String(entity.version) }),
    el('dt', { text: 'Checksum' }), el('dd', {}, el('code', { text: entity.checksum })),
    el('dt', { text: 'Updated' }), el('dd', { text: entity.updatedAt }),
    entity.status ? [el('dt', { text: 'Status' }), el('dd', { text: `${entity.status.isPublished ? 'published' : 'unpublished'}${entity.status.isDisabledByAdmin ? `, disabled by ${entity.status.disabledBy}` : ''}` })] : null);

  const verify = el('button', { type: 'button', class: 'btn', text: 'Verify checksum', onclick: async () => {
    const asParam = as === 'admin' ? 'admin' : 'user';
    const { response, json } = await consoleFetch(`/api/verify/${encodeURIComponent(id)}?as=${asParam}`);
    verifyOutput.replaceChildren(response.ok && json?.visible
      ? el('div', { class: `banner ${json.match ? 'ok' : 'bad'}`, text: json.match
        ? `Match. Recomputed ${json.computed.slice(0, 22)}… over the ${json.payloadBytes} payload bytes exactly as served.`
        : `MISMATCH. The API says ${json.claimed} but the payload hashes to ${json.computed || '(unknown algorithm)'}.` })
      : el('div', { class: 'banner', text: json?.error ?? 'Could not verify.' }));
  } });

  const adminAct = (verb) => async () => {
    const act = await call({ method: 'POST', path: `${entityPath(id)}/${verb}`, as, custom });
    const message = el('div', { class: `banner ${act.status === 200 ? 'ok' : ''}`, text: act.status === 200
      ? `${verb === 'disable' ? 'Disabled' : 'Enabled'}. Visible to users: ${act.json.visible ? 'yes' : 'no'}. The CMS's own data is untouched.`
      : `${act.status}: ${problemText(act.json, act.error ?? 'Refused.')}` });
    if (act.status === 200) {
      await listEntities(false);
      await showEntity(id, message);
    } else {
      adminOutput.replaceChildren(message);
    }
  };

  detail.replaceChildren(
    el('h2', { text: 'Entity' }), facts,
    el('h3', { text: 'Payload' }), el('pre', { text: pretty(entity.payload) }),
    el('div', { class: 'row wrap' }, verify,
      el('button', { type: 'button', class: 'btn', text: 'Disable', onclick: adminAct('disable') }),
      el('button', { type: 'button', class: 'btn', text: 'Enable', onclick: adminAct('enable') })),
    verifyOutput, adminOutput,
    el('p', { class: 'hint', text: 'Disable and Enable run as the account you are viewing as. Only an admin is allowed to; anyone else gets 403.' }));
}

function initBrowse() {
  browsePicker = accountPicker('user', ['user', 'admin', 'cms', 'anonymous', 'wrong', 'custom']);
  $('#browse-as').replaceWith(browsePicker.node);
  $('#b-load').addEventListener('click', () => { browse.selected = null; listEntities(false); });
  $('#b-more').addEventListener('click', () => listEntities(true));
  $('#b-get').addEventListener('click', () => { const id = $('#b-lookup').value.trim(); if (id) showEntity(id); });
  $('#b-lookup').addEventListener('keydown', (event) => { if (event.key === 'Enter') $('#b-get').click(); });
  $('#b-reset').addEventListener('click', resetAllData);
}

// Erases everything so a test can start again. Always runs as the admin account, whichever one is being viewed.
async function resetAllData() {
  if (!window.confirm('Erase every entity, tombstone, ledger row and queued batch in the CMS? This cannot be undone.')) return;
  const button = $('#b-reset');
  const box = $('#b-reset-result');
  button.disabled = true;
  try {
    const result = await call({ method: 'POST', path: 'api/admin/reset', as: 'admin', body: JSON.stringify({ confirm: 'reset-all-data' }) });
    box.hidden = false;
    if (result.status === 200 && result.json) {
      box.className = 'banner ok';
      box.textContent = `Reset. Erased ${result.json.entities} entities, ${result.json.tombstones} tombstones, ${result.json.ledgerRows} ledger rows and ${result.json.batches} batches (the queue is empty too). The CMS is empty.`;
      browse.selected = null;
      fill($('#b-detail'), el('h2', { text: 'Entity' }), el('p', { class: 'hint', text: 'Pick an entity from the list, or look one up by id.' }));
      await listEntities(false);
    } else if (result.status === 404) {
      box.className = 'banner';
      box.textContent = 'This CMS has no reset route: it is switched off. Set Admin:AllowReset to true (the Development settings already do) and restart it. It can never be enabled in Production.';
    } else {
      box.className = 'banner bad';
      box.textContent = `${result.status || 'No response'}: ${problemText(result.json, result.error ?? 'Refused.')}`;
    }
  } finally { button.disabled = false; }
}

// ---------------------------------------------------------------- queue
// What an operator watches: is the queue draining, and has anything failed.
let queueTimer = null;

function watchQueue(on) {
  clearInterval(queueTimer);
  queueTimer = null;
  if (!on) return;
  refreshQueue(true);
  queueTimer = setInterval(() => { if ($('#q-auto').checked) refreshQueue(true); }, 2000);
}

async function refreshQueue(quiet = false) {
  const message = $('#q-message');
  const result = await call({ path: 'api/admin/ingestion', as: 'admin', quiet });
  if (result.status !== 200 || !result.json) {
    message.hidden = false;
    message.className = 'banner bad';
    message.textContent = result.status === 401 || result.status === 403
      ? 'The admin account is not allowed to read the queue (is it configured?).'
      : `Could not read the queue: ${result.status || result.error || 'no response'}.`;
    return;
  }

  const report = result.json;
  const notes = [];
  if (!report.workerEnabled && report.pendingBatches > 0) notes.push('The worker is switched off (Inbox:WorkerEnabled), so queued batches are waiting and will not be applied.');
  if (report.failedBatches > 0) notes.push(`${report.failedBatches} batch(es) failed and were set aside. Fix the cause, then requeue them below.`);
  message.hidden = notes.length === 0;
  message.className = report.failedBatches > 0 ? 'banner bad' : 'banner';
  message.textContent = notes.join(' ');

  fill($('#q-facts'),
    el('dt', { text: 'Worker' }), el('dd', { text: report.workerEnabled ? 'running' : 'switched off' }),
    el('dt', { text: 'Waiting' }), el('dd', { text: `${report.pendingBatches} batches, ${report.pendingEvents} events` }),
    el('dt', { text: 'Oldest waiting' }), el('dd', { text: report.oldestPendingAgeSeconds == null ? 'nothing is waiting' : `${report.oldestPendingAgeSeconds} s` }),
    el('dt', { text: 'Space used' }), el('dd', { text: `${fmtBytes(report.usedBytes)} of ${fmtBytes(report.maxBytes)} (a full queue answers 429)` }),
    el('dt', { text: 'Failed' }), el('dd', { text: String(report.failedBatches) }));

  const table = $('#q-failed');
  table.hidden = report.failed.length === 0;
  $('#q-none').hidden = report.failed.length > 0;
  $('tbody', table).replaceChildren(...report.failed.map((batch) => el('tr', {},
    el('td', {}, el('code', { text: batch.batchId })),
    el('td', { text: new Date(batch.receivedAt).toLocaleString() }),
    el('td', { class: 'num', text: String(batch.events) }),
    el('td', { class: 'num', text: String(batch.attempts) }),
    el('td', {}, el('code', { text: batch.lastError ?? '-' })),
    el('td', {}, el('button', {
      type: 'button', class: 'btn small', text: 'Requeue',
      onclick: async (event) => {
        event.currentTarget.disabled = true;
        await call({ method: 'POST', path: `api/admin/ingestion/${batch.batchId}/requeue`, as: 'admin' });
        await refreshQueue();
      },
    })))));
}

function initQueue() {
  $('#q-refresh').addEventListener('click', () => refreshQueue());
}

// ---------------------------------------------------------------- endpoints
// One list of what the CMS exposes, so the endpoint under test is never a guess. The service has a test that fails when
// it gains an endpoint without a role-matrix row; that is the cue to update this list as well.
const ENDPOINTS = [
  { key: 'ingest', method: 'POST', path: '/cms/events', who: 'CMS account', view: 'send',
    what: 'Send a batch of publish, unPublish and delete events. Validated and stored at once; applied in the background (the default), so most batches are answered 202 with outcome "queued".',
    answers: '202 accepted, follow Location; 200 final results; 400 bad body; 401; 403; 413 too large; 429 queue full or throttled; 503 retry' },
  { key: 'batch', method: 'GET', path: '/cms/batches/{batchId}', who: 'CMS account, Admin', view: 'send',
    what: 'Where a batch has got to: queued, completed (with each event\'s final outcome) or failed. Never returns payloads.',
    answers: '200; 404 unknown batch; 401; 403' },
  { key: 'list', method: 'GET', path: '/api/entities', who: 'User, Admin', view: 'browse',
    what: 'List entities, newest page by cursor. Users see published, enabled ones; admins see all, with a status.',
    answers: '200 page; 400 bad cursor; 401; 403' },
  { key: 'get', method: 'GET', path: '/api/entities/{id}', who: 'User, Admin', view: 'browse',
    what: 'One entity with its payload and checksum. A hidden entity looks the same as a missing one.',
    answers: '200; 404 missing or hidden; 401; 403' },
  { key: 'disable', method: 'POST', path: '/api/entities/{id}/disable', who: 'Admin', view: 'browse',
    what: 'Hide an entity from users. The CMS data is not touched.',
    answers: '200 new state; 404; 401; 403' },
  { key: 'enable', method: 'POST', path: '/api/entities/{id}/enable', who: 'Admin', view: 'browse',
    what: 'Undo a disable.',
    answers: '200 new state; 404; 401; 403' },
  { key: 'report', method: 'GET', path: '/api/admin/ingestion', who: 'Admin', view: 'queue',
    what: 'Is the queue keeping up: batches and events waiting, age of the oldest, space used, and any failed batches.',
    answers: '200; 401; 403' },
  { key: 'requeue', method: 'POST', path: '/api/admin/ingestion/{batchId}/requeue', who: 'Admin', view: 'queue',
    what: 'Put a failed batch back in the queue once the cause is fixed.',
    answers: '200; 404 unknown; 409 not failed; 401; 403' },
  { key: 'reset', method: 'POST', path: '/api/admin/reset', who: 'Admin', view: 'browse', danger: true,
    what: 'TEST ONLY. Erase every entity, tombstone, ledger row and queued batch. Needs {"confirm":"reset-all-data"}. Off unless Admin:AllowReset is true, and never allowed in Production.',
    answers: '200 counts; 400 no confirmation; 404 switched off; 401; 403' },
  { key: 'health', method: 'GET', path: '/health', who: 'Anyone', view: null,
    what: 'Is the service up and its database reachable.',
    answers: '200; 503' },
];

// The asset store: S3-style multipart upload, scanning, and ranged downloads.
ENDPOINTS.push(
  { key: 'asset-put', method: 'PUT', path: '/cms/assets/{key}', who: 'CMS account', view: 'assets',
    what: 'Upload one object (up to the simple limit) in a single request. Streamed to disk and stored in quarantine; it is not served until the scanner clears it.',
    answers: '202 with the asset id; 400 bad key or checksum; 413 too large; 429 store full; 401; 403' },
  { key: 'asset-start', method: 'POST', path: '/cms/assets/uploads', who: 'CMS account', view: 'assets',
    what: 'Start a multipart upload: name, content type and total size. Returns the upload id and the part limits.',
    answers: '201; 400; 413 too large; 429 store full; 401; 403' },
  { key: 'asset-part', method: 'PUT', path: '/cms/assets/uploads/{uploadId}/parts/{n}', who: 'CMS account', view: 'assets',
    what: 'Upload one part. Parts can be sent in parallel and sent again (the last one wins). X-Content-SHA256 is checked.',
    answers: '200 with the part checksum; 400; 404 unknown or expired upload; 413; 429; 401; 403' },
  { key: 'asset-complete', method: 'POST', path: '/cms/assets/uploads/{uploadId}/complete', who: 'CMS account', view: 'assets',
    what: 'List the parts in order. They are assembled, hashed and checked against the declared size, then quarantined for scanning.',
    answers: '202 with the asset; 400 bad part list or size; 404; 401; 403' },
  { key: 'asset-abort', method: 'DELETE', path: '/cms/assets/uploads/{uploadId}', who: 'CMS account', view: 'assets',
    what: 'Abandon an upload and remove its parts.', answers: '204; 404; 401; 403' },
  { key: 'asset-status', method: 'GET', path: '/cms/assets/{id}', who: 'CMS account, Admin', view: 'assets',
    what: 'The asset\'s state (quarantined, clean, rejected, failed), checksum and scan verdict. Never the bytes.',
    answers: '200; 404; 401; 403' },
  { key: 'asset-get', method: 'GET', path: '/api/assets/{id}', who: 'User, Admin', view: 'assets',
    what: 'Download a clean asset. Supports Range, If-Range, ETag and If-None-Match, so many ranges can be fetched in parallel and a download resumed. Anything not clean is a 404.',
    answers: '200; 206 partial; 304; 404; 416 bad range; 429 too many open downloads; 401; 403' },
  { key: 'asset-delete', method: 'DELETE', path: '/api/assets/{id}', who: 'Admin', view: 'assets',
    what: 'Hard delete: the row goes and the bytes are overwritten and removed.', answers: '204; 404; 401; 403' },
  { key: 'asset-list', method: 'GET', path: '/api/admin/assets', who: 'Admin', view: 'assets',
    what: 'List assets, optionally by state.', answers: '200; 401; 403' },
  { key: 'asset-rescan', method: 'POST', path: '/api/admin/assets/{id}/rescan', who: 'Admin', view: 'assets',
    what: 'Put a clean asset back in quarantine for another scan. It is not served until it is cleared again.', answers: '200; 409 not clean; 401; 403' },
  { key: 'asset-requeue', method: 'POST', path: '/api/admin/assets/{id}/requeue', who: 'Admin', view: 'assets',
    what: 'Put an asset whose scanning was set aside back in the queue.', answers: '200; 409 not set aside; 401; 403' },
);

const VIEW_ENDPOINTS = {
  send: ['ingest', 'batch'],
  browse: ['list', 'get', 'disable', 'enable', 'reset'],
  matrix: ['health', 'ingest', 'list', 'get', 'disable'],
  large: ['ingest'],
  assets: ['asset-put', 'asset-start', 'asset-part', 'asset-complete', 'asset-status', 'asset-get', 'asset-list'],
  queue: ['report', 'requeue'],
  endpoints: [],
};

const endpointPattern = (path) => new RegExp('^' + path.replace(/[.*+?^$()|[\]\\]/g, '\\$&')
  .replace('{id}', '[^/]+').replace('{batchId}', '[^/]+').replace('{uploadId}', '[^/]+').replace('{key}', '[^/]+').replace('{n}', '[0-9]+') + '$');

function lastCallTo(endpoint) {
  // The background health check is kept out of the request log, so it is tracked on its own.
  if (endpoint.key === 'health') return lastHealth;
  const pattern = endpointPattern(endpoint.path);
  return logEntries.find((entry) => entry.method === endpoint.method && pattern.test('/' + entry.path.split('?')[0]));
}

function methodTag(endpoint) {
  return el('span', { class: 'method ' + (endpoint.danger ? 'm-danger' : endpoint.method === 'GET' ? 'm-get' : 'm-post'), text: endpoint.method });
}

let focusedEndpoint = null;

function renderEndpoints() {
  const table = $('#ep-table');
  const head = el('thead', {}, el('tr', {}, ['Endpoint', 'Who may call', 'What it does', 'Answers', 'Last call from this console', ''].map((h) => el('th', { text: h }))));
  const rows = ENDPOINTS.map((endpoint) => {
    const last = lastCallTo(endpoint);
    return el('tr', { class: endpoint.key === focusedEndpoint ? 'focus' : '' },
      el('td', { class: 'ep-path' }, methodTag(endpoint), ' ', el('code', { text: endpoint.path })),
      el('td', { text: endpoint.who }),
      el('td', { text: endpoint.what }),
      el('td', { class: 'hint', text: endpoint.answers }),
      el('td', {}, last
        ? [statusChip(last.status), ' ', el('span', { class: 'hint', text: `as ${last.as}, ${last.ms} ms, ${last.time.toLocaleTimeString()}` })]
        : el('span', { class: 'hint', text: 'not called yet' })),
      el('td', {}, endpoint.view ? el('button', { type: 'button', class: 'btn small', text: 'Open', onclick: () => showView(endpoint.view) }) : null));
  });
  fill(table, head, el('tbody', {}, rows));
}

// The thin strip under the tabs: the endpoints the current view calls, always visible.
function renderStrip(view) {
  const strip = $('#ep-strip');
  const keys = VIEW_ENDPOINTS[view] ?? [];
  strip.hidden = keys.length === 0;
  fill(strip, el('span', { class: 'hint', text: 'This view calls' }),
    keys.map((key) => {
      const endpoint = ENDPOINTS.find((e) => e.key === key);
      return el('button', {
        type: 'button', class: 'ep', title: endpoint.what,
        onclick: () => { focusedEndpoint = key; showView('endpoints'); },
      }, methodTag(endpoint), ' ', el('code', { text: endpoint.path }));
    }));
}

// ---------------------------------------------------------------- auth matrix
const MATRIX_ACCOUNTS = [
  { id: 'cms', label: 'CMS account' },
  { id: 'user', label: 'User' },
  { id: 'admin', label: 'Admin' },
  { id: 'anonymous', label: 'No credentials' },
  { id: 'wrong', label: 'Wrong password' },
];

const NOT_2XX = 'not 2xx';
const MATRIX_COLUMNS = [
  { label: 'GET /health', request: () => ({ path: 'health' }),
    expect: { cms: 200, user: 200, admin: 200, anonymous: 200, wrong: 200 } },
  { label: 'POST /cms/events', request: () => ({ method: 'POST', path: 'cms/events', body: '[]' }),
    expect: { cms: 200, user: 403, admin: 403, anonymous: 401, wrong: 401 } },
  { label: 'GET /api/entities', request: () => ({ path: 'api/entities?limit=1' }),
    expect: { cms: 403, user: 200, admin: 200, anonymous: 401, wrong: 401 } },
  { label: 'GET /api/entities/{id}', request: (f) => ({ path: entityPath(f.id) }),
    expect: { cms: 403, user: 200, admin: 200, anonymous: 401, wrong: 401 } },
  { label: 'PUT /api/entities/{id}', request: (f) => ({ method: 'PUT', path: entityPath(f.id), body: '{"payload":{"title":"changed"}}' }),
    note: 'there is no way to change content',
    expect: { cms: NOT_2XX, user: NOT_2XX, admin: NOT_2XX, anonymous: NOT_2XX, wrong: NOT_2XX } },
  { label: 'POST …/disable', request: (f) => ({ method: 'POST', path: entityPath(f.id) + '/disable' }),
    expect: { cms: 403, user: 403, admin: 200, anonymous: 401, wrong: 401 } },
];

async function runMatrix() {
  const button = $('#m-run');
  const summary = $('#m-summary');
  const table = $('#m-table');
  button.disabled = true;
  summary.hidden = true;

  const cells = new Map();
  table.replaceChildren(
    el('thead', {}, el('tr', {}, el('th', { text: 'Caller' }), MATRIX_COLUMNS.map((c) => el('th', {}, c.label, c.note ? el('small', { class: 'hint', text: c.note }) : null)))),
    el('tbody', {}, MATRIX_ACCOUNTS.map((account) => el('tr', {}, el('td', { text: account.label }),
      MATRIX_COLUMNS.map((column, index) => {
        const cell = el('td', { class: 'pending', text: '…' });
        cells.set(`${account.id}:${index}`, cell);
        return cell;
      })))));

  try {
    const fixture = { id: 'matrix-' + rand(6) };
    const created = await call({
      method: 'POST', path: 'cms/events', as: 'cms',
      body: JSON.stringify([ev('publish', fixture.id, 1, { title: 'Auth matrix fixture' }, isoAt(-60e3))]),
    });
    if (created.status !== 200 || created.json?.results?.[0]?.outcome !== 'applied') {
      summary.hidden = false;
      summary.className = 'banner bad';
      summary.textContent = `Could not create the fixture entity as the CMS account (${created.status || created.error}). Is the CMS running and the CMS account configured?`;
      return;
    }

    let passed = 0;
    let total = 0;
    for (const account of MATRIX_ACCOUNTS) {
      for (let index = 0; index < MATRIX_COLUMNS.length; index++) {
        const column = MATRIX_COLUMNS[index];
        const expected = column.expect[account.id];
        const result = await call({ as: account.id, ...column.request(fixture) });
        const ok = expected === NOT_2XX ? (result.status !== 0 && (result.status < 200 || result.status >= 300)) : result.status === expected;
        total++;
        if (ok) passed++;

        const cell = cells.get(`${account.id}:${index}`);
        cell.className = ok ? 'pass' : 'fail';
        fill(cell, String(result.status || 'none'),
          ok ? null : el('small', { text: `expected ${expected}` }),
          result.status === 429 ? el('small', { text: 'throttled' }) : null);
        cell.title = result.correlation ? `X-Correlation-Id ${result.correlation}` : '';
      }
    }

    // Leave the fixture visible again.
    await call({ method: 'POST', path: entityPath(fixture.id) + '/enable', as: 'admin' });

    summary.hidden = false;
    summary.className = 'banner ' + (passed === total ? 'ok' : 'bad');
    summary.textContent = passed === total
      ? `All ${total} combinations behaved as designed.`
      : `${total - passed} of ${total} combinations did not behave as designed (highlighted).`;
  } finally { button.disabled = false; }
}

// ---------------------------------------------------------------- large files
let probeBusy = false;

function verdictRow(tone, text) {
  return el('div', { class: `banner ${tone}`, text });
}

function initLarge() {
  let chosen = null;
  $('#l-file').addEventListener('change', (event) => {
    const file = event.target.files[0];
    const box = $('#l-verdict');
    chosen = file ? { fileName: file.name, contentType: file.type || 'application/octet-stream', sizeBytes: file.size } : null;
    box.hidden = !file;
    $('#l-ref').hidden = !file;
    if (!file) return;

    const { maxBodyBytes, maxPayloadBytes } = config.limits;
    box.replaceChildren(
      el('p', {}, el('b', { text: file.name }), ` is ${fmtBytes(file.size)}${file.type ? ` (${file.type})` : ''}.`),
      file.size > maxBodyBytes
        ? verdictRow('bad', `Over the webhook's ${fmtBytes(maxBodyBytes)} request limit: the CMS would answer 413 and keep nothing.`)
        : verdictRow('ok', `Within the ${fmtBytes(maxBodyBytes)} request limit.`),
      file.size > maxPayloadBytes
        ? verdictRow('bad', `Over the ${fmtBytes(maxPayloadBytes)} limit on a single payload: that event would be rejected (payload_too_large).`)
        : verdictRow('ok', `Within the ${fmtBytes(maxPayloadBytes)} payload limit.`),
      verdictRow('info', 'Either way the right shape is a reference: the file stays in the CMS\'s media storage and the event carries its address, size and checksum.'));
  });

  $('#l-ref').addEventListener('click', () => {
    if (!chosen) return;
    const s = scenarioContext();
    setBatch([ev('publish', s.id, 1, { title: chosen.fileName, media: chosen }, s.t(0))]);
    showView('send');
  });

  for (const preset of document.querySelectorAll('.preset')) {
    preset.addEventListener('click', () => { $('#p-size').value = preset.dataset.size; $('#p-unit').value = preset.dataset.unit; });
  }
  $('#p-run').addEventListener('click', runProbe);
}

async function runProbe() {
  if (probeBusy) return;
  probeBusy = true;
  const button = $('#p-run');
  const out = $('#p-result');
  button.disabled = true;
  out.hidden = false;
  out.replaceChildren(el('p', { class: 'hint', text: 'Sending… (the probe gives up after 20 seconds)' }));

  const bytes = Math.round(Number($('#p-size').value) * UNITS[$('#p-unit').value]);
  const mode = document.querySelector('input[name="p-mode"]:checked').value;
  const expectContinue = $('#p-expect').checked;

  const entry = { time: new Date(), method: 'POST', path: `(probe) cms/events, ${fmtBytes(bytes)} ${mode}`, as: 'cms', status: 0, ms: 0, responseText: '', json: null };
  try {
    const { response, json } = await consoleFetch('/api/probe/oversize', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ bytes, mode, expectContinue }),
    });
    if (!response.ok || !json?.outcome) {
      out.replaceChildren(verdictRow('bad', json?.error ?? `The console refused the probe (${response.status}).`));
      return;
    }
    entry.status = json.status ?? 0;
    entry.ms = json.elapsedMs;
    entry.responseText = JSON.stringify(json);
    entry.json = json;
    addLog(entry);
    renderProbe(out, json);
  } catch (error) {
    out.replaceChildren(verdictRow('bad', `The probe failed: ${error.message}`));
  } finally {
    probeBusy = false;
    button.disabled = false;
  }
}

function renderProbe(out, r) {
  const saved = r.requestedBytes - r.bytesRead;
  const verdicts = {
    refused: ['ok', `Refused with 413 after ${r.elapsedMs} ms. The sender had read ${fmtBytes(r.bytesRead)} of the ${fmtBytes(r.requestedBytes)}`
      + (saved > 0 ? `, so ${fmtBytes(saved)} never had to be transferred or held in memory.` : '.')],
    completed: ['info', `The CMS read the body and answered ${r.status}: it is within the limit, so it was parsed (whitespace is not a batch, hence the error).`],
    connection_reset: ['warn', r.mode === 'chunked'
      ? `With no declared length the CMS cannot know the size up front. It read up to its limit (${fmtBytes(r.bytesRead)} pulled by the sender, including buffering), then stopped and closed the connection. `
        + 'Its memory use stayed bounded, but the sender sees a reset rather than a 413, so webhook senders should declare Content-Length.'
      : `The CMS refused the request and closed the connection while the sender was still pushing the body (${fmtBytes(r.bytesRead)} read so far), so the sender saw a reset instead of the 413. `
        + (r.expectContinue
          ? 'Even with Expect: 100-continue the answer was lost; see the response detail.'
          : 'Tick "Expect: 100-continue" and run it again: the sender then waits for the verdict before sending any body, and gets a clean 413.')],
    timed_out: ['bad', `The CMS was still accepting data after 20 seconds (${fmtBytes(r.bytesRead)} read). It is not enforcing a body limit.`],
  };
  const [tone, text] = verdicts[r.outcome] ?? ['info', r.outcome];

  fill(out,
    verdictRow(tone, text),
    el('dl', { class: 'kv' },
      el('dt', { text: 'Requested' }), el('dd', { text: `${fmtBytes(r.requestedBytes)} (${r.mode}${r.expectContinue ? ', Expect: 100-continue' : ''})` }),
      el('dt', { text: 'HTTP status' }), el('dd', { text: r.status ? String(r.status) : 'none' }),
      el('dt', { text: 'Time to answer' }), el('dd', { text: `${r.elapsedMs} ms` }),
      el('dt', { text: 'Bytes the sender read' }), el('dd', { text: fmtBytes(r.bytesRead) })),
    r.detail ? el('details', {}, el('summary', { text: 'Response detail' }), el('pre', { text: r.detail })) : null);
}

// ---------------------------------------------------------------- start
async function start() {
  try {
    const { response, json } = await consoleFetch('/api/config');
    if (response.ok && json) config = json;
  } catch { /* the banner below explains */ }

  $('#cms-url').textContent = config.cmsBaseUrl || '(unknown)';
  const missing = ['cms', 'user', 'admin'].filter((id) => !config.accounts.includes(id));
  if (missing.length) {
    const banner = $('#setup-banner');
    banner.hidden = false;
    banner.textContent = `No credentials configured for: ${missing.join(', ')}. In Development the console reads them from the CMS's App_Data/dev-credentials.json (the CMS writes it the first time it runs); otherwise set Console:Accounts. Those accounts are disabled below.`;
  }

  initSend();
  initBrowse();
  initLarge();
  initQueue();

  for (const tab of document.querySelectorAll('#tabs button')) tab.addEventListener('click', () => showView(tab.dataset.view));
  $('#health-btn').addEventListener('click', checkHealth);
  $('#m-run').addEventListener('click', runMatrix);
  $('#log-clear').addEventListener('click', () => { logEntries.length = 0; renderLog(); });

  showView(location.hash.slice(1));
  renderLog();
  // Ask every couple of seconds while the CMS is not answering (it may still be starting, for example when both are
  // launched together from Visual Studio), and every 20 seconds once it is.
  const watchHealth = async () => setTimeout(watchHealth, (await checkHealth()) ? 20000 : 2000);
  watchHealth();
}

start();
