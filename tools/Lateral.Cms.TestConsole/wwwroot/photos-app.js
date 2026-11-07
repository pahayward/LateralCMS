'use strict';

// The photo library's screens: a timeline of everything, favourites, albums, trash, a viewer with the photo's details,
// uploads with progress and scan results. All state lives in the CMS (see photos-store.js); this file only draws it.

const App = {
  view: 'photos',
  albumId: null,
  query: '',
  selection: new Set(),
  anchor: null,
  order: [],
  lightbox: null, // { ids, index, original, info }
};

const VIEWS = [
  { id: 'photos', label: 'Photos', icon: 'photo' },
  { id: 'favourites', label: 'Favourites', icon: 'star' },
  { id: 'albums', label: 'Albums', icon: 'album' },
  { id: 'trash', label: 'Trash', icon: 'trash' },
];

const photoOf = (id) => Library.photos.get(id);
const selectedPhotos = () => [...App.selection].map(photoOf).filter(Boolean);
const mediaUrl = (assetId) => `/media/${assetId}`;
const plural = (n, one, many = `${one}s`) => `${n.toLocaleString()} ${n === 1 ? one : many}`;

// ---------------------------------------------------------------- small pieces: toasts and dialogs

function toast(message, { action, kind = '' } = {}) {
  const node = el('div', { class: `toast ${kind}`.trim() }, el('span', { text: message }));
  if (action) {
    node.append(el('button', { type: 'button', class: 'toast-action', text: action.label, onclick: () => { node.remove(); action.run(); } }));
  }

  $('#toasts').append(node);
  setTimeout(() => node.remove(), action ? 9000 : 5000);
}

/** Runs something that talks to the CMS and tells the user, plainly, if it did not work. */
async function attempt(work) {
  try { return await work(); } catch (error) { toast(error.message ?? 'Something went wrong.', { kind: 'error' }); return undefined; }
}

const modalStack = [];

function modal({ title, content, actions = [], wide = false, onClose }) {
  const layer = $('#modals');
  let backdrop;
  const close = () => {
    onClose?.();
    backdrop.remove();
    modalStack.splice(modalStack.indexOf(close), 1);
    layer.hidden = modalStack.length === 0;
  };

  const buttons = actions.map((a) => el('button', {
    type: 'button',
    class: `btn ${a.kind ?? ''}`.trim(),
    text: a.label,
    onclick: async () => { if ((await a.run?.()) !== false) close(); },
  }));
  backdrop = el('div', { class: 'modal-backdrop', onclick: (event) => { if (event.target === backdrop) close(); } },
    el('div', { class: `modal${wide ? ' wide' : ''}`, role: 'dialog', 'aria-label': title },
      el('h2', { text: title }), content, el('div', { class: 'modal-actions' }, buttons)));
  layer.append(backdrop);
  layer.hidden = false;
  modalStack.push(close);
  (backdrop.querySelector('input, textarea, .btn.primary, .btn') ?? backdrop).focus?.();
  return close;
}

function confirmDialog({ title, text, label, danger = false }) {
  return new Promise((resolve) => {
    modal({
      title,
      content: el('p', { class: 'modal-text', text }),
      onClose: () => resolve(false),
      actions: [
        { label: 'Cancel', run: () => { resolve(false); } },
        { label, kind: danger ? 'danger' : 'primary', run: () => { resolve(true); } },
      ],
    });
  });
}

function askName({ title, label, initial = '', confirm = 'Save' }) {
  return new Promise((resolve) => {
    const input = el('input', { type: 'text', class: 'field', value: initial, maxlength: '100', 'aria-label': label, placeholder: label, autocomplete: 'off' });
    let answered = false;
    const finish = (value) => { if (!answered) { answered = true; resolve(value); } };
    modal({
      title,
      content: el('div', { class: 'modal-form' }, input),
      onClose: () => finish(null),
      actions: [
        { label: 'Cancel', run: () => { finish(null); } },
        { label: confirm, kind: 'primary', run: () => { const v = input.value.trim(); if (!v) { input.focus(); return false; } finish(v); } },
      ],
    });
    input.addEventListener('keydown', (event) => { if (event.key === 'Enter') $('.modal-actions .primary', $('#modals')).click(); });
    input.select();
  });
}

/** Pick an album (or make one) for the given photos. */
function albumPicker(ids) {
  let close;
  const list = el('div', { class: 'picker-list' });
  const fillList = () => {
    const albums = [...Library.albums.values()].sort((a, b) => a.name.localeCompare(b.name));
    fill(list, albums.length === 0 ? el('p', { class: 'modal-text muted', text: 'No albums yet. Make the first one.' }) : albums.map((album) => {
      const count = [...Library.photos.values()].filter((p) => !p.trashedAt && p.albums.includes(album.id)).length;
      return el('button', {
        type: 'button', class: 'picker-item',
        onclick: async () => { close(); await attempt(() => Library.addToAlbum(ids, album.id)); toast(`Added ${plural(ids.length, 'photo')} to ${album.name}`); },
      }, icon('album'), el('span', { class: 'grow', text: album.name }), el('span', { class: 'muted', text: plural(count, 'item') }));
    }));
  };

  fillList();
  const name = el('input', { type: 'text', class: 'field', placeholder: 'New album', maxlength: '100', 'aria-label': 'New album name', autocomplete: 'off' });
  const create = async () => {
    const value = name.value.trim();
    if (!value) return;
    const album = await attempt(() => Library.createAlbum(value));
    if (!album) return;
    close();
    await attempt(() => Library.addToAlbum(ids, album.id));
    toast(`Added ${plural(ids.length, 'photo')} to ${album.name}`);
  };

  name.addEventListener('keydown', (event) => { if (event.key === 'Enter') create(); });
  close = modal({
    title: 'Add to album',
    content: el('div', { class: 'modal-form' }, el('div', { class: 'row' }, name, el('button', { type: 'button', class: 'btn primary', text: 'Create', onclick: create })), list),
    actions: [{ label: 'Cancel' }],
  });
}

