'use strict';

// Queue and admin: what an operator watches (is the queue draining, has anything failed), and the admin tools. The CMS
// stores a batch, answers 202 and applies it a moment later; this is where it waits.

(() => {
  const st = { timer: 0, samples: [], auto: true };
  const ui = {};

  function spark(values) {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 240 48');
    svg.setAttribute('class', 'spark');
    svg.setAttribute('role', 'img');
    svg.setAttribute('aria-label', 'Events waiting, last minute');
    if (values.length < 2) return svg;
    const max = Math.max(1, ...values);
    const points = values.map((v, i) => `${(i / (values.length - 1)) * 238 + 1},${46 - (v / max) * 42}`).join(' ');
    const line = document.createElementNS(ns, 'polyline');
    line.setAttribute('points', points);
    line.setAttribute('fill', 'none');
    line.setAttribute('stroke', 'currentColor');
    line.setAttribute('stroke-width', '2');
    svg.append(line);
    return svg;
  }

  async function refresh(quiet = true) {
    const entry = await Eng.call({ path: 'api/admin/ingestion', as: 'admin', quiet });
    if (entry.status !== 200 || !entry.json) {
      ui.message.hidden = false;
      ui.message.className = 'banner bad';
      ui.message.textContent = entry.status === 401 || entry.status === 403 ? 'The admin account is not allowed to read the queue (is it configured?).' : `Could not read the queue: ${entry.status || entry.error || 'no response'}.`;
      return;
    }

    const r = entry.json;
    const notes = [];
    if (!r.workerEnabled && r.pendingBatches > 0) notes.push('The worker is switched off (Inbox:WorkerEnabled), so queued batches are waiting and will not be applied.');
    if (r.failedBatches > 0) notes.push(`${r.failedBatches} batch(es) failed and were set aside. Fix the cause, then requeue them below.`);
    ui.message.hidden = notes.length === 0;
    ui.message.className = r.failedBatches > 0 ? 'banner bad' : 'banner warn';
    ui.message.textContent = notes.join(' ');

    st.samples.push(r.pendingEvents);
    if (st.samples.length > 60) st.samples.shift();
    const stat = (value, label, tone = '') => el('div', { class: `stat ${tone}` }, el('div', { class: 'value', text: value }), el('div', { class: 'label', text: label }));
    const used = r.maxBytes ? Math.min(100, (r.usedBytes / r.maxBytes) * 100) : 0;
    const bar = el('div', {}); bar.style.setProperty('width', `${used.toFixed(1)}%`);
    fill(ui.stats,
      stat(r.workerEnabled ? 'Running' : 'Off', 'Worker', r.workerEnabled ? 'ok' : 'bad'),
      stat(`${r.pendingBatches}`, `Batches waiting (${r.pendingEvents} events)`),
      stat(r.oldestPendingAgeSeconds == null ? 'none' : `${r.oldestPendingAgeSeconds} s`, 'Oldest waiting'),
      stat(String(r.failedBatches), `Failed batches (${fmtBytes(r.failedBytes ?? 0)})`, r.failedBatches > 0 ? 'bad' : 'ok'),
      el('div', { class: 'stat' }, el('div', { class: 'value', text: fmtBytes(r.usedBytes) }), el('div', { class: 'label', text: `of ${fmtBytes(r.maxBytes)} (a full queue answers 429)` }), el('div', { class: 'progress' }, bar)));
    fill(ui.spark, spark(st.samples));

    ui.none.hidden = r.failed.length > 0;
    ui.failedTable.hidden = r.failed.length === 0;
    fill(ui.failedBody, r.failed.map((batch) => el('tr', {},
      el('td', {}, el('code', { text: batch.batchId })),
      el('td', { text: new Date(batch.receivedAt).toLocaleString() }),
      el('td', { class: 'num', text: String(batch.events) }), el('td', { class: 'num', text: String(batch.attempts) }),
      el('td', {}, el('code', { text: batch.lastError ?? '-' })),
      el('td', {}, el('button', {
        type: 'button', class: 'btn small', text: 'Requeue',
        onclick: async (event) => { event.currentTarget.disabled = true; await Eng.call({ method: 'POST', path: `api/admin/ingestion/${batch.batchId}/requeue`, as: 'admin' }); await refresh(false); },
      })))));
  }

  function watch(on) {
    clearInterval(st.timer);
    st.timer = 0;
    if (!on) return;
    refresh(true);
    st.timer = setInterval(() => { if (st.auto) refresh(true); }, 2000);
  }

  async function resetAll() {
    if (!window.confirm('Erase every entity, tombstone, ledger row and queued batch in the CMS? This cannot be undone.')) return;
    ui.reset.disabled = true;
    try {
      const entry = await Eng.call({ method: 'POST', path: 'api/admin/reset', as: 'admin', body: JSON.stringify({ confirm: 'reset-all-data' }) });
      ui.resetResult.hidden = false;
      if (entry.status === 200 && entry.json) {
        ui.resetResult.className = 'banner ok';
        ui.resetResult.textContent = `Reset. Erased ${entry.json.entities} entities, ${entry.json.tombstones} tombstones, ${entry.json.ledgerRows} ledger rows and ${entry.json.batches} batches (the queue is empty too). Assets and the audit log are kept.`;
      } else if (entry.status === 404) {
        ui.resetResult.className = 'banner';
        ui.resetResult.textContent = 'This CMS has no reset route: it is switched off. Set Admin:AllowReset to true (the Development settings already do) and restart it. It can never be enabled in Production.';
      } else {
        ui.resetResult.className = 'banner bad';
        ui.resetResult.textContent = `${entry.status || 'No response'}: ${entry.json?.detail ?? entry.error ?? 'Refused.'}`;
      }
    } finally { ui.reset.disabled = false; }
  }

  async function rescanOutdated() {
    const entry = await Eng.call({ method: 'POST', path: 'api/admin/assets/rescan-outdated', as: 'admin' });
    ui.assetResult.hidden = false;
    ui.assetResult.className = `banner ${entry.status === 200 ? 'ok' : 'bad'}`;
    ui.assetResult.textContent = entry.status === 200 ? `Scanner ${entry.json.scannerVersion}: ${entry.json.queued} asset(s) cleared by an older version were queued for another scan.` : `${entry.status}: ${entry.json?.detail ?? entry.error ?? 'Refused.'}`;
  }

  function build(container) {
    ui.message = el('div', { class: 'banner', hidden: true });
    ui.stats = el('div', { class: 'grid four' });
    ui.spark = el('div', { class: 'spark-box' });
    ui.none = el('p', { class: 'hint', text: 'Nothing has failed.' });
    ui.failedBody = el('tbody');
    ui.failedTable = el('div', { class: 'table-wrap', hidden: true }, el('table', {}, el('thead', {}, el('tr', {}, ['Batch', 'Received', 'Events', 'Attempts', 'Last error', ''].map((h) => el('th', { text: h })))), ui.failedBody));
    const auto = el('input', { type: 'checkbox', checked: true, 'aria-label': 'Refresh every 2 seconds' });
    auto.addEventListener('change', () => { st.auto = auto.checked; });
    ui.reset = el('button', { type: 'button', class: 'btn danger', text: 'Erase all data', onclick: resetAll });
    ui.resetResult = el('div', { class: 'banner', hidden: true });
    ui.assetResult = el('div', { class: 'banner', hidden: true });

    fill(container,
      el('div', { class: 'card' },
        el('div', { class: 'card-head' }, el('h2', { text: 'Ingestion queue' }), el('span', { class: 'row' }, el('label', { class: 'inline' }, auto, 'Refresh every 2 s'), el('button', { type: 'button', class: 'btn small', text: 'Refresh', onclick: () => refresh(false) }))),
        el('p', { class: 'hint', text: 'The CMS stores a batch, answers 202 and applies it a moment later. This is where it waits. A queue that keeps growing, or a batch that has failed, is what an operator watches for. Reading it needs the admin account.' }),
        ui.message, ui.stats,
        el('div', { class: 'row' }, el('span', { class: 'hint', text: 'Events waiting, last minute' }), ui.spark)),
      el('div', { class: 'card' },
        el('div', { class: 'card-head' }, el('h2', { text: 'Failed batches' }), el('span', { class: 'hint', text: 'Set aside after too many failed attempts. They do not hold up the queue; an admin requeues them once the cause is fixed.' })),
        ui.none, ui.failedTable),
      el('div', { class: 'grid two' },
        el('div', { class: 'card' }, el('h2', { text: 'Assets' }),
          el('p', { class: 'hint', text: 'Every clean asset records which scanner version cleared it. After a signature update, queue the ones cleared by an older version; they are not served again until cleared.' }),
          el('div', {}, el('button', { type: 'button', class: 'btn', text: 'Re-scan assets cleared by an older scanner', onclick: rescanOutdated })), ui.assetResult),
        el('div', { class: 'card' }, el('div', { class: 'card-head' }, el('h2', { text: 'Test data' }), el('span', { class: 'chip warn', text: 'for testing only' })),
          el('p', { class: 'hint', text: 'Erases every entity, tombstone, ledger row and queued batch so a test can start again. It always runs as the admin account, and only works while the CMS has Admin:AllowReset switched on (Development does). The audit log and stored assets are kept.' }),
          el('div', {}, ui.reset), ui.resetResult)));
  }

  Eng.register('ops', {
    title: 'Queue and admin',
    subtitle: 'Is the queue keeping up? Has anything failed? The tools an operator reaches for.',
    build,
    show() { watch(true); },
    hide() { watch(false); },
  });
})();
