'use strict';

// The engineering console's core: building the page, making calls through the console's proxy, showing any call as curl,
// PowerShell or raw HTTP, the request dock, and the router that moves between views. Credentials never reach this page:
// it names an account for each call and the console's server adds the Authorization header.

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

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
  for (const kid of kids.flat()) if (kid != null) node.append(typeof kid === 'string' ? document.createTextNode(kid) : kid);
  return node;
}

// replaceChildren turns a null argument into the text "null", so drop them first.
function fill(node, ...kids) {
  node.replaceChildren(...kids.flat().filter((kid) => kid != null));
}

const MiB = 1024 * 1024;
const GiB = 1024 * MiB;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const rand = (n = 6) => Array.from(crypto.getRandomValues(new Uint8Array(n)), (b) => (b % 36).toString(36)).join('');
const isoAt = (offsetMs = 0) => new Date(Date.now() + offsetMs).toISOString();
const pretty = (value) => JSON.stringify(value, null, 2);
const b64 = (text) => btoa(String.fromCharCode(...new TextEncoder().encode(text)));

function fmtBytes(n) {
  if (!Number.isFinite(n)) return '';
  const names = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let i = 0;
  let value = n;
  while (value >= 1024 && i < names.length - 1) { value /= 1024; i++; }
  return (i === 0 ? String(value) : value.toFixed(value >= 100 ? 0 : 1)) + ' ' + names[i];
}

async function sha256Hex(buffer) {
  const digest = await crypto.subtle.digest('SHA-256', buffer);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

function shortChecksum(value) {
  if (!value) return '';
  const [algo, hex = ''] = String(value).split(':');
  return hex.length > 14 ? `${algo}:${hex.slice(0, 8)}…${hex.slice(-4)}` : String(value);
}

// ---------------------------------------------------------------- icons

const ICONS = {
  home: 'M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z',
  book: 'M18 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zM6 4h5v8l-2.5-1.5L6 12V4z',
  records: 'M2 20h20v-4H2v4zm2-3h2v2H4v-2zM2 4v4h20V4H2zm4 3H4V5h2v2zm-4 7h20v-4H2v4zm2-3h2v2H4v-2z',
  file: 'M14 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V8l-6-6zm-1 7V3.5L18.5 9H13z',
  send: 'M2.01 21L23 12 2.01 3 2 10l15 2-15 2z',
  tool: 'M22.7 19l-9.1-9.1c.9-2.3.4-5-1.5-6.9-2-2-5-2.4-7.4-1.3L9 6 6 9 1.6 4.7C.4 7.1.9 10.1 2.9 12.1c1.9 1.9 4.6 2.4 6.9 1.5l9.1 9.1c.4.4 1 .4 1.4 0l2.3-2.3c.5-.4.5-1.1.1-1.4z',
  pulse: 'M3.5 18.5l6-6 4 4L22 6.9 20.6 5.5l-7.1 7.1-4-4L2 16z',
  shield: 'M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm-2 16l-4-4 1.41-1.41L10 14.17l6.59-6.59L18 9l-8 8z',
  play: 'M8 5v14l11-7z',
  copy: 'M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z',
  close: 'M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z',
  up: 'M7.41 15.41L12 10.83l4.59 4.58L18 14l-6-6-6 6z',
  down: 'M7.41 8.59L12 13.17l4.59-4.58L18 10l-6 6-6-6z',
  link: 'M3.9 12c0-1.71 1.39-3.1 3.1-3.1h4V7H7c-2.76 0-5 2.24-5 5s2.24 5 5 5h4v-1.9H7c-1.71 0-3.1-1.39-3.1-3.1zM8 13h8v-2H8v2zm9-6h-4v1.9h4c1.71 0 3.1 1.39 3.1 3.1s-1.39 3.1-3.1 3.1h-4V17h4c2.76 0 5-2.24 5-5s-2.24-5-5-5z',
  check: 'M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z',
  image: 'M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z',
  list: 'M3 13h2v-2H3v2zm0 4h2v-2H3v2zm0-8h2V7H3v2zm4 4h14v-2H7v2zm0 4h14v-2H7v2zM7 7v2h14V7H7z',
};

function icon(name, size = 18) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  path.setAttribute('d', ICONS[name] ?? ICONS.file);
  path.setAttribute('fill', 'currentColor');
  svg.append(path);
  return svg;
}

// ---------------------------------------------------------------- the console's state