// ---------------------------------------------------------------- what is on screen

function nameQuery() { return App.query.trim().toLowerCase(); }

const hayCache = new WeakMap();
function haystack(photo) {
  let text = hayCache.get(photo);
  if (!text) {
    text = [photo.name, photo.caption, cameraLine(photo.camera), longDate(photo.takenAt), monthTitle(photo.takenAt), photo.favourite ? 'favourite favorite' : ''].join(' ').toLowerCase();
    hayCache.set(photo, text);
  }

  return text + ' ' + photo.albums.map((a) => Library.albums.get(a)?.name ?? '').join(' ').toLowerCase();
}

function visiblePhotos() {
  const tokens = nameQuery().split(/\s+/).filter(Boolean);
  const list = [];
  for (const photo of Library.photos.values()) {
    const trashed = Boolean(photo.trashedAt);
    if (App.view === 'trash' ? !trashed : trashed) continue;
    if (App.view === 'favourites' && !photo.favourite) continue;
    if (App.view === 'album' && !photo.albums.includes(App.albumId)) continue;
    if (tokens.length > 0) {
      const text = haystack(photo);
      if (!tokens.every((t) => text.includes(t))) continue;
    }

    list.push(photo);
  }

  // The ids put the newest first, so this is the order the CMS itself lists them in.
  return App.view === 'trash' ? list.sort((a, b) => b.trashedAt - a.trashedAt) : list.sort((a, b) => (a.id < b.id ? -1 : 1));
}

const tileCache = new Map();

function toggleSelect(id, range = false) {
  if (range && App.anchor && App.order.includes(App.anchor)) {
    const a = App.order.indexOf(App.anchor);
    const b = App.order.indexOf(id);
    const [from, to] = a < b ? [a, b] : [b, a];
    for (let i = from; i <= to; i++) App.selection.add(App.order[i]);
  } else if (App.selection.has(id)) {
    App.selection.delete(id);
  } else {
    App.selection.add(id);
  }

  App.anchor = id;
  schedule();
}

function paintTile(tile, photo) {
  tile.classList.toggle('selected', App.selection.has(photo.id));
  tile.classList.toggle('favourite', photo.favourite);
  tile.setAttribute('aria-label', `${photo.caption || photo.name}, ${longDate(photo.takenAt)}`);
  tile.setAttribute('aria-pressed', App.selection.has(photo.id) ? 'true' : 'false');
}

function tileFor(photo) {
  let entry = tileCache.get(photo.id);
  if (!entry || entry.thumb !== photo.thumb.assetId) {
    const image = el('img', { src: mediaUrl(photo.thumb.assetId), alt: '', loading: 'lazy', decoding: 'async', draggable: 'false' });
    image.addEventListener('load', () => image.classList.add('loaded'));
    image.addEventListener('error', () => image.parentElement?.classList.add('broken'));
    const tile = el('figure', { class: 'tile', tabindex: '0', role: 'button' },
      image,
      el('button', {
        type: 'button', class: 'tile-check', 'aria-label': 'Select photo', title: 'Select',
        onclick: (event) => { event.stopPropagation(); toggleSelect(photo.id, event.shiftKey); },
      }, icon('circle', 22), icon('check', 22)),
      el('span', { class: 'tile-star', 'aria-hidden': 'true' }, icon('starFilled', 18)));
    // A very wide or very tall picture is cropped in the grid (the viewer shows all of it), so one panorama cannot take a whole row.
    tile.style.setProperty('--ar', Math.min(2.4, Math.max(0.6, photo.width / photo.height)).toFixed(4));
    tile.addEventListener('click', (event) => {
      if (App.selection.size > 0) toggleSelect(photo.id, event.shiftKey);
      else openLightbox(photo.id);
    });
    tile.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') openLightbox(photo.id);
      else if (event.key === ' ') { event.preventDefault(); toggleSelect(photo.id, event.shiftKey); }
    });
    entry = { el: tile, thumb: photo.thumb.assetId };
    tileCache.set(photo.id, entry);
  }

  paintTile(entry.el, photo);
  return entry.el;
}

function timeline(list) {
  const root = el('div', { class: 'timeline' });
  const now = new Date();
  let section = null;
  let grid = null;
  let currentDay = '';
  let members = [];
  const closeSection = () => { if (section) section.dataset.count = String(members.length); };
  const dayOf = (photo) => (App.view === 'trash' ? photo.trashedAt : photo.takenAt);

  for (const photo of list) {
    const key = dayKey(dayOf(photo));
    if (key !== currentDay) {
      closeSection();
      currentDay = key;
      members = [];
      const ids = members;
      const check = el('button', {
        type: 'button', class: 'day-check', 'aria-label': 'Select everything on this day', title: 'Select all from this day',
        onclick: () => {
          const all = ids.every((id) => App.selection.has(id));
          ids.forEach((id) => (all ? App.selection.delete(id) : App.selection.add(id)));
          schedule();
        },
      }, icon('circle', 20), icon('check', 20));
      grid = el('div', { class: 'grid' });
      section = el('section', { class: 'day', 'data-month': monthKey(dayOf(photo)) },
        el('h2', { class: 'day-head' }, check, el('span', { text: App.view === 'trash' ? `Deleted ${dayLabel(dayOf(photo), now)}` : dayLabel(dayOf(photo), now) })),
        grid);
      root.append(section);
    }

    members.push(photo.id);
    grid.append(tileFor(photo));
  }

  closeSection();
  return root;
}

