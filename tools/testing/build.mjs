// Turns the two test suites' .trx result files into the data the engineering console's Testing page reads:
// tools/Lateral.Cms.TestConsole/wwwroot/test-results.json. Each test keeps its class, a plain-English description (its name, spelled
// out), its outcome, how long it took and any failure message; each class keeps what its source file says it covers, the folder it
// lives in, and the requirement rows (docs/REQUIREMENTS.md) that cite it.
//
//   bash tools/testing/run.sh            run both suites with .trx output, then call this
//   node tools/testing/build.mjs <folder holding service.trx and console.trx>
//
// The page shows the last run recorded here; it does not run tests itself.

import { readFileSync, writeFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execSync } from 'node:child_process';
import os from 'node:os';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const folder = process.argv[2];
if (!folder) { console.error('usage: node tools/testing/build.mjs <folder with service.trx and console.trx>'); process.exit(2); }

const SUITES = [
  { id: 'service', name: 'Service tests', project: 'tests/Lateral.Cms.Tests', trx: 'service.trx', about: 'The CMS itself, through its real HTTP pipeline and a real SQLite file per test class: reconciliation rules, ingestion, the read API, assets and scanning, accounts and security, persistence, and the documentation checks.' },
  { id: 'console', name: 'Console tests', project: 'tests/Lateral.Cms.TestConsole.Tests', trx: 'console.trx', about: 'The test console that serves the photo library and the engineering pages: its sign-in, request guard, proxy to the CMS, media route, limits and the files it serves.' },
];

const unescape = (s) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
const seconds = (d) => { const m = /^(?:(\d+)\.)?(\d+):(\d+):([\d.]+)$/.exec(d ?? ''); return m ? (((+(m[1] ?? 0) * 24 + +m[2]) * 60 + +m[3]) * 60 + parseFloat(m[4])) : 0; };

// ---- source files: class summaries and areas
const sources = new Map(); // class name -> { summary, area, file }
const walk = (dir) => readdirSync(dir).flatMap((n) => n === 'bin' || n === 'obj' ? [] : statSync(join(dir, n)).isDirectory() ? walk(join(dir, n)) : n.endsWith('.cs') ? [join(dir, n)] : []);
for (const suite of SUITES) {
  const base = join(root, suite.project);
  for (const file of walk(base)) {
    const text = readFileSync(file, 'utf8').replace(/^﻿/, '').replace(/\r\n/g, '\n');
    const rel = relative(base, file).split(/[\\/]/);
    const area = rel.length > 1 ? rel[0] : suite.id === 'console' ? 'Console' : 'General';
    const re = /((?:[ \t]*\/\/\/.*\n)+)?[ \t]*(?:public |internal )?(?:sealed |static |partial |abstract )*class (\w+)/g;
    for (let m; (m = re.exec(text));) {
      const summary = (m[1] ?? '').replace(/^[ \t]*\/\/\/ ?/gm, '').replace(/<\/?summary>|<\/?para>/g, '').replace(/<(?:c|see)[^>]*?(?:cref="([^"]*)")?[^>]*>(.*?)<\/(?:c|see)>/g, '$2').replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim();
      if (!sources.has(m[2]) || summary) sources.set(m[2], { summary, area, file: relative(root, file).replace(/\\/g, '/') });
    }
  }
}

// ---- requirements that cite each class
const citedBy = new Map();
for (const line of readFileSync(join(root, 'docs', 'REQUIREMENTS.md'), 'utf8').split('\n')) {
  const id = /^\| ([A-Z]{2}-\d{2}) \|/.exec(line)?.[1];
  if (!id) continue;
  for (const [, name] of line.matchAll(/`(\w+Tests?)`/g)) { if (!citedBy.has(name)) citedBy.set(name, new Set()); citedBy.get(name).add(id); }
}

const sentence = (method) => {
  const s = method.replace(/_/g, ' ').replace(/\s+/g, ' ').trim();
  return s.charAt(0).toUpperCase() + s.slice(1) + '.';
};

