'use strict';

// Helpers for the photo library: building the page, reading a picture's own details, making smaller copies of it, and drawing
// sample pictures. Nothing here talks to the CMS. Text from the CMS or from a file is only ever shown as text, never as markup.

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

function el(tag, props = {}, ...kids) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else if (value === true) node.setAttribute(key, '');
    else if (value !== false && value != null) node.setAttribute(key, value);
  }
  for (const kid of kids.flat()) if (kid != null) node.append(kid);
  return node;
}

function fill(node, ...kids) {
  node.replaceChildren(...kids.flat().filter((kid) => kid != null));
}

// ---------------------------------------------------------------- icons (24px, Material-style paths)

const ICONS = {
  menu: 'M3 18h18v-2H3v2zm0-5h18v-2H3v2zm0-7v2h18V6H3z',
  search: 'M15.5 14h-.79l-.28-.27A6.47 6.47 0 0 0 16 9.5 6.5 6.5 0 1 0 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z',
  close: 'M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z',
  photo: 'M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z',
  star: 'M22 9.24l-7.19-.62L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21 12 17.27 18.18 21l-1.63-7.03L22 9.24zM12 15.4l-3.76 2.27 1-4.28-3.32-2.88 4.38-.38L12 6.1l1.71 4.04 4.38.38-3.32 2.88 1 4.28L12 15.4z',
  starFilled: 'M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z',
  album: 'M18 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zM6 4h5v8l-2.5-1.5L6 12V4zm0 15l3-3.86 2.14 2.58 3-3.86L18 19H6z',
  trash: 'M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z',
  download: 'M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z',
  upload: 'M9 16h6v-6h4l-7-7-7 7h4v6zm-4 2h14v2H5v-2z',
  info: 'M11 7h2v2h-2zm0 4h2v6h-2zm1-9C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z',
  check: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z',
  circle: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8z',
  back: 'M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z',
  left: 'M15.41 7.41L14 6l-6 6 6 6 1.41-1.41L10.83 12z',
  right: 'M10 6L8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z',
  add: 'M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z',
  restore: 'M13 3a9 9 0 0 0-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42A8.954 8.954 0 0 0 13 21a9 9 0 0 0 0-18zm-1 5v5l4.28 2.54.72-1.21-3.5-2.08V8H12z',
  shield: 'M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm-2 16l-4-4 1.41-1.41L10 14.17l6.59-6.59L18 9l-8 8z',
  code: 'M9.4 16.6L4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4zm5.2 0l4.6-4.6-4.6-4.6L16 6l6 6-6 6-1.4-1.4z',
  edit: 'M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z',
  expand: 'M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z',
  error: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z',
  cloud: 'M19.35 10.04A7.49 7.49 0 0 0 12 4C9.11 4 6.6 5.64 5.35 8.04A5.994 5.994 0 0 0 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96z',
  more: 'M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z',
};

function icon(name, size = 24) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  path.setAttribute('d', ICONS[name] ?? ICONS.photo);
  path.setAttribute('fill', 'currentColor');
  svg.append(path);
  return svg;
}

function iconButton(name, label, onclick, extra = '') {
  return el('button', { type: 'button', class: `icon-btn ${extra}`.trim(), title: label, 'aria-label': label, onclick }, icon(name));
}

// ---------------------------------------------------------------- formatting

const MiB = 1024 * 1024;
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

function fmtBytes(n) {
  if (!Number.isFinite(n)) return '';
  const names = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  let value = n;
  while (value >= 1000 && i < names.length - 1) { value /= 1000; i++; }
  return (i === 0 ? String(Math.round(value)) : value.toFixed(value >= 100 ? 0 : 1)) + ' ' + names[i];
}

const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
const dayKey = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const monthKey = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;

/** "Today", "Yesterday", "Mon, 3 Nov" or, in another year, "Fri, 7 Mar 2025". */
function dayLabel(d, now = new Date()) {
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  if (sameDay(d, now)) return 'Today';
  if (sameDay(d, yesterday)) return 'Yesterday';
  const text = `${DAYS[d.getDay()].slice(0, 3)}, ${d.getDate()} ${MONTHS[d.getMonth()].slice(0, 3)}`;
  return d.getFullYear() === now.getFullYear() ? text : `${text} ${d.getFullYear()}`;
}

