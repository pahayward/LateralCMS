// Takes the screenshots in docs/images with a real browser, so the README can be remade whenever the console changes.
//
//   node tools/screenshots/capture.mjs [console-url]        default http://localhost:5191
//   node tools/screenshots/capture.mjs http://localhost:5191 overview people   only those shots
//
// The Swagger shots are of the service itself, not the console, and its page asks for a password. Give SERVICE_URL, the address of
// a service the browser can reach already signed in (for example a small proxy that adds the Authorization header), or they are skipped.
//
// Start the console with Console__SignIn__Enabled=false for this (the shots do not sign in). Needs the demo CMS and console running (see the README) and Chrome or Edge installed. It drives the browser over the
// DevTools protocol (no packages): load a page, optionally run a little script in it (open a record, run the tour), wait, and
// save a PNG. The scripts run in the page exactly as a person's clicks would.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = resolve(here, '..', '..', 'docs', 'images');
const base = (process.argv[2] ?? 'http://localhost:5191').replace(/\/$/, '');
const only = new Set(process.argv.slice(3));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/google-chrome', '/usr/bin/chromium', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
].find(existsSync);
if (!browser) throw new Error('Chrome or Edge was not found.');

// ---- what to photograph ---------------------------------------------------------------------------------------------
// run: a script run in the page after it loads. wait: milliseconds to let the page settle after it.
// Clicks look buttons up by their text, the way a person would.
const click = (text) => `[...document.querySelectorAll('button,a,[role=tab]')].find((b) => b.textContent.trim().startsWith(${JSON.stringify(text)}))?.click()`;
const asAdmin = (path) => `fetch('/api/cms/${path}', { headers: { 'X-Console-Request': '1', 'X-Console-Act-As': 'admin' } }).then((r) => r.json())`;

const shots = [
  // The photo library, the front page of the console.
  { name: 'photos-timeline', url: '/', width: 1400, height: 900, wait: 3500 },
  { name: 'photos-albums', url: '/#/albums', width: 1400, height: 520, wait: 3500 },
  { name: 'photos-favourites', url: '/#/favourites', width: 1400, height: 900, wait: 3500 },
  { name: 'photos-photo', url: '/', width: 1400, height: 900, wait: 3000, run: `document.querySelector('.tile')?.click()`, after: 2500 },

  // The engineering console.
  { name: 'tour', url: '/engineering#/overview', width: 1400, height: 1000, wait: 2000, run: click('Run the whole tour'), after: 25000, scrollTop: 780 },
  { name: 'architecture', url: '/engineering#/design', width: 1400, height: 1150, wait: 2000, run: click('Architecture'), after: 2500 },
  { name: 'requirements', url: '/engineering#/requirements', width: 1400, height: 1500, wait: 2500 },
  { name: 'record', url: '/engineering#/records', width: 1400, height: 1000, wait: 2500,
    run: `${asAdmin('api/entities?limit=100')}.then((l) => { location.hash = '#/records/' + encodeURIComponent((l.items.find((i) => i.id.startsWith('photo-')) ?? l.items[0]).id); })`, after: 3500 },
  { name: 'assets', url: '/engineering#/assets', width: 1400, height: 1300, wait: 3000 },
  { name: 'events', url: '/engineering#/events', width: 1400, height: 1400, wait: 2500 },
  { name: 'people', url: '/engineering#/accounts', width: 1400, height: 1700, wait: 3000 },
  { name: 'settings', url: '/engineering#/settings', width: 1400, height: 1400, wait: 3000 },
  { name: 'storage', url: '/engineering#/settings/storage', width: 1400, height: 1300, wait: 3000 },
  { name: 'home-server', url: '/engineering#/settings/home', width: 1400, height: 1600, wait: 2500 },
  // The API reference, served by the service itself.
  { name: 'swagger', service: true, url: '/swagger', width: 1400, height: 1000, wait: 3500 },
  { name: 'swagger-events', service: true, url: '/swagger', width: 1400, height: 1500, wait: 3000,
    run: `[...document.querySelectorAll('.opblock')].find((b) => b.textContent.includes('Send a batch of CMS events'))?.querySelector('.opblock-summary-control')?.click()`, after: 1500 },
  { name: 'security', url: '/engineering#/verify', width: 1400, height: 1000, wait: 2500, run: click('Run the matrix'), after: 20000 },
];

// ---- the browser ----------------------------------------------------------------------------------------------------
const profile = mkdtempSync(join(tmpdir(), 'lateral-shots-'));
const port = 9300 + Math.floor(Math.random() * 500);
const child = spawn(browser, [
  '--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--hide-scrollbars', '--no-first-run',
  '--disable-gpu', '--force-device-scale-factor=1', 'about:blank',
], { stdio: 'ignore' });

async function connect() {
  for (let i = 0; i < 60; i++) {
    try {
      const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const page = pages.find((p) => p.type === 'page');
      if (page) return page.webSocketDebuggerUrl;
    } catch { /* not up yet */ }
    await sleep(250);
  }
  throw new Error('The browser did not start.');
}

const socket = new WebSocket(await connect());
await new Promise((resolveOpen) => socket.addEventListener('open', resolveOpen, { once: true }));
let next = 0;
const waiting = new Map();
socket.addEventListener('message', (event) => {
  const message = JSON.parse(event.data);
  if (message.id && waiting.has(message.id)) {
    const { resolve: done, reject } = waiting.get(message.id);
    waiting.delete(message.id);
    if (message.error) reject(new Error(`${message.error.message}`)); else done(message.result);
  }
});
const send = (method, params = {}) => new Promise((done, reject) => {
  const id = ++next;
  waiting.set(id, { resolve: done, reject });
  socket.send(JSON.stringify({ id, method, params }));
});

mkdirSync(out, { recursive: true });
await send('Page.enable');
await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: process.env.SCHEME ?? 'light' }] });

let taken = 0;
try {
  for (const shot of shots) {
    if (only.size > 0 && !only.has(shot.name)) continue;
    if (shot.service && !process.env.SERVICE_URL) { console.log(`${shot.name}: skipped (set SERVICE_URL)`); continue; }
    await send('Emulation.setDeviceMetricsOverride', { width: shot.width, height: shot.height, deviceScaleFactor: 1, mobile: false });
    await send('Page.navigate', { url: (shot.service ? process.env.SERVICE_URL.replace(/\/$/, '') : base) + shot.url });
    await sleep(shot.wait ?? 2500);
    if (shot.run) {
      const result = await send('Runtime.evaluate', { expression: shot.run, awaitPromise: true, returnByValue: true });
      if (result.exceptionDetails) console.warn(`${shot.name}: ${result.exceptionDetails.text} ${result.exceptionDetails.exception?.description ?? ''}`);
      await sleep(shot.after ?? 1500);
    }

    if (shot.scrollTop !== undefined) {
      await send('Runtime.evaluate', { expression: `document.querySelector('#eng-main').scrollTop = ${shot.scrollTop}` });
      await sleep(600);
    }

    const { data } = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(join(out, `${shot.name}.png`), Buffer.from(data, 'base64'));
    taken++;
    console.log(`${shot.name}.png`);
  }
} finally {
  socket.close();
  child.kill();
  await sleep(500);
  try { rmSync(profile, { recursive: true, force: true }); } catch { /* the browser may still hold it; it is a temp folder */ }
}

console.log(`${taken} screenshot(s) in ${out}`);