function skeleton() {
  const shapes = [1.5, 1.33, 1.78, 0.75, 1.5, 1, 1.5, 0.67, 1.78, 1.33, 1.5, 1, 0.75, 1.5, 1.78, 1.33];
  return el('div', { class: 'timeline skeleton', 'aria-label': 'Loading your photos' },
    el('div', { class: 'day' }, el('h2', { class: 'day-head' }, el('span', { class: 'bar' })),
      el('div', { class: 'grid' }, shapes.map((ratio) => {
        const tile = el('figure', { class: 'tile ghost' });
        tile.style.setProperty('--ar', String(ratio));
        return tile;
      }))));
}

function emptyState() {
  const message = ({
    photos: ['Your library is empty', 'Add photos and they are scanned, then stored in the CMS: the pictures as files, everything about them as versioned records.'],
    favourites: ['No favourites yet', 'Star a photo and it appears here.'],
    trash: ['Trash is empty', 'Photos you delete wait here until you delete them forever.'],
    album: ['This album is empty', 'Select photos in your library and choose Add to album.'],
  })[App.view] ?? ['Nothing here', ''];
  const searching = nameQuery() !== '';
  const title = searching ? `No results for “${App.query.trim()}”` : message[0];
  const text = searching ? 'Try a month, a year, a file name, a camera, or an album.' : message[1];
  const actions = !searching && App.view === 'photos' ? el('div', { class: 'empty-actions' },
    el('button', { type: 'button', class: 'btn primary', onclick: () => $('#file-input').click() }, icon('upload', 20), el('span', { text: 'Upload photos' })),
    el('button', { type: 'button', class: 'btn', onclick: addSamples, text: 'Add sample photos' })) : null;
  return el('div', { class: 'empty' }, el('div', { class: 'empty-art' }, icon(searching ? 'search' : (VIEWS.find((v) => v.id === App.view)?.icon ?? 'album'), 56)), el('h2', { text: title }), el('p', { text }), actions);
}

function errorState() {
  return el('div', { class: 'empty' }, el('div', { class: 'empty-art bad' }, icon('error', 56)), el('h2', { text: 'The library is not available' }),
    el('p', { text: Library.state.error }), el('button', { type: 'button', class: 'btn primary', text: 'Try again', onclick: () => Library.load() }));
}

function albumHeader() {
  const album = Library.albums.get(App.albumId);
  if (!album) {
    return el('div', { class: 'page-head' }, Library.state.loaded
      ? [el('h1', { text: 'Album not found' }), el('a', { class: 'btn', href: '#/albums', text: 'Back to albums' })]
      : el('h1', { text: 'Album' }));
  }

  const count = [...Library.photos.values()].filter((p) => !p.trashedAt && p.albums.includes(album.id)).length;
  return el('div', { class: 'page-head' },
    iconButton('back', 'Back to albums', () => { location.hash = '#/albums'; }),
    el('div', { class: 'grow' }, el('h1', { class: 'album-title', text: album.name, title: 'Rename' }), el('p', { class: 'muted', text: plural(count, 'item') })),
    el('button', {
      type: 'button', class: 'btn', onclick: async () => {
        const name = await askName({ title: 'Rename album', label: 'Album name', initial: album.name });
        if (name) await attempt(() => Library.renameAlbum(album.id, name));
      },
    }, icon('edit', 18), el('span', { text: 'Rename' })),
    el('button', {
      type: 'button', class: 'btn', onclick: async () => {
        if (!await confirmDialog({ title: 'Delete album?', text: `“${album.name}” will be deleted. The photos in it stay in your library.`, label: 'Delete album', danger: true })) return;
        await attempt(() => Library.deleteAlbum(album.id));
        location.hash = '#/albums';
        toast('Album deleted');
      },
    }, icon('trash', 18), el('span', { text: 'Delete' })));
}

function renderAlbums(content) {
  const tokens = nameQuery();
  const albums = [...Library.albums.values()].filter((a) => !tokens || a.name.toLowerCase().includes(tokens)).sort((a, b) => a.name.localeCompare(b.name));
  const newCard = el('button', {
    type: 'button', class: 'album-card new',
    onclick: async () => {
      const name = await askName({ title: 'New album', label: 'Album name', confirm: 'Create' });
      if (name) await attempt(() => Library.createAlbum(name));
    },
  }, el('div', { class: 'album-cover' }, icon('add', 40)), el('div', { class: 'album-name', text: 'New album' }));

  const cards = albums.map((album) => {
    const members = [...Library.photos.values()].filter((p) => !p.trashedAt && p.albums.includes(album.id)).sort((a, b) => (a.id < b.id ? -1 : 1));
    const cover = members[0];
    return el('a', { class: 'album-card', href: `#/albums/${encodeURIComponent(album.id)}` },
      el('div', { class: 'album-cover' }, cover ? el('img', { src: mediaUrl(cover.thumb.assetId), alt: '', loading: 'lazy' }) : icon('album', 44)),
      el('div', { class: 'album-name', text: album.name }), el('div', { class: 'album-count muted', text: plural(members.length, 'item') }));
  });

  fill(content, el('div', { class: 'page-head' }, el('h1', { text: 'Albums' })), el('div', { class: 'album-grid' }, newCard, cards));
}

function trashBanner(list) {
  return el('div', { class: 'banner' }, icon('trash', 20),
    el('span', { class: 'grow', text: `${plural(list.length, 'photo')} in the trash. They stay until you delete them forever.` }),
    el('button', { type: 'button', class: 'btn', text: 'Empty trash', onclick: () => purgeDialog(list.map((p) => p.id)) }));
}

function renderContent() {
  const content = $('#content');
  if (App.view === 'albums') {
    App.order = [];
    renderAlbums(content);
    $('#scrubber').hidden = true;
    return;
  }

  const list = visiblePhotos();
  App.order = list.map((p) => p.id);
  for (const id of [...App.selection]) if (!App.order.includes(id)) App.selection.delete(id);

  const parts = [];
  if (App.view === 'album') parts.push(albumHeader());
  if (App.view === 'trash' && list.length > 0) parts.push(trashBanner(list));
  if (list.length > 0) parts.push(timeline(list));
  else if (Library.state.error && !Library.state.loaded) parts.push(errorState());
  else if (!Library.state.loaded) parts.push(skeleton());
  else parts.push(emptyState());
  fill(content, parts);
  buildScrubber();
}