const longDate = (d) => `${DAYS[d.getDay()]}, ${d.getDate()} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
const clock = (d) => d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
const monthTitle = (d) => `${MONTHS[d.getMonth()]} ${d.getFullYear()}`;

const rand = (n = 6) => Array.from(crypto.getRandomValues(new Uint8Array(n)), (b) => (b % 36).toString(36)).join('');

async function sha256Hex(buffer) {
  const digest = await crypto.subtle.digest('SHA-256', buffer);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
}

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// ---------------------------------------------------------------- what kind of picture is it

/** The picture type from the first bytes of the file (the browser's idea of the type is not trusted), or null. */
function sniffImageType(bytes) {
  const b = bytes;
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return 'image/jpeg';
  if (b.length >= 8 && b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47) return 'image/png';
  if (b.length >= 6 && b[0] === 0x47 && b[1] === 0x49 && b[2] === 0x46 && b[3] === 0x38) return 'image/gif';
  if (b.length >= 12 && b[0] === 0x52 && b[1] === 0x49 && b[2] === 0x46 && b[3] === 0x46 && b[8] === 0x57 && b[9] === 0x45 && b[10] === 0x42 && b[11] === 0x50) return 'image/webp';
  return null;
}

const EXTENSIONS = { 'image/jpeg': 'jpg', 'image/png': 'png', 'image/gif': 'gif', 'image/webp': 'webp' };

/** A file name the CMS accepts as an asset key (no slashes or control characters), keeping the extension. */
function safeName(name, type) {
  const cleaned = String(name || 'photo').replace(/[\\/\u0000-\u001f]/g, '_').trim().slice(0, 150) || 'photo';
  return /\.[A-Za-z0-9]{2,5}$/.test(cleaned) ? cleaned : `${cleaned}.${EXTENSIONS[type] ?? 'bin'}`;
}

// ---------------------------------------------------------------- EXIF: when it was taken, and with what

/** Reads the date taken and camera details from a JPEG's EXIF block. Anything missing or odd is left out. */
function readExif(buffer) {
  const out = {};
  try {
    const view = new DataView(buffer);
    if (view.byteLength < 12 || view.getUint16(0) !== 0xffd8) return out;
    let offset = 2;
    while (offset + 4 < view.byteLength) {
      const marker = view.getUint16(offset);
      if (marker === 0xffda || (marker & 0xff00) !== 0xff00) break;
      const length = view.getUint16(offset + 2);
      if (marker === 0xffe1 && view.getUint32(offset + 4) === 0x45786966) {
        parseTiff(view, offset + 10, out);
        break;
      }
      offset += 2 + length;
    }
  } catch { /* a damaged block just means no details */ }
  return out;
}

function parseTiff(view, base, out) {
  const little = view.getUint16(base) === 0x4949;
  const u16 = (o) => view.getUint16(base + o, little);
  const u32 = (o) => view.getUint32(base + o, little);
  const ascii = (o, n) => {
    let s = '';
    for (let i = 0; i < n; i++) {
      const c = view.getUint8(base + o + i);
      if (c === 0) break;
      s += String.fromCharCode(c);
    }
    return s.trim();
  };
  const rational = (o) => { const d = u32(o + 4); return d ? u32(o) / d : 0; };

  const walk = (ptr, handle) => {
    const count = u16(ptr);
    for (let i = 0; i < count && i < 200; i++) {
      const entry = ptr + 2 + i * 12;
      const type = u16(entry + 2);
      const n = u32(entry + 4);
      const size = ({ 1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 4, 10: 8 })[type] ?? 1;
      const at = size * n <= 4 ? entry + 8 : u32(entry + 8);
      handle(u16(entry), type, n, at);
    }
  };
  const parseDate = (text) => {
    const m = /^(\d{4}):(\d{2}):(\d{2}) (\d{2}):(\d{2}):(\d{2})/.exec(text);
    return m ? new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]) : null;
  };

  let exifPointer = 0;
  walk(u32(4), (tag, type, n, at) => {
    if (tag === 0x010f) out.make = ascii(at, n);
    else if (tag === 0x0110) out.model = ascii(at, n);
    else if (tag === 0x0132 && !out.takenAt) out.takenAt = parseDate(ascii(at, n));
    else if (tag === 0x8769) exifPointer = u32(at);
  });
  if (exifPointer) {
    walk(exifPointer, (tag, type, n, at) => {
      if (tag === 0x9003) out.takenAt = parseDate(ascii(at, n)) ?? out.takenAt;
      else if (tag === 0x829a) out.exposure = rational(at);
      else if (tag === 0x829d) out.fNumber = rational(at);
      else if (tag === 0x8827) out.iso = type === 3 ? u16(at) : u32(at);
      else if (tag === 0x920a) out.focal = rational(at);
    });
  }

  if (out.takenAt && Number.isNaN(out.takenAt.getTime())) delete out.takenAt;
  if (out.make && out.model?.startsWith(out.make)) out.model = out.model.slice(out.make.length).trim();
}

/** The camera line for the info panel, from the details kept with the photo. */
function cameraLine(camera) {
  if (!camera) return '';
  return [camera.make, camera.model].filter(Boolean).join(' ');
}

function settingsLine(camera) {
  if (!camera) return '';
  const parts = [];
  if (camera.fNumber) parts.push(`ƒ/${Number(camera.fNumber).toFixed(1).replace(/\.0$/, '')}`);
  if (camera.exposure) parts.push(camera.exposure >= 1 ? `${camera.exposure}s` : `1/${Math.round(1 / camera.exposure)}s`);
  if (camera.focal) parts.push(`${Math.round(camera.focal)} mm`);
  if (camera.iso) parts.push(`ISO ${camera.iso}`);
  return parts.join('  ·  ');
}

// ---------------------------------------------------------------- smaller copies of a picture

async function decodeImage(blob) {
  // from-image applies the photo's own rotation, so a portrait shot is stored upright.
  return createImageBitmap(blob, { imageOrientation: 'from-image' });
}

/** A JPEG copy whose longer side is at most longEdge pixels (never enlarged). Transparent areas become white. */
async function renderRendition(bitmap, longEdge, quality) {
  const scale = Math.min(1, longEdge / Math.max(bitmap.width, bitmap.height));
  const width = Math.max(1, Math.round(bitmap.width * scale));
  const height = Math.max(1, Math.round(bitmap.height * scale));
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext('2d');
  context.fillStyle = '#fff';
  context.fillRect(0, 0, width, height);
  context.imageSmoothingQuality = 'high';
  context.drawImage(bitmap, 0, 0, width, height);
  const blob = await new Promise((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('Could not make a smaller copy.'))), 'image/jpeg', quality));
  return { blob, width, height };
}

// ---------------------------------------------------------------- sample pictures, drawn here so nothing is downloaded

function mulberry32(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const SCENES = [
  { title: 'Sunrise over the ridge', sky: ['#1d2b53', '#c0587a', '#ffb46b', '#ffe3a3'], horizon: 0.64, sun: { x: 0.68, y: 0.58, r: 0.1, color: '255,236,190' }, ridges: ['#5d3f6a', '#3c2a55', '#1f1838'], water: false },
  { title: 'Alpine lake', sky: ['#4a7fc1', '#8fc0e8', '#dcefff'], horizon: 0.58, sun: { x: 0.2, y: 0.18, r: 0.07, color: '255,255,240' }, ridges: ['#9fb8cf', '#587f9c', '#2f4f5e'], water: true },
  { title: 'Dunes at dusk', sky: ['#2c1d4f', '#9b4a6b', '#f08a5d', '#ffd08a'], horizon: 0.7, sun: { x: 0.35, y: 0.66, r: 0.08, color: '255,214,160' }, ridges: ['#c0714d', '#9b4f3a', '#5c2d2b'], water: false, dunes: true },
  { title: 'Pine forest mist', sky: ['#6f8f8c', '#b6c9c3', '#e9efe9'], horizon: 0.6, sun: { x: 0.8, y: 0.25, r: 0.06, color: '255,255,250' }, ridges: ['#a9bdb5', '#6a8a82', '#2c4743'], water: false, trees: true },
  { title: 'City lights', sky: ['#0b1026', '#2a2f5e', '#7a5a8f', '#f0a07a'], horizon: 0.72, sun: null, ridges: ['#1a1f3d', '#12152b', '#0a0c1a'], water: true, stars: true, city: true },
  { title: 'Coastal cliffs', sky: ['#3d7bb8', '#8bbfe3', '#f4e3c8'], horizon: 0.55, sun: { x: 0.5, y: 0.2, r: 0.06, color: '255,250,225' }, ridges: ['#c9b79a', '#8a7a62', '#4a4234'], water: true },
  { title: 'Northern lights', sky: ['#050a1c', '#0d2b3f', '#1b5a55', '#2aa37a'], horizon: 0.74, sun: null, ridges: ['#10222b', '#0a161d', '#04090d'], water: true, stars: true, aurora: true },
  { title: 'Autumn valley', sky: ['#6b93c4', '#a7c5de', '#f6e7c8'], horizon: 0.6, sun: { x: 0.75, y: 0.3, r: 0.06, color: '255,246,214' }, ridges: ['#d99a4f', '#b3612f', '#6f3320'], water: false, trees: true },
  { title: 'Snowy peaks', sky: ['#5b86c6', '#a5c7ea', '#eaf4ff'], horizon: 0.66, sun: { x: 0.3, y: 0.22, r: 0.06, color: '255,255,245' }, ridges: ['#dfe9f4', '#a9bdd6', '#5b6f8c'], water: false },
  { title: 'Harbour at night', sky: ['#070a18', '#1b2145', '#3a3f7a'], horizon: 0.68, sun: { x: 0.8, y: 0.2, r: 0.04, color: '240,244,255' }, ridges: ['#161a33', '#0e1124', '#07091a'], water: true, stars: true, city: true },
  { title: 'Lavender fields', sky: ['#7aa0d6', '#b7cdee', '#f6dccb'], horizon: 0.5, sun: { x: 0.6, y: 0.2, r: 0.06, color: '255,246,230' }, ridges: ['#8f78b8', '#6f58a0', '#4a3a78'], water: false, rows: true },
  { title: 'Desert road', sky: ['#3d78c2', '#8dbbea', '#f5dfc0'], horizon: 0.55, sun: { x: 0.45, y: 0.3, r: 0.07, color: '255,246,218' }, ridges: ['#d5a46c', '#b87a4a', '#8a5636'], water: false, dunes: true },
];

const SAMPLE_SHAPES = [[3, 2], [4, 3], [16, 9], [3, 2], [1, 1], [3, 4], [3, 2], [21, 9], [4, 3], [2, 3]];

function ridgeY(x, rnd, amplitude, phases) {
  let y = 0;
  let freq = 1.2;
  let amp = 1;
  for (const phase of phases) { y += Math.sin(x * freq * Math.PI * 2 + phase) * amp; freq *= 2.1; amp *= 0.5; }
  return y * amplitude;
}

function drawScene(context, width, height, scene, rnd) {
  const horizon = height * scene.horizon;

  const sky = context.createLinearGradient(0, 0, 0, horizon);
  scene.sky.forEach((color, i) => sky.addColorStop(i / (scene.sky.length - 1), color));
  context.fillStyle = sky;
  context.fillRect(0, 0, width, height);

  if (scene.stars) {
    for (let i = 0; i < 420; i++) {
      context.fillStyle = `rgba(255,255,255,${(0.2 + rnd() * 0.8).toFixed(2)})`;
      const size = 1 + rnd() * 2.2 * (width / 1600);
      context.fillRect(rnd() * width, rnd() * horizon * 0.85, size, size);
    }
  }

  if (scene.aurora) {
    for (let band = 0; band < 3; band++) {
      const gradient = context.createLinearGradient(0, horizon * 0.1, 0, horizon * 0.8);
      gradient.addColorStop(0, 'rgba(60,255,170,0)');
      gradient.addColorStop(0.5, `rgba(${60 + band * 40},255,${170 - band * 30},0.35)`);
      gradient.addColorStop(1, 'rgba(60,255,170,0)');
      context.fillStyle = gradient;
      context.beginPath();
      context.moveTo(0, horizon * 0.8);
      const phase = rnd() * 6;
      for (let x = 0; x <= width; x += width / 60) {
        context.lineTo(x, horizon * (0.3 + band * 0.07) + Math.sin(x / width * 5 + phase) * horizon * 0.12);
      }
      context.lineTo(width, horizon * 0.8);
      context.closePath();
      context.fill();
    }
  }

  if (scene.sun) {
    const sx = width * scene.sun.x;
    const sy = height * scene.sun.y;
    const radius = height * scene.sun.r;
    const glow = context.createRadialGradient(sx, sy, radius * 0.2, sx, sy, radius * 6);
    glow.addColorStop(0, `rgba(${scene.sun.color},0.9)`);
    glow.addColorStop(0.25, `rgba(${scene.sun.color},0.25)`);
    glow.addColorStop(1, `rgba(${scene.sun.color},0)`);
    context.fillStyle = glow;
    context.fillRect(0, 0, width, height);
    context.fillStyle = `rgba(${scene.sun.color},1)`;
    context.beginPath();
    context.arc(sx, sy, radius, 0, Math.PI * 2);
    context.fill();
  }

  scene.ridges.forEach((color, layer) => {
    const base = horizon + height * layer * 0.045 - height * (scene.ridges.length - layer - 1) * 0.03;
    const phases = [rnd() * 6, rnd() * 6, rnd() * 6, rnd() * 6];
    const amplitude = height * (scene.dunes ? 0.035 : 0.06) * (1 + (scene.ridges.length - layer) * 0.45);
    context.fillStyle = color;
    context.beginPath();
    context.moveTo(0, height);
    for (let x = 0; x <= width; x += 6) {
      context.lineTo(x, base - Math.abs(ridgeY(x / width, rnd, amplitude, phases)) - (scene.dunes ? 0 : amplitude * 0.4));
    }
    context.lineTo(width, height);
    context.closePath();
    context.fill();

    const haze = context.createLinearGradient(0, base - amplitude * 2, 0, base + amplitude);
    haze.addColorStop(0, 'rgba(255,255,255,0)');
    haze.addColorStop(1, `rgba(${scene.sky.at(-1) === '#2aa37a' ? '40,120,100' : '255,240,225'},${layer === 0 ? 0.22 : 0.1})`);
    context.fillStyle = haze;
    context.fill();
  });

  if (scene.trees) {
    for (let i = 0; i < 90; i++) {
      const tx = rnd() * width;
      const th = height * (0.05 + rnd() * 0.09);
      const ty = height * (0.7 + rnd() * 0.25);
      context.fillStyle = scene.ridges.at(-1);
      context.beginPath();
      context.moveTo(tx, ty - th);
      context.lineTo(tx + th * 0.22, ty);
      context.lineTo(tx - th * 0.22, ty);
      context.closePath();
      context.fill();
    }
  }

  if (scene.rows) {
    for (let r = 0; r < 26; r++) {
      const t = r / 26;
      context.strokeStyle = `rgba(${90 + t * 80},${60 + t * 30},${150 + t * 40},${0.25 + t * 0.5})`;
      context.lineWidth = 1 + t * 7;
      context.beginPath();
      context.moveTo(width * 0.5, horizon + 4);
      context.lineTo(width * (-0.4 + (r / 25) * 1.8), height + 20);
      context.stroke();
    }
  }

  if (scene.city) {
    for (let i = 0; i < 140; i++) {
      const bx = rnd() * width;
      const bw = width * (0.012 + rnd() * 0.02);
      const bh = height * (0.03 + rnd() * 0.13);
      context.fillStyle = '#0b0d1c';
      context.fillRect(bx, horizon - bh + height * 0.05, bw, bh);
      for (let w = 0; w < 6; w++) {
        if (rnd() < 0.55) {
          context.fillStyle = `rgba(255,${200 + rnd() * 40},${120 + rnd() * 60},${0.5 + rnd() * 0.5})`;
          context.fillRect(bx + rnd() * (bw - 3), horizon - bh + height * 0.05 + rnd() * bh, 2.5, 3.5);
        }
      }
    }
  }

  if (scene.water) {
    const top = horizon + height * 0.03;
    const water = context.createLinearGradient(0, top, 0, height);
    water.addColorStop(0, 'rgba(20,40,70,0.55)');
    water.addColorStop(1, 'rgba(5,10,25,0.85)');
    context.fillStyle = water;
    context.fillRect(0, top, width, height - top);
    if (scene.sun) {
      for (let i = 0; i < 60; i++) {
        context.fillStyle = `rgba(${scene.sun.color},${(0.05 + rnd() * 0.25).toFixed(2)})`;
        const y = top + rnd() * (height - top);
        const spread = 10 + (y - top) * 0.5;
        context.fillRect(width * scene.sun.x - spread * 0.5 + (rnd() - 0.5) * spread, y, spread * (0.3 + rnd()), 1.5 + rnd() * 2);
      }
    }
  }

  // Film grain and a soft vignette, so it reads as a photograph rather than a flat drawing.
  for (let i = 0; i < 22000; i++) {
    context.fillStyle = `rgba(${rnd() < 0.5 ? '255,255,255' : '0,0,0'},${(rnd() * 0.07).toFixed(3)})`;
    context.fillRect(rnd() * width, rnd() * height, 1.4, 1.4);
  }
  const vignette = context.createRadialGradient(width / 2, height / 2, Math.min(width, height) * 0.45, width / 2, height / 2, Math.max(width, height) * 0.75);
  vignette.addColorStop(0, 'rgba(0,0,0,0)');
  vignette.addColorStop(1, 'rgba(0,0,0,0.38)');
  context.fillStyle = vignette;
  context.fillRect(0, 0, width, height);
}

/**
 * A plan for n sample photos spread over about two and a half years, in clusters (several on one day, like a trip),
 * the same every time so the library looks the same on every machine.
 */
function samplePlan(count, now = new Date()) {
  const rnd = mulberry32(20261106);
  const plan = [];
  let start = new Date(now.getFullYear(), now.getMonth(), now.getDate(), 12, 0, 0);
  start.setDate(start.getDate() - 1);
  while (plan.length < count) {
    // A trip or an outing: several pictures over a day or two, then a gap of weeks before the next one.
    const cluster = 4 + Math.floor(rnd() * 6);
    for (let k = 0; k < cluster && plan.length < count; k++) {
      const index = plan.length;
      const scene = SCENES[(index * 5 + Math.floor(rnd() * 3)) % SCENES.length];
      const shape = SAMPLE_SHAPES[Math.floor(rnd() * SAMPLE_SHAPES.length)];
      const taken = new Date(start);
      taken.setDate(taken.getDate() - Math.floor(rnd() * 2));
      taken.setHours(7 + Math.floor(rnd() * 13), Math.floor(rnd() * 60), Math.floor(rnd() * 60));
      plan.push({ scene, shape, seed: Math.floor(rnd() * 1e9), takenAt: taken, name: `IMG_${String(4000 + index * 7 + Math.floor(rnd() * 6)).padStart(4, '0')}.jpg` });
    }
    start = new Date(start);
    start.setDate(start.getDate() - (18 + Math.floor(rnd() * 80)));
  }
  return plan;
}

/** Draws one planned sample as a real JPEG file. */
async function makeSampleFile(item, longEdge = 2200) {
  const [a, b] = item.shape;
  const width = a >= b ? longEdge : Math.round(longEdge * a / b);
  const height = a >= b ? Math.round(longEdge * b / a) : longEdge;
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  drawScene(canvas.getContext('2d'), width, height, item.scene, mulberry32(item.seed));
  const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.9));
  return new File([blob], item.name, { type: 'image/jpeg', lastModified: item.takenAt.getTime() });
}
