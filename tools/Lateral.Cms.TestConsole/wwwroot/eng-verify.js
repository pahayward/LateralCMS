'use strict';

// Verify: three checks a tester can run against the live service and read at a glance.
//   * Who can do what: every endpoint as every kind of caller, compared with the design.
//   * Validation: one event per rule, at and just past each boundary, with the exact reason the CMS must give.
//   * Limits: what happens to a body far bigger than the webhook allows.

(() => {
  const ui = {};

  // ---------------------------------------------------------------- who can do what

  const ACCOUNTS = [['cms', 'CMS account'], ['user', 'User'], ['admin', 'Admin'], ['anonymous', 'No credentials'], ['wrong', 'Wrong password']];
  const NOT_2XX = 'not 2xx';
  const entityPath = (id) => `api/entities/${encodeURIComponent(id)}`;
  const COLUMNS = [
    { label: 'GET /health', req: () => ({ path: 'health' }), expect: { cms: 200, user: 200, admin: 200, anonymous: 200, wrong: 200 } },
    { label: 'POST /cms/events', req: () => ({ method: 'POST', path: 'cms/events', body: '[]' }), expect: { cms: 200, user: 403, admin: 403, anonymous: 401, wrong: 401 } },
    { label: 'GET /api/entities', req: () => ({ path: 'api/entities?limit=1' }), expect: { cms: 403, user: 200, admin: 200, anonymous: 401, wrong: 401 } },
    { label: 'GET /api/entities/{id}', req: (f) => ({ path: entityPath(f.id) }), expect: { cms: 403, user: 200, admin: 200, anonymous: 401, wrong: 401 } },
    { label: 'PUT /api/entities/{id}', note: 'there is no way to change content', req: (f) => ({ method: 'PUT', path: entityPath(f.id), body: '{"payload":{"title":"changed"}}' }), expect: { cms: NOT_2XX, user: NOT_2XX, admin: NOT_2XX, anonymous: NOT_2XX, wrong: NOT_2XX } },
    { label: 'POST …/disable', req: (f) => ({ method: 'POST', path: entityPath(f.id) + '/disable' }), expect: { cms: 403, user: 403, admin: 200, anonymous: 401, wrong: 401 } },
    { label: 'GET /api/admin/ingestion', req: () => ({ path: 'api/admin/ingestion' }), expect: { cms: 403, user: 403, admin: 200, anonymous: 401, wrong: 401 } },
    { label: 'GET /api/admin/assets', req: () => ({ path: 'api/admin/assets?limit=1' }), expect: { cms: 403, user: 403, admin: 200, anonymous: 401, wrong: 401 } },
  ];

  async function runMatrix() {
    ui.matrixRun.disabled = true;
    ui.matrixSummary.hidden = true;
    const cells = new Map();
    fill(ui.matrix,
      el('thead', {}, el('tr', {}, el('th', { text: 'Caller' }), COLUMNS.map((c) => el('th', {}, c.label, c.note ? el('small', { class: 'hint', text: c.note }) : null)))),
      el('tbody', {}, ACCOUNTS.map(([id, label]) => el('tr', {}, el('td', { text: label }), COLUMNS.map((_, i) => { const cell = el('td', { class: 'pending', text: '…' }); cells.set(`${id}:${i}`, cell); return cell; })))));
    try {
      const fixture = { id: 'matrix-' + rand(6) };
      const created = await Eng.call({ method: 'POST', path: 'cms/events', as: 'cms', quiet: true, body: JSON.stringify([{ type: 'publish', id: fixture.id, version: 1, payload: { title: 'Auth matrix fixture' }, timestamp: isoAt(-60e3) }]) });
      if (created.status !== 202 && created.status !== 200) {
        ui.matrixSummary.hidden = false; ui.matrixSummary.className = 'banner bad';
        ui.matrixSummary.textContent = `Could not create the fixture record as the CMS account (${created.status || created.error}). Is the CMS running and the CMS account configured?`;
        return;
      }

      // The fixture is applied in the background: wait until a user can read it.
      for (let i = 0; i < 40; i++) { if ((await Eng.call({ path: entityPath(fixture.id), as: 'user', quiet: true })).status === 200) break; await sleep(150); }

      let passed = 0;
      let total = 0;
      for (const [id] of ACCOUNTS) {
        for (let i = 0; i < COLUMNS.length; i++) {
          const column = COLUMNS[i];
          const expected = column.expect[id];
          const entry = await Eng.call({ as: id, quiet: true, ...column.req(fixture) });
          const ok = expected === NOT_2XX ? (entry.status !== 0 && (entry.status < 200 || entry.status >= 300)) : entry.status === expected;
          total++; if (ok) passed++;
          const cell = cells.get(`${id}:${i}`);
          cell.className = ok ? 'pass' : 'fail';
          fill(cell, String(entry.status || 'none'), ok ? null : el('small', { text: `expected ${expected}` }), entry.status === 429 ? el('small', { text: 'throttled' }) : null);
          cell.title = 'Click to open this call in the workbench';
          cell.style.setProperty('cursor', 'pointer');
          cell.addEventListener('click', () => Eng.openInWorkbench({ ...entry.request, endpointId: undefined, as: id }));
        }
      }

      await Eng.call({ method: 'POST', path: entityPath(fixture.id) + '/enable', as: 'admin', quiet: true });
      ui.matrixSummary.hidden = false;
      ui.matrixSummary.className = `banner ${passed === total ? 'ok' : 'bad'}`;
      ui.matrixSummary.textContent = passed === total ? `All ${total} combinations behaved as designed.` : `${total - passed} of ${total} combinations did not behave as designed (highlighted).`;
    } finally { ui.matrixRun.disabled = false; }
  }

  // ---------------------------------------------------------------- validation cases

  const accepted = ['queued', 'applied'];
  const event = (extra, id = 'case-' + rand(6)) => ({ type: 'publish', id, version: 1, payload: { a: 1 }, timestamp: isoAt(), ...extra });
  const without = (name) => { const e = event({}); delete e[name]; return e; };
  const deep = (levels) => JSON.parse('{"a":'.repeat(levels) + '1' + '}'.repeat(levels));

  /** Each case: what is sent (an event, or a raw body), and what must come back. */
  const CASES = [
    // the envelope
    { group: 'The envelope', name: 'An empty array', raw: '[]', status: 200 },
    { group: 'The envelope', name: 'A JSON object instead of an array', raw: '{}', status: 400 },
    { group: 'The envelope', name: 'Text that is not JSON', raw: 'hello', status: 400 },
    { group: 'The envelope', name: 'A trailing comma', raw: '[{"type":"publish",}]', status: 400 },
    { group: 'The envelope', name: '500 events (the most allowed)', build: () => Array.from({ length: 500 }, () => event({})), status: [200, 202] },
    { group: 'The envelope', name: '501 events (one too many)', build: () => Array.from({ length: 501 }, () => event({})), status: 413 },
    // version
    { group: 'Version', name: 'Version 1 (the lowest)', event: () => event({ version: 1 }), outcome: accepted },
    { group: 'Version', name: 'Version 0', event: () => event({ version: 0 }), reason: 'invalid_version' },
    { group: 'Version', name: 'Version -1', event: () => event({ version: -1 }), reason: 'invalid_version' },
    { group: 'Version', name: 'Version 1.5', event: () => event({ version: 1.5 }), reason: 'invalid_version' },
    { group: 'Version', name: 'Version "1" as text', event: () => event({ version: '1' }), reason: 'invalid_version' },
    { group: 'Version', name: 'Version missing', event: () => without('version'), reason: 'missing_version' },
    // id
    { group: 'Id', name: 'An id of 200 characters (the longest)', event: () => event({}, 'a'.repeat(200)), outcome: accepted },
    { group: 'Id', name: 'An id of 201 characters', event: () => event({}, 'a'.repeat(201)), reason: 'invalid_id' },
    { group: 'Id', name: 'An empty id', event: () => event({ id: '' }), reason: 'missing_id' },
    { group: 'Id', name: 'An id with a slash', event: () => event({ id: 'a/b-' + rand(4) }), reason: 'invalid_id' },
    { group: 'Id', name: 'An id with a new line', event: () => event({ id: 'a\nb' }), reason: 'invalid_id' },
    { group: 'Id', name: 'An id with emoji and accents', event: () => event({}, 'café-\u{1F600}-' + rand(4)), outcome: accepted },
    // type
    { group: 'Type', name: 'Type in capitals (PUBLISH)', event: () => event({ type: 'PUBLISH' }), outcome: accepted },
    { group: 'Type', name: 'Type missing', event: () => without('type'), reason: 'missing_type' },
    { group: 'Type', name: 'An unknown type (archive)', event: () => event({ type: 'archive' }), outcome: ['ignored_unsupported_type'], reason: 'unsupported_type' },
    // payload
    { group: 'Payload', name: 'An empty object', event: () => event({ payload: {} }), outcome: accepted },
    { group: 'Payload', name: 'An array instead of an object', event: () => event({ payload: [1] }), reason: 'invalid_payload' },
    { group: 'Payload', name: 'Text instead of an object', event: () => event({ payload: 'x' }), reason: 'invalid_payload' },
    { group: 'Payload', name: 'Payload missing', event: () => without('payload'), reason: 'missing_payload' },
    { group: 'Payload', name: 'Just under the size limit', event: () => event({ payload: { t: 'x'.repeat(Eng.config.limits.maxPayloadBytes - 64) } }), outcome: accepted },
    { group: 'Payload', name: 'Over the size limit', event: () => event({ payload: { t: 'x'.repeat(Eng.config.limits.maxPayloadBytes + 1024) } }), reason: 'payload_too_large' },
    { group: 'Payload', name: 'Nested too deeply (the whole request is limited to 32 levels)', build: () => [event({ payload: deep(40) })], status: 400 },
    { group: 'Payload', name: 'A lone surrogate in a string', raw: `[{"type":"publish","id":"case-${rand(5)}","version":1,"payload":{"t":"\\ud800"},"timestamp":"${isoAt()}"}]`, reason: 'malformed_event' },
    { group: 'Payload', name: 'HTML and quotes in a string (stored as text)', event: () => event({ payload: { t: '<script>alert(1)</script>&"\'' } }), outcome: accepted },
    // timestamp
    { group: 'Timestamp', name: 'A normal time with a zone', event: () => event({ timestamp: '2024-01-01T00:00:00+02:00' }), outcome: accepted },
    { group: 'Timestamp', name: 'A date with no time', event: () => event({ timestamp: '2024-01-01' }), reason: 'invalid_timestamp' },
    { group: 'Timestamp', name: 'A time with no zone', event: () => event({ timestamp: '2024-01-01T00:00:00' }), reason: 'invalid_timestamp' },
    { group: 'Timestamp', name: 'Not a time', event: () => event({ timestamp: 'yesterday' }), reason: 'invalid_timestamp' },
    { group: 'Timestamp', name: '30 minutes in the future (allowed skew)', event: () => event({ timestamp: isoAt(30 * 60e3) }), outcome: accepted },
    { group: 'Timestamp', name: '61 minutes in the future', event: () => event({ timestamp: isoAt(61 * 60e3) }), reason: 'timestamp_in_future' },
    { group: 'Timestamp', name: 'Timestamp missing', event: () => without('timestamp'), reason: 'missing_timestamp' },
  ];

  const bodyOf = (c) => (c.raw !== undefined ? c.raw : JSON.stringify(c.build ? c.build() : [c.event()]));
  const wantText = (c) => (c.status ? `HTTP ${[].concat(c.status).join(' or ')}` : c.reason ? `rejected: ${c.reason}` : `accepted (${c.outcome.join(' or ')})`);

  async function runCases() {
    ui.caseRun.disabled = true;
    ui.caseSummary.hidden = true;
    let passed = 0;
    for (const [index, c] of CASES.entries()) {
      const cell = ui.caseCells[index];
      fill(cell.got, '…');
      cell.row.className = '';
      const body = bodyOf(c);
      const who = ui.casePicker.value();
      const entry = await Eng.call({ method: 'POST', path: 'cms/events', as: who.as, custom: who.custom, body, quiet: true });
      const result = entry.json?.results?.[0];
      let ok;
      let got;
      if (c.status) { ok = [].concat(c.status).includes(entry.status); got = `HTTP ${entry.status}`; }
      else if (!result) { ok = false; got = `HTTP ${entry.status}${entry.json?.detail ? ': ' + entry.json.detail : ''}`; }
      else if (c.reason) { ok = result.outcome !== 'queued' && result.outcome !== 'applied' && result.reason === c.reason; got = `${result.outcome}${result.reason ? ': ' + result.reason : ''}`; }
      else { ok = c.outcome.includes(result.outcome); got = `${result.outcome}${result.reason ? ': ' + result.reason : ''}`; }
      if (ok) passed++;
      fill(cell.got, got);
      cell.result.className = ok ? 'pass' : 'fail';
      cell.result.textContent = ok ? 'pass' : 'FAIL';
      cell.open.onclick = () => Eng.openInWorkbench({ method: 'POST', path: '/cms/events', params: {}, query: {}, headers: {}, body, as: who.as, custom: who.custom, endpointId: 'ingest' });
    }

    ui.caseSummary.hidden = false;
    ui.caseSummary.className = `banner ${passed === CASES.length ? 'ok' : 'bad'}`;
    ui.caseSummary.textContent = passed === CASES.length ? `All ${CASES.length} cases gave exactly the answer the design promises.` : `${CASES.length - passed} of ${CASES.length} cases did not (highlighted).`;
    ui.caseRun.disabled = false;
  }

  // ---------------------------------------------------------------- sign-in

  const SIGN_IN = [
    ['A Bearer token instead of Basic', 'Bearer abc'],
    ['Basic with text that is not base64', 'Basic !!!'],
    ['Basic with no colon in it', 'Basic ' + btoa('nocolon')],
    ['Basic with an empty password', 'Basic ' + btoa('someone:')],
  ];

  async function runSignIn() {
    ui.signRun.disabled = true;
    const rows = [];
    for (const [name, header] of SIGN_IN) {
      // These go straight to the CMS through the proxy's custom-credentials slot, so the header is exactly what is written here.
      const entry = await Eng.call({ path: 'api/entities?limit=1', as: 'custom', custom: header.startsWith('Basic ') ? header.slice(6) : 'x', quiet: true });
      rows.push([name, header.startsWith('Basic ') ? header : `${header} (sent as Basic: the proxy can only send Basic)`, entry.status, header.startsWith('Basic ') ? entry.status === 401 : entry.status === 401]);
    }

    fill(ui.signBody, rows.map(([name, sent, status, ok]) => el('tr', {}, el('td', { text: name }), el('td', {}, el('code', { text: sent.length > 48 ? sent.slice(0, 48) + '…' : sent })), el('td', {}, statusChip(status)), el('td', { class: ok ? 'pass' : 'fail', text: ok ? 'pass (401)' : 'FAIL' }))));
    ui.signRun.disabled = false;
  }

  async function runThrottle() {
    if (!window.confirm('This sends 12 requests with a wrong password. The CMS throttles repeated failed sign-ins per address, so for about a minute EVERY request from this computer (including the photo library) will get 429. Go ahead?')) return;
    ui.throttleRun.disabled = true;
    const codes = [];
    for (let i = 0; i < 12; i++) codes.push((await Eng.call({ path: 'api/entities?limit=1', as: 'wrong', quiet: i > 0 })).status);
    ui.throttleOut.hidden = false;
    ui.throttleOut.className = `banner ${codes.includes(429) ? 'ok' : 'bad'}`;
    ui.throttleOut.textContent = `Statuses: ${codes.join(' ')}. ${codes.includes(429) ? 'The throttle engaged (429 with Retry-After). It lapses after about a minute.' : 'No 429: the throttle did not engage.'}`;
    ui.throttleRun.disabled = false;
  }

  // ---------------------------------------------------------------- limits

  let probeBusy = false;
  const note = (tone, text) => el('div', { class: `banner ${tone}`, text });

  async function runProbe() {
    if (probeBusy) return;
    probeBusy = true;
    ui.probeRun.disabled = true;
    ui.probeOut.hidden = false;
    fill(ui.probeOut, el('p', { class: 'hint', text: 'Sending… (the probe gives up after 20 seconds)' }));
    const unit = { KiB: 1024, MiB, GiB }[ui.probeUnit.value];
    const bytes = Math.round(Number(ui.probeSize.value) * unit);
    const mode = $('input[name="p-mode"]:checked', ui.root).value;
    const expectContinue = ui.probeExpect.checked;
    try {
      const response = await fetch('/api/probe/oversize', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Console-Request': '1' }, body: JSON.stringify({ bytes, mode, expectContinue }) });
      const json = await response.json().catch(() => null);
      if (!response.ok || !json?.outcome) { fill(ui.probeOut, note('bad', json?.error ?? `The console refused the probe (${response.status}).`)); return; }
      Eng.addLog({ time: new Date(), request: { method: 'POST', path: '/cms/events', params: {}, query: {}, headers: {}, body: `(probe: ${fmtBytes(bytes)} ${mode})`, as: 'cms' }, status: json.status ?? 0, ms: json.elapsedMs, headers: {}, size: 0, text: JSON.stringify(json), json, error: null, correlation: null });
      renderProbe(json);
    } catch (error) {
      fill(ui.probeOut, note('bad', `The probe failed: ${error.message}`));
    } finally { probeBusy = false; ui.probeRun.disabled = false; }
  }

  function renderProbe(r) {
    const saved = r.requestedBytes - r.bytesRead;
    const verdicts = {
      refused: ['ok', `Refused with 413 after ${r.elapsedMs} ms. The sender had read ${fmtBytes(r.bytesRead)} of the ${fmtBytes(r.requestedBytes)}${saved > 0 ? `, so ${fmtBytes(saved)} never had to be transferred or held in memory.` : '.'}`],
      completed: ['info', `The CMS read the body and answered ${r.status}: it is within the limit, so it was parsed (whitespace is not a batch, hence the error).`],
      connection_reset: ['warn', r.mode === 'chunked'
        ? `With no declared length the CMS cannot know the size up front. It read up to its limit (${fmtBytes(r.bytesRead)} pulled by the sender, including buffering), then stopped and closed the connection. Its memory use stayed bounded, but the sender sees a reset rather than a 413, so webhook senders should declare Content-Length.`
        : `The CMS refused the request and closed the connection while the sender was still pushing the body (${fmtBytes(r.bytesRead)} read so far), so the sender saw a reset instead of the 413. ${r.expectContinue ? 'Even with Expect: 100-continue the answer was lost; see the response detail.' : 'Tick "Expect: 100-continue" and run it again: the sender then waits for the verdict before sending any body, and gets a clean 413.'}`],
      timed_out: ['bad', `The CMS was still accepting data after 20 seconds (${fmtBytes(r.bytesRead)} read). It is not enforcing a body limit.`],
    };
    const [tone, text] = verdicts[r.outcome] ?? ['info', r.outcome];
    fill(ui.probeOut, note(tone, text),
      el('dl', { class: 'kv' },
        el('dt', { text: 'Requested' }), el('dd', { text: `${fmtBytes(r.requestedBytes)} (${r.mode}${r.expectContinue ? ', Expect: 100-continue' : ''})` }),
        el('dt', { text: 'HTTP status' }), el('dd', { text: r.status ? String(r.status) : 'none' }),
        el('dt', { text: 'Time to answer' }), el('dd', { text: `${r.elapsedMs} ms` }),
        el('dt', { text: 'Bytes the sender read' }), el('dd', { text: fmtBytes(r.bytesRead) })),
      r.detail ? el('details', {}, el('summary', { text: 'Response detail' }), el('pre', { class: 'code wrap', text: r.detail })) : null);
  }

  // ---------------------------------------------------------------- the page

  function build(container) {
    ui.root = container;
    ui.matrix = el('table', { class: 'matrix' });
    ui.matrixRun = el('button', { type: 'button', class: 'btn primary', text: 'Run the matrix', onclick: runMatrix });
    ui.matrixSummary = el('div', { class: 'banner info', hidden: true });

    ui.casePicker = Eng.accountPicker('cms', ['cms', 'user', 'admin', 'anonymous', 'wrong']);
    ui.caseRun = el('button', { type: 'button', class: 'btn primary', text: `Run all ${CASES.length} cases`, onclick: runCases });
    ui.caseSummary = el('div', { class: 'banner', hidden: true });
    ui.caseCells = [];
    let group = '';
    const caseRows = CASES.flatMap((c, index) => {
      const rows = [];
      if (c.group !== group) { group = c.group; rows.push(el('tr', {}, el('th', { colspan: '5', text: c.group }))); }
      const got = el('td', { class: 'hint', text: '' });
      const result = el('td', { text: '' });
      const open = el('button', { type: 'button', class: 'btn small ghost', text: 'Open' });
      const row = el('tr', {}, el('td', { text: c.name }), el('td', { class: 'hint', text: wantText(c) }), got, result, el('td', {}, open));
      ui.caseCells[index] = { row, got, result, open };
      open.onclick = () => Eng.openInWorkbench({ method: 'POST', path: '/cms/events', params: {}, query: {}, headers: {}, body: bodyOf(c), as: 'cms', endpointId: 'ingest' });
      rows.push(row);
      return rows;
    });

    ui.signRun = el('button', { type: 'button', class: 'btn', text: 'Run the sign-in cases', onclick: runSignIn });
    ui.signBody = el('tbody');
    ui.throttleRun = el('button', { type: 'button', class: 'btn danger', text: 'Trigger the throttle…', onclick: runThrottle });
    ui.throttleOut = el('div', { class: 'banner', hidden: true });

    ui.probeSize = el('input', { type: 'number', min: '1', value: '10', class: 'narrow', 'aria-label': 'Size' });
    ui.probeUnit = el('select', { 'aria-label': 'Unit' }, ['KiB', 'MiB', 'GiB'].map((u) => el('option', { value: u, text: u, selected: u === 'GiB' })));
    ui.probeExpect = el('input', { type: 'checkbox', 'aria-label': 'Expect: 100-continue' });
    ui.probeRun = el('button', { type: 'button', class: 'btn primary', text: 'Run the probe', onclick: runProbe });
    ui.probeOut = el('div', { class: 'form', hidden: true });
    const preset = (size, unit) => el('button', { type: 'button', class: 'btn small', text: `${size} ${unit}`, onclick: () => { ui.probeSize.value = size; ui.probeUnit.value = unit; } });
    const chosen = el('div', { class: 'form' });
    const file = el('input', { type: 'file', 'aria-label': 'Pick a file to check against the limits' });
    file.addEventListener('change', () => {
      const f = file.files[0];
      if (!f) { fill(chosen); return; }
      const { maxBodyBytes, maxPayloadBytes } = Eng.config.limits;
      fill(chosen,
        el('p', {}, el('b', { text: f.name }), ` is ${fmtBytes(f.size)}${f.type ? ` (${f.type})` : ''}.`),
        note(f.size > maxBodyBytes ? 'bad' : 'ok', f.size > maxBodyBytes ? `Over the webhook's ${fmtBytes(maxBodyBytes)} request limit: the CMS would answer 413 and keep nothing.` : `Within the ${fmtBytes(maxBodyBytes)} request limit.`),
        note(f.size > maxPayloadBytes ? 'bad' : 'ok', f.size > maxPayloadBytes ? `Over the ${fmtBytes(maxPayloadBytes)} limit on one payload: that event would be rejected (payload_too_large).` : `Within the ${fmtBytes(maxPayloadBytes)} payload limit.`),
        note('info', 'Either way the right shape is a reference: the file goes to Assets and the event carries its address, size and checksum.'));
    });

    fill(container,
      el('div', { class: 'card' },
        el('div', { class: 'card-head' }, el('h2', { text: 'Who can do what' }), ui.matrixRun),
        el('p', { class: 'hint', text: 'Publishes a fixture record as the CMS account, then calls endpoints as every kind of caller and compares each status with what the design says. Click any cell to open that call in the workbench. The wrong-password row runs last because failed sign-ins are throttled per address (a second run within a minute may show 429 there). The admin disable is undone at the end.' }),
        ui.matrixSummary, el('div', { class: 'table-wrap' }, ui.matrix)),
      el('div', { class: 'card' },
        el('div', { class: 'card-head' }, el('h2', { text: 'Validation: one event per rule' }), el('span', { class: 'row' }, el('label', { class: 'inline' }, 'Send as', ui.casePicker.node), ui.caseRun)),
        el('p', { class: 'hint', text: 'Every row sends one event (or one malformed body) at or just past a boundary and checks the exact reason the CMS gives. Rows that expect "accepted" create a throwaway record; they are applied in the background. The service\'s own tests cover each of these rules as well.' }),
        ui.caseSummary,
        el('div', { class: 'table-wrap' }, el('table', { class: 'case-table' }, el('thead', {}, el('tr', {}, ['Case', 'Must give', 'Got', 'Result', ''].map((h) => el('th', { text: h })))), el('tbody', {}, caseRows)))),
      el('div', { class: 'grid two' },
        el('div', { class: 'card' }, el('h2', { text: 'Sign-in edge cases' }),
          el('p', { class: 'hint', text: 'Malformed Authorization headers must be refused with 401, never accepted and never a server error.' }),
          el('div', { class: 'row wrap' }, ui.signRun, ui.throttleRun),
          el('div', { class: 'table-wrap' }, el('table', {}, el('thead', {}, el('tr', {}, ['Case', 'Sent', 'Status', ''].map((h) => el('th', { text: h })))), ui.signBody)), ui.throttleOut),
        el('div', { class: 'card' }, el('h2', { text: 'Check a real file against the limits' }), file, chosen,
          el('p', { class: 'hint', text: 'Nothing is uploaded. The browser reads only the name, type and size.' }))),
      el('div', { class: 'card' },
        el('h2', { text: 'Probe the webhook with an oversized body' }),
        el('p', { class: 'hint', text: 'The CMS holds the file elsewhere; this service only receives the event about it, so the webhook refuses large bodies outright. This really sends the webhook a request of that size.' }),
        el('div', { class: 'row wrap' }, el('label', { class: 'inline' }, 'Size', ui.probeSize), ui.probeUnit, preset(1, 'MiB'), preset(6, 'MiB'), preset(10, 'GiB')),
        el('div', { class: 'row wrap' },
          el('label', { class: 'inline' }, el('input', { type: 'radio', name: 'p-mode', value: 'declared', checked: true }), 'Declare the length'),
          el('label', { class: 'inline' }, el('input', { type: 'radio', name: 'p-mode', value: 'chunked' }), 'Chunked (no length)'),
          el('label', { class: 'inline' }, ui.probeExpect, 'Expect: 100-continue')),
        el('div', {}, ui.probeRun), ui.probeOut));
  }

  Eng.register('verify', {
    title: 'Security and limits',
    subtitle: 'Checks you can run against the live service: who may do what, whether every validation rule gives its exact answer, and what happens to oversized input.',
    build,
  });
})();
