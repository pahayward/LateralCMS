// Drives a real Chrome through the website's sign-in and pages as admin or user and reports failed requests and console errors.
//   node tools/testing/browser-check.mjs http://localhost:5100 admin|user     (or http://localhost:8200/lateral behind a proxy)
// Needs Google Chrome at /usr/bin/google-chrome and the console's App_Data/console-credentials.json; never prints a password.
import { spawn } from 'node:child_process';
import { readFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [base, who] = [process.argv[2], process.argv[3] ?? 'admin'];
const creds = JSON.parse(readFileSync('/home/phil/LateralCMS/tools/Lateral.Cms.TestConsole/App_Data/console-credentials.json', 'utf8')).People.find((p) => p.Name === who);
const port = 9422;
const chrome = spawn('/usr/bin/google-chrome', ['--headless=new', '--no-sandbox', '--disable-gpu', `--remote-debugging-port=${port}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), 'chr-'))}`, 'about:blank'], { stdio: ['ignore','ignore','pipe'] });
chrome.stderr.on('data', (d) => { if (process.env.DEBUG) process.stderr.write(d); });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let target;
for (let i = 0; i < 50 && !target; i++) { try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === 'page'); } catch { await sleep(200); } }
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0; const pending = new Map(); const bad = []; const logs = []; const seen = new Set();
ws.onmessage = (m) => {
  const d = JSON.parse(m.data);
  if (d.id && pending.has(d.id)) { pending.get(d.id)(d.result); pending.delete(d.id); }
  if (d.method === 'Network.responseReceived') { const u = new URL(d.params.response.url); seen.add(u.pathname); if (d.params.response.status >= 400) bad.push(`${d.params.response.status} ${u.pathname}`); }
  if (d.method === 'Network.loadingFailed') bad.push(`FAILED ${d.params.errorText} ${d.params.requestId}`);
  if (d.method === 'Runtime.exceptionThrown') logs.push('EXC ' + (d.params.exceptionDetails.exception?.description ?? d.params.exceptionDetails.text).slice(0, 200));
  if (d.method === 'Runtime.consoleAPICalled' && d.params.type === 'error') logs.push('CONSOLE ' + d.params.args.map((a) => a.value ?? a.description).join(' ').slice(0, 200));
};
const send = (method, params = {}) => new Promise((r) => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evalJs = async (expr) => (await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result?.value;
await send('Network.enable'); await send('Runtime.enable'); await send('Page.enable');

const step = async (name, fn) => { const out = await fn(); console.log(name.padEnd(46), out); };
await send('Page.navigate', { url: base + '/' }); await sleep(2500);
await step('1 redirected to the sign-in page', () => evalJs('location.pathname'));
await step('2 sign-in form shown', () => evalJs('!!document.getElementById("form")'));
await evalJs(`(() => { const f = document.getElementById('form'); f.username.value = ${JSON.stringify(creds.Name)}; f.password.value = ${JSON.stringify(creds.Password)}; f.requestSubmit(); })()`);
await sleep(4500);
await step('3 after sign-in, page', () => evalJs('location.pathname'));
await step('4 who is shown in the bar', () => evalJs('document.querySelector(".site-bar .who span")?.textContent'));
await step('5 photo library built (header present)', () => evalJs('!!document.querySelector("#app-header") && !!document.querySelector("#upload-btn")'));
await step('6 engineering link in the bar', () => evalJs('document.querySelector(\'.site-bar nav a[href$="/engineering"]\')?.getAttribute("href") ?? "(hidden)"'));
if (who === 'admin') {
  await send('Page.navigate', { url: base + '/engineering' }); await sleep(4500);
  await step('7 engineering console built', () => evalJs('document.querySelectorAll("#nav .nav-item").length + " nav items; title: " + document.getElementById("eng-title")?.textContent'));
  await step('8 health check reached the CMS', () => evalJs('document.getElementById("health-text")?.textContent'));
  await evalJs('document.querySelector(\'[data-view="testing"]\')?.click()'); await sleep(2500);
  await step('9 Testing page loaded results', () => evalJs('document.querySelector("#view-testing .stat .value")?.textContent + " tests recorded"'));
  await evalJs('document.querySelector(\'[data-view="requirements"]\')?.click()'); await sleep(2000);
  await step('10 Requirements opens with original brief', () => evalJs('document.querySelector("#view-requirements details summary b")?.textContent'));
}
if (who === 'user') {
  await send('Page.navigate', { url: base + '/engineering' }); await sleep(2500);
  await step('7 user sent away from engineering to', () => evalJs('location.pathname'));
  await send('Page.navigate', { url: base + '/' }); await sleep(3500);
  // Behave like a plain-http LAN page: no crypto.subtle. Then upload a real JPEG through the file input.
  await evalJs('Object.defineProperty(crypto, "subtle", { value: undefined, configurable: true }); typeof crypto.subtle');
  await step('8 crypto.subtle removed (simulates http LAN)', () => evalJs('String(crypto.subtle)'));
  await evalJs(`(async () => { const c = document.createElement('canvas'); c.width = 320; c.height = 200; const g = c.getContext('2d'); g.fillStyle = '#4a7'; g.fillRect(0,0,320,200); g.fillStyle='#fff'; g.fillText('prefix test ' + Date.now(), 20, 100);
    const blob = await new Promise((r) => c.toBlob(r, 'image/jpeg', 0.9)); const dt = new DataTransfer(); dt.items.add(new File([blob], 'prefix-test.jpg', { type: 'image/jpeg' }));
    const input = document.getElementById('file-input'); input.files = dt.files; input.dispatchEvent(new Event('change', { bubbles: true })); })()`);
  await sleep(9000);
  await step('9 photo tiles after upload', () => evalJs('document.querySelectorAll(".tile").length'));
  await step('10 any error toast / banner', () => evalJs('[...document.querySelectorAll(".toast,.banner,.error")].map((e) => e.textContent.trim()).join(" | ") || "(none)"'));
  await step('11 cms calls made (paths seen)', () => Promise.resolve([...seen].filter((p) => p.includes('/api/cms/') || p.includes('/media/')).slice(0, 6).join(', ')));
}
const outside = [...seen].filter((p) => !p.startsWith(new URL(base).pathname) && !p.startsWith('/lateral'));
console.log('requests outside the prefix:', outside.length ? outside : 'none');
console.log('failed/4xx requests:', bad.length ? bad : 'none');
console.log('console errors:', logs.length ? logs : 'none');
ws.close(); chrome.kill();
