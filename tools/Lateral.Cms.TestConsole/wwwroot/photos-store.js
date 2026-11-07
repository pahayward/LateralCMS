'use strict';

// The photo library's data layer. The CMS is the only place anything is kept:
//   * a photo is an entity, `photo-<reversed time taken>-<random>`. The CMS lists entities in id order, so with the time
//     reversed in the id the CMS's own paging returns the library newest first, with no sorting on this side;
//   * an album is an entity, `album-<random>`;
//   * the pictures are assets: a small thumbnail, a preview and the untouched original, each scanned before it can be served;
//   * every change is a new version of the photo's entity; trashing is a flag, deleting forever is a CMS delete.
// The page never holds credentials. It names an account for each call: "cms" writes, "user" reads, "admin" removes files for good.

const Cms = (() => {
  const log = [];
  const watchers = new Set();

  function record(entry) {
    log.unshift(entry);
    if (log.length > 120) log.pop();
    watchers.forEach((fn) => fn(entry));
  }

  async function call(method, path, { as, body, headers = {}, label = '' } = {}) {
    const started = performance.now();
    const init = { method, headers: { 'X-Console-Request': '1', 'X-Console-Act-As': as, ...headers } };
    if (body !== undefined) {
      init.body = JSON.stringify(body);
      init.headers['Content-Type'] = 'application/json';
    }

    try {
      const response = await fetch('/api/cms/' + path, init);
      record({ at: new Date(), method, path, as, label, status: response.status, ms: Math.round(performance.now() - started) });
      return response;
    } catch (error) {
      record({ at: new Date(), method, path, as, label, status: 0, ms: Math.round(performance.now() - started) });
      throw error;
    }
  }

  async function json(response) {
    const text = await response.text();
    try { return JSON.parse(text); } catch { return { raw: text }; }
  }

  /** A PUT with upload progress, which fetch cannot give. */
  function put(path, blob, headers, as, label, onBytes) {
    return new Promise((resolve, reject) => {
      const started = performance.now();
      const request = new XMLHttpRequest();
      request.open('PUT', '/api/cms/' + path);
      request.setRequestHeader('X-Console-Request', '1');
      request.setRequestHeader('X-Console-Act-As', as);
      for (const [name, value] of Object.entries(headers)) request.setRequestHeader(name, value);
      request.upload.onprogress = (event) => onBytes?.(event.loaded);
      request.onload = () => {
        record({ at: new Date(), method: 'PUT', path, as, label, status: request.status, ms: Math.round(performance.now() - started) });
        let body = {};
        try { body = JSON.parse(request.responseText); } catch { /* not JSON */ }
        resolve({ status: request.status, body });
      };
      request.onerror = () => {
        record({ at: new Date(), method: 'PUT', path, as, label, status: 0, ms: Math.round(performance.now() - started) });
        reject(new Error('The upload could not reach the CMS.'));
      };
      request.send(blob);
    });
  }

  return { call, json, put, log, watch: (fn) => watchers.add(fn) };
})();

const problemText = (body, fallback) => body?.detail ?? body?.title ?? body?.error ?? fallback;

// ---------------------------------------------------------------- the library

