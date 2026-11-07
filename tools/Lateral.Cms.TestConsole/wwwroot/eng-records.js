'use strict';

// Records: list what the CMS holds, filter it, and drill into one record to see its payload, how each kind of caller sees it,
// the request that fetches it, and what can be done to it. Listing is by id, a page at a time (a cursor), exactly as the API gives it.

(() => {
  const rec = { as: 'admin', rows: [], cursor: null, filter: '', status: 'all', selected: null, loading: false, tab: 'record', detailToken: 0 };
  const ui = {};

  const ago = (iso) => {
    const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    if (seconds < 90) return 'just now';
    if (seconds < 5400) return `${Math.round(seconds / 60)} min ago`;
    if (seconds < 129600) return `${Math.round(seconds / 3600)} h ago`;
    return `${Math.round(seconds / 86400)} days ago`;
  };

  const snippet = (payload) => {
    if (payload?.kind === 'photo') return `photo: ${payload.name ?? ''}`;
    if (payload?.kind === 'album') return `album: ${payload.name ?? ''}`;
    const text = JSON.stringify(payload ?? {});
    return text.length > 80 ? text.slice(0, 80) + '…' : text;
  };

  const thumbOf = (payload) => payload?.thumb?.assetId ?? null;
  const previewOf = (payload) => payload?.preview?.assetId ?? payload?.thumb?.assetId ?? null;

  const statusOf = (row) => {
    if (!row.status) return 'visible';
    if (row.status.isDisabledByAdmin) return 'disabled';
    return row.status.isPublished ? 'published' : 'unpublished';
  };

  // ---------------------------------------------------------------- listing

  async function load({ all = false, append = false } = {}) {
    if (rec.loading) return;
    rec.loading = true;
    ui.message.hidden = true;
    ui.progress.hidden = !all;
    if (!append) { rec.rows = []; rec.cursor = null; }
    let pages = 0;
    try {
      do {
        const query = { limit: '100' };
        if (rec.cursor) query.cursor = rec.cursor;
        const entry = await Eng.call({ path: 'api/entities', query, as: rec.as, quiet: all && pages > 0 });
        if (entry.status !== 200 || !entry.json?.items) {
          ui.message.hidden = false;
          ui.message.className = `banner ${entry.status === 0 || entry.status >= 500 ? 'bad' : 'warn'}`;
          ui.message.textContent = `${entry.status || 'No response'}: ${entry.json?.detail ?? entry.error ?? 'Not allowed.'}`;
          break;
        }

        rec.rows.push(...entry.json.items);
        rec.cursor = entry.json.nextCursor ?? null;
        pages++;
        draw();
        if (all) { ui.progressBar.style.setProperty('width', rec.cursor ? '60%' : '100%'); ui.progressText.textContent = `${rec.rows.length} records so far…`; await sleep(0); }
      } while (all && rec.cursor);
    } finally {
      rec.loading = false;
      ui.progress.hidden = true;
      draw();
    }
  }

  function visibleRows() {
    const text = rec.filter.trim().toLowerCase();
    return rec.rows.filter((row) => {
      if (rec.as === 'admin' && rec.status !== 'all' && statusOf(row) !== rec.status) return false;
      if (!text) return true;
      return row.id.toLowerCase().includes(text) || JSON.stringify(row.payload).toLowerCase().includes(text);
    });
  }

  function draw() {
    const rows = visibleRows();
    const asAdmin = rec.as === 'admin';
    ui.statusBox.hidden = !asAdmin;
    ui.summary.textContent = `${rows.length}${rows.length !== rec.rows.length ? ` of ${rec.rows.length}` : ''} shown · ${rec.cursor ? 'more pages available' : rec.rows.length ? 'end of the list' : ''}`;
    ui.more.hidden = !rec.cursor;
    ui.all.hidden = !rec.cursor;
    ui.table.hidden = rec.rows.length === 0;
    ui.empty.hidden = rec.rows.length !== 0 || rec.loading || !ui.message.hidden;
    ui.empty.textContent = rec.as === 'user'
      ? 'Nothing is visible to users yet. Users see only published, enabled records. Send some events, or look as an admin.'
      : 'Nothing here yet. Send some events first.';
    const hasThumbs = rows.some((r) => thumbOf(r.payload));
    fill(ui.head, el('tr', {}, hasThumbs ? el('th', { text: '' }) : null, ['Id', 'Ver', asAdmin ? 'Status' : null, 'Updated', 'Payload'].filter(Boolean).map((h) => el('th', { text: h }))));
    fill(ui.body, rows.map((row) => {
      const status = statusOf(row);
      return el('tr', { class: `pick${row.id === rec.selected ? ' picked' : ''}`, onclick: () => open(row.id) },
        hasThumbs ? el('td', {}, thumbOf(row.payload) ? el('img', { class: 'thumb', alt: '', loading: 'lazy', src: `/media/${thumbOf(row.payload)}` }) : null) : null,
        el('td', {}, el('code', { text: row.id })),
        el('td', { class: 'num', text: String(row.version) }),
        asAdmin ? el('td', {}, el('span', { class: `chip ${status === 'published' ? 'ok' : status === 'disabled' ? 'bad' : 'warn'}`, text: status })) : null,
        el('td', { class: 'hint', title: row.updatedAt, text: ago(row.updatedAt) }),
        el('td', { class: 'hint', text: snippet(row.payload) }));
    }));
  }

  // ---------------------------------------------------------------- one record

  async function open(id) {
    rec.selected = id;
    draw();
    const token = ++rec.detailToken;
    fill(ui.detail, el('p', { class: 'hint', text: 'Reading…' }));
    const entry = await Eng.call({ path: `api/entities/${encodeURIComponent(id)}`, as: rec.as });
    if (token !== rec.detailToken) return;
    renderDetail(id, entry);
  }

  function entityRequest(id, as = rec.as) {
    return { method: 'GET', path: '/api/entities/{id}', params: { id }, query: {}, headers: {}, as, endpointId: 'get' };
  }

  function renderDetail(id, entry) {
    if (entry.status !== 200 || !entry.json) {
      fill(ui.detail,
        el('div', { class: 'card-head' }, el('h2', { text: 'Record' }), statusChip(entry.status)),
        el('code', { text: id }),
        el('div', { class: `meaning ${entry.status === 404 ? 'warn' : 'bad'}` }, Eng.endpoint('get').responses[String(entry.status)] ?? (entry.error ?? 'Not available.')),
        rec.as === 'user' && entry.status === 404 ? el('div', { class: 'row' }, el('button', { type: 'button', class: 'btn', text: 'Look as an admin', onclick: () => { setViewer('admin'); open(id); } })) : null,
        visibilityBlock(id));
      return;
    }

    const entity = entry.json;
    const tabs = [['record', 'Record'], ['visibility', 'Who sees it'], ['request', 'Request'], ['actions', 'Actions']];
    const body = el('div', { class: 'col' });
    const draw = () => {
      fill(body);
      if (rec.tab === 'record') body.append(...recordTab(entity).filter(Boolean));
      else if (rec.tab === 'visibility') body.append(visibilityBlock(entity.id));
      else if (rec.tab === 'request') body.append(...requestTab(entity).filter(Boolean));
      else body.append(...actionsTab(entity).filter(Boolean));
    };

    const strip = el('div', { class: 'tabs', role: 'tablist' }, tabs.map(([key, label]) => el('button', {
      type: 'button', class: 'tab', role: 'tab', 'aria-selected': String(rec.tab === key), text: label,
      onclick: () => { rec.tab = key; $$('.tab', strip).forEach((t, i) => t.setAttribute('aria-selected', String(tabs[i][0] === key))); draw(); },
    })));

    const previewId = previewOf(entity.payload);
    fill(ui.detail,
      el('div', { class: 'card-head' }, el('h2', { text: 'Record' }), el('span', { class: 'row' }, statusChip(entry.status), el('span', { class: 'hint', text: `${entry.ms} ms` }))),
      previewId ? el('img', { class: 'preview-img', alt: 'The picture this record points at', src: `/media/${previewId}` }) : null,
      strip, body);
    draw();
  }

  function recordTab(entity) {
    const verify = el('div', { class: 'col' });
    const status = entity.status;
    const media = [];
    const collect = (value, path = '') => {
      if (value && typeof value === 'object') for (const [k, v] of Object.entries(value)) collect(v, path ? `${path}.${k}` : k);
      else if (typeof value === 'string' && /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(value) && /asset/i.test(path)) media.push([path, value]);
    };
    collect(entity.payload);

    return [
      el('dl', { class: 'kv' },
        el('dt', { text: 'Id' }), el('dd', {}, el('code', { text: entity.id })),
        el('dt', { text: 'Version' }), el('dd', { text: String(entity.version) }),
        el('dt', { text: 'Checksum' }), el('dd', {}, el('button', { type: 'button', class: 'linkish', title: 'Copy', onclick: () => copyText(entity.checksum), text: entity.checksum })),
        el('dt', { text: 'Last event' }), el('dd', { text: `${new Date(entity.updatedAt).toLocaleString()} (${ago(entity.updatedAt)})` }),
        status ? [el('dt', { text: 'Status' }), el('dd', { text: `${status.isPublished ? 'published' : 'unpublished'}${status.isDisabledByAdmin ? `, disabled by ${status.disabledBy}` : ''}` })] : null),
      el('h3', { text: 'Payload' }),
      Eng.jsonView(entity.payload),
      el('div', { class: 'row wrap' },
        el('button', { type: 'button', class: 'btn small', text: 'Copy the JSON', onclick: () => copyText(pretty(entity.payload)) }),
        el('button', {
          type: 'button', class: 'btn small', text: 'Verify the checksum',
          onclick: async () => {
            const response = await fetch(`/api/verify/${encodeURIComponent(entity.id)}?as=${rec.as === 'admin' ? 'admin' : 'user'}`, { headers: { 'X-Console-Request': '1' } });
            const json = await response.json().catch(() => null);
            fill(verify, response.ok && json?.visible
              ? el('div', { class: `banner ${json.match ? 'ok' : 'bad'}`, text: json.match ? `Match. Recomputed ${json.computed.slice(0, 22)}… over the ${json.payloadBytes} payload bytes exactly as served.` : `MISMATCH. The API says ${json.claimed} but the payload hashes to ${json.computed || '(unknown algorithm)'}.` })
              : el('div', { class: 'banner', text: json?.error ?? 'Could not verify.' }));
          },
        })),
      verify,
      media.length ? el('div', { class: 'col' }, el('h3', { text: 'Files this record points at' }), media.map(([path, id]) => el('div', { class: 'row' },
        el('code', { text: path }), el('button', { type: 'button', class: 'linkish', text: id.slice(0, 8) + '…', onclick: () => Eng.show('assets', id) })))) : null,
      sessionHistory(entity.id),
    ];
  }

  /** Ask for the record as every kind of caller, so visibility is something you see rather than something you are told. */
  function visibilityBlock(id) {
    const out = el('div', { class: 'col' });
    const run = async () => {
      fill(out, el('p', { class: 'hint', text: 'Asking as each caller…' }));
      const callers = [['user', 'a user'], ['admin', 'an admin'], ['cms', 'the CMS account'], ['anonymous', 'no credentials']].filter(([as]) => hasAccount(as));
      const results = [];
      for (const [as, label] of callers) results.push([label, await Eng.call({ path: `api/entities/${encodeURIComponent(id)}`, as })]);
      const note = (label, entry) => {
        if (entry.status === 200) return entry.json?.status ? `Sees it (${entry.json.status.isPublished ? 'published' : 'unpublished'}${entry.json.status.isDisabledByAdmin ? ', disabled' : ''})` : 'Sees it';
        if (entry.status === 404) return 'Not found (missing, unpublished, disabled or deleted look the same to a user)';
        if (entry.status === 403) return 'Refused: this account cannot read the API';
        if (entry.status === 401) return 'Refused: no valid credentials';
        return `Answer ${entry.status}`;
      };
      fill(out,
        el('table', {}, el('thead', {}, el('tr', {}, ['Asked as', 'Status', 'Meaning'].map((h) => el('th', { text: h })))),
          el('tbody', {}, results.map(([label, entry]) => el('tr', {}, el('td', { text: label }), el('td', {}, statusChip(entry.status)), el('td', { text: note(label, entry) }))))),
        el('p', { class: 'hint', text: 'A user cannot tell a hidden record from one that never existed. That is deliberate: the API does not confirm that a restricted record is there.' }));
    };
    const wrap = el('div', { class: 'col' }, el('h3', { text: 'Who can see this record' }), out, el('button', { type: 'button', class: 'btn small', text: 'Ask again', onclick: run }));
    run();
    return wrap;
  }

  function requestTab(entity) {
    const code = Eng.codeTabs(() => entityRequest(entity.id), { onOpen: () => Eng.openInWorkbench(entityRequest(entity.id)) });
    return [el('p', { class: 'hint', text: `The call this page just made to read the record, as ${rec.as === 'admin' ? 'an admin' : 'a user'}. Copy it into a terminal to repeat it.` }), code.node,
      el('h3', { text: 'The same record, with the other callers' }),
      el('div', { class: 'row wrap' }, ['user', 'admin', 'anonymous'].map((as) => el('button', { type: 'button', class: 'btn small', text: `As ${as}`, onclick: () => Eng.openInWorkbench(entityRequest(entity.id, as)) })))];
  }

  function actionsTab(entity) {
    const out = el('div', { class: 'col' });
    const admin = async (verb) => {
      const entry = await Eng.call({ method: 'POST', path: `api/entities/${encodeURIComponent(entity.id)}/${verb}`, as: 'admin' });
      fill(out, el('div', { class: `banner ${entry.status === 200 ? 'ok' : 'bad'}`, text: entry.status === 200
        ? `${verb === 'disable' ? 'Disabled' : 'Enabled'}. Visible to users now: ${entry.json.visible ? 'yes' : 'no'}. The CMS's own data is untouched.`
        : `${entry.status}: ${entry.json?.detail ?? entry.error ?? 'Refused.'}` }));
      if (entry.status === 200) { await load(); await open(entity.id); }
    };
    const prefill = (events) => { Eng.prefillEvents = events; Eng.show('events'); };
    const at = (minutes) => new Date(Date.now() + minutes * 60000).toISOString();
    const nextVersion = entity.version + 1;
    return [
      el('h3', { text: 'Admin override' }),
      el('p', { class: 'hint', text: 'Hide the record from users without touching the CMS. Admins only; any other account gets 403.' }),
      el('div', { class: 'row wrap' },
        el('button', { type: 'button', class: 'btn', text: 'Disable', onclick: () => admin('disable') }),
        el('button', { type: 'button', class: 'btn', text: 'Enable', onclick: () => admin('enable') })),
      out,
      el('h3', { text: 'Send an event about this record' }),
      el('p', { class: 'hint', text: 'These fill the batch on the Send events page; nothing is sent until you press Send there.' }),
      el('div', { class: 'row wrap' },
        el('button', { type: 'button', class: 'btn', text: `Publish version ${nextVersion}`, onclick: () => prefill([{ type: 'publish', id: entity.id, version: nextVersion, payload: { ...entity.payload, editedAt: at(0) }, timestamp: at(0) }]) }),
        el('button', { type: 'button', class: 'btn', text: `Replay version ${entity.version}`, onclick: () => prefill([{ type: 'publish', id: entity.id, version: entity.version, payload: entity.payload, timestamp: at(0) }]) }),
        el('button', { type: 'button', class: 'btn', text: 'Unpublish', onclick: () => prefill([{ type: 'unPublish', id: entity.id, version: nextVersion, payload: entity.payload, timestamp: at(0) }]) }),
        el('button', { type: 'button', class: 'btn danger', text: 'Delete', onclick: () => prefill([{ type: 'delete', id: entity.id, timestamp: at(0) }]) })),
      el('p', { class: 'hint', text: 'Replay shows idempotence (ignored, not applied twice). Delete is permanent and leaves only a payload-free tombstone that blocks older events.' }),
    ];
  }

  /** Requests this console made that mention the record: a session trail a tester can follow. */
  function sessionHistory(id) {
    const mine = Eng.log.filter((e) => e.request.params?.id === id || (typeof e.request.body === 'string' && e.request.body.includes(id)) || Eng.fullPath(e.request).includes(encodeURIComponent(id))).slice(0, 8);
    if (mine.length === 0) return null;
    return el('div', { class: 'col' }, el('h3', { text: 'In this console\'s request log' }),
      el('div', { class: 'history' }, mine.map((e) => el('button', { type: 'button', onclick: () => Eng.openInWorkbench(e.request) },
        methodTag(e.request.method), el('code', { text: Eng.fullPath(e.request).slice(0, 50) }), statusChip(e.status), el('span', { class: 'hint', text: e.time.toLocaleTimeString() })))));
  }

  // ---------------------------------------------------------------- the page

  function setViewer(as) {
    rec.as = as;
    $$('button', ui.seg).forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.as === as)));
    rec.selected = null;
    fill(ui.detail, el('p', { class: 'hint', text: 'Pick a record from the list, or look one up by id.' }));
    load();
  }

  function build(container) {
    ui.seg = el('div', { class: 'seg', role: 'group', 'aria-label': 'Look at the records as' }, [['user', 'As a user'], ['admin', 'As an admin']].map(([as, label]) => el('button', {
      type: 'button', 'data-as': as, 'aria-pressed': String(as === rec.as), text: label, onclick: () => setViewer(as),
    })));
    const filter = el('input', { type: 'search', placeholder: 'Filter by id or anything in the payload…', 'aria-label': 'Filter the loaded records', class: 'mono' });
    filter.addEventListener('input', () => { rec.filter = filter.value; draw(); });
    ui.status = el('select', { 'aria-label': 'Status' }, ['all', 'published', 'unpublished', 'disabled'].map((s) => el('option', { value: s, text: s === 'all' ? 'Any status' : s })));
    ui.status.addEventListener('change', () => { rec.status = ui.status.value; draw(); });
    ui.statusBox = el('span', {}, ui.status);
    const lookup = el('input', { placeholder: 'Look up an id', class: 'mono', 'aria-label': 'Look up a record by id', spellcheck: 'false' });
    const go = () => { const id = lookup.value.trim(); if (id) open(id); };
    lookup.addEventListener('keydown', (event) => { if (event.key === 'Enter') go(); });

    ui.message = el('div', { class: 'banner', hidden: true });
    ui.summary = el('span', { class: 'hint' });
    ui.head = el('thead');
    ui.body = el('tbody');
    ui.table = el('div', { class: 'table-wrap' }, el('table', {}, ui.head, ui.body));
    ui.empty = el('p', { class: 'hint pad', hidden: true });
    ui.more = el('button', { type: 'button', class: 'btn', text: 'Load the next page', onclick: () => load({ append: true }) });
    ui.all = el('button', { type: 'button', class: 'btn', text: 'Load everything', title: 'Pages through the whole list, 100 at a time', onclick: () => load({ all: true, append: true }) });
    ui.progressBar = el('div');
    ui.progressText = el('span', { class: 'hint' });
    ui.progress = el('div', { class: 'col', hidden: true }, el('div', { class: 'progress' }, ui.progressBar), ui.progressText);
    ui.detail = el('div', { class: 'card rec-detail' }, el('p', { class: 'hint', text: 'Pick a record from the list, or look one up by id.' }));

    fill(container,
      el('div', { class: 'card' },
        el('div', { class: 'row wrap' }, ui.seg, filter, ui.statusBox, el('span', { class: 'grow' }),
          lookup, el('button', { type: 'button', class: 'btn', text: 'Look up', onclick: go }),
          el('button', { type: 'button', class: 'btn primary', text: 'Refresh', onclick: () => load() })),
        el('p', { class: 'hint', text: 'A user sees only published, enabled records, so one you have just unpublished or disabled drops out of that list. An admin sees everything, with a status. The list is in id order, a page at a time.' })),
      el('div', { class: 'rec' },
        el('div', { class: 'card' }, el('div', { class: 'card-head' }, el('h2', { text: 'Records' }), ui.summary), ui.message, ui.empty, ui.table,
          el('div', { class: 'row wrap' }, ui.more, ui.all), ui.progress),
        ui.detail));
  }

  Eng.register('records', {
    title: 'Records',
    subtitle: 'What the CMS holds. Filter, page through it, and open any record to see its payload and exactly who can see it.',
    build,
    show(params) {
      if (rec.rows.length === 0 && !rec.loading) load();
      if (params) open(params);
    },
  });
})();
