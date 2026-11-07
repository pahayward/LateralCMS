'use strict';

// Send events: build a batch (from a scenario, the form, or by editing the JSON), see it as the curl that sends it, send it
// as any caller, and watch the CMS decide: 202 queued, then each event's final outcome.

(() => {
  const ev = (type, id, version, payload, timestamp) => {
    const event = { type, id };
    if (version !== undefined) event.version = version;
    if (payload !== undefined) event.payload = payload;
    event.timestamp = timestamp;
    return event;
  };

  const SCENARIOS = [
    { name: 'Publish an article', note: 'One new record at version 1.', build: (s) => [ev('publish', s.id, 1, { title: 'Hello', body: 'First draft' }, s.t(0))] },
    { name: 'Update, then replay', note: 'Replays come back ignored, not applied twice.', build: (s) => [
      ev('publish', s.id, 1, { title: 'Hello' }, s.t(0)), ev('publish', s.id, 2, { title: 'Hello again' }, s.t(1)),
      ev('publish', s.id, 1, { title: 'Hello' }, s.t(0)), ev('publish', s.id, 2, { title: 'Hello again' }, s.t(1))] },
    { name: 'Out-of-order delivery', note: 'Version 3 arrives before 1 and 2. The end state is the same.', build: (s) => [
      ev('publish', s.id, 3, { title: 'Third' }, s.t(2)), ev('publish', s.id, 1, { title: 'First' }, s.t(0)), ev('publish', s.id, 2, { title: 'Second' }, s.t(1))] },
    { name: 'Unpublish keeps the data', note: 'Users lose it, admins still see it.', build: (s) => [
      ev('publish', s.id, 1, { title: 'Going dark' }, s.t(0)), ev('unPublish', s.id, 2, { title: 'Going dark' }, s.t(1))] },
    { name: 'Hard delete', note: 'Gone for good; older events cannot bring it back.', build: (s) => [
      ev('publish', s.id, 1, { title: 'Short lived' }, s.t(0)), ev('delete', s.id, undefined, undefined, s.t(1)), ev('publish', s.id, 2, { title: 'Too late' }, s.t(0.5))] },
    { name: 'Same version, different content', note: 'A deterministic winner, flagged as a conflict.', build: (s) => [
      ev('publish', s.id, 1, { title: 'Version A' }, s.t(0)), ev('publish', s.id, 1, { title: 'Version B' }, s.t(0))] },
    { name: 'A batch with bad events', note: 'Each bad event is rejected with its own reason; the good one is applied.', build: (s) => [
      { id: s.id + '-a', version: 1, payload: {}, timestamp: s.t(0) },
      { type: 'publish', version: 1, payload: {}, timestamp: s.t(0) },
      { type: 'publish', id: s.id + '-c', version: 'abc', payload: {}, timestamp: s.t(0) },
      { type: 'publish', id: s.id + '-d', version: 1, payload: {}, timestamp: isoAt(24 * 3600e3) },
      { type: 'publish', id: s.id + '-e', version: 1, payload: 'not an object', timestamp: s.t(0) },
      ev('archive', s.id + '-f', 1, {}, s.t(0)),
      ev('publish', s.id + '-ok', 1, { title: 'I am fine' }, s.t(0))] },
    { name: 'Payload over the limit', note: 'Only that event is rejected (payload_too_large).', build: (s) => [
      ev('publish', s.id + '-big', 1, { text: 'x'.repeat(Eng.config.limits.maxPayloadBytes + 1024) }, s.t(0)), ev('publish', s.id + '-ok', 1, { title: 'Small and fine' }, s.t(0))] },
    { name: 'A video, by reference', note: 'How a 10 GB file belongs in an event: its address, size and checksum.', build: (s) => [ev('publish', s.id, 1, {
      title: 'Launch video', media: { url: `https://media.example.com/${s.id}/launch.mp4`, contentType: 'video/mp4', sizeBytes: 10 * GiB, sha256: '0'.repeat(64) } }, s.t(0))] },
  ];

  const ui = {};
  let follow = 0;
  Eng.scenarios = SCENARIOS; // the Design page offers these as "try it" buttons

  const context = () => { const base = Date.now() - 30 * 60e3; return { id: 'demo-' + rand(5), t: (minutes) => new Date(base + minutes * 60e3).toISOString() }; };

  Eng.scenarioContext = context;

  function readBatch() {
    if (ui.batch.value.trim() === '') return [];
    try { const value = JSON.parse(ui.batch.value); return Array.isArray(value) ? value : null; } catch { return null; }
  }

  function badge() {
    const events = readBatch();
    ui.badge.className = events === null ? 'chip warn' : 'chip';
    ui.badge.textContent = events === null ? 'not a JSON array' : events.length === 0 ? 'empty' : `${events.length} event${events.length === 1 ? '' : 's'}`;
    ui.code?.refresh();
  }

  function setBatch(events) { ui.batch.value = pretty(events); badge(); }

  const request = () => { const who = ui.picker.value(); return { method: 'POST', path: '/cms/events', params: {}, query: {}, headers: {}, body: ui.batch.value, as: who.as, custom: who.custom, endpointId: 'ingest' }; };

  // ---------------------------------------------------------------- the answer

  function resultsTable(results) {
    return el('div', { class: 'table-wrap' }, el('table', {},
      el('thead', {}, el('tr', {}, ['#', 'Id', 'Type', 'Ver', 'Outcome', 'Reason', 'Checksum'].map((h) => el('th', { text: h })))),
      el('tbody', {}, results.map((r) => el('tr', {},
        el('td', { text: String(r.index) }),
        el('td', {}, r.id ? el('button', { type: 'button', class: 'linkish', title: 'Open this record', onclick: () => Eng.show('records', r.id) }, el('code', { text: r.id })) : '-'),
        el('td', { text: r.type ?? '-' }), el('td', { text: r.version ?? '-' }),
        el('td', {}, outcomeChip(r.outcome)),
        el('td', {}, r.reason ? el('code', { text: r.reason }) : el('span', { class: 'hint', text: '-' })),
        el('td', {}, r.checksum ? el('code', { title: r.checksum, text: shortChecksum(r.checksum) }) : '-'))))));
  }

  function outcomeChip(outcome) {
    const tone = outcome === 'applied' ? 'ok' : outcome === 'rejected' ? 'bad' : outcome === 'queued' ? 'info' : 'warn';
    return el('span', { class: `chip ${tone}`, text: outcome });
  }

  const summary = (json) => `Received ${json.received}: ${json.queued ? `${json.queued} queued, ` : ''}${json.applied} applied, ${json.ignored} ignored, ${json.rejected} rejected.`;

  function vanished(results) {
    const gone = results.filter((r) => r.outcome === 'applied' && ['unpublish', 'delete'].includes(String(r.type).toLowerCase()));
    if (gone.length === 0) return null;
    return el('div', { class: 'banner info' },
      el('p', { text: 'What users and admins see now:' }),
      el('ul', {}, gone.map((r) => el('li', {}, el('code', { text: r.id }), String(r.type).toLowerCase() === 'delete'
        ? ' was deleted. Nobody can see it; only a payload-free tombstone remains.'
        : ' was unpublished. Users no longer see it. Admins still do, marked unpublished.'))),
      el('button', { type: 'button', class: 'btn small', text: 'Open it in Records', onclick: () => Eng.show('records', gone[0].id) }));
  }

  function renderResult(entry) {
    follow++; // a new send ends the follow-up of the previous one
    ui.result.hidden = false;
    const parts = [el('div', { class: 'card-head' }, el('h2', { text: 'Response' }),
      el('span', { class: 'row' }, statusChip(entry.status), el('span', { class: 'hint', text: `${entry.ms} ms` }),
        entry.correlation ? el('button', { type: 'button', class: 'linkish', title: 'Copy the correlation id', onclick: () => copyText(entry.correlation), text: entry.correlation }) : null))];
    if (entry.error) parts.push(el('div', { class: 'banner bad', text: `The request did not complete: ${entry.error}` }));
    const retry = entry.headers['retry-after'];
    if (retry) parts.push(el('div', { class: 'banner warn', text: `The server asked the sender to wait ${retry} s before retrying (Retry-After).` }));

    const json = entry.json;
    if (json && Array.isArray(json.results)) {
      const line = el('p', { text: summary(json) });
      const holder = el('div', {}, resultsTable(json.results));
      const after = el('div', {}, vanished(json.results));
      parts.push(line, holder);
      if (json.status === 'queued') {
        const banner = el('div', { class: 'banner info', text: 'Accepted (202). The batch is stored and waiting for the worker. Following it…' });
        parts.push(banner);
        followBatch(json.batchId, entry.request, { banner, line, holder, after });
      }

      parts.push(after);
    } else if (!entry.error) {
      const text = Eng.endpoint('ingest').responses[String(entry.status)];
      parts.push(el('div', { class: `banner ${entry.status < 500 ? 'warn' : 'bad'}`, text: `${entry.status}: ${text ?? ''} ${json?.detail ?? ''}`.trim() }));
    }

    if (entry.text) parts.push(el('details', {}, el('summary', { text: 'Raw response' }), Eng.jsonView(entry.json ?? entry.text)));
    fill(ui.result, parts);
  }

  async function followBatch(batchId, original, view) {
    const token = ++follow;
    const started = performance.now();
    for (let poll = 0; token === follow; poll++) {
      await sleep(poll === 0 ? 400 : 1000);
      if (token !== follow) return;
      const entry = await Eng.call({ path: `cms/batches/${batchId}`, as: original.as, custom: original.custom, quiet: true });
      if (token !== follow) return;
      const json = entry.json;
      if (entry.status !== 200 || !json || !Array.isArray(json.results)) {
        view.banner.className = 'banner bad';
        view.banner.textContent = `Could not read the batch status (${entry.status || entry.error || 'no response'}).`;
        return;
      }

      fill(view.holder, resultsTable(json.results));
      view.line.textContent = summary(json);
      const seconds = ((performance.now() - started) / 1000).toFixed(1);
      if (json.status === 'completed') {
        view.banner.className = 'banner ok';
        view.banner.textContent = `Completed after ${seconds} s. The outcomes above are final.`;
        fill(view.after, vanished(json.results));
        Eng.addLog(entry);
        return;
      }

      if (json.status === 'failed') {
        view.banner.className = 'banner bad';
        view.banner.textContent = `The CMS gave up on this batch after ${json.attempts} attempts (${json.lastError}). Nothing from it was applied. An admin can requeue it from Queue and admin.`;
        Eng.addLog(entry);
        return;
      }

      view.banner.textContent = json.attempts ? `Queued: attempt ${json.attempts} failed, so it is waiting to be retried (${seconds} s so far).` : `Queued: waiting for the worker to apply it (${seconds} s so far).`;
      if (performance.now() - started > 120000) { view.banner.textContent += ' Still waiting after two minutes. Check Queue and admin.'; return; }
    }
  }

  // ---------------------------------------------------------------- the page

  function build(container) {
    ui.picker = Eng.accountPicker('cms');
    ui.picker.onChange(badge);
    ui.batch = el('textarea', { rows: '16', spellcheck: 'false', 'aria-label': 'Batch of events as JSON' });
    ui.batch.addEventListener('input', badge);
    ui.badge = el('span', { class: 'chip' });
    ui.result = el('div', { class: 'card', hidden: true });

    const field = (label, input) => el('label', {}, label, input);
    ui.id = el('input', { class: 'mono', value: 'demo-' + rand(5), spellcheck: 'false', autocomplete: 'off', 'aria-label': 'Record id' });
    ui.type = el('input', { value: 'publish', autocomplete: 'off', list: 'ev-types', 'aria-label': 'Event type' });
    const types = el('datalist', { id: 'ev-types' }, ['publish', 'unPublish', 'delete', 'archive'].map((t) => el('option', { value: t })));
    ui.version = el('input', { type: 'number', min: '0', value: '1', 'aria-label': 'Version' });
    ui.ts = el('input', { class: 'mono', value: isoAt(), spellcheck: 'false', autocomplete: 'off', 'aria-label': 'Timestamp' });
    ui.payload = el('textarea', { rows: '4', spellcheck: 'false', 'aria-label': 'Payload' });
    ui.payload.value = '{ "title": "Hello", "body": "First draft" }';
    ui.error = el('p', { class: 'hint', hidden: true });
    ui.refFile = el('input', { type: 'file', 'aria-label': 'Attach a file as a reference' });
    ui.refNote = el('p', { class: 'hint', hidden: true });
    let attached = null;
    ui.refFile.addEventListener('change', () => {
      const file = ui.refFile.files[0];
      attached = file ? { fileName: file.name, contentType: file.type || 'application/octet-stream', sizeBytes: file.size } : null;
      ui.refNote.hidden = !file;
      if (file) ui.refNote.textContent = `Only a reference to "${file.name}" (${fmtBytes(file.size)}) goes into the payload; its bytes are never sent. For the bytes themselves, use Assets.`;
    });

    const add = () => {
      ui.error.hidden = true;
      const events = readBatch();
      if (events === null) { ui.error.textContent = 'The batch is not a JSON array yet. Fix it or clear it first.'; ui.error.hidden = false; return; }
      const type = ui.type.value.trim();
      const event = { type, id: ui.id.value };
      if (type.toLowerCase() !== 'delete') {
        event.version = Number(ui.version.value);
        try {
          const payload = JSON.parse(ui.payload.value || '{}');
          if (attached && payload && typeof payload === 'object' && !Array.isArray(payload)) payload.media = attached;
          event.payload = payload;
        } catch { ui.error.textContent = 'The payload is not valid JSON.'; ui.error.hidden = false; return; }
      }

      event.timestamp = ui.ts.value.trim();
      events.push(event);
      setBatch(events);
      ui.version.value = String(Number(ui.version.value) + 1);
      ui.ts.value = isoAt();
    };

    const scenarios = el('details', { open: true, class: 'card' },
      el('summary', {}, el('b', { text: 'Start from a scenario' }), ' ', el('span', { class: 'hint', text: 'Each fills the batch with fresh ids. Edit it before sending if you like.' })),
      el('div', { class: 'cards-start' }, SCENARIOS.map((scenario) => el('button', { type: 'button', class: 'start-card', onclick: () => { setBatch(scenario.build(context())); ui.batch.scrollIntoView({ block: 'nearest' }); } }, el('b', { text: scenario.name }), el('span', { text: scenario.note })))));

    ui.code = Eng.codeTabs(request, { onOpen: () => Eng.openInWorkbench(request()) });
    const send = el('button', { type: 'button', class: 'btn primary', text: 'Send the batch' });
    send.addEventListener('click', async () => {
      send.disabled = true;
      try { renderResult(await Eng.send(request())); } finally { send.disabled = false; }
    });

    fill(container, scenarios,
      el('div', { class: 'split' },
        el('div', { class: 'card' }, el('h2', { text: 'Build an event' }), el('div', { class: 'form' },
          el('div', { class: 'row' }, field('Record id', ui.id), el('button', { type: 'button', class: 'btn small', text: 'New id', onclick: () => { ui.id.value = 'demo-' + rand(5); } })),
          el('div', { class: 'row wrap' }, field('Type', ui.type), types, field('Version', ui.version), field('Timestamp (UTC)', ui.ts)),
          field('Payload (a JSON object; ignored for delete)', ui.payload),
          field('Attach a file as a reference', ui.refFile), ui.refNote, ui.error,
          el('div', {}, el('button', { type: 'button', class: 'btn primary', text: 'Add to the batch', onclick: add })))),
        el('div', { class: 'card' },
          el('div', { class: 'card-head' }, el('h2', { text: 'Batch' }), ui.badge),
          ui.batch,
          el('div', { class: 'row wrap' }, el('label', { class: 'inline' }, 'Send as', ui.picker.node), el('span', { class: 'grow' }),
            el('button', { type: 'button', class: 'btn ghost', text: 'Clear', onclick: () => { ui.batch.value = ''; badge(); ui.result.hidden = true; } }), send))),
      el('div', { class: 'card' }, el('h2', { text: 'The same request, outside this page' }), ui.code.node),
      ui.result);
    badge();
  }

  Eng.register('events', {
    title: 'Send events',
    subtitle: 'Act as the CMS webhook: build a batch, see the curl for it, send it, and watch it go from accepted to applied.',
    build,
    show() {
      if (Eng.prefillEvents) { setBatch(Eng.prefillEvents); Eng.prefillEvents = null; }
    },
  });
})();