const Library = (() => {
  const photos = new Map();
  const albums = new Map();
  const watchers = new Set();
  const state = { loading: false, loaded: false, error: null, pending: 0, lastLoad: 0 };
  const NEWEST_FIRST = 9999999999999;

  let notifyTimer = 0;
  function notify() {
    if (notifyTimer) return;
    notifyTimer = setTimeout(() => { notifyTimer = 0; watchers.forEach((fn) => fn()); }, 60);
  }

  function toPhoto(entity) {
    const p = entity.payload;
    if (!p || p.kind !== 'photo' || !p.thumb?.assetId || !p.original?.assetId) return null;
    const taken = new Date(p.takenAt);
    return {
      id: entity.id,
      version: entity.version,
      checksum: entity.checksum,
      updatedAt: entity.updatedAt,
      payload: p,
      name: p.name || 'Photo',
      caption: p.caption || '',
      takenAt: Number.isNaN(taken.getTime()) ? new Date(entity.updatedAt) : taken,
      width: p.width || 4,
      height: p.height || 3,
      contentType: p.contentType || 'image/jpeg',
      original: p.original,
      preview: p.preview ?? p.thumb,
      thumb: p.thumb,
      sha256: p.original.sha256,
      bytes: p.original.bytes ?? 0,
      camera: p.camera ?? null,
      favourite: Boolean(p.favourite),
      albums: Array.isArray(p.albums) ? p.albums : [],
      trashedAt: p.trashedAt ? new Date(p.trashedAt) : null,
    };
  }

  function toAlbum(entity) {
    const p = entity.payload;
    if (!p || p.kind !== 'album' || !p.name) return null;
    return { id: entity.id, version: entity.version, payload: p, name: String(p.name), createdAt: new Date(p.createdAt ?? entity.updatedAt) };
  }

  function setEntity(entity, into = { photos, albums }) {
    const photo = toPhoto(entity);
    if (photo) into.photos.set(photo.id, photo);
    const album = photo ? null : toAlbum(entity);
    if (album) into.albums.set(album.id, album);
  }

  /** Reads the whole library from the CMS, a page at a time. The first time, photos appear as their page arrives. */
  async function load() {
    if (state.loading) return;
    state.loading = true;
    state.error = null;
    const fresh = state.loaded ? { photos: new Map(), albums: new Map() } : { photos, albums };
    try {
      let cursor = null;
      do {
        const response = await Cms.call('GET', `api/entities?limit=100${cursor ? '&cursor=' + encodeURIComponent(cursor) : ''}`, { as: 'user', label: 'List the library' });
        if (!response.ok) throw new Error(problemText(await Cms.json(response), `The CMS answered ${response.status}.`));
        const page = await response.json();
        for (const entity of page.items) setEntity(entity, fresh);
        cursor = page.nextCursor;
        if (!state.loaded) notify();
      } while (cursor);

      if (fresh.photos !== photos) {
        photos.clear();
        albums.clear();
        fresh.photos.forEach((value, key) => photos.set(key, value));
        fresh.albums.forEach((value, key) => albums.set(key, value));
      }

      state.loaded = true;
      state.lastLoad = Date.now();
    } catch (error) {
      state.error = error.message === 'Failed to fetch' ? 'The library cannot reach the CMS. Is it running?' : error.message;
    } finally {
      state.loading = false;
      notify();
    }
  }

  /** Sends events and waits for the CMS to apply them (it answers 202 and works through them in the background). */
  async function send(events, label) {
    const response = await Cms.call('POST', 'cms/events', { as: 'cms', body: events, label });
    const ack = await Cms.json(response);
    if (response.status !== 202 && response.status !== 200) throw new Error(problemText(ack, `The CMS refused the change (${response.status}).`));

    let batch = ack;
    for (let i = 0; batch.status === 'queued' && i < 200; i++) {
      await wait(i < 6 ? 100 : 250);
      batch = await Cms.json(await Cms.call('GET', `cms/batches/${ack.batchId}`, { as: 'cms', label: 'Follow the batch' }));
    }

    if (batch.status === 'queued') throw new Error('The CMS is still working on this change.');
    const rejected = (batch.results ?? []).find((r) => r.outcome === 'rejected');
    if (rejected || batch.status === 'failed') throw new Error(`The CMS did not accept the change${rejected?.reason ? ` (${rejected.reason})` : ''}.`);
    if ((batch.results ?? []).some((r) => r.outcome === 'ignored')) throw new Error('Someone else changed this photo first. The library will refresh.');
    return batch;
  }

  /**
   * Changes photos by publishing their next version. The page shows the change at once and takes it back, with the reason,
   * if the CMS does not accept it.
   */
  async function update(ids, mutate, label) {
    const targets = ids.map((id) => photos.get(id)).filter(Boolean);
    if (targets.length === 0) return;
    const before = targets.map((p) => ({ id: p.id, version: p.version, checksum: p.checksum, updatedAt: p.updatedAt, payload: p.payload }));
    const events = [];
    const now = new Date().toISOString();
    for (const photo of targets) {
      const payload = mutate(structuredClone(photo.payload), photo);
      const version = photo.version + 1;
      events.push({ type: 'publish', id: photo.id, version, payload, timestamp: now });
      setEntity({ id: photo.id, version, checksum: photo.checksum, updatedAt: now, payload });
    }

    state.pending++;
    notify();
    try {
      for (let i = 0; i < events.length; i += 200) await send(events.slice(i, i + 200), label);
    } catch (error) {
      before.forEach((entity) => setEntity(entity));
      notify();
      throw error;
    } finally {
      state.pending--;
    }
  }

  const trash = (ids) => update(ids, (p) => { p.trashedAt = new Date().toISOString(); return p; }, 'Move to trash');
  const restore = (ids) => update(ids, (p) => { p.trashedAt = null; return p; }, 'Restore from trash');
  const setFavourite = (ids, value) => update(ids, (p) => { p.favourite = value; return p; }, value ? 'Favourite' : 'Unfavourite');
  const setCaption = (id, text) => update([id], (p) => { p.caption = text.trim().slice(0, 500); return p; }, 'Edit description');
  const addToAlbum = (ids, albumId) => update(ids, (p) => { p.albums = [...new Set([...(p.albums ?? []), albumId])]; return p; }, 'Add to album');
  const removeFromAlbum = (ids, albumId) => update(ids, (p) => { p.albums = (p.albums ?? []).filter((a) => a !== albumId); return p; }, 'Remove from album');

  async function createAlbum(name) {
    const id = `album-${rand(8)}`;
    const payload = { kind: 'album', schema: 1, name: name.trim().slice(0, 100), createdAt: new Date().toISOString() };
    await send([{ type: 'publish', id, version: 1, payload, timestamp: new Date().toISOString() }], 'Create album');
    setEntity({ id, version: 1, updatedAt: payload.createdAt, payload });
    notify();
    return albums.get(id);
  }

  async function renameAlbum(id, name) {
    const album = albums.get(id);
    if (!album) return;
    const payload = { ...album.payload, name: name.trim().slice(0, 100) };
    const before = { id, version: album.version, updatedAt: album.createdAt.toISOString(), payload: album.payload };
    setEntity({ id, version: album.version + 1, updatedAt: new Date().toISOString(), payload });
    notify();
    try {
      await send([{ type: 'publish', id, version: album.version + 1, payload, timestamp: new Date().toISOString() }], 'Rename album');
    } catch (error) {
      setEntity(before);
      notify();
      throw error;
    }
  }

  async function deleteAlbum(id) {
    await send([{ type: 'delete', id, timestamp: new Date().toISOString() }], 'Delete album');
    albums.delete(id);
    notify();
  }

  /** Gone for good: the CMS deletes the photo (and will not take an older version back), then each file is removed. */
  async function purge(ids) {
    const targets = ids.map((id) => photos.get(id)).filter(Boolean);
    for (let i = 0; i < targets.length; i += 200) {
      await send(targets.slice(i, i + 200).map((p) => ({ type: 'delete', id: p.id, timestamp: new Date().toISOString() })), 'Delete forever');
    }

    targets.forEach((p) => photos.delete(p.id));
    notify();

    const files = targets.flatMap((p) => [p.original.assetId, p.preview.assetId, p.thumb.assetId]);
    const queue = [...new Set(files)];
    await Promise.all(Array.from({ length: 4 }, async () => {
      while (queue.length > 0) await removeAsset(queue.pop());
    }));
  }

  async function removeAsset(id) {
    try { await Cms.call('DELETE', `api/assets/${id}`, { as: 'admin', label: 'Remove a file' }); } catch { /* already gone */ }
  }

  async function freshEntity(id) {
    const response = await Cms.call('GET', `api/entities/${encodeURIComponent(id)}`, { as: 'user', label: 'Read the photo' });
    return response.ok ? response.json() : null;
  }

  async function assetStatus(id) {
    const response = await Cms.call('GET', `cms/assets/${id}`, { as: 'cms', label: 'Ask about a file' });
    return response.ok ? Cms.json(response) : null;
  }

  function stats() {
    let bytes = 0;
    let count = 0;
    for (const p of photos.values()) {
      count++;
      bytes += (p.original.bytes ?? 0) + (p.preview.bytes ?? 0) + (p.thumb.bytes ?? 0);
    }

    return { photos: count, albums: albums.size, bytes, files: count * 3 };
  }

  const photoIdFor = (takenAt) => `photo-${String(Math.max(0, NEWEST_FIRST - takenAt.getTime())).padStart(13, '0')}-${rand(6)}`;

  return {
    photos, albums, state, watch: (fn) => watchers.add(fn), notify, load, send, update, trash, restore, setFavourite, setCaption,
    addToAlbum, removeFromAlbum, createAlbum, renameAlbum, deleteAlbum, purge, removeAsset, freshEntity, assetStatus, stats,
    photoIdFor, setEntity,
  };
})();