// ---------------------------------------------------------------- the date rail on the right

let marks = [];

function buildScrubber() {
  const rail = $('#scrubber');
  const content = $('#content');
  const sections = $$('.day', content);
  if (sections.length === 0 || content.scrollHeight < content.clientHeight * 1.6 || App.view === 'albums') {
    rail.hidden = true;
    return;
  }

  rail.hidden = false;
  const total = content.scrollHeight;
  const first = new Map();
  for (const section of sections) if (!first.has(section.dataset.month)) first.set(section.dataset.month, section);

  let lastYear = '';
  marks = [...first].map(([key, section]) => {
    const [year, month] = key.split('-');
    const label = year !== lastYear ? year : '';
    lastYear = year;
    return { key, y: section.offsetTop / total, title: `${MONTHS[+month - 1]} ${year}`, label, section };
  });

  const ticks = marks.map((mark) => {
    const tick = el('div', { class: `tick${mark.label ? ' year' : ''}` }, mark.label ? el('span', { text: mark.label }) : null);
    tick.style.setProperty('top', `${(mark.y * 100).toFixed(2)}%`);
    return tick;
  });
  fill(rail, ticks, el('div', { class: 'rail-thumb' }), el('div', { class: 'rail-bubble' }));
  moveRailThumb();
}

function moveRailThumb() {
  const content = $('#content');
  const thumb = $('.rail-thumb', $('#scrubber'));
  if (!thumb) return;
  thumb.style.setProperty('top', `${((content.scrollTop / content.scrollHeight) * 100).toFixed(2)}%`);
}

function wireScrubber() {
  const rail = $('#scrubber');
  const content = $('#content');
  const fraction = (event) => {
    const box = rail.getBoundingClientRect();
    return Math.min(1, Math.max(0, (event.clientY - box.top) / box.height));
  };
  const markAt = (f) => marks.reduce((best, m) => (m.y <= f + 0.002 ? m : best), marks[0]);
  const bubble = (event) => {
    const b = $('.rail-bubble', rail);
    if (!b || marks.length === 0) return;
    b.textContent = markAt(fraction(event)).title;
    b.style.setProperty('top', `${(fraction(event) * 100).toFixed(2)}%`);
    b.classList.add('show');
  };
  let dragging = false;
  const jump = (event) => {
    if (marks.length === 0) return;
    content.scrollTop = Math.max(0, markAt(fraction(event)).section.offsetTop - 8);
  };

  rail.addEventListener('pointermove', (event) => { bubble(event); if (dragging) jump(event); });
  rail.addEventListener('pointerleave', () => { if (!dragging) $('.rail-bubble', rail)?.classList.remove('show'); });
  rail.addEventListener('pointerdown', (event) => { dragging = true; rail.setPointerCapture(event.pointerId); jump(event); bubble(event); });
  rail.addEventListener('pointerup', () => { dragging = false; $('.rail-bubble', rail)?.classList.remove('show'); });
  content.addEventListener('scroll', moveRailThumb, { passive: true });
}

// ---------------------------------------------------------------- actions

function toggleFavourite(photos) {
  const all = photos.every((p) => p.favourite);
  return attempt(() => Library.setFavourite(photos.map((p) => p.id), !all));
}

async function trashPhotos(photos) {
  const ids = photos.map((p) => p.id);
  App.selection.clear();
  const done = await attempt(() => Library.trash(ids).then(() => true));
  if (!done) return;
  toast(`Moved ${plural(ids.length, 'photo')} to trash`, { action: { label: 'Undo', run: () => attempt(() => Library.restore(ids)) } });
}

async function restorePhotos(photos) {
  App.selection.clear();
  const done = await attempt(() => Library.restore(photos.map((p) => p.id)).then(() => true));
  if (done) toast(`Restored ${plural(photos.length, 'photo')}`);
}

async function purgeDialog(ids) {
  const ok = await confirmDialog({
    title: ids.length === 1 ? 'Delete forever?' : `Delete ${ids.length} photos forever?`,
    text: 'The CMS deletes the photo and its three files are wiped from storage. This cannot be undone.',
    label: 'Delete forever',
    danger: true,
  });
  if (!ok) return;
  App.selection.clear();
  const done = await attempt(() => Library.purge(ids).then(() => true));
  if (done) toast(`Deleted ${plural(ids.length, 'photo')} forever`);
}

async function downloadPhotos(photos) {
  for (const photo of photos) {
    Uploads.download(photo);
    if (photos.length > 1) await wait(450);
  }
}

function addSamples() {
  Uploads.addSamples(36);
  toast('Adding 36 sample photos. Each is drawn here, scanned by the CMS, then stored.');
}

// ---------------------------------------------------------------- chrome: header, selection bar, sidebar

function buildSidebar() {
  const nav = $('#sidebar');
  const items = VIEWS.map((view) => el('a', { class: 'nav-item', href: `#/${view.id}`, 'data-view': view.id }, icon(view.icon), el('span', { class: 'grow', text: view.label }), el('span', { class: 'count', 'data-count': view.id })));
  fill(nav, el('div', { class: 'nav-list' }, items), el('div', { class: 'storage', id: 'storage' }));
}

