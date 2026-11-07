// Turns docs/REQUIREMENTS.md (the matrix, kept true by DeliveryTests) into the data the engineering console's Requirements page
// reads: tools/Lateral.Cms.TestConsole/wwwroot/requirements.json. Each row keeps its area, text, priority, evidence codes, limit,
// status, mutations and tests, and is marked as coming from the original brief's matrix (section 5 of REBUILD-BRIEF.md) or as
// added later.
//
//   node tools/requirements/build.mjs
//
// Run it after changing docs/REQUIREMENTS.md, and after UPDATE_REQUIREMENTS=1 regenerates its Tests column. A service test
// (RequirementsTraceTests) fails if the JSON no longer matches the document.

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (...p) => readFileSync(join(root, ...p), 'utf8').replace(/\r\n/g, '\n');
const requirements = read('docs', 'REQUIREMENTS.md');
const brief = read('docs', 'REBUILD-BRIEF.md');

// The ids the brief's own matrix (section 5) lists: those requirements came with the brief; the rest were added later.
const start = brief.indexOf('\n## 5. Requirements matrix');
const end = brief.indexOf('\n## 6.', start);
const fromBrief = new Set(brief.slice(start, end).match(/\b[A-Z]{2}-\d{2}\b/g) ?? []);

const kindOf = (status) => (/^Done/.test(status) ? 'done' : /^(Open|Not done)/.test(status) ? 'open' : 'partly');

const rows = [];
const areas = [];
let area = '';
for (const line of requirements.split('\n')) {
  const heading = /^### (.+)$/.exec(line);
  if (heading) { area = heading[1].trim(); if (!areas.includes(area)) areas.push(area); continue; }
  // | ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests |   (the requirement text is the only cell that may hold a pipe)
  const m = /^\| ([A-Z]{2}-\d{2}) \| (.*?) \| ((?:MUST|SHOULD|COULD)(?: \/ (?:MUST|SHOULD|COULD))?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|$/.exec(line);
  if (!m) continue;
  const [, id, text, pri, evidence, limit, status, mutations, tests] = m;
  rows.push({
    id,
    area,
    text: text.trim(),
    pri: pri.split(' ')[0],
    priNote: pri.includes('/') ? pri : '',
    evidence: evidence.trim() === '-' ? [] : evidence.trim().replace(/\s*\(.*\)$/, '').split(/\s+/).filter((c) => /^[A-Z]$/.test(c)),
    limit: limit.trim() === '-' ? '' : limit.trim(),
    status: status.trim(),
    statusKind: kindOf(status.trim()),
    mutations: mutations.trim() === '-' ? [] : mutations.trim().split(/\s+/),
    tests: tests.trim() === '-' ? [] : [...tests.matchAll(/`([^`]+)`(?: \((\d+)\))?/g)].map((t) => ({ name: t[1], count: t[2] ? Number(t[2]) : null })),
    origin: fromBrief.has(id) ? 'brief' : 'added',
  });
}

if (rows.length < 100) throw new Error(`Only ${rows.length} requirement rows were read: has the table format changed?`);
const out = join(root, 'tools', 'Lateral.Cms.TestConsole', 'wwwroot', 'requirements.json');
writeFileSync(out, JSON.stringify({ about: 'Generated from docs/REQUIREMENTS.md by tools/requirements/build.mjs. Do not edit by hand.', areas, rows }, null, 2) + '\n');
const count = (f) => rows.filter(f).length;
console.log(`${rows.length} requirements (${count((r) => r.origin === 'brief')} from the brief's matrix, ${count((r) => r.origin === 'added')} added); ` +
  `${count((r) => r.statusKind === 'done')} done, ${count((r) => r.statusKind === 'partly')} partly, ${count((r) => r.statusKind === 'open')} open`);