// ---------------------------------------------------------------- putting files in

const Limits = { maxSimple: 8 * MiB, minPart: 5 * MiB, maxPart: 64 * MiB, partWorkers: 4 };

async function loadLimits() {
  try {
    const config = await (await fetch('/api/config', { headers: { 'X-Console-Request': '1' } })).json();
    Limits.maxSimple = config.limits?.maxSimpleBytes ?? Limits.maxSimple;
    Limits.minPart = config.limits?.minPartBytes ?? Limits.minPart;
    Limits.maxPart = config.limits?.maxPartBytes ?? Limits.maxPart;
  } catch { /* the defaults match the CMS's */ }
}

/** One asset: a single PUT when small, otherwise parallel parts. Returns the CMS's description of it. */
async function uploadAsset(blob, key, type, label, onBytes) {
  const hash = await sha256Hex(await blob.arrayBuffer());
  if (blob.size <= Limits.maxSimple) {
    const { status, body } = await Cms.put(`cms/assets/${encodeURIComponent(key)}`, blob, { 'Content-Type': type, 'X-Content-SHA256': hash }, 'cms', label, onBytes);
    if (status !== 202) throw new Error(problemText(body, `The CMS refused the file (${status}).`));
    return body;
  }

  const partSize = Math.min(Limits.maxPart, Math.max(Limits.minPart, 8 * MiB));
  const count = Math.ceil(blob.size / partSize);
  const init = await Cms.call('POST', 'cms/assets/uploads', { as: 'cms', label: `${label}: start`, body: { key, contentType: type, size: blob.size } });
  const begun = await Cms.json(init);
  if (init.status !== 201) throw new Error(problemText(begun, `The CMS would not start the upload (${init.status}).`));

  const receipts = new Array(count);
  const sent = new Array(count).fill(0);
  let next = 0;
  const worker = async () => {
    while (next < count) {
      const index = next++;
      const slice = blob.slice(index * partSize, Math.min(blob.size, (index + 1) * partSize));
      const partHash = await sha256Hex(await slice.arrayBuffer());
      for (let attempt = 1; ; attempt++) {
        const { status } = await Cms.put(`cms/assets/uploads/${begun.uploadId}/parts/${index + 1}`, slice, { 'X-Content-SHA256': partHash }, 'cms',
          `${label}: part ${index + 1} of ${count}`, (loaded) => { sent[index] = loaded; onBytes?.(sent.reduce((a, b) => a + b, 0)); });
        if (status === 200) { sent[index] = slice.size; receipts[index] = { partNumber: index + 1, sha256: partHash }; break; }
        if (attempt >= 3 || (status < 500 && status !== 429)) throw new Error(`Part ${index + 1} was refused (${status}).`);
        await wait(300 * attempt);
      }
    }
  };

  try {
    await Promise.all(Array.from({ length: Math.min(Limits.partWorkers, count) }, worker));
  } catch (error) {
    await Cms.call('DELETE', `cms/assets/uploads/${begun.uploadId}`, { as: 'cms', label: `${label}: abandon` });
    throw error;
  }

  const done = await Cms.call('POST', `cms/assets/uploads/${begun.uploadId}/complete`, { as: 'cms', label: `${label}: complete`, body: { parts: receipts } });
  const body = await Cms.json(done);
  if (done.status !== 202) throw new Error(problemText(body, `The CMS could not put the parts together (${done.status}).`));
  return body;
}