function renderChrome() {
  const active = App.view === 'album' ? 'albums' : App.view;
  $$('.nav-item', $('#sidebar')).forEach((a) => a.classList.toggle('active', a.dataset.view === active));
  const photos = [...Library.photos.values()];
  const counts = { photos: photos.filter((p) => !p.trashedAt).length, favourites: photos.filter((p) => !p.trashedAt && p.favourite).length, albums: Library.albums.size, trash: photos.filter((p) => p.trashedAt).length };
  $$('[data-count]', $('#sidebar')).forEach((n) => { const c = counts[n.dataset.count]; n.textContent = c > 0 ? c.toLocaleString() : ''; });

  const stats = Library.stats();
  fill($('#storage'),
    el('div', { class: 'storage-head' }, icon('cloud', 20), el('span', { text: 'Stored in the CMS' })),
    el('div', { class: 'storage-figure', text: fmtBytes(stats.bytes) }),
    el('p', { class: 'muted', text: `${plural(stats.photos, 'photo')}, ${plural(stats.files, 'file')}. Each photo is a thumbnail, a preview and the original.` }));

  const header = $('#app-header');
  const selecting = App.selection.size > 0;
  header.classList.toggle('selecting', selecting);
  document.body.classList.toggle('selecting', selecting);
  $('#selection-bar').hidden = !selecting;
  if (selecting) renderSelectionBar();

  const search = $('#search');
  if (search.value !== App.query && document.activeElement !== search) search.value = App.query;
  $('#search-clear').hidden = App.query === '';
  document.title = App.view === 'photos' ? 'Lateral Photos' : `${VIEWS.find((v) => v.id === active)?.label ?? 'Photos'} · Lateral Photos`;
}

function renderSelectionBar() {
  const picked = selectedPhotos();
  const bar = $('#selection-bar');
  const buttons = [];
  if (App.view === 'trash') {
    buttons.push(iconButton('restore', 'Restore', () => restorePhotos(picked)), iconButton('trash', 'Delete forever', () => purgeDialog(picked.map((p) => p.id))));
  } else {
    const allFav = picked.every((p) => p.favourite);
    buttons.push(
      iconButton(allFav ? 'starFilled' : 'star', allFav ? 'Remove from favourites' : 'Favourite', () => toggleFavourite(picked)),
      iconButton('album', 'Add to album', () => albumPicker(picked.map((p) => p.id))));
    if (App.view === 'album') {
      buttons.push(iconButton('close', 'Remove from this album', async () => {
        const ids = picked.map((p) => p.id);
        App.selection.clear();
        await attempt(() => Library.removeFromAlbum(ids, App.albumId));
        toast(`Removed ${plural(ids.length, 'photo')} from the album`);
      }));
    }

    buttons.push(iconButton('download', 'Download', () => downloadPhotos(picked)), iconButton('trash', 'Move to trash', () => trashPhotos(picked)));
  }

  fill(bar, iconButton('close', 'Clear selection', () => { App.selection.clear(); schedule(); }), el('span', { class: 'sel-count', text: `${App.selection.size.toLocaleString()} selected` }), el('span', { class: 'grow' }), buttons);
}

// ---------------------------------------------------------------- the viewer

let lbToken = 0;
let infoToken = 0;

function openLightbox(id) {
  App.lightbox = { ids: [...App.order], index: Math.max(0, App.order.indexOf(id)), original: false, info: App.lightbox?.info ?? false };
  history.pushState({ lightbox: true }, '', location.href);
  $('#lightbox').hidden = false;
  document.body.classList.add('viewing');
  showLightbox(true);
}

function closeLightbox(fromHistory = false) {
  if (!App.lightbox) return;
  App.lightbox = null;
  $('#lightbox').hidden = true;
  document.body.classList.remove('viewing');
  $('#lb-img').removeAttribute('src');
  if (!fromHistory && history.state?.lightbox) history.back();
}

function stepLightbox(delta) {
  const lb = App.lightbox;
  if (!lb) return;
  const index = lb.index + delta;
  if (index < 0 || index >= lb.ids.length) return;
  lb.index = index;
  lb.original = false;
  showLightbox(true);
}

function buildLightbox() {
  fill($('#lightbox'),
    el('div', { class: 'lb-bar' },
      iconButton('back', 'Back', () => closeLightbox(), 'light'),
      el('div', { class: 'lb-title' }, el('div', { class: 'lb-name', id: 'lb-name' }), el('div', { class: 'lb-sub', id: 'lb-sub' })),
      el('div', { class: 'lb-actions', id: 'lb-actions' })),
    el('div', { class: 'lb-main' },
      el('button', { type: 'button', class: 'lb-nav prev', 'aria-label': 'Previous photo', onclick: () => stepLightbox(-1) }, icon('left', 36)),
      el('div', { class: 'lb-stage', id: 'lb-stage' }, el('img', { id: 'lb-img', alt: '', draggable: 'false' })),
      el('button', { type: 'button', class: 'lb-nav next', 'aria-label': 'Next photo', onclick: () => stepLightbox(1) }, icon('right', 36))),
    el('aside', { class: 'lb-info', id: 'lb-info', hidden: true, 'aria-label': 'Photo details' }));
  $('#lb-stage').addEventListener('click', (event) => { if (event.target === $('#lb-stage')) closeLightbox(); });
}

