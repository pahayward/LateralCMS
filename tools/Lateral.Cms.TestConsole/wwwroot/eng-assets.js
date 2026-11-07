'use strict';

// Assets: upload a file the way a real client would (one PUT when small, parallel multipart parts when large), watch the
// scanner decide, download in parallel byte ranges and check the result against the SHA-256 the service reports, and
// administer what is there. Nothing is served until it is clean.

(() => {
  const st = { current: null, listTimer: 0, filter: '' };
  const ui = {};

  const stateTone = { clean: 'ok', quarantined: 'info', rejected: 'bad', failed: 'warn' };
  const stateChip = (state) => el('span', { class: `chip ${stateTone[state] ?? ''}`, text: state });
  const log = (text, kind = '') => ui.log.prepend(el('div', { class: kind === 'bad' ? 'log-bad' : kind === 'ok' ? 'log-ok' : '', text: `${new Date().toLocaleTimeString()}  ${text}` }));
  const limits = () => Eng.config.limits;

  // ---------------------------------------------------------------- uploading

  async function upload(blob, key, type, partMiB, workers) {
    const started = performance.now();
    if (blob.size <= limits().maxSimpleBytes) {
      log(`PUT ${key} (${fmtBytes(blob.size)}) in one request`);
      const hash = await sha256Hex(await blob.arrayBuffer());
      const entry = await Eng.send({ method: 'PUT', path: '/cms/assets/{key}', params: { key }, query: {}, headers: { 'Content-Type': type || 'application/octet-stream', 'X-Content-SHA256': hash }, body: blob, as: 'cms', endpointId: 'asset-put' });
      log(`${entry.status} in ${entry.ms} ms`, entry.status === 202 ? 'ok' : 'bad');
      if (entry.status !== 202) { log(entry.json?.detail ?? 'Refused.', 'bad'); return null; }
      return entry.json;
    }

    const partSize = Math.min(limits().maxPartBytes, Math.max(limits().minPartBytes, partMiB * MiB));
    const parts = Math.ceil(blob.size / partSize);
    const init = await Eng.send({ method: 'POST', path: '/cms/assets/uploads', params: {}, query: {}, headers: {}, body: JSON.stringify({ key, contentType: type || 'application/octet-stream', size: blob.size }), as: 'cms', endpointId: 'asset-start' });
    if (init.status !== 201) { log(`Could not start the upload: ${init.status} ${init.json?.detail ?? ''}`, 'bad'); return null; }
    const uploadId = init.json.uploadId;
    log(`Multipart upload ${uploadId}: ${parts} parts of up to ${fmtBytes(partSize)}, ${workers} at a time`);

    const receipts = new Array(parts);
    let done = 0;
    let next = 0;
    const worker = async () => {
      while (next < parts) {
        const index = next++;
        const slice = blob.slice(index * partSize, Math.min(blob.size, (index + 1) * partSize));
        const hash = await sha256Hex(await slice.arrayBuffer());
        for (let attempt = 1; ; attempt++) {
          const entry = await Eng.send({ method: 'PUT', path: '/cms/assets/uploads/{uploadId}/parts/{partNumber}', params: { uploadId, partNumber: String(index + 1) }, query: {}, headers: { 'X-Content-SHA256': hash }, body: slice, as: 'cms', endpointId: 'asset-part' }, { quiet: true });
          if (entry.status === 200) { receipts[index] = { partNumber: index + 1, sha256: hash }; break; }
          if (attempt >= 3 || (entry.status < 500 && entry.status !== 429)) throw new Error(`part ${index + 1} was refused (${entry.status})`);
          log(`Part ${index + 1} got ${entry.status}; trying again`);
        }

        done++;
        ui.progress.textContent = `${done} of ${parts} parts uploaded`;
      }
    };

    await Promise.all(Array.from({ length: Math.min(workers, parts) }, worker));
    const complete = await Eng.send({ method: 'POST', path: '/cms/assets/uploads/{uploadId}/complete', params: { uploadId }, query: {}, headers: {}, body: JSON.stringify({ parts: receipts }), as: 'cms', endpointId: 'asset-complete' });
    log(`complete: ${complete.status} in ${Math.round(performance.now() - started)} ms`, complete.status === 202 ? 'ok' : 'bad');
    return complete.status === 202 ? complete.json : null;
  }

  async function follow(asset) {
    select(asset);
    for (let i = 0; i < 120; i++) {
      const entry = await Eng.call({ path: `cms/assets/${asset.id}`, as: 'cms', quiet: i > 0 });
      if (entry.json) select(entry.json, false);
      if (entry.json?.state && entry.json.state !== 'quarantined') { refreshList(); return entry.json; }
      await sleep(500);
    }

    return null;
  }

  async function send(file) {
    ui.progress.textContent = '';
    ui.link.hidden = true;
    try {
      const asset = await upload(file.blob, file.name, file.type, Number(ui.part.value), Number(ui.workers.value));
      if (asset) await follow(asset);
    } catch (error) {
      log(`Upload failed: ${error.message}`, 'bad');
    }
  }

  // ---------------------------------------------------------------- the selected asset

  function select(asset, announce = true) {
    st.current = asset;
    Eng.context.assetId = asset.id;
    const state = asset.state ?? '?';
    const text = ({
      clean: `Clean (${asset.detectedType}, scanner ${asset.scannerVersion}). It can be downloaded.`,
      quarantined: 'Quarantined: waiting for the scanner. It is not served.',
      rejected: `Rejected: ${asset.verdict} (${asset.signature}). The bytes were wiped; only this verdict remains.`,
      failed: `The scan could not finish (${asset.lastError}) and was set aside. The asset stays quarantined and is never served.`,
    })[state] ?? JSON.stringify(asset);
    fill(ui.asset,
      el('dl', { class: 'kv' },
        el('dt', { text: 'Id' }), el('dd', {}, el('button', { type: 'button', class: 'linkish', onclick: () => copyText(asset.id), text: asset.id })),
        el('dt', { text: 'Name' }), el('dd', { text: asset.key ?? '' }),
        el('dt', { text: 'State' }), el('dd', {}, stateChip(state)),
        el('dt', { text: 'Size' }), el('dd', { text: asset.size != null ? fmtBytes(asset.size) : '' }),
        el('dt', { text: 'SHA-256' }), el('dd', {}, el('code', { text: asset.sha256 ?? '' }))),
      el('div', { class: `banner ${state === 'clean' ? 'ok' : state === 'quarantined' ? 'info' : 'bad'}`, text }),
      el('details', {}, el('summary', { text: 'Raw status' }), Eng.jsonView(asset)));
    ui.actions.hidden = false;
    if (announce) ui.progress.textContent = '';
  }

  async function download(ranges, as) {
    const asset = st.current;
    if (!asset) { log('Choose or upload an asset first.', 'bad'); return; }
    const started = performance.now();
    const probe = await Eng.send({ method: 'GET', path: '/api/assets/{id}', params: { id: asset.id }, query: {}, headers: { Range: 'bytes=0-0' }, as, endpointId: 'asset-get' });
    if (probe.status === 404) { log(`${as} gets 404: the asset is not clean (or does not exist).`, 'bad'); return; }
    if (probe.status === 403) { log(`${as} gets 403: that account cannot read assets.`, 'bad'); return; }
    if (probe.status !== 206) { log(`Expected 206 to a range request, got ${probe.status}`, 'bad'); return; }
    const total = Number(probe.headers['content-range'].split('/')[1]);
    const etag = probe.headers.etag;
    const size = Math.ceil(total / ranges);
    const pieces = new Array(ranges);
    await Promise.all(Array.from({ length: ranges }, async (_, i) => {
      const from = i * size;
      const to = Math.min(total, from + size) - 1;
      if (from > to) { pieces[i] = new Blob(); return; }
      const entry = await Eng.send({ method: 'GET', path: '/api/assets/{id}', params: { id: asset.id }, query: {}, headers: { Range: `bytes=${from}-${to}`, 'If-Range': etag }, as, endpointId: 'asset-get' }, { quiet: true });
      if (entry.status !== 206) throw new Error(`range ${i} gave ${entry.status}`);
      pieces[i] = entry.blob;
    }));

    const blob = new Blob(pieces);
    const seconds = (performance.now() - started) / 1000;
    let verdict = 'not verified (too large to hash in the browser)';
    if (blob.size <= 1024 * MiB) {
      const actual = await sha256Hex(await blob.arrayBuffer());
      verdict = actual === asset.sha256 ? 'SHA-256 matches the one the service reported' : `SHA-256 MISMATCH (${actual})`;
    }

    log(`${ranges} parallel ranges as ${as}: ${fmtBytes(blob.size)} in ${seconds.toFixed(2)} s (${(blob.size / MiB / Math.max(seconds, 0.001)).toFixed(1)} MiB/s). ${verdict}`, verdict.startsWith('SHA') && !verdict.includes('MISMATCH') ? 'ok' : 'bad');
    ui.link.href = URL.createObjectURL(blob);
    ui.link.download = asset.key ?? 'asset';
    ui.link.hidden = false;
    ui.link.textContent = `Save ${asset.key} (${fmtBytes(blob.size)})`;
  }

  async function adminAct(method, path, label) {
    const entry = await Eng.call({ method, path, as: 'admin' });
    log(`${label}: ${entry.status}${entry.json?.detail ? ' ' + entry.json.detail : ''}`, entry.status < 300 ? 'ok' : 'bad');
    await refreshList();
    return entry;
  }

  // ---------------------------------------------------------------- the list

  async function refreshList() {
    clearTimeout(st.listTimer);
    const query = { limit: '50' };
    if (st.filter) query.state = st.filter;
    const entry = await Eng.call({ path: 'api/admin/assets', query, as: 'admin', quiet: true });
    const items = Array.isArray(entry.json) ? entry.json : entry.json?.items;
    if (!Array.isArray(items)) {
      fill(ui.body, el('tr', {}, el('td', { colspan: '6', text: `Could not list assets: ${entry.status || entry.error}` })));
      return;
    }

    ui.count.textContent = `${items.length}${items.length === 50 ? '+' : ''} shown`;
    fill(ui.body, items.map((asset) => el('tr', { class: `pick${st.current?.id === asset.id ? ' picked' : ''}`, onclick: () => { select(asset); refreshList(); } },
      el('td', {}, el('code', { text: asset.id.slice(0, 8) }), ' ', asset.key),
      el('td', {}, stateChip(asset.state)),
      el('td', { text: asset.verdict ? `${asset.verdict}${asset.signature ? ` (${asset.signature})` : ''}` : '-' }),
      el('td', { class: 'num', text: fmtBytes(asset.size) }),
      el('td', { class: 'hint', text: asset.scannerVersion ?? '-' }),
      el('td', {}, el('div', { class: 'row' },
        el('button', { type: 'button', class: 'btn small', text: 'Rescan', onclick: (e) => { e.stopPropagation(); adminAct('POST', `api/admin/assets/${asset.id}/rescan`, 'Rescan'); } }),
        el('button', { type: 'button', class: 'btn small danger', text: 'Delete', onclick: (e) => { e.stopPropagation(); adminAct('DELETE', `api/assets/${asset.id}`, 'Delete'); } }))))));
    if (items.some((a) => a.state === 'quarantined')) st.listTimer = setTimeout(refreshList, 1500);
  }

  // ---------------------------------------------------------------- quick test files

  const samples = {
    clean: () => ({ name: 'notes.txt', type: 'text/plain', blob: new Blob(['A perfectly ordinary text file.\n'.repeat(200)]) }),
    virus: () => ({ name: 'mock-malware.txt', type: 'text/plain', blob: new Blob(['harmless words ', 'LATERAL-MOCK-VIRUS-TEST-FILE', ' more words']) }),
    svg: () => ({ name: 'logo.svg', type: 'image/svg+xml', blob: new Blob(['<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>']) }),
    exe: () => ({ name: 'holiday.png', type: 'image/png', blob: new Blob([new Uint8Array([0x4d, 0x5a, 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0]), new Uint8Array(512)]) }),
    error: () => ({ name: 'flaky.txt', type: 'text/plain', blob: new Blob(['this file makes the mock scanner fail: MOCK-SCAN-ERROR']) }),
    big: () => {
      const bytes = new Uint8Array(24 * MiB);
      for (let i = 0; i < bytes.length; i += 65536) crypto.getRandomValues(bytes.subarray(i, Math.min(bytes.length, i + 65536)));
      bytes.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
      return { name: 'big-picture.png', type: 'image/png', blob: new Blob([bytes]) };
    },
  };

  function build(container) {
    const button = (text, onclick, title = '') => el('button', { type: 'button', class: 'btn', text, title, onclick });
    ui.progress = el('p', { class: 'hint' });
    ui.log = el('div', { class: 'mono-log' });
    ui.link = el('a', { hidden: true, class: 'btn' });
    ui.file = el('input', { type: 'file', 'aria-label': 'File to upload' });
    ui.part = el('input', { type: 'number', min: '5', max: '64', value: '8', class: 'narrow', 'aria-label': 'Part size in MiB' });
    ui.workers = el('input', { type: 'number', min: '1', max: '16', value: '4', class: 'narrow', 'aria-label': 'Parts at a time' });
    ui.asset = el('div', { class: 'col' }, el('p', { class: 'hint', text: 'Upload a file, or click one in the list below.' }));
    ui.actions = el('div', { class: 'col', hidden: true },
      el('h3', { text: 'Download' }),
      el('div', { class: 'row wrap' },
        button('As a user, 8 parallel ranges', () => download(8, 'user').catch((e) => log(e.message, 'bad'))),
        button('As an admin, 4 ranges', () => download(4, 'admin').catch((e) => log(e.message, 'bad'))),
        button('As the CMS account', () => download(1, 'cms').catch((e) => log(e.message, 'bad')), 'The CMS account cannot read assets: 403')),
      ui.link,
      el('div', { class: 'row wrap' }, button('Refresh the status', async () => { const e = await Eng.call({ path: `cms/assets/${st.current.id}`, as: 'cms' }); if (e.json) select(e.json); }),
        button('Open the status call in the workbench', () => Eng.openInWorkbench({ method: 'GET', path: '/cms/assets/{id:guid}', params: { id: st.current.id }, query: {}, headers: {}, as: 'cms', endpointId: 'asset-status' }))));
    ui.count = el('span', { class: 'hint' });
    ui.body = el('tbody');
    const filter = el('select', { 'aria-label': 'Show assets in this state' }, ['', 'quarantined', 'clean', 'rejected', 'failed'].map((s) => el('option', { value: s, text: s || 'Any state' })));
    filter.addEventListener('change', () => { st.filter = filter.value; refreshList(); });

    fill(container,
      el('div', { class: 'card' },
        el('p', {}, 'Files go to the upload API as the CMS account and land in ', el('b', { text: 'quarantine' }), '. A scanner decides: only a ', el('b', { text: 'clean' }),
          ' asset can be downloaded; a flagged one is wiped and only its verdict is kept. The scanner here is a mock that recognises obvious test files: it shows the pipeline, not real malware protection.')),
      el('div', { class: 'grid two' },
        el('div', { class: 'card' },
          el('h2', { text: 'Upload' }),
          ui.file,
          el('p', { class: 'hint', text: `Up to ${fmtBytes(limits().maxSimpleBytes)} it is one PUT; above that the file is cut into parts and sent in parallel.` }),
          el('div', { class: 'row wrap' }, el('label', { class: 'inline' }, 'Part size (MiB)', ui.part), el('label', { class: 'inline' }, 'Parts at a time', ui.workers)),
          el('div', {}, button('Upload the chosen file', () => { const f = ui.file.files[0]; if (f) send({ name: f.name, type: f.type, blob: f }); else log('Choose a file first.', 'bad'); })),
          el('h3', { text: 'Or a ready-made file' }),
          el('div', { class: 'row wrap' },
            button('Clean text', () => send(samples.clean()), 'Cleared and downloadable'),
            button('Mock malware', () => send(samples.virus()), 'Contains the mock scanner\'s test signature: rejected and wiped'),
            button('SVG with a script', () => send(samples.svg()), 'Active content: rejected'),
            button('Executable named .png', () => send(samples.exe()), 'A disguised executable: rejected'),
            button('Scanner error', () => send(samples.error()), 'Needs Assets:Mock:EnableFailureMarkers on the CMS; the asset stays quarantined and retries'),
            button('24 MiB picture', () => send(samples.big()), 'Uploaded in parallel parts')),
          ui.progress),
        el('div', { class: 'card' }, el('h2', { text: 'The asset' }), ui.asset, ui.actions)),
      el('div', { class: 'card' },
        el('div', { class: 'card-head' }, el('h2', { text: 'Assets in the store' }),
          el('span', { class: 'row' }, filter, ui.count, button('Refresh', refreshList))),
        el('div', { class: 'table-wrap' }, el('table', {}, el('thead', {}, el('tr', {}, ['Asset', 'State', 'Verdict', 'Size', 'Scanner', ''].map((h) => el('th', { text: h })))), ui.body))),
      el('div', { class: 'card flat' }, el('h3', { text: 'What happened' }), ui.log));
  }

  Eng.register('assets', {
    title: 'Assets',
    subtitle: 'Upload, scan, download. Nothing is served until the scanner says clean; many ranges of a clean file can be fetched at once.',
    build,
    async show(params) {
      refreshList();
      if (params) {
        const entry = await Eng.call({ path: `cms/assets/${params}`, as: 'cms' });
        if (entry.json) select(entry.json);
      }
    },
    hide() { clearTimeout(st.listTimer); },
  });
})();
