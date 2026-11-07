// The Assets tab: upload files the way a real client would (one PUT for small files, parallel multipart parts for large ones),
// watch the scanner decide, then download in parallel byte ranges and check the result against the SHA-256 the service reports.
// It relies on helpers from app.js ($, el, fill). Credentials never reach this page: it only names an account.
(() => {
  const root = $('#assets-root');
  const MiB = 1024 * 1024;
  let limits = { maxSimpleBytes: 8 * MiB, minPartBytes: 5 * MiB, maxPartBytes: 64 * MiB };
  let current = null; // { id, sha256, size, key }

  const fmt = (bytes) => bytes >= MiB ? `${(bytes / MiB).toFixed(1)} MiB` : bytes >= 1024 ? `${(bytes / 1024).toFixed(1)} KiB` : `${bytes} B`;

  async function api(path, as, init = {}) {
    return fetch('/api/cms/' + path, { ...init, headers: { 'X-Console-Request': '1', 'X-Console-Act-As': as, ...(init.headers ?? {}) } });
  }

  async function json(response) {
    const text = await response.text();
    try { return JSON.parse(text); } catch { return { raw: text }; }
  }

  async function sha256(buffer) {
    const digest = await crypto.subtle.digest('SHA-256', buffer);
    return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
  }

  function log(message, kind = '') {
    const line = el('div', { class: kind === 'bad' ? 'bad-text' : kind === 'ok' ? 'ok-text' : '', text: `${new Date().toLocaleTimeString()}  ${message}` });
    $('#as-log').prepend(line);
  }

  // ---- upload ----------------------------------------------------------------------------------------

  async function runPool(count, workers, task) {
    let next = 0;
    await Promise.all(Array.from({ length: Math.min(workers, count) }, async () => {
      while (next < count) await task(next++);
    }));
  }

  async function upload(blob, key, type, partMiB, workers) {
    const started = performance.now();
    if (blob.size <= limits.maxSimpleBytes) {
      log(`PUT ${key} (${fmt(blob.size)}) in one request`);
      const hash = await sha256(await blob.arrayBuffer());
      const response = await api(`cms/assets/${encodeURIComponent(key)}`, 'cms', {
        method: 'PUT', body: blob, headers: { 'Content-Type': type || 'application/octet-stream', 'X-Content-SHA256': hash },
      });
      const body = await json(response);
      log(`${response.status} ${response.statusText} in ${Math.round(performance.now() - started)} ms`, response.status === 202 ? 'ok' : 'bad');
      return response.status === 202 ? body : null;
    }

    const partSize = Math.min(limits.maxPartBytes, Math.max(limits.minPartBytes, partMiB * MiB));
    const parts = Math.ceil(blob.size / partSize);
    const init = await api('cms/assets/uploads', 'cms', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key, contentType: type || 'application/octet-stream', size: blob.size }),
    });
    const begun = await json(init);
    if (init.status !== 201) {
      log(`Could not start the upload: ${init.status} ${begun.detail ?? ''}`, 'bad');
      return null;
    }

    log(`Multipart upload ${begun.uploadId}: ${parts} parts of up to ${fmt(partSize)}, ${workers} at a time`);
    const receipts = new Array(parts);
    let done = 0;
    await runPool(parts, workers, async (index) => {
      const slice = blob.slice(index * partSize, Math.min(blob.size, (index + 1) * partSize));
      const hash = await sha256(await slice.arrayBuffer());
      for (let attempt = 1; ; attempt++) {
        const response = await api(`cms/assets/uploads/${begun.uploadId}/parts/${index + 1}`, 'cms', {
          method: 'PUT', body: slice, headers: { 'X-Content-SHA256': hash },
        });
        if (response.status === 200) { receipts[index] = { partNumber: index + 1, sha256: hash }; break; }
        if (attempt >= 3 || response.status < 500 && response.status !== 429) {
          log(`Part ${index + 1} failed: ${response.status}`, 'bad');
          throw new Error(`part ${index + 1} failed`);
        }
        log(`Part ${index + 1} got ${response.status}; trying again`);
      }

      done++;
      $('#as-progress').textContent = `${done} of ${parts} parts uploaded`;
    });

    const response = await api(`cms/assets/uploads/${begun.uploadId}/complete`, 'cms', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ parts: receipts }),
    });
    const body = await json(response);
    log(`complete: ${response.status} in ${Math.round(performance.now() - started)} ms`, response.status === 202 ? 'ok' : 'bad');
    return response.status === 202 ? body : null;
  }

  async function follow(asset) {
    current = { id: asset.id, sha256: asset.sha256, size: asset.size, key: asset.key };
    $('#as-current').textContent = asset.id;
    for (let i = 0; i < 120; i++) {
      const response = await api(`cms/assets/${asset.id}`, 'cms');
      const body = await json(response);
      showStatus(body);
      if (body.state && body.state !== 'quarantined') return body;
      await new Promise((resolve) => setTimeout(resolve, 500));
    }

    return null;
  }

  function showStatus(body) {
    const state = body.state ?? '?';
    const box = $('#as-status');
    box.className = 'banner ' + (state === 'clean' ? 'ok' : state === 'quarantined' ? '' : 'bad');
    box.textContent = state === 'clean'
      ? `Clean (${body.detectedType}, scanner ${body.scannerVersion}). It can be downloaded.`
      : state === 'quarantined' ? 'Quarantined: waiting for the scanner. It is not served.'
        : state === 'rejected' ? `Rejected: ${body.verdict} (${body.signature}). The bytes were wiped; only this verdict remains.`
          : state === 'failed' ? `The scan could not finish (${body.lastError}) and was set aside. The asset stays quarantined and is never served.`
            : JSON.stringify(body);
    $('#as-raw').textContent = JSON.stringify(body, null, 2);
  }

  // ---- download --------------------------------------------------------------------------------------

  async function download(ranges, as) {
    if (!current) return log('Upload something first.', 'bad');
    const started = performance.now();
    const probe = await api(`api/assets/${current.id}`, as, { headers: { Range: 'bytes=0-0' } });
    if (probe.status === 404) return log(`${as} gets 404: the asset is not clean (or does not exist).`, 'bad');
    if (probe.status !== 206) return log(`Expected 206 to a range request, got ${probe.status}`, 'bad');
    const total = Number(probe.headers.get('Content-Range').split('/')[1]);
    const etag = probe.headers.get('ETag');
    await probe.arrayBuffer();

    const size = Math.ceil(total / ranges);
    const pieces = new Array(ranges);
    await Promise.all(Array.from({ length: ranges }, async (_, i) => {
      const from = i * size;
      const to = Math.min(total, from + size) - 1;
      if (from > to) { pieces[i] = new Blob(); return; }
      const response = await api(`api/assets/${current.id}`, as, { headers: { Range: `bytes=${from}-${to}`, 'If-Range': etag } });
      if (response.status !== 206) throw new Error(`range ${i} gave ${response.status}`);
      pieces[i] = await response.blob();
    }));

    const blob = new Blob(pieces);
    const seconds = (performance.now() - started) / 1000;
    let verdict = 'not verified (file too large to hash in the browser)';
    if (blob.size <= 1024 * MiB) {
      const actual = await sha256(await blob.arrayBuffer());
      verdict = actual === current.sha256 ? 'SHA-256 matches the one the service reported' : `SHA-256 MISMATCH (${actual})`;
    }

    log(`${ranges} parallel ranges as ${as}: ${fmt(blob.size)} in ${seconds.toFixed(2)} s (${(blob.size / MiB / Math.max(seconds, 0.001)).toFixed(1)} MiB/s). ${verdict}`, verdict.startsWith('SHA') && !verdict.includes('MISMATCH') ? 'ok' : 'bad');
    const link = $('#as-link');
    link.href = URL.createObjectURL(blob);
    link.download = current.key;
    link.hidden = false;
    link.textContent = `Save ${current.key} (${fmt(blob.size)})`;
  }

  // ---- admin -----------------------------------------------------------------------------------------

  async function refreshList() {
    const response = await api('api/admin/assets?limit=50', 'admin');
    const body = await json(response);
    const table = $('#as-table tbody');
    if (!Array.isArray(body)) { table.replaceChildren(el('tr', {}, el('td', { colspan: 5, text: `Could not list assets: ${response.status}` }))); return; }
    table.replaceChildren(...body.map((asset) => el('tr', {},
      el('td', {}, el('code', { text: asset.id.slice(0, 8) }), ' ', asset.key),
      el('td', { text: asset.state }),
      el('td', { text: asset.verdict ? `${asset.verdict}${asset.signature ? ' (' + asset.signature + ')' : ''}` : '-' }),
      el('td', { class: 'num', text: fmt(asset.size) }),
      el('td', {},
        el('button', { type: 'button', class: 'btn small', text: 'Use', onclick: () => { current = { id: asset.id, sha256: asset.sha256, size: asset.size, key: asset.key }; $('#as-current').textContent = asset.id; showStatus(asset); } }), ' ',
        el('button', { type: 'button', class: 'btn small', text: 'Rescan', onclick: async () => { await api(`api/admin/assets/${asset.id}/rescan`, 'admin', { method: 'POST' }); refreshList(); } }), ' ',
        el('button', { type: 'button', class: 'btn small', text: 'Delete', onclick: async () => { await api(`api/assets/${asset.id}`, 'admin', { method: 'DELETE' }); refreshList(); } })))));
  }

  // ---- quick test files ----------------------------------------------------------------------------------

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

  async function send(file) {
    $('#as-progress').textContent = '';
    $('#as-link').hidden = true;
    try {
      const asset = await upload(file.blob, file.name, file.type, Number($('#as-part').value), Number($('#as-workers').value));
      if (asset) await follow(asset);
    } catch (error) {
      log(`Upload failed: ${error.message}`, 'bad');
    }
  }

  // ---- layout ------------------------------------------------------------------------------------------

  function build() {
    const button = (text, onclick, title = '') => el('button', { type: 'button', class: 'btn', text, title, onclick });
    fill(root,
      el('div', { class: 'card' },
        el('h2', { text: 'Assets: upload, scan, download' }),
        el('p', {}, 'Files go to the CMS account\'s upload API and land in ', el('strong', { text: 'quarantine' }),
          '. A scanner decides: only a ', el('strong', { text: 'clean' }), ' asset can be downloaded; a flagged one is wiped and only its verdict is kept. ',
          'The scanner here is a mock that recognises obvious test files; it shows the pipeline, not real malware protection.')),
      el('div', { class: 'grid two' },
        el('div', { class: 'card' },
          el('h3', { text: 'Upload' }),
          el('input', { type: 'file', id: 'as-file' }),
          el('p', { class: 'hint' }, 'Up to the simple limit it is one PUT; above it the file is cut into parts and sent in parallel.'),
          el('label', {}, 'Part size (MiB) ', el('input', { id: 'as-part', type: 'number', min: 5, max: 64, value: 8, style: 'width:5em' })),
          ' ',
          el('label', {}, 'Parts at a time ', el('input', { id: 'as-workers', type: 'number', min: 1, max: 16, value: 4, style: 'width:4em' })),
          el('p', {}, button('Upload the chosen file', () => { const f = $('#as-file').files[0]; if (f) send({ name: f.name, type: f.type, blob: f }); else log('Choose a file first.', 'bad'); })),
          el('h3', { text: 'Or try a ready-made file' }),
          el('p', {},
            button('Clean text', () => send(samples.clean()), 'Cleared and downloadable'),
            button('Mock malware', () => send(samples.virus()), 'Contains the mock scanner\'s test signature: rejected and wiped'),
            button('SVG with a script', () => send(samples.svg()), 'Active content: rejected'),
            button('Executable named .png', () => send(samples.exe()), 'A disguised executable: rejected'),
            button('Scanner error', () => send(samples.error()), 'Needs Assets:Mock:EnableFailureMarkers on the CMS; the asset stays quarantined and retries'),
            button('24 MiB picture', () => send(samples.big()), 'Uploaded in parallel parts')),
          el('p', { id: 'as-progress', class: 'hint' })),
        el('div', { class: 'card' },
          el('h3', { text: 'The asset' }),
          el('p', {}, 'Id: ', el('code', { id: 'as-current', text: 'none yet' })),
          el('div', { id: 'as-status', class: 'banner' }, 'Nothing uploaded yet.'),
          el('h3', { text: 'Download' }),
          el('p', {},
            button('Download as a user, 8 parallel ranges', () => download(8, 'user').catch((e) => log(e.message, 'bad'))),
            button('as an admin, 4 ranges', () => download(4, 'admin').catch((e) => log(e.message, 'bad'))),
            button('as the CMS account', () => download(1, 'cms').catch((e) => log(e.message, 'bad')), 'The CMS account cannot read assets: 403')),
          el('p', {}, el('a', { id: 'as-link', hidden: true, text: '' })),
          el('details', {}, el('summary', { text: 'Raw status' }), el('pre', { id: 'as-raw' })))),
      el('div', { class: 'card' },
        el('h3', { text: 'Admin: assets and their verdicts' }),
        el('p', {}, button('Refresh the list', refreshList)),
        el('table', { id: 'as-table' },
          el('thead', {}, el('tr', {}, el('th', { text: 'Asset' }), el('th', { text: 'State' }), el('th', { text: 'Verdict' }), el('th', { class: 'num', text: 'Size' }), el('th', { text: '' }))),
          el('tbody'))),
      el('div', { class: 'card' }, el('h3', { text: 'Log' }), el('div', { id: 'as-log', class: 'mono' })));
  }

  build();
  fetch('/api/config').then((r) => r.json()).then((config) => {
    if (config.limits?.maxSimpleBytes) limits = { maxSimpleBytes: config.limits.maxSimpleBytes, minPartBytes: config.limits.minPartBytes, maxPartBytes: config.limits.maxPartBytes };
  }).catch(() => { /* the defaults are fine */ });
})();