const Eng = {
  config: { cmsBaseUrl: '', accounts: [], limits: { maxBodyBytes: 5 * MiB, maxPayloadBytes: 256 * 1024, maxBatchSize: 500, maxSimpleBytes: 8 * MiB, minPartBytes: 5 * MiB, maxPartBytes: 64 * MiB } },
  catalogue: { areas: [], endpoints: [] },
  context: {}, // the last ids seen, so a path parameter can be filled in without copying anything
  log: [],
  logWatchers: new Set(),
  views: {},
  current: null,
  health: { up: null, at: null, status: 0 },
};

const ACCOUNTS = [
  { id: 'cms', label: 'CMS account (the webhook sender)', variable: '$CMS' },
  { id: 'user', label: 'User', variable: '$READER' },
  { id: 'admin', label: 'Admin', variable: '$ADMIN' },
  { id: 'anonymous', label: 'No credentials' },
  { id: 'wrong', label: 'Right username, wrong password' },
  { id: 'custom', label: 'Custom credentials' },
];

const hasAccount = (id) => !['cms', 'user', 'admin'].includes(id) || Eng.config.accounts.includes(id);

Eng.routeParts = (route) => {
  const [method, ...rest] = route.split(' ');
  const pattern = rest.join(' ');
  return { method, pattern, display: pattern.replace(/\{(\w+):\w+\}/g, '{$1}') };
};

Eng.endpoint = (id) => Eng.catalogue.endpoints.find((e) => e.id === id);

Eng.endpointFor = (method, path) => {
  const bare = path.split('?')[0];
  return Eng.catalogue.endpoints.find((e) => {
    const parts = Eng.routeParts(e.route);
    if (parts.method !== method) return false;
    const regex = new RegExp('^' + parts.display.replace(/[.*+?^$()|[\]\\]/g, '\\$&').replace(/\{\w+\}/g, '[^/]+') + '$');
    return regex.test(bare);
  });
};

/** Which role is the natural one to try an endpoint as: the first that may call it. */
Eng.defaultAccount = (endpoint) => {
  const order = ['cms', 'user', 'admin', 'anonymous'];
  return order.find((id) => endpoint.who.includes(id) && hasAccount(id)) ?? 'anonymous';
};

// ---------------------------------------------------------------- requests

/**
 * A request is { method, path, params, query, headers, body, as, custom }. path is a template such as
 * /api/entities/{id}; params fills it in. body is text, a Blob, or undefined.
 */
Eng.resolvePath = (request) => {
  const template = Eng.routeParts(`GET ${request.path}`).display;
  return template.replace(/\{(\w+)\}/g, (_, name) => encodeURIComponent(request.params?.[name] ?? `{${name}}`));
};

Eng.queryString = (request) => {
  const query = new URLSearchParams();
  for (const [name, value] of Object.entries(request.query ?? {})) if (value !== '' && value != null) query.set(name, value);
  const text = query.toString();
  return text ? '?' + text : '';
};

Eng.fullPath = (request) => Eng.resolvePath(request) + Eng.queryString(request);

const sq = (text) => `'${String(text).replace(/'/g, `'\\''`)}'`;
const psq = (text) => `'${String(text).replace(/'/g, "''")}'`;
const setHeaders = (request) => Object.entries(request.headers ?? {}).filter(([, v]) => v !== '' && v != null);

Eng.bodyText = (request) => (typeof request.body === 'string' ? request.body : request.body instanceof Blob ? null : undefined);

Eng.curl = (request, { headers = true } = {}) => {
  const path = Eng.fullPath(request);
  const flags = request.method === 'HEAD' ? 'curl -sS -I' : `curl -sS${headers ? ' -i' : ''}`;
  const lines = [flags + (request.method !== 'GET' && request.method !== 'HEAD' ? ` -X ${request.method}` : '') + ` "$BASE${path}"`];
  const account = ACCOUNTS.find((a) => a.id === request.as);
  if (account?.variable) lines.push(`-u "${account.variable}"`);
  else if (request.as === 'wrong') lines.push('-u "someone@example.com:not-the-password"');
  else if (request.as === 'custom') lines.push('-u "$CUSTOM"');

  const sent = setHeaders(request);
  const text = Eng.bodyText(request);
  if (text !== undefined && !sent.some(([k]) => k.toLowerCase() === 'content-type')) lines.push(`-H ${sq('Content-Type: application/json')}`);
  for (const [name, value] of sent) lines.push(`-H ${sq(`${name}: ${value}`)}`);

  let note = '';
  if (request.body instanceof Blob) lines.push('--data-binary @file');
  else if (text !== undefined && text !== '') {
    if (text.length <= 1500) lines.push(`-d ${sq(text)}`);
    else { lines.push('--data-binary @body.json'); note = '# save the JSON body (see the Request tab) as body.json first\n'; }
  }

  return note + lines.join(' \\\n  ');
};