function showLightbox(switching) {
  const lb = App.lightbox;
  if (!lb) return;
  const photo = photoOf(lb.ids[lb.index]);
  if (!photo) {
    lb.ids.splice(lb.index, 1);
    if (lb.ids.length === 0) return closeLightbox();
    lb.index = Math.min(lb.index, lb.ids.length - 1);
    return showLightbox(true);
  }

  const img = $('#lb-img');
  const mode = lb.original ? 'original' : 'preview';
  if (switching || img.dataset.photo !== photo.id || img.dataset.mode !== mode) {
    const token = ++lbToken;
    img.dataset.photo = photo.id;
    img.dataset.mode = mode;
    img.classList.add('loading');
    img.classList.toggle('natural', lb.original);
    img.src = mediaUrl(photo.thumb.assetId); // already in the browser from the grid: shows at once while the better one loads
    const better = new Image();
    better.onload = () => { if (token === lbToken) { img.src = better.src; img.classList.remove('loading'); } };
    better.src = mediaUrl(lb.original ? photo.original.assetId : photo.preview.assetId);
    for (const delta of [-1, 1]) {
      const near = photoOf(lb.ids[lb.index + delta]);
      if (near) new Image().src = mediaUrl(near.preview.assetId);
    }
  }

  $('#lb-name').textContent = photo.caption || photo.name;
  $('#lb-sub').textContent = `${longDate(photo.takenAt)}, ${clock(photo.takenAt)}`;
  $('.lb-nav.prev').hidden = lb.index === 0;
  $('.lb-nav.next').hidden = lb.index >= lb.ids.length - 1;

  const trashed = Boolean(photo.trashedAt);
  fill($('#lb-actions'),
    trashed ? null : iconButton(photo.favourite ? 'starFilled' : 'star', photo.favourite ? 'Remove from favourites' : 'Favourite', () => toggleFavourite([photo]), `light${photo.favourite ? ' on' : ''}`),
    iconButton('expand', lb.original ? 'Show the preview' : 'View the original', () => { lb.original = !lb.original; showLightbox(true); }, `light${lb.original ? ' on' : ''}`),
    trashed ? null : iconButton('album', 'Add to album', () => albumPicker([photo.id]), 'light'),
    iconButton('download', 'Download the original', () => downloadPhotos([photo]), 'light'),
    iconButton('info', 'Details', () => { lb.info = !lb.info; showLightbox(false); }, `light${lb.info ? ' on' : ''}`),
    trashed
      ? [iconButton('restore', 'Restore', () => lightboxAct(() => restorePhotos([photo])), 'light'), iconButton('trash', 'Delete forever', () => lightboxAct(() => purgeDialog([photo.id])), 'light')]
      : iconButton('trash', 'Move to trash', () => lightboxAct(() => trashPhotos([photo])), 'light'));

  const info = $('#lb-info');
  info.hidden = !lb.info;
  $('#lightbox').classList.toggle('with-info', lb.info);
  if (lb.info) renderInfo(photo);
}

/** Does something to the photo on screen, then moves to the next one (or closes if it was the last). */
async function lightboxAct(work) {
  const lb = App.lightbox;
  const id = lb?.ids[lb.index];
  await work();
  if (!App.lightbox || !id) return;
  const photo = photoOf(id);
  if (photo && (Boolean(photo.trashedAt) === (App.view === 'trash'))) return;
  const at = lb.ids.indexOf(id);
  lb.ids.splice(at, 1);
  if (lb.ids.length === 0) return closeLightbox();
  lb.index = Math.min(at, lb.ids.length - 1);
  showLightbox(true);
}

function infoRow(label, value) {
  return el('div', { class: 'info-row' }, el('div', { class: 'info-label', text: label }), el('div', { class: 'info-value' }, value));
}

function renderInfo(photo) {
  const info = $('#lb-info');
  const key = `${photo.id}:${photo.version}`;
  if (info.dataset.key === key && info.childElementCount > 0) return; // same photo, same version: leave what is there (and any typing in it)
  info.dataset.key = key;
  const token = ++infoToken;
  const caption = el('textarea', { class: 'field caption', rows: '2', maxlength: '500', placeholder: 'Add a description', 'aria-label': 'Description' });
  caption.value = photo.caption;
  caption.addEventListener('change', () => attempt(() => Library.setCaption(photo.id, caption.value)));

  const albums = photo.albums.map((id) => Library.albums.get(id)).filter(Boolean);
  const stored = el('div', { class: 'stored' }, el('p', { class: 'muted', text: 'Reading from the CMS…' }));
  const camera = cameraLine(photo.camera);

  fill(info,
    el('div', { class: 'info-head' }, el('h2', { text: 'Details' }), iconButton('close', 'Close details', () => { App.lightbox.info = false; showLightbox(false); })),
    caption,
    infoRow('Taken', `${longDate(photo.takenAt)}, ${clock(photo.takenAt)}`),
    infoRow('File', `${photo.name}`),
    infoRow('Size', `${photo.width} × ${photo.height}  ·  ${((photo.width * photo.height) / 1e6).toFixed(1)} MP  ·  ${fmtBytes(photo.bytes)}`),
    camera ? infoRow('Camera', el('div', {}, el('div', { text: camera }), el('div', { class: 'muted', text: settingsLine(photo.camera) }))) : null,
    infoRow('Albums', albums.length === 0 ? el('span', { class: 'muted', text: 'Not in an album' }) : el('div', { class: 'chips' }, albums.map((a) => el('a', { class: 'chip', href: `#/albums/${encodeURIComponent(a.id)}`, onclick: () => closeLightbox(true), text: a.name })))),
    el('h3', { text: 'In the CMS' }),
    stored);

  Promise.all([Library.freshEntity(photo.id), ...['original', 'preview', 'thumb'].map((k) => Library.assetStatus(photo[k].assetId))]).then(([entity, ...files]) => {
    if (token !== infoToken || !stored.isConnected) return;
    const labels = ['Original', 'Preview', 'Thumbnail'];
    const sizes = [photo.original.bytes, photo.preview.bytes, photo.thumb.bytes];
    fill(stored,
      infoRow('Record', el('code', { text: photo.id })),
      infoRow('Version', entity ? `${entity.version}  ·  checksum ${shortChecksum(entity.checksum)}` : 'unknown'),
      infoRow('SHA-256', el('code', { text: photo.sha256 })),
      el('div', { class: 'files' }, files.map((file, i) => el('div', { class: 'file' },
        icon(file?.state === 'clean' ? 'shield' : 'error', 20),
        el('div', { class: 'grow' }, el('div', { text: `${labels[i]}  ·  ${fmtBytes(sizes[i] ?? 0)}` }),
          el('div', { class: 'muted', text: file ? (file.state === 'clean' ? `Scanned clean${file.scannerVersion ? ` by ${file.scannerVersion}` : ''}${file.scannedAt ? `, ${new Date(file.scannedAt).toLocaleString()}` : ''}` : file.state) : 'Not found' })),
        el('a', { class: 'btn small', href: `${mediaUrl(photo[['original', 'preview', 'thumb'][i]].assetId)}?download=1`, text: 'Open' })))));
  });
}

