// Deliberate-defect check. For each entry in mutations.json it breaks one rule in the source, runs the service test
// suite, records which tests failed, and restores the file. A mutation that no test notices is a hole in the tests.
//
//   node tools/mutation-check/run.mjs            run every mutation
//   node tools/mutation-check/run.mjs M03 M07    run only these
//
// Results go to tools/mutation-check/last-run.json. It edits files in place (and always restores them, checking a hash),
// so do not edit the source while it runs.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..', '..');
const mutations = JSON.parse(readFileSync(join(here, 'mutations.json'), 'utf8'));
const only = new Set(process.argv.slice(2));
const sha = (text) => createHash('sha256').update(text).digest('hex');

function runTests() {
  const result = spawnSync(
    'dotnet', ['test', join(repo, 'tests', 'Lateral.Cms.Tests'), '-c', 'Release', '--nologo', '-v', 'minimal'],
    { cwd: repo, encoding: 'utf8', timeout: 6 * 60 * 1000, maxBuffer: 64 * 1024 * 1024, env: { ...process.env, DOTNET_ROLL_FORWARD: 'Major', CI: '' } });
  const output = `${result.stdout ?? ''}\n${result.stderr ?? ''}`;
  if (result.error?.code === 'ETIMEDOUT') return { outcome: 'killed', how: 'the run hung (timeout)', failed: [] };
  if (/error CS\d+/.test(output)) return { outcome: 'invalid', how: 'does not compile', failed: [] };
  const failed = [...output.matchAll(/^\s+Failed (Lateral\.Cms\.Tests\.\S+)/gm)].map((m) => m[1].replace(/\(.*$/, ''));
  const unique = [...new Set(failed)];
  if (unique.length > 0) return { outcome: 'killed', how: 'a test failed', failed: unique };
  return /Passed!/.test(output) ? { outcome: 'SURVIVED', how: 'all tests passed', failed: [] } : { outcome: 'killed', how: 'the run did not finish cleanly', failed: [] };
}

const results = [];
for (const m of mutations) {
  if (only.size > 0 && !only.has(m.id)) continue;
  const path = join(repo, m.file);
  const original = readFileSync(path, 'utf8');
  const crlf = original.includes('\r\n');
  const find = crlf ? m.find.replace(/\r?\n/g, '\r\n') : m.find;
  const replace = crlf ? m.replace.replace(/\r?\n/g, '\r\n') : m.replace;
  const occurrences = original.split(find).length - 1;
  let entry;
  if (occurrences !== 1) {
    entry = { ...m, outcome: 'invalid', how: `the text to break was found ${occurrences} times, expected 1`, failed: [] };
  } else {
    try {
      writeFileSync(path, original.replace(find, () => replace));
      entry = { ...m, ...runTests() };
    } finally {
      writeFileSync(path, original);
      if (sha(readFileSync(path, 'utf8')) !== sha(original)) throw new Error(`Could not restore ${m.file}`);
    }
  }
  results.push(entry);
  console.log(`${entry.id} ${entry.outcome.padEnd(9)} ${entry.rule} -> ${entry.failed.slice(0, 2).join(', ') || entry.how}`);
}

if (only.size === 0) {
  writeFileSync(join(here, 'last-run.json'), JSON.stringify(results, null, 2) + '\n');
} else {
  // A partial run replaces just its own entries in the recorded log.
  const lastRun = join(here, 'last-run.json');
  const previous = existsSync(lastRun) ? JSON.parse(readFileSync(lastRun, 'utf8')).filter((r) => !only.has(r.id)) : [];
  const merged = [...previous, ...results].sort((a, b) => a.id.localeCompare(b.id));
  writeFileSync(lastRun, JSON.stringify(merged, null, 2) + '\n');
}

const survived = results.filter((r) => r.outcome === 'SURVIVED' || r.outcome === 'invalid');
console.log(`\n${results.length - survived.length} of ${results.length} caught.${survived.length ? ' Not caught or invalid: ' + survived.map((r) => r.id).join(', ') : ''}`);
process.exit(survived.length === 0 ? 0 : 1);
