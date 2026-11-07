'use strict';

// The API workbench: every endpoint the CMS has, ready to edit and send, shown as curl / PowerShell / raw HTTP, with the
// answer explained. It is the "any call" tool; the other pages are shortcuts to it for the things testers do most.

(() => {
  const wb = { endpoint: null, picker: null, parts: null, code: null, history: [], generated: null, chosen: null, filter: '' };
  const ui = {};

  const tokens = (value) => JSON.parse(JSON.stringify(value).replaceAll('{{id}}', 'demo-' + rand(5)).replaceAll('{{now}}', isoAt()));

  // ---------------------------------------------------------------- choosing an endpoint

  function renderList() {
    const text = wb.filter.trim().toLowerCase();
    fill(ui.list, Eng.catalogue.areas.map((area) => {
      const items = Eng.catalogue.endpoints.filter((e) => e.area === area.id && (!text || `${e.route} ${e.title} ${e.what}`.toLowerCase().includes(text)));
      if (items.length === 0) return null;
      return el('div', { class: 'wb-group' }, el('h4', { text: area.title }), items.map((endpoint) => {
        const parts = Eng.routeParts(endpoint.route);
        return el('button', {
          type: 'button', class: 'wb-item', title: `${endpoint.title}. ${endpoint.what}`, 'aria-current': String(endpoint === wb.endpoint),
          onclick: () => select({ method: parts.method, path: parts.pattern, params: {}, query: {}, headers: {}, as: Eng.defaultAccount(endpoint), endpointId: endpoint.id }),
        }, methodTag(parts.method, endpoint.danger), el('code', { text: parts.display }));
      }));
    }));
  }

  /** Works out which endpoint a request is for, and its path parameters, even if the path was given with the ids filled in. */
  function identify(request) {
    if (request.endpointId && Eng.endpoint(request.endpointId)) return Eng.endpoint(request.endpointId);
    const exact = Eng.catalogue.endpoints.find((e) => e.route === `${request.method} ${request.path}`);
    if (exact) return exact;
    return Eng.endpointFor(request.method, request.path);
  }

  function extractParams(endpoint, request) {
    if (Object.keys(request.params ?? {}).length > 0) return request.params;
    const display = Eng.routeParts(endpoint.route).display;
    const names = [...display.matchAll(/\{(\w+)\}/g)].map((m) => m[1]);
    const regex = new RegExp('^' + display.replace(/[.*+?^$()|[\]\\]/g, '\\$&').replace(/\{\w+\}/g, '([^/]+)') + '$');
    const match = regex.exec(request.path.split('?')[0]);
    return match ? Object.fromEntries(names.map((name, i) => [name, decodeURIComponent(match[i + 1])])) : {};
  }

  function select(request) {
    const endpoint = identify(request);
    if (!endpoint) { Eng.toast('That request is not one of the CMS endpoints'); return; }
    wb.endpoint = endpoint;
    wb.generated = null;
    wb.chosen = null;
    const parts = Eng.routeParts(endpoint.route);
    const given = extractParams(endpoint, request);
    renderList();

    // the fixed header strip
    fill(ui.title, methodTag(parts.method, endpoint.danger), el('code', { class: 'big', text: parts.display }),
      el('span', { class: 'grow' }), endpoint.who.map((who) => el('span', { class: 'role', text: who })));
    ui.what.textContent = endpoint.what;
    ui.picker.set(request.as ?? Eng.defaultAccount(endpoint));

    // path parameters, filled from what was seen last
    fill(ui.params, (endpoint.params ?? []).map((p) => {
      const input = el('input', { class: 'mono', value: given[p.name] ?? Eng.context[p.context] ?? p.example ?? '', 'aria-label': p.name, spellcheck: 'false', autocomplete: 'off' });
      input.addEventListener('input', refresh);
      input.dataset.param = p.name;
      return el('label', {}, el('span', {}, el('b', { text: `{${p.name}}` }), ' ', p.about), input);
    }));
    ui.paramsBox.hidden = (endpoint.params ?? []).length === 0;

    fill(ui.query, (endpoint.query ?? []).map((q) => {
      const input = el('input', { class: 'mono', value: request.query?.[q.name] ?? q.example ?? '', placeholder: q.example ?? '', 'aria-label': q.name, spellcheck: 'false', autocomplete: 'off' });
      input.addEventListener('input', refresh);
      input.dataset.query = q.name;
      return el('label', {}, el('span', {}, el('b', { text: q.name }), ' ', q.about), input);
    }));
    ui.queryBox.hidden = (endpoint.query ?? []).length === 0;

    wb.headerRows = [];
    fill(ui.headers);
    for (const h of endpoint.headers ?? []) addHeaderRow(h.name, request.headers?.[h.name] ?? h.example ?? '', h.about);
    for (const [name, value] of Object.entries(request.headers ?? {})) if (!(endpoint.headers ?? []).some((h) => h.name === name)) addHeaderRow(name, value);
    ui.headersBox.hidden = false;

    const kind = endpoint.body?.kind;
    ui.bodyBox.hidden = !kind;
    ui.jsonBox.hidden = kind !== 'json';
    ui.fileBox.hidden = kind !== 'file';
    ui.bodyNote.textContent = endpoint.body?.note ?? '';
    if (kind === 'json') ui.body.value = typeof request.body === 'string' ? request.body : pretty(tokens(endpoint.body.example ?? {}));
    ui.fileInfo.textContent = 'No file chosen yet. Choose one, or generate some bytes.';

    ui.answer.hidden = true;
    ui.followups.hidden = true;
    refresh();
    ui.root.scrollIntoView?.({ block: 'nearest' });
  }

  function addHeaderRow(name = '', value = '', about = '') {
    const nameInput = el('input', { class: 'mono', value: name, placeholder: 'Header', 'aria-label': 'Header name', spellcheck: 'false' });
    const valueInput = el('input', { class: 'mono', value, placeholder: about || 'value', title: about, 'aria-label': 'Header value', spellcheck: 'false' });
    const row = el('div', { class: 'kvrow' }, nameInput, valueInput, el('button', { type: 'button', class: 'btn small ghost', text: 'Remove', onclick: () => { wb.headerRows.splice(wb.headerRows.indexOf(entry), 1); row.remove(); refresh(); } }));
    const entry = { nameInput, valueInput };
    nameInput.addEventListener('input', refresh);
    valueInput.addEventListener('input', refresh);
    wb.headerRows.push(entry);
    ui.headers.append(row);
  }

  // ---------------------------------------------------------------- reading the form

  function read() {
    const endpoint = wb.endpoint;
    const parts = Eng.routeParts(endpoint.route);
    const params = {};
    $$('[data-param]', ui.params).forEach((i) => { params[i.dataset.param] = i.value; });
    const query = {};
    $$('[data-query]', ui.query).forEach((i) => { query[i.dataset.query] = i.value; });
    const headers = {};
    for (const { nameInput, valueInput } of wb.headerRows) if (nameInput.value.trim() && valueInput.value !== '') headers[nameInput.value.trim()] = valueInput.value;

    let body;
    if (endpoint.body?.kind === 'json' && ui.body.value.trim() !== '') body = ui.body.value;
    if (endpoint.body?.kind === 'file' && (wb.generated || wb.chosen)) body = wb.generated ?? wb.chosen;
    const identity = ui.picker.value();
    return { method: parts.method, path: parts.pattern, params, query, headers, body, as: identity.as, custom: identity.custom, endpointId: endpoint.id };
  }

  function refresh() {
    if (!wb.endpoint) return;
    const request = read();
    wb.code?.refresh();
    const endpoint = wb.endpoint;
    const allowed = endpoint.who.includes(request.as);
    const known = ['cms', 'user', 'admin', 'anonymous'].includes(request.as);
    ui.expect.hidden = !known || allowed;
    ui.expect.textContent = known && !allowed
      ? `The design says ${request.as === 'anonymous' ? 'a caller with no credentials' : `the ${request.as} account`} is refused here (${request.as === 'anonymous' ? '401' : '403'}). Send it anyway to see.`
      : '';
  }

  // ---------------------------------------------------------------- sending and showing the answer

  async function sendIt() {
    if (!wb.endpoint) return;
    const request = read();
    ui.send.disabled = true;
    ui.send.textContent = 'Sending…';
    try {
      const entry = await Eng.send(request);
      wb.history.unshift({ request, status: entry.status, ms: entry.ms, time: entry.time });
      wb.history.length = Math.min(wb.history.length, 12);
      renderHistory();
      showAnswer(entry);
    } finally {
      ui.send.disabled = false;
      ui.send.textContent = 'Send';
    }
  }

  function explain(entry) {
    const endpoint = wb.endpoint;
    const text = endpoint.responses?.[String(entry.status)];
    if (entry.error) return ['bad', `The request did not complete: ${entry.error}`];
    if (text) return [statusTone(entry.status), text];
    if (entry.status === 429) return ['warn', 'Too many requests. Retry-After says how long to wait.'];
    if (entry.status >= 500) return ['bad', 'The server failed. That is never the right answer to a bad request: please report it, with the correlation id if there is one.'];
    return ['warn', 'The design does not list this status for this endpoint. Worth a look.'];
  }

  function showAnswer(entry) {
    const [tone, text] = explain(entry);
    ui.answer.hidden = false;
    const headerRows = Object.entries(entry.headers).sort(([a], [b]) => a.localeCompare(b));
    let preview;
    if (entry.error) preview = null;
    else if (entry.json !== null) preview = Eng.jsonView(entry.json);
    else if (entry.blob?.type.startsWith('image/') && entry.size > 0) preview = el('img', { class: 'preview-img', alt: 'The response, as an image', src: URL.createObjectURL(entry.blob) });
    else if (entry.text) preview = el('pre', { class: 'code wrap', text: entry.text.length > 20000 ? entry.text.slice(0, 20000) + '\n…' : entry.text });
    else if (entry.size > 0) preview = el('p', {}, `${fmtBytes(entry.size)} of ${entry.contentType || 'binary data'}. `, el('a', { href: URL.createObjectURL(entry.blob), download: 'response.bin', text: 'Download it' }));
    else preview = el('p', { class: 'hint', text: '(empty body)' });

    fill(ui.answer,
      el('div', { class: 'card-head' }, el('h2', { text: 'Response' }),
        el('span', { class: 'row' }, statusChip(entry.status), el('span', { class: 'hint', text: `${entry.ms} ms · ${fmtBytes(entry.size)}` }),
          entry.correlation ? el('button', { type: 'button', class: 'linkish', title: 'Copy the correlation id', onclick: () => copyText(entry.correlation), text: entry.correlation }) : null)),
      el('div', { class: `meaning ${tone}` }, el('b', {}, `${entry.status || 'No response'}: `), text),
      headerRows.length ? el('details', {}, el('summary', { text: `Headers (${headerRows.length})` }), el('p', { class: 'hint', text: 'The console passes through only the headers a tester needs (and adds X-Console-Elapsed-Ms).' }),
        el('table', {}, el('tbody', {}, headerRows.map(([name, value]) => el('tr', {}, el('td', {}, el('code', { text: name })), el('td', {}, el('code', { text: value }))))))) : null,
      preview);

    followUps(entry);
  }

  /** The obvious next call, one click away: follow a batch, ask about an asset, send the next part. */
  function followUps(entry) {
    const next = [];
    const json = entry.json;
    const go = (id, patch) => () => { const endpoint = Eng.endpoint(id); const parts = Eng.routeParts(endpoint.route); select({ method: parts.method, path: parts.pattern, params: {}, query: {}, headers: {}, as: Eng.defaultAccount(endpoint), endpointId: id, ...patch }); };
    if (wb.endpoint.id === 'ingest' && json?.batchId) next.push(['Follow this batch', go('batch', { params: { batchId: json.batchId } })]);
    if (wb.endpoint.id === 'ingest' && json?.results?.[0]?.id) next.push(['Read the first record as a user', go('get', { params: { id: json.results[0].id }, as: 'user' })]);
    if ((wb.endpoint.id === 'asset-put' || wb.endpoint.id === 'asset-complete') && json?.id) next.push(['Ask about the asset', go('asset-status', { params: { id: json.id } })]);
    if (wb.endpoint.id === 'asset-status' && json?.state === 'clean') next.push(['Download it as a user', go('asset-get', { params: { id: json.id }, as: 'user' })]);
    if (wb.endpoint.id === 'asset-start' && json?.uploadId) next.push(['Upload part 1', go('asset-part', { params: { uploadId: json.uploadId, partNumber: '1' } })]);
    if (wb.endpoint.id === 'get' && entry.status === 404) next.push(['Look again as an admin', go('get', { params: { id: entry.request.params.id }, as: 'admin' })]);
    ui.followups.hidden = next.length === 0;
    fill(ui.followups, el('span', { class: 'hint', text: 'Next:' }), next.map(([label, run]) => el('button', { type: 'button', class: 'btn small', text: label, onclick: run })));
  }

  function renderHistory() {
    ui.historyBox.hidden = wb.history.length === 0;
    fill(ui.history, wb.history.map((h) => el('button', { type: 'button', title: 'Load this request again', onclick: () => select(h.request) },
      methodTag(h.request.method), el('code', { text: Eng.fullPath(h.request) }), statusChip(h.status), el('span', { class: 'hint', text: h.request.as }))));
  }

  // ---------------------------------------------------------------- building the page

  function build(container) {
    ui.root = container;
    ui.list = el('div', { class: 'col' });
    const search = el('input', { type: 'search', placeholder: 'Find an endpoint…', 'aria-label': 'Find an endpoint' });
    search.addEventListener('input', () => { wb.filter = search.value; renderList(); });

    ui.title = el('div', { class: 'row' });
    ui.what = el('p', { class: 'hint' });
    wb.picker = ui.picker = Eng.accountPicker('cms');
    ui.picker.onChange(refresh);
    ui.expect = el('div', { class: 'meaning warn', hidden: true });
    ui.params = el('div', { class: 'form' });
    ui.paramsBox = el('div', { class: 'form' }, el('h3', { text: 'Path' }), ui.params);
    ui.query = el('div', { class: 'form' });
    ui.queryBox = el('div', { class: 'form' }, el('h3', { text: 'Query string' }), ui.query);
    ui.headers = el('div', { class: 'kvrows' });
    ui.headersBox = el('div', { class: 'form' }, el('div', { class: 'row' }, el('h3', { class: 'grow', text: 'Headers' }),
      el('button', { type: 'button', class: 'btn small', text: 'Add a header', onclick: () => addHeaderRow() })), ui.headers);
    ui.bodyNote = el('p', { class: 'hint' });
    ui.body = el('textarea', { rows: '10', spellcheck: 'false', 'aria-label': 'Request body' });
    ui.body.addEventListener('input', refresh);
    ui.jsonBox = el('div', { class: 'form' }, ui.body, el('div', { class: 'row wrap' },
      el('button', { type: 'button', class: 'btn small', text: 'Insert the example', onclick: () => { ui.body.value = pretty(tokens(wb.endpoint.body.example ?? {})); refresh(); } }),
      el('button', { type: 'button', class: 'btn small', text: 'Tidy the JSON', onclick: () => { try { ui.body.value = pretty(JSON.parse(ui.body.value)); } catch { Eng.toast('That is not valid JSON, which is fine if you meant to test that'); } refresh(); } }),
      el('button', { type: 'button', class: 'btn small', text: 'Make it 3 events', onclick: () => { ui.body.value = pretty(Array.from({ length: 3 }, () => tokens(wb.endpoint.body.example ?? [])[0] ?? {})); refresh(); } })));
    ui.fileInfo = el('p', { class: 'hint' });
    const file = el('input', { type: 'file', 'aria-label': 'File to send' });
    file.addEventListener('change', () => { wb.chosen = file.files[0] ?? null; wb.generated = null; ui.fileInfo.textContent = wb.chosen ? `${wb.chosen.name}: ${fmtBytes(wb.chosen.size)}` : 'No file chosen yet.'; refresh(); });
    const size = el('input', { type: 'number', min: '0', value: '16', 'aria-label': 'KiB to generate' });
    ui.fileBox = el('div', { class: 'form' }, file, el('div', { class: 'row wrap' },
      el('label', { class: 'inline' }, 'or generate ', size, ' KiB of random bytes'),
      el('button', { type: 'button', class: 'btn small', text: 'Generate', onclick: () => {
        const bytes = new Uint8Array(Math.round(Number(size.value) * 1024));
        for (let i = 0; i < bytes.length; i += 65536) crypto.getRandomValues(bytes.subarray(i, Math.min(bytes.length, i + 65536)));
        wb.generated = new Blob([bytes]);
        wb.chosen = null;
        ui.fileInfo.textContent = `${fmtBytes(bytes.length)} of random bytes generated (the scanner will call them application/octet-stream).`;
        refresh();
      } })), ui.fileInfo);
    ui.bodyBox = el('div', { class: 'form' }, el('h3', { text: 'Body' }), ui.bodyNote, ui.jsonBox, ui.fileBox);

    ui.send = el('button', { type: 'button', class: 'btn primary', text: 'Send', onclick: sendIt });
    wb.code = Eng.codeTabs(() => (wb.endpoint ? read() : null));
    ui.answer = el('div', { class: 'card', hidden: true });
    ui.followups = el('div', { class: 'row wrap', hidden: true });
    ui.history = el('div', { class: 'history' });
    ui.historyBox = el('div', { class: 'card flat', hidden: true }, el('h3', { text: 'This session' }), ui.history);

    fill(container, el('div', { class: 'wb' },
      el('div', { class: 'card wb-list' }, search, ui.list),
      el('div', { class: 'col' },
        el('div', { class: 'card' },
          ui.title, ui.what,
          el('div', { class: 'row wrap' }, el('label', { class: 'inline' }, 'Act as', ui.picker.node)),
          ui.expect, ui.paramsBox, ui.queryBox, ui.headersBox, ui.bodyBox,
          el('div', { class: 'row wrap' }, ui.send, el('button', { type: 'button', class: 'btn', text: 'Copy as curl', onclick: () => copyText(Eng.curl(read()), 'curl copied') })),
          wb.code.node),
        ui.followups, ui.answer, ui.historyBox)));
    renderList();
  }

  Eng.register('workbench', {
    title: 'API workbench',
    subtitle: 'Any call the CMS accepts: edit it, see it as curl or PowerShell, send it, and have the answer explained.',
    build,
    show() {
      if (Eng.workbenchRequest) { const r = Eng.workbenchRequest; Eng.workbenchRequest = null; select(r); return; }
      if (!wb.endpoint) {
        const endpoint = Eng.endpoint('ingest');
        if (endpoint) select({ method: 'POST', path: '/cms/events', params: {}, query: {}, headers: {}, as: 'cms', endpointId: 'ingest' });
      }
    },
  });
})();