function shortChecksum(value) {
  if (!value) return '';
  const [algo, hex = ''] = String(value).split(':');
  return hex.length > 14 ? `${algo}:${hex.slice(0, 8)}…${hex.slice(-4)}` : String(value);
}

// ---------------------------------------------------------------- uploads and downloads tray

let trayCollapsed = false;
let wasActive = 0;

function renderTray() {
  const tray = $('#tray');
  const items = Uploads.items;
  tray.hidden = items.length === 0;
  if (items.length === 0) return;

  const active = Uploads.active();
  const done = items.filter((i) => i.status === 'done').length;
  const title = active > 0 ? `${plural(active, 'item')} in progress` : `${done} of ${items.length} finished`;
  // What is happening now first, then what is waiting, then what has finished (most recent first).
  const rank = (item) => (['uploading', 'scanning', 'saving', 'preparing'].includes(item.status) ? 0 : item.status === 'queued' ? 1 : 2);
  const ordered = items.map((item, index) => ({ item, index })).sort((a, b) => rank(a.item) - rank(b.item) || (rank(a.item) === 2 ? b.index - a.index : a.index - b.index)).map((entry) => entry.item);
  const rows = ordered.map((item) => {
    const lead = item.preview
      ? el('img', { class: 'tray-thumb', src: item.preview, alt: '' })
      : el('div', { class: 'tray-thumb ph' }, icon(item.kind === 'download' ? 'download' : 'photo', 20));
    const tone = item.status === 'done' ? 'ok' : item.status === 'blocked' || item.status === 'error' ? 'bad' : item.status === 'duplicate' ? 'warn' : '';
    const running = ['uploading', 'scanning', 'saving', 'preparing'].includes(item.status);
    const bar = running ? el('div', { class: `bar${item.status === 'uploading' ? '' : ' wait'}` }, (() => { const fillBar = el('div', { class: 'bar-fill' }); fillBar.style.setProperty('width', `${Math.round(item.progress * 100)}%`); return fillBar; })()) : null;
    return el('li', { class: `tray-row ${tone}` }, lead,
      el('div', { class: 'grow' }, el('div', { class: 'tray-name', text: item.name }), el('div', { class: `tray-note ${tone}`, text: item.status === 'uploading' && item.kind === 'upload' ? `Uploading ${Math.round(item.progress * 100)}%` : item.note }), bar),
      !running && item.status !== 'queued' ? iconButton('close', 'Dismiss', () => Uploads.dismiss(item), 'small') : null);
  });

  fill(tray,
    el('div', { class: 'tray-head' }, el('span', { class: 'tray-title', text: title }), el('span', { class: 'grow' }),
      active === 0 ? el('button', { type: 'button', class: 'linkish', text: 'Clear', onclick: () => Uploads.clearFinished() }) : null,
      iconButton(trayCollapsed ? 'expand' : 'close', trayCollapsed ? 'Show' : 'Hide', () => { trayCollapsed = !trayCollapsed; renderTray(); }, 'small')),
    trayCollapsed ? null : el('ul', { class: 'tray-list' }, rows));

  if (wasActive > 0 && active === 0) {
    const added = items.filter((i) => i.status === 'done' && i.kind === 'upload').length;
    const blocked = items.filter((i) => i.status === 'blocked').length;
    if (added > 0) toast(`Added ${plural(added, 'photo')} to your library`);
    if (blocked > 0) toast(`${plural(blocked, 'file')} blocked by the security scan`, { kind: 'error' });
  }

  wasActive = active;
}

// ---------------------------------------------------------------- behind the scenes

let behindQueued = false;

function renderBehind() {
  const drawer = $('#behind');
  if (drawer.hidden) return;
  const stats = Library.stats();
  const log = Cms.log.slice(0, 60);
  const fact = (value, label) => el('div', { class: 'fact' }, el('div', { class: 'fact-value', text: value }), el('div', { class: 'muted', text: label }));

  fill(drawer,
    el('div', { class: 'drawer-head' }, el('h2', { text: 'Behind the scenes' }), iconButton('close', 'Close', () => toggleBehind(false))),
    el('div', { class: 'drawer-body' },
      el('p', { text: 'This app keeps nothing of its own. Everything you see is read from, and written to, the Lateral CMS.' }),
      el('div', { class: 'facts' }, fact(stats.photos.toLocaleString(), 'photo records'), fact(stats.albums.toLocaleString(), 'album records'), fact(stats.files.toLocaleString(), 'files'), fact(fmtBytes(stats.bytes), 'stored')),
      el('dl', { class: 'how' },
        el('dt', { text: 'A photo' }), el('dd', { text: 'One entity, photo-<reversed time>-<id>. The CMS lists by id, so the newest come first with no sorting here. Its details are its payload.' }),
        el('dt', { text: 'Its pictures' }), el('dd', { text: 'Three assets: thumbnail and preview made in your browser, and the untouched original. Large ones go up as parallel parts and come down as parallel ranges.' }),
        el('dt', { text: 'Safety' }), el('dd', { text: 'Every file is scanned before the CMS will serve it. A blocked file is wiped; only the verdict stays.' }),
        el('dt', { text: 'A change' }), el('dd', { text: 'A new version of the photo. Favourites, descriptions, albums and trash are all just versions. Delete forever is a CMS delete.' }),
        el('dt', { text: 'Who' }), el('dd', { text: 'The page acts as cms to write, user to read and admin to remove files. Passwords stay on the console’s server.' })),
      el('div', { class: 'row wrap' },
        el('button', { type: 'button', class: 'btn', text: 'Add 36 sample photos', onclick: addSamples }),
        el('button', { type: 'button', class: 'btn', text: 'Test the scanner', title: 'Uploads a picture that carries the scanner’s test signature', onclick: () => { Uploads.addScannerTest(); toast('Uploading a file the scanner should block'); } }),
        el('a', { class: 'btn', href: '/engineering', text: 'Engineering console' })),
      el('h3', { text: 'What it asked the CMS' }),
      log.length === 0 ? el('p', { class: 'muted', text: 'Nothing yet.' }) : el('ol', { class: 'log' }, log.map((entry) =>
        el('li', {}, el('span', { class: `chip ${entry.status >= 200 && entry.status < 300 ? 'ok' : entry.status === 0 || entry.status >= 400 ? 'bad' : 'warn'}`, text: String(entry.status || '—') }),
          el('code', { text: `${entry.method} ${entry.path.length > 54 ? entry.path.slice(0, 54) + '…' : entry.path}` }),
          el('span', { class: 'muted', text: `${entry.as} · ${entry.ms} ms${entry.label ? ' · ' + entry.label : ''}` }))))));
}