Eng.powershell = (request) => {
  const path = Eng.fullPath(request);
  const account = ACCOUNTS.find((a) => a.id === request.as);
  const header = [];
  if (account?.variable) header.push(`Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(${account.variable}))`);
  else if (request.as === 'wrong') header.push(`Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('someone@example.com:not-the-password'))`);
  for (const [name, value] of setHeaders(request)) if (name.toLowerCase() !== 'content-type') header.push(`${psq(name)} = ${psq(value)}`);

  const lines = [];
  lines.push(header.length ? `$h = @{ ${header.join('; ')} }` : '$h = @{}');
  const text = Eng.bodyText(request);
  const type = setHeaders(request).find(([k]) => k.toLowerCase() === 'content-type')?.[1] ?? 'application/json';
  let call = `Invoke-WebRequest -Method ${request.method[0] + request.method.slice(1).toLowerCase()} -Uri "$BASE${path}" -Headers $h -SkipHttpErrorCheck`;
  if (request.body instanceof Blob) call += ` -ContentType ${psq(type)} -InFile .\\file`;
  else if (text !== undefined && text !== '') {
    if (text.length <= 1500) call += ` -ContentType ${psq(type)} -Body ${psq(text)}`;
    else { lines.unshift('# save the JSON body (see the Request tab) as body.json first'); call += ` -ContentType ${psq(type)} -InFile .\\body.json`; }
  }

  lines.push(call);
  return lines.join('\n');
};

Eng.rawHttp = (request) => {
  const host = (() => { try { return new URL(Eng.config.cmsBaseUrl).host; } catch { return 'localhost'; } })();
  const sent = setHeaders(request);
  const lines = [`${request.method} ${Eng.fullPath(request)} HTTP/1.1`, `Host: ${host}`];
  const account = ACCOUNTS.find((a) => a.id === request.as);
  if (account?.variable) lines.push(`Authorization: Basic <base64 of ${account.variable}>`);
  else if (request.as === 'wrong') lines.push('Authorization: Basic <base64 of a real username with a wrong password>');
  const text = Eng.bodyText(request);
  if (text !== undefined && !sent.some(([k]) => k.toLowerCase() === 'content-type')) lines.push('Content-Type: application/json');
  for (const [name, value] of sent) lines.push(`${name}: ${value}`);
  if (request.body instanceof Blob) lines.push(`Content-Length: ${request.body.size}`, '', `<${request.body.size} bytes>`);
  else if (text) lines.push(`Content-Length: ${new TextEncoder().encode(text).length}`, '', text);
  return lines.join('\n');
};

Eng.setup = (shell = 'bash') => {
  const base = Eng.config.cmsBaseUrl || 'http://localhost:5080';
  if (shell === 'powershell') {
    return [
      `$BASE = '${base}'`,
      '$c = Get-Content src/Lateral.Cms/App_Data/dev-credentials.json -Raw | ConvertFrom-Json',
      '$CMS = "$($c.Auth.Cms.Username):$($c.Auth.Cms.Passwords[0])"',
      '$READER = "$($c.Auth.Users[0].Username):$($c.Auth.Users[0].Password)"',
      '$ADMIN = "$($c.Auth.Users[1].Username):$($c.Auth.Users[1].Password)"',
    ].join('\n');
  }

  return [
    `BASE=${base}`,
    'PASSWORDS=($(grep -oE "[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}" src/Lateral.Cms/App_Data/dev-credentials.json))',
    'CMS="cms-ingest-svc:${PASSWORDS[0]}"',
    'READER="user@lateral.example:${PASSWORDS[1]}"',
    'ADMIN="admin@lateral.example:${PASSWORDS[2]}"',
  ].join('\n');
};

/**
 * Sends a request through the console's proxy. Returns everything about the answer: status, headers, size, time, the raw
 * bytes (as a Blob) and, when it is text, the text and any JSON in it. Never throws; a failure is in entry.error.
 */