const out = { generatedAt: new Date().toISOString(), commit: '', dirty: false, machine: { os: '', dotnet: '', node: process.version }, suites: [] };
try { out.commit = execSync('git rev-parse --short HEAD', { cwd: root }).toString().trim(); out.dirty = execSync('git status --porcelain', { cwd: root }).toString().trim().length > 0; } catch { /* not a checkout */ }
try { out.machine.os = execSync('. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME"', { shell: '/bin/bash' }).toString().trim() || `${os.type()} ${os.release()}`; } catch { out.machine.os = `${os.type()} ${os.release()}`; }
try { out.machine.dotnet = execSync('dotnet --version').toString().trim(); } catch { /* none */ }

for (const suite of SUITES) {
  const path = join(folder, suite.trx);
  if (!existsSync(path)) { console.error(`missing ${path}`); process.exit(2); }
  const xml = readFileSync(path, 'utf8');
  const classes = new Map();
  let wall = 0;
  const times = /<Times [^>]*start="([^"]+)"[^>]*finish="([^"]+)"/.exec(xml);
  if (times) wall = new Date(times[2]) - new Date(times[1]);
  for (const m of xml.matchAll(/<UnitTestResult\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
    const attr = (n) => new RegExp(`\\b${n}="([^"]*)"`).exec(m[1])?.[1];
    const full = unescape(attr('testName') ?? '');
    const paren = full.indexOf('(');
    const head = paren < 0 ? full : full.slice(0, paren);
    const args = paren < 0 ? '' : full.slice(paren);
    const dot = head.lastIndexOf('.');
    const method = head.slice(dot + 1);
    const className = head.slice(0, dot).split('.').pop();
    const outcome = attr('outcome') === 'Passed' ? 'passed' : attr('outcome') === 'Failed' ? 'failed' : 'skipped';
    const error = unescape(/<Message>([\s\S]*?)<\/Message>/.exec(m[2] ?? '')?.[1] ?? '').trim();
    const ms = Math.round(seconds(attr('duration')) * 1000);
    if (!classes.has(className)) {
      const src = sources.get(className);
      classes.set(className, { name: className, area: src?.area ?? 'General', file: src?.file ?? '', summary: src?.summary ?? '', requirements: [...(citedBy.get(className) ?? [])].sort(), tests: new Map() });
    }
    const group = classes.get(className);
    if (!group.tests.has(method)) group.tests.set(method, { name: method, description: sentence(method), outcome: 'passed', ms: 0, cases: [] });
    const test = group.tests.get(method);
    test.ms += ms;
    if (outcome === 'failed' || (outcome === 'skipped' && test.outcome === 'passed')) test.outcome = outcome;
    if (args || outcome !== 'passed') test.cases.push({ args, outcome, ms, error: error.slice(0, 1200) });
    if (outcome === 'failed') test.error = error.slice(0, 1200);
  }
  const list = [...classes.values()].sort((a, b) => a.area.localeCompare(b.area) || a.name.localeCompare(b.name)).map((c) => ({ ...c, tests: [...c.tests.values()].sort((a, b) => a.name.localeCompare(b.name)) }));
  const flat = list.flatMap((c) => c.tests.flatMap((t) => t.cases.length && t.cases[0].args ? t.cases : [{ outcome: t.outcome }]));
  const count = (o) => flat.filter((t) => t.outcome === o).length;
  out.suites.push({
    id: suite.id, name: suite.name, project: suite.project, about: suite.about,
    command: `dotnet test ${suite.project}`,
    total: flat.length, passed: count('passed'), failed: count('failed'), skipped: count('skipped'), wallMs: wall,
    classes: list,
  });
  console.log(`${suite.name}: ${count('passed')} passed, ${count('failed')} failed, ${count('skipped')} skipped, ${list.length} classes`);
}

writeFileSync(join(root, 'tools', 'Lateral.Cms.TestConsole', 'wwwroot', 'test-results.json'), JSON.stringify(out, null, 1) + '\n');