function toggleBehind(open) {
  const drawer = $('#behind');
  drawer.hidden = !(open ?? drawer.hidden);
  $('#behind-btn').classList.toggle('on', !drawer.hidden);
  renderBehind();
}

// ---------------------------------------------------------------- put it together

let renderQueued = false;
function schedule() {
  if (renderQueued) return;
  renderQueued = true;
  // On the next frame; or, if the page is in the background and frames are not being drawn, a moment from now.
  const run = () => {
    if (!renderQueued) return;
    renderQueued = false;
    renderChrome();
    renderContent();
    if (App.lightbox) showLightbox(false);
    if (!$('#behind').hidden) renderBehind();
  };
  requestAnimationFrame(run);
  setTimeout(run, 150);
}

function route() {
  const parts = decodeURIComponent(location.hash.replace(/^#\/?/, '')).split('/');
  const name = parts[0] || 'photos';
  if (name === 'albums' && parts[1]) {
    App.view = 'album';
    App.albumId = parts[1];
  } else {
    App.view = VIEWS.some((v) => v.id === name) ? name : 'photos';
    App.albumId = null;
  }

  App.selection.clear();
  App.anchor = null;
  $('#content').scrollTop = 0;
  document.body.classList.remove('nav-open');
  schedule();
}

function wire() {
  $$('[data-icon]').forEach((node) => node.replaceChildren(icon(node.dataset.icon)));
  buildSidebar();
  buildLightbox();
  wireScrubber();

  $('#menu-btn').addEventListener('click', () => document.body.classList.toggle(window.innerWidth <= 860 ? 'nav-open' : 'nav-collapsed'));
  $('#scrim').addEventListener('click', () => document.body.classList.remove('nav-open'));
  $('#upload-btn').addEventListener('click', () => $('#file-input').click());
  $('#file-input').addEventListener('change', (event) => { Uploads.add([...event.target.files]); event.target.value = ''; });
  $('#behind-btn').addEventListener('click', () => toggleBehind());

  let typing = 0;
  $('#search').addEventListener('input', (event) => {
    clearTimeout(typing);
    typing = setTimeout(() => { App.query = event.target.value; schedule(); }, 120);
  });
  $('#search-form').addEventListener('submit', (event) => event.preventDefault());
  $('#search-clear').addEventListener('click', () => { App.query = ''; schedule(); $('#search').focus(); });

  // Dropping files anywhere on the page adds them.
  let depth = 0;
  const hasFiles = (event) => [...(event.dataTransfer?.types ?? [])].includes('Files');
  window.addEventListener('dragenter', (event) => { if (hasFiles(event)) { depth++; $('#drop').hidden = false; } });
  window.addEventListener('dragleave', (event) => { if (hasFiles(event) && --depth <= 0) { depth = 0; $('#drop').hidden = true; } });
  window.addEventListener('dragover', (event) => { if (hasFiles(event)) event.preventDefault(); });
  window.addEventListener('drop', (event) => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    depth = 0;
    $('#drop').hidden = true;
    Uploads.add([...event.dataTransfer.files]);
  });

  window.addEventListener('keydown', (event) => {
    const typingInField = ['INPUT', 'TEXTAREA'].includes(document.activeElement?.tagName);
    if (event.key === 'Escape') {
      if (modalStack.length > 0) modalStack.at(-1)();
      else if (App.lightbox) closeLightbox();
      else if (App.selection.size > 0) { App.selection.clear(); schedule(); }
      else if (!$('#behind').hidden) toggleBehind(false);
      return;
    }

    if (typingInField || modalStack.length > 0) return;
    if (event.key === '/') { event.preventDefault(); $('#search').focus(); return; }
    if (!App.lightbox) return;
    if (event.key === 'ArrowLeft') stepLightbox(-1);
    else if (event.key === 'ArrowRight') stepLightbox(1);
    else if (event.key.toLowerCase() === 'i') { App.lightbox.info = !App.lightbox.info; showLightbox(false); }
    else if (event.key === 'Delete' || event.key === 'Backspace') {
      const photo = photoOf(App.lightbox.ids[App.lightbox.index]);
      if (photo && !photo.trashedAt) lightboxAct(() => trashPhotos([photo]));
    }
  });

  window.addEventListener('popstate', () => { if (App.lightbox) closeLightbox(true); });
  window.addEventListener('hashchange', route);
  window.addEventListener('resize', () => { clearTimeout(wire.resize); wire.resize = setTimeout(buildScrubber, 150); });
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden && Library.state.loaded && Library.state.pending === 0 && Uploads.active() === 0 && Date.now() - Library.state.lastLoad > 15000) Library.load();
  });

  Library.watch(schedule);
  Uploads.watch(renderTray);
  Cms.watch(() => {
    if ($('#behind').hidden || behindQueued) return;
    behindQueued = true;
    setTimeout(() => { behindQueued = false; renderBehind(); }, 250);
  });
}

wire();
route();
loadLimits().then(() => Library.load());
