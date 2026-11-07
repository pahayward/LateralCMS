'use strict';

// Overview: what this is, whether it is healthy right now, where to start, and a guided tour that walks one record through
// its whole life against the real service (publish, read, replay, unpublish, delete). The tour is the part to present.

(() => {
  const ui = {};
  const tour = { id: '', version: 0, done: new Set(), running: false };

  // ---------------------------------------------------------------- live status

  async function refreshStatus() {
    const stat = (value, label, tone = '', note = '') => el('div', { class: `stat ${tone}` }, el('div', { class: 'value', text: value }), el('div', { class: 'label', text: label }), note ? el('div', { class: 'hint', text: note }) : null);
    fill(ui.stats, stat('…', 'Checking'));
    await Eng.checkHealth();
    const [queue, list, assets] = await Promise.all([
      Eng.call({ path: 'api/admin/ingestion', as: 'admin', quiet: true }),
      Eng.call({ path: 'api/entities', query: { limit: '100' }, as: 'admin', quiet: true }),
      Eng.call({ path: 'api/admin/assets', query: { limit: '500' }, as: 'admin', quiet: true }),
    ]);

    const cards = [stat(Eng.health.up ? 'Up' : 'Down', 'The CMS', Eng.health.up ? 'ok' : 'bad', Eng.health.up ? `answered in ${Eng.health.ms} ms` : 'not answering /health')];
    if (queue.json) {
      const q = queue.json;
      cards.push(stat(q.workerEnabled ? 'Running' : 'Off', 'Worker', q.workerEnabled ? 'ok' : 'bad', `${q.pendingBatches} batches waiting`));
      cards.push(stat(String(q.failedBatches), 'Failed batches', q.failedBatches > 0 ? 'bad' : 'ok', q.failedBatches > 0 ? 'an admin should look' : 'nothing set aside'));
    }

    if (list.json?.items) cards.push(stat(`${list.json.items.length}${list.json.nextCursor ? '+' : ''}`, 'Records', '', 'published, unpublished or disabled'));
    const items = Array.isArray(assets.json) ? assets.json : assets.json?.items;
    if (Array.isArray(items)) {
      const by = items.reduce((a, i) => ((a[i.state] = (a[i.state] ?? 0) + 1), a), {});
      cards.push(stat(`${items.length}${items.length === 500 ? '+' : ''}`, 'Assets', '', Object.entries(by).map(([s, n]) => `${n} ${s}`).join(', ') || 'none yet'));
    }

    fill(ui.stats, cards);
  }

  // ---------------------------------------------------------------- the guided tour

  const at = (minutes = 0) => new Date(Date.now() + minutes * 60000).toISOString();
  const post = (events, as = 'cms') => Eng.call({ method: 'POST', path: 'cms/events', as, body: JSON.stringify(events) });
  const read = (as) => Eng.call({ path: `api/entities/${encodeURIComponent(tour.id)}`, as });

  async function settle(ack) {
    const batchId = ack.json?.batchId;
    if (!batchId) return ack;
    for (let i = 0; i < 80; i++) {
      const entry = await Eng.call({ path: `cms/batches/${batchId}`, as: 'cms', quiet: i > 0 });
      if (entry.json?.status && entry.json.status !== 'queued') return entry;
      await sleep(250);
    }

    return ack;
  }

  const outcomeLine = (entry) => (entry.json?.results ?? []).map((r) => `${r.type}${r.version != null ? ` v${r.version}` : ''} → ${r.outcome}${r.reason ? ` (${r.reason})` : ''}`).join('; ');

  const STEPS = [
    {
      title: 'Publish a record',
      text: 'The CMS tells us a new article exists by POSTing an event. We validate it, store it durably and answer 202: accepted, not yet applied.',
      async run() {
        tour.id = `tour-${rand(5)}`; tour.version = 1;
        const ack = await post([{ type: 'publish', id: tour.id, version: 1, payload: { title: 'The guided tour', body: 'First draft' }, timestamp: at() }]);
        return [[`HTTP ${ack.status}: ${ack.json?.status === 'queued' ? 'stored and queued' : ack.json?.status}`, ack.status === 202 ? 'ok' : 'bad'], [`batch ${ack.json?.batchId}`, ''], [outcomeLine(ack), '']];
      },
    },
    {
      title: 'Let the worker apply it',
      text: 'A background worker applies each batch with a pure function: an event changes state only if it sorts after what is stored. We follow the batch until it is decided.',
      async run() {
        const final = await settle({ json: { batchId: Eng.context.batchId } });
        return [[`batch ${final.json?.status}`, final.json?.status === 'completed' ? 'ok' : 'bad'], [outcomeLine(final), '']];
      },
    },
    {
      title: 'Read it back, as three different callers',
      text: 'Users read the API. The CMS account cannot (it only writes). Nobody without credentials can.',
      async run() {
        const user = await read('user'); const cms = await read('cms'); const none = await read('anonymous');
        return [[`user → ${user.status} (version ${user.json?.version}, checksum ${shortChecksum(user.json?.checksum)})`, user.status === 200 ? 'ok' : 'bad'],
          [`CMS account → ${cms.status} (it writes, it does not read)`, cms.status === 403 ? 'ok' : 'bad'], [`no credentials → ${none.status}`, none.status === 401 ? 'ok' : 'bad']];
      },
    },
    {
      title: 'Send an old version after a new one',
      text: 'Delivery can be late, repeated or out of order. Version 2 arrives, then version 1: the older one must change nothing.',
      async run() {
        const two = await settle(await post([{ type: 'publish', id: tour.id, version: 2, payload: { title: 'The guided tour', body: 'Second draft' }, timestamp: at(1) }]));
        const one = await settle(await post([{ type: 'publish', id: tour.id, version: 1, payload: { title: 'The guided tour', body: 'First draft' }, timestamp: at() }]));
        const now = await read('user');
        tour.version = 2;
        return [[`version 2 → ${two.json?.results?.[0]?.outcome}`, 'ok'], [`then version 1 → ${one.json?.results?.[0]?.outcome} (${one.json?.results?.[0]?.reason ?? ''})`, one.json?.results?.[0]?.outcome?.startsWith('ignored') ? 'ok' : 'bad'],
          [`the record still says: ${now.json?.payload?.body} (version ${now.json?.version})`, now.json?.version === 2 ? 'ok' : 'bad']];
      },
    },
    {
      title: 'Unpublish it',
      text: 'An unpublish hides the record from users but keeps the data. Users get the same 404 as for something that never existed; admins still see it.',
      async run() {
        const done = await settle(await post([{ type: 'unPublish', id: tour.id, version: 3, payload: { title: 'The guided tour', body: 'Second draft' }, timestamp: at(2) }]));
        const user = await read('user'); const admin = await read('admin');
        tour.version = 3;
        return [[outcomeLine(done), ''], [`user → ${user.status} (hidden: indistinguishable from missing)`, user.status === 404 ? 'ok' : 'bad'],
          [`admin → ${admin.status}, published: ${admin.json?.status?.isPublished}`, admin.status === 200 && admin.json?.status?.isPublished === false ? 'ok' : 'bad']];
      },
    },
    {
      title: 'Delete it for good',
      text: 'A delete removes the record and its payload and leaves only a tombstone. After that no older event can bring it back.',
      async run() {
        const done = await settle(await post([{ type: 'delete', id: tour.id, timestamp: at(3) }]));
        const admin = await read('admin');
        const late = await settle(await post([{ type: 'publish', id: tour.id, version: 9, payload: { title: 'Too late' }, timestamp: at(2) }]));
        const after = await read('admin');
        return [[outcomeLine(done), ''], [`admin → ${admin.status} (gone, even for an admin)`, admin.status === 404 ? 'ok' : 'bad'],
          [`an older publish afterwards → ${late.json?.results?.[0]?.outcome} (${late.json?.results?.[0]?.reason ?? ''})`, late.json?.results?.[0]?.outcome?.startsWith('ignored') ? 'ok' : 'bad'],
          [`still gone → ${after.status}`, after.status === 404 ? 'ok' : 'bad']];
      },
    },
  ];

  function drawSteps() {
    fill(ui.steps, STEPS.map((step, index) => {
      const done = tour.done.has(index);
      const ready = index === 0 || tour.done.has(index - 1);
      const result = el('div', { class: 'result' });
      const holder = { result };
      step.holder = holder;
      const node = el('div', { class: `step${done ? ' done' : ''}` },
        el('div', { class: 'no', text: done ? '✓' : String(index + 1) }),
        el('div', {}, el('h3', { text: step.title }), el('p', { class: 'hint', text: step.text }),
          el('div', { class: 'row wrap' }, el('button', {
            type: 'button', class: 'btn small primary', text: done ? 'Run again' : 'Run this step', disabled: !ready || tour.running,
            onclick: () => runStep(index),
          }), done && tour.id ? el('button', { type: 'button', class: 'btn small', text: 'Open the record', onclick: () => Eng.show('records', tour.id) }) : null),
          result));
      if (step.last) fill(result, step.last.map(([text, tone]) => el('div', { class: `chip ${tone}`, text })));
      return node;
    }));
  }

  async function runStep(index) {
    if (tour.running) return;
    tour.running = true;
    drawSteps();
    const step = STEPS[index];
    fill(step.holder.result, el('span', { class: 'hint', text: 'Running…' }));
    try {
      step.last = (await step.run()).filter(([text]) => text);
      tour.done.add(index);
    } catch (error) {
      step.last = [[`Failed: ${error.message}`, 'bad']];
    } finally {
      tour.running = false;
      drawSteps();
    }
  }

  async function runAll() {
    tour.done.clear();
    for (const step of STEPS) delete step.last;
    drawSteps();
    for (let i = 0; i < STEPS.length; i++) { await runStep(i); if (!tour.done.has(i)) break; }
  }

  // ---------------------------------------------------------------- the page

  function build(container) {
    ui.stats = el('div', { class: 'grid four' });
    ui.steps = el('div', { class: 'tour' });
    const go = (view, title, text) => el('button', { type: 'button', class: 'start-card', onclick: () => Eng.show(view) }, el('b', { text: title }), el('span', { text }));

    fill(container,
      el('div', { class: 'card' },
        el('h2', { text: 'What is this?' }),
        el('p', {}, 'A content-management system sends this service ', el('b', { text: 'events' }), ' (publish, unpublish, delete). The service keeps the latest state of each record, whatever order, however many times and however late the events arrive, and serves it to ',
          el('b', { text: 'users' }), ' and ', el('b', { text: 'admins' }), '. Files travel separately as ', el('b', { text: 'assets' }), ', and nothing is served until it has been scanned.'),
        el('p', { class: 'hint', text: 'This console talks to the real running service. Everything it does is a plain HTTP call; every call is shown as curl so you can repeat it anywhere.' }),
        el('div', { class: 'row wrap' }, el('button', { type: 'button', class: 'btn primary', text: 'See the architecture', onclick: () => Eng.show('design') }),
          el('button', { type: 'button', class: 'btn', text: 'Jump to the guided tour', onclick: () => ui.tourCard.scrollIntoView({ behavior: 'smooth', block: 'start' }) }),
          el('button', { type: 'button', class: 'btn ghost', text: 'Refresh the status', onclick: refreshStatus }))),
      ui.stats,
      el('div', {}, el('h2', { text: 'Start here' }), el('div', { class: 'cards-start' },
        go('events', 'Send some events', 'Build a batch from a scenario and watch it go from accepted to applied.'),
        go('records', 'Browse the records', 'Filter, drill into one, and see exactly who can see it.'),
        go('workbench', 'Make any API call', 'Every endpoint, editable, as curl or PowerShell, with the answer explained.'),
        go('assets', 'Upload a file', 'Quarantine, scan, then download in parallel ranges.'),
        go('verify', 'Check the rules', 'Who may do what, and every validation rule with its exact answer.'),
        go('design', 'Read the design', 'The architecture, a record’s life, the reconciliation rules and the limits.'),
        go('requirements', 'See how the brief was met', 'Every original requirement beside how it was met, the tests that prove it and what is still open.'))),
      ui.tourCard = el('div', { class: 'card' },
        el('div', { class: 'card-head' }, el('div', {}, el('h2', { text: 'Guided tour: the life of one record' }), el('p', { class: 'hint', text: 'Each step runs against the real service. Run them one at a time while you talk, or all at once.' })),
          el('button', { type: 'button', class: 'btn primary', text: 'Run the whole tour', onclick: runAll })),
        ui.steps));
    drawSteps();
  }

  Eng.register('overview', {
    title: 'Overview',
    subtitle: 'A webhook in, a read API out, and a store that always ends up in the right state.',
    build,
    show() { refreshStatus(); },
  });
})();