Eng.send = async (request, { quiet = false } = {}) => {
  const headers = { 'X-Console-Request': '1', 'X-Console-Act-As': request.as ?? 'anonymous' };
  if (request.as === 'custom' && request.custom) headers['X-Console-Custom-Credentials'] = request.custom;
  for (const [name, value] of setHeaders(request)) headers[name] = value;
  const text = Eng.bodyText(request);
  if (text !== undefined && !Object.keys(headers).some((k) => k.toLowerCase() === 'content-type')) headers['Content-Type'] = 'application/json';

  const entry = { time: new Date(), request: structuredCloneRequest(request), status: 0, ms: 0, headers: {}, size: 0, text: '', json: null, blob: null, contentType: '', error: null, correlation: null };
  const started = performance.now();
  try {
    const response = await fetch('/api/cms/' + Eng.fullPath(request).replace(/^\//, ''), { method: request.method, headers, body: request.method === 'GET' || request.method === 'HEAD' ? undefined : request.body });
    entry.status = response.status;
    entry.headers = Object.fromEntries(response.headers.entries());
    entry.contentType = response.headers.get('content-type') ?? '';
    entry.correlation = response.headers.get('x-correlation-id');
    entry.blob = await response.blob();
    entry.size = entry.blob.size;
    if (/json|text|xml|javascript/i.test(entry.contentType) || entry.size === 0) {
      entry.text = await entry.blob.text();
      try { entry.json = JSON.parse(entry.text); } catch { entry.json = null; }
    }
  } catch (error) {
    entry.error = error.message;
  }

  entry.ms = Math.round(performance.now() - started);
  Eng.remember(entry);
  if (!quiet) Eng.addLog(entry);
  return entry;
};

// Request bodies can be Blobs, which structuredClone keeps; the rest is plain.
function structuredCloneRequest(request) {
  return { ...request, params: { ...request.params }, query: { ...request.query }, headers: { ...request.headers } };
}

/** Remember ids the answers reveal, so the next request can use them without copy and paste. */
Eng.remember = (entry) => {
  const json = entry.json;
  if (!json || entry.status >= 400) return;
  const path = entry.request.path;
  if (json.batchId) Eng.context.batchId = json.batchId;
  if (json.uploadId) Eng.context.uploadId = json.uploadId;
  if (/assets/.test(path) && json.id && /^[0-9a-f-]{36}$/.test(json.id)) Eng.context.assetId = json.id;
  if (Array.isArray(json.results) && json.results[0]?.id) Eng.context.entityId = json.results.find((r) => r.outcome === 'queued' || r.outcome === 'applied')?.id ?? json.results[0].id;
  if (/entities/.test(path) && json.id) Eng.context.entityId = json.id;
  if (Array.isArray(json.items) && json.items[0]?.id && /entities/.test(path)) Eng.context.entityId ??= json.items[0].id;
};

/** The older, simpler call used by views that only want JSON: returns { status, json, text, ms, correlation, error }. */
Eng.call = async ({ method = 'GET', path, as = 'anonymous', custom, body, query, headers, quiet = false }) => {
  // A concrete path with its query already in it is accepted: split it so it can be shown as a request.
  const [bare, q] = path.split('?');
  const queryObject = { ...Object.fromEntries(new URLSearchParams(q ?? '')), ...query };
  const entry = await Eng.send({ method, path: '/' + bare.replace(/^\//, ''), params: {}, query: queryObject, headers: headers ?? {}, body, as, custom }, { quiet });
  return entry;
};

// ---------------------------------------------------------------- the request dock

Eng.addLog = (entry) => {
  Eng.log.unshift(entry);
  if (Eng.log.length > 100) Eng.log.pop();
  Eng.logWatchers.forEach((fn) => fn(entry));
};

const statusTone = (code) => (!code ? 'bad' : code < 300 ? 'ok' : code < 500 ? 'warn' : 'bad');

function statusChip(code) {
  return el('span', { class: `chip ${statusTone(code)}`, text: code ? String(code) : 'no response' });
}

function methodTag(method, danger = false) {
  return el('span', { class: `method m-${danger ? 'danger' : method.toLowerCase()}`, text: method });
}

async function copyText(text, note = 'Copied') {
  try { await navigator.clipboard.writeText(text); Eng.toast(note); } catch { Eng.toast('Could not copy: select the text and copy it by hand'); }
}

Eng.toast = (message) => {
  const box = $('#eng-toasts');
  const node = el('div', { class: 'toast', text: message });
  box.append(node);
  setTimeout(() => node.remove(), 2600);
};

function renderDock() {
  $('#dock-count').textContent = String(Eng.log.length);
  const list = $('#dock-list');
  fill(list, Eng.log.length === 0
    ? el('p', { class: 'hint pad', text: 'Every call this console makes is listed here, newest first, with the curl command that makes the same call.' })
    : Eng.log.map((entry) => {
      const request = entry.request;
      const body = el('div', { class: 'dock-body', hidden: true });
      const row = el('li', { class: 'dock-row' },
        el('div', {
          class: 'dock-line', role: 'button', tabindex: '0',
          onclick: () => { if (body.childElementCount === 0) fillDockBody(body, entry); body.hidden = !body.hidden; },
          onkeydown: (event) => { if (event.key === 'Enter') event.currentTarget.click(); },
        },
        el('span', { class: 'hint time', text: entry.time.toLocaleTimeString() }),
        methodTag(request.method),
        el('span', { class: 'path', title: Eng.fullPath(request), text: Eng.fullPath(request) }),
        el('span', { class: 'who hint', text: request.as ?? 'anonymous' }),
        statusChip(entry.status),
        el('span', { class: 'ms hint', text: `${entry.ms} ms` })),
        body);
      return row;
    }));
}

function fillDockBody(body, entry) {
  const clip = (text) => (text.length > 3000 ? text.slice(0, 3000) + `\n… (${text.length - 3000} more characters)` : text);
  fill(body,
    el('div', { class: 'row wrap' },
      el('button', { type: 'button', class: 'btn small', onclick: () => copyText(Eng.curl(entry.request), 'curl copied'), text: 'Copy as curl' }),
      el('button', { type: 'button', class: 'btn small', onclick: () => Eng.openInWorkbench(entry.request), text: 'Open in the workbench' }),
      entry.correlation ? el('button', { type: 'button', class: 'linkish', title: 'Copy the correlation id', onclick: () => copyText(entry.correlation), text: `X-Correlation-Id ${entry.correlation}` }) : null),
    el('pre', { class: 'code', text: Eng.curl(entry.request) }),
    entry.error ? el('div', { class: 'banner bad', text: entry.error }) : null,
    entry.text ? el('pre', { class: 'code', text: clip(entry.json ? pretty(entry.json) : entry.text) }) : (entry.size ? el('p', { class: 'hint', text: `${fmtBytes(entry.size)} of ${entry.contentType || 'binary data'}` }) : null));
}

// ---------------------------------------------------------------- the account picker

Eng.accountPicker = (initial, only) => {
  const choices = ACCOUNTS.filter((a) => !only || only.includes(a.id));
  const select = el('select', { 'aria-label': 'Act as' }, choices.map((a) => el('option', { value: a.id, text: a.label + (hasAccount(a.id) ? '' : ' (not configured)'), disabled: !hasAccount(a.id) })));
  select.value = hasAccount(initial) ? initial : 'anonymous';
  const user = el('input', { placeholder: 'username', autocomplete: 'off', 'aria-label': 'Custom username' });
  const pass = el('input', { placeholder: 'password', type: 'password', autocomplete: 'off', 'aria-label': 'Custom password' });
  const custom = el('span', { class: 'row', hidden: true }, user, pass);
  const listeners = new Set();
  select.addEventListener('change', () => { custom.hidden = select.value !== 'custom'; listeners.forEach((fn) => fn()); });
  return {
    node: el('span', { class: 'picker row' }, select, custom),
    value: () => ({ as: select.value, custom: select.value === 'custom' ? b64(`${user.value}:${pass.value}`) : undefined }),
    set: (as) => { select.value = as; custom.hidden = as !== 'custom'; },
    onChange: (fn) => listeners.add(fn),
    select,
  };
};

// ---------------------------------------------------------------- code blocks with tabs

/** A block that shows one request as curl, PowerShell or raw HTTP, with copy buttons. `read` returns the current request. */
Eng.codeTabs = (read, { onOpen } = {}) => {
  const out = el('pre', { class: 'code' });
  const tabs = [
    ['curl', (r) => Eng.curl(r)],
    ['PowerShell', (r) => Eng.powershell(r)],
    ['HTTP', (r) => Eng.rawHttp(r)],
    ['Setup', () => `# bash\n${Eng.setup('bash')}\n\n# PowerShell\n${Eng.setup('powershell')}`],
  ];
  let active = 0;
  const buttons = tabs.map(([name], index) => el('button', {
    type: 'button', class: 'tab', role: 'tab', 'aria-selected': String(index === 0), text: name,
    onclick: () => { active = index; draw(); },
  }));
  const draw = () => {
    buttons.forEach((b, i) => b.setAttribute('aria-selected', String(i === active)));
    const request = read();
    out.textContent = request || tabs[active][0] === 'Setup' ? tabs[active][1](request) : 'Choose an endpoint to see its request here.';
  };
  const node = el('div', { class: 'codeblock' },
    el('div', { class: 'tabs small', role: 'tablist' }, buttons, el('span', { class: 'grow' }),
      el('button', { type: 'button', class: 'btn small', text: 'Copy', onclick: () => copyText(out.textContent) }),
      onOpen ? el('button', { type: 'button', class: 'btn small', text: 'Open in workbench', onclick: onOpen }) : null),
    out);
  draw();
  return { node, refresh: draw };
};

/** JSON with its keys coloured; plain text for anything else. */
Eng.jsonView = (value) => {
  const text = typeof value === 'string' ? value : pretty(value);
  const pre = el('pre', { class: 'code json' });
  const token = /("(?:\\.|[^"\\])*")(\s*:)?|\b(true|false|null)\b|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g;
  let last = 0;
  for (const match of text.matchAll(token)) {
    pre.append(document.createTextNode(text.slice(last, match.index)));
    const kind = match[2] ? 'key' : match[1] ? 'str' : match[3] ? 'lit' : 'num';
    pre.append(el('span', { class: `j-${kind}`, text: match[1] ? match[1] : match[0] }));
    if (match[2]) pre.append(document.createTextNode(match[2]));
    last = match.index + match[0].length;
  }

  pre.append(document.createTextNode(text.slice(last)));
  return pre;
};

// ---------------------------------------------------------------- views and the router

/** A view is { title, subtitle, build(container, params), show?(params) }. build runs once; show runs each time it is opened. */
Eng.register = (name, view) => { Eng.views[name] = { ...view, built: false, container: null }; };

Eng.openInWorkbench = (request) => { Eng.workbenchRequest = request; Eng.show('workbench'); };

Eng.show = (name, params = '') => {
  if (!Eng.views[name]) name = 'overview';
  const view = Eng.views[name];
  const outlet = $('#eng-view');
  if (!view.built) {
    view.container = el('section', { class: 'view', id: `view-${name}`, hidden: true });
    outlet.append(view.container);
    view.build(view.container, params);
    view.built = true;
  }

  for (const other of Object.values(Eng.views)) if (other.container) other.container.hidden = other !== view;
  Eng.current?.hide?.();
  Eng.current = view;
  $('#eng-title').textContent = view.title;
  $('#eng-sub').textContent = view.subtitle;
  $$('.nav-item').forEach((button) => button.setAttribute('aria-current', button.dataset.view === name ? 'page' : 'false'));
  renderStrip(name);
  const wanted = `#/${name}${params ? '/' + params : ''}`;
  if (location.hash !== wanted) history.replaceState(null, '', wanted);
  view.show?.(params);
  $('#eng-main').scrollTop = 0;
};

/** The endpoints a page uses, always under its title: one click puts any of them in the workbench. */
function renderStrip(name) {
  const strip = $('#eng-strip');
  const own = Eng.catalogue.endpoints.filter((e) => e.view === name);
  strip.hidden = own.length === 0;
  fill(strip, el('span', { class: 'hint', text: 'This page calls' }), own.map((endpoint) => {
    const parts = Eng.routeParts(endpoint.route);
    return el('button', {
      type: 'button', class: 'ep', title: `${endpoint.title}: open in the workbench`,
      onclick: () => Eng.openInWorkbench({ method: parts.method, path: parts.pattern, params: {}, query: {}, headers: {}, as: Eng.defaultAccount(endpoint), endpointId: endpoint.id }),
    }, methodTag(parts.method, endpoint.danger), ' ', el('code', { text: parts.display }));
  }));
}

// ---------------------------------------------------------------- health

Eng.checkHealth = async () => {
  const entry = await Eng.send({ method: 'GET', path: '/health', params: {}, query: {}, headers: {}, as: 'anonymous' }, { quiet: true });
  Eng.health = { up: entry.status === 200, at: new Date(), status: entry.status, ms: entry.ms };
  const dot = $('#health-dot');
  dot.className = 'dot ' + (Eng.health.up ? 'ok' : 'bad');
  dot.title = Eng.health.up ? `The CMS answered /health in ${entry.ms} ms` : `The CMS did not answer /health (${entry.status || entry.error || 'no response'})`;
  $('#health-text').textContent = Eng.health.up ? 'up' : 'not answering';
  Eng.healthWatchers?.forEach((fn) => fn());
  return Eng.health.up;
};
