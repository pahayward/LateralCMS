// Prints the deliberate-defect log as a markdown table, from last-run.json, for docs/EVIDENCE.md.
//   node tools/mutation-check/report.mjs
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const results = JSON.parse(readFileSync(join(here, 'last-run.json'), 'utf8'));
const short = (name) => name.replace(/^Lateral\.Cms\.Tests\./, '').replace(/\./, ' › ');

console.log('| ID | Area | Deliberate defect | Result | A test that failed (first of N) |');
console.log('|---|---|---|---|---|');
for (const r of results) {
  const failed = r.failed.length === 0 ? r.how : `\`${short(r.failed[0])}\`${r.failed.length > 1 ? ` (+${r.failed.length - 1} more)` : ''}`;
  console.log(`| ${r.id} | ${r.area} | ${r.rule} | ${r.outcome} | ${failed} |`);
}
const caught = results.filter((r) => r.outcome === 'killed').length;
console.log(`\n${caught} of ${results.length} defects were caught.`);