/** Waits until every file has been through the scanner. Returns a map of id to the CMS's final description of it. */
async function waitForScan(ids, onTick) {
  const done = new Map();
  const deadline = Date.now() + 120000;
  while (Date.now() < deadline && done.size < ids.length) {
    const open = ids.filter((id) => !done.has(id));
    const answers = await Promise.all(open.map(async (id) => [id, await Library.assetStatus(id)]));
    for (const [id, body] of answers) if (body?.state && body.state !== 'quarantined') done.set(id, body);
    onTick?.(done.size, ids.length);
    if (done.size < ids.length) await wait(500);
  }

  return done;
}

const Uploads = (() => {
  const items = [];
  const watchers = new Set();
  const notify = () => watchers.forEach((fn) => fn());
  let running = 0;
  let serial = 0;

  function make(source, name) {
    const item = { uid: ++serial, name, source, status: 'queued', note: 'Waiting', progress: 0, preview: null, photoId: null, kind: 'upload' };
    items.push(item);
    return item;
  }

  function set(item, patch) {
    Object.assign(item, patch);
    notify();
  }

  function add(files) {
    for (const file of files) make(file, file.name);
    notify();
    pump();
  }

  function addSamples(count) {
    for (const plan of samplePlan(count)) make(() => makeSampleFile(plan), plan.name);
    notify();
    pump();
  }

  /** A real picture with the scanner's test signature added, to watch the scanner refuse it. Harmless: it is only text. */
  function addScannerTest() {
    make(async () => {
      const canvas = document.createElement('canvas');
      canvas.width = 480;
      canvas.height = 320;
      const context = canvas.getContext('2d');
      context.fillStyle = '#b3261e';
      context.fillRect(0, 0, 480, 320);
      context.fillStyle = '#fff';
      context.font = 'bold 34px sans-serif';
      context.fillText('Scanner test', 130, 175);
      const png = new Uint8Array(await (await new Promise((resolve) => canvas.toBlob(resolve, 'image/png'))).arrayBuffer());
      const marker = new TextEncoder().encode('\nLATERAL-MOCK-VIRUS-TEST-FILE\n');
      return new File([png, marker], 'scanner-test.png', { type: 'image/png', lastModified: Date.now() });
    }, 'scanner-test.png');
    notify();
    pump();
  }

  function pump() {
    while (running < 2) {
      const next = items.find((i) => i.status === 'queued' && i.kind === 'upload');
      if (!next) return;
      running++;
      next.status = 'preparing';
      process(next).catch((error) => set(next, { status: 'error', note: error.message ?? 'Something went wrong.' })).finally(() => { running--; pump(); });
    }
  }

  async function process(item) {
    set(item, { status: 'preparing', note: 'Reading the picture' });
    const file = typeof item.source === 'function' ? await item.source() : item.source;
    const head = new Uint8Array(await file.slice(0, 16).arrayBuffer());
    const type = sniffImageType(head);
    if (!type) return set(item, { status: 'error', note: 'Not a picture the library keeps (JPEG, PNG, GIF or WebP).' });

    const buffer = await file.arrayBuffer();
    const sha256 = await sha256Hex(buffer);
    const twin = [...Library.photos.values()].find((p) => p.sha256 === sha256);
    if (twin) return set(item, { status: 'duplicate', note: twin.trashedAt ? 'Already in your trash' : 'Already in your library', photoId: twin.id });

    const exif = type === 'image/jpeg' ? readExif(buffer.slice(0, 256 * 1024)) : {};
    const takenAt = exif.takenAt ?? (file.lastModified ? new Date(file.lastModified) : new Date());

    let bitmap;
    try { bitmap = await decodeImage(file); } catch { return set(item, { status: 'error', note: 'This picture could not be opened.' }); }
    const [thumb, preview] = [await renderRendition(bitmap, 640, 0.8), await renderRendition(bitmap, 1920, 0.85)];
    const { width, height } = bitmap;
    bitmap.close?.();
    set(item, { preview: URL.createObjectURL(thumb.blob), note: 'Uploading' });

    const name = safeName(file.name, type);
    const total = file.size + thumb.blob.size + preview.blob.size;
    const sent = { original: 0, thumb: 0, preview: 0 };
    const tick = (which) => (bytes) => { sent[which] = bytes; set(item, { status: 'uploading', progress: Math.min(1, (sent.original + sent.thumb + sent.preview) / total), note: 'Uploading' }); };
    const uploaded = await Promise.allSettled([
      uploadAsset(file, name, type, `Original of ${name}`, tick('original')),
      uploadAsset(thumb.blob, `${name}.thumb.jpg`, 'image/jpeg', `Thumbnail of ${name}`, tick('thumb')),
      uploadAsset(preview.blob, `${name}.preview.jpg`, 'image/jpeg', `Preview of ${name}`, tick('preview')),
    ]);
    const failed = uploaded.find((r) => r.status === 'rejected');
    if (failed) {
      // Do not leave the files that did arrive lying about with nothing pointing at them.
      uploaded.forEach((r) => { if (r.status === 'fulfilled') Library.removeAsset(r.value.id); });
      throw failed.reason;
    }

    const [original, thumbAsset, previewAsset] = uploaded.map((r) => r.value);

    const ids = [original.id, thumbAsset.id, previewAsset.id];
    set(item, { status: 'scanning', progress: 1, note: 'Scanning for threats' });
    const verdicts = await waitForScan(ids, (n, of) => set(item, { note: `Scanning for threats (${n} of ${of})` }));

    const refused = ids.map((id) => verdicts.get(id)).find((v) => v && v.state !== 'clean');
    if (verdicts.size < ids.length || refused) {
      ids.forEach((id) => Library.removeAsset(id));
      if (refused?.state === 'rejected') {
        return set(item, { status: 'blocked', note: `Blocked by the security scan${refused.signature ? ` (${refused.signature})` : ''}. Nothing was added.` });
      }

      return set(item, { status: 'error', note: refused ? 'The scan could not finish, so nothing was added.' : 'The scan is taking too long, so nothing was added.' });
    }

    set(item, { status: 'saving', note: 'Adding to your library' });
    const id = Library.photoIdFor(takenAt);
    const payload = {
      kind: 'photo',
      schema: 1,
      name: file.name,
      takenAt: takenAt.toISOString(),
      addedAt: new Date().toISOString(),
      width,
      height,
      contentType: type,
      original: { assetId: original.id, bytes: file.size, sha256 },
      preview: { assetId: previewAsset.id, bytes: preview.blob.size, width: preview.width, height: preview.height },
      thumb: { assetId: thumbAsset.id, bytes: thumb.blob.size, width: thumb.width, height: thumb.height },
      caption: '',
      favourite: false,
      albums: [],
      trashedAt: null,
    };
    if (exif.make || exif.model || exif.fNumber) {
      payload.camera = Object.fromEntries(Object.entries({ make: exif.make, model: exif.model, fNumber: exif.fNumber, exposure: exif.exposure, iso: exif.iso, focal: exif.focal }).filter(([, v]) => v));
    }

    try {
      await Library.send([{ type: 'publish', id, version: 1, payload, timestamp: new Date().toISOString() }], `Add ${file.name}`);
    } catch (error) {
      ids.forEach((fileId) => Library.removeAsset(fileId));
      throw error;
    }

    const entity = await Library.freshEntity(id);
    Library.setEntity(entity ?? { id, version: 1, checksum: '', updatedAt: new Date().toISOString(), payload });
    Library.notify();
    set(item, { status: 'done', note: 'Added', photoId: id });
  }

  /** Saves the original. Large ones come down in parallel byte ranges, which the CMS supports, and are put back together here. */
  async function download(photo) {
    const item = { uid: ++serial, name: photo.name, status: 'uploading', note: 'Downloading', progress: 0, preview: null, kind: 'download' };
    items.push(item);
    notify();
    const save = (href) => {
      const link = el('a', { href, download: photo.name });
      document.body.append(link);
      link.click();
      link.remove();
    };

    try {
      const size = photo.original.bytes;
      if (size < 4 * MiB) {
        save(`/media/${photo.original.assetId}?download=1`);
        return set(item, { status: 'done', progress: 1, note: 'Saved' });
      }

      const lanes = 4;
      const slice = Math.ceil(size / lanes);
      const pieces = new Array(lanes);
      const got = new Array(lanes).fill(0);
      await Promise.all(Array.from({ length: lanes }, async (_, lane) => {
        const from = lane * slice;
        const to = Math.min(size - 1, from + slice - 1);
        const response = await fetch(`/media/${photo.original.assetId}`, { headers: { Range: `bytes=${from}-${to}` } });
        if (response.status !== 206 && response.status !== 200) throw new Error(`Part ${lane + 1} failed (${response.status}).`);
        const reader = response.body.getReader();
        const chunks = [];
        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          chunks.push(value);
          got[lane] += value.length;
          set(item, { progress: got.reduce((a, b) => a + b, 0) / size });
        }

        pieces[lane] = new Blob(chunks);
      }));

      // The pieces came from four separate requests: check the whole against the checksum the library recorded at upload.
      set(item, { note: 'Checking the file' });
      const whole = new Blob(pieces, { type: photo.contentType });
      if (await sha256Hex(await whole.arrayBuffer()) !== photo.sha256) throw new Error('The downloaded file did not match its checksum, so it was not saved.');
      const url = URL.createObjectURL(whole);
      save(url);
      setTimeout(() => URL.revokeObjectURL(url), 60000);
      set(item, { status: 'done', progress: 1, note: `Saved: ${lanes} parallel ranges, checksum verified` });
    } catch (error) {
      set(item, { status: 'error', note: error.message ?? 'The download failed.' });
    }
  }

  const dismiss = (item) => { items.splice(items.indexOf(item), 1); notify(); };
  const clearFinished = () => { for (let i = items.length - 1; i >= 0; i--) if (!['queued', 'preparing', 'uploading', 'scanning', 'saving'].includes(items[i].status)) items.splice(i, 1); notify(); };
  const active = () => items.filter((i) => ['queued', 'preparing', 'uploading', 'scanning', 'saving'].includes(i.status)).length;

  return { items, watch: (fn) => watchers.add(fn), add, addSamples, addScannerTest, download, dismiss, clearFinished, active };
})();
