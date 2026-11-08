'use strict';

// Testing: what the two test suites check and how the last run went. The data is test-results.json, written by
// `bash tools/testing/run.sh` from the suites' own result files; this page shows that recorded run, it does not run tests.
// Every test is listed under its class with a plain-English description, whether it passed, how long it took and, if it failed, why.

(() => {
  const st = { data: null, suite: 'service', outcome: 'all', area: '', text: '' };
  const ui = {};
  const tone = { passed: 'ok', failed: 'bad', skipped: 'warn' };
  const word = { passed: 'Passed', failed: 'Failed', skipped: 'Skipped' };
  const dur = (ms) => (ms >= 60000 ? `${Math.floor(ms / 60000)} m ${Math.round((ms % 60000) / 1000)} s` : ms >= 1000 ? `${(ms / 1000).toFixed(1)} s` : `${ms} ms`);

  const stat = (value, label, toneName = '', note = '') => el('div', { class: `stat ${toneName}` }, el('div', { class: 'value', text: String(value) }),
    el('div', { class: 'label', text: label }), note ? el('div', { class: 'hint', text: note }) : null);

  function summary() {
    const suites = st.data.suites;
    const sum = (k) => suites.reduce((n, s) => n + s[k], 0);
    const failed = sum('failed');
    const when = new Date(st.data.generatedAt);
    return [
      el('div', { class: `banner ${failed ? 'bad' : 'ok'}` },
        el('b', { text: failed ? `${failed} failing. ` : 'All passing. ' }),
        `Last recorded run: ${when.toLocaleString()} on ${st.data.machine.os || 'unknown OS'}, .NET SDK ${st.data.machine.dotnet || '?'}, commit ${st.data.commit || '?'}${st.data.dirty ? ' (with uncommitted changes)' : ''}.`),
      el('div', { class: 'grid four' },
        stat(sum('total'), 'Tests recorded', '', suites.map((s) => `${s.total} ${s.name.toLowerCase()}`).join(', ')),
        stat(sum('passed'), 'Passed', 'ok'),
        stat(failed, 'Failed', failed ? 'bad' : 'ok'),
        stat(sum('skipped'), 'Skipped', '', `${suites.map((s) => dur(s.wallMs)).join(' and ')} wall time`)),
      el('p', { class: 'hint' }, 'This page shows the recorded run. To refresh it: ', el('code', { text: 'bash tools/testing/run.sh' }),
        ' (runs both suites, then rewrites test-results.json). The strict build the CI runs is ', el('code', { text: 'CI=true dotnet build -c Release' }), '.'),
    ];
  }

  const suiteOf = () => st.data.suites.find((s) => s.id === st.suite);

  function matches(test, cls) {
    if (st.outcome !== 'all' && test.outcome !== st.outcome) return false;
    const q = st.text.trim().toLowerCase();
    return !q || `${test.name} ${test.description} ${cls.name} ${cls.summary} ${cls.requirements.join(' ')}`.toLowerCase().includes(q);
  }

  function testRow(test) {
    const detail = test.cases.length
      ? el('details', {}, el('summary', { text: `${test.cases.length} case${test.cases.length === 1 ? '' : 's'}` }),
        el('div', { class: 'col' }, test.cases.map((c) => el('div', { class: 'row wrap' },
          el('span', { class: `chip ${tone[c.outcome]}`, text: word[c.outcome] }), el('code', { text: c.args || '(no arguments)' }),
          c.error ? el('pre', { class: 'code', text: c.error }) : null))))
      : null;
    return el('tr', {},
      el('td', {}, el('span', { class: `chip ${tone[test.outcome]}`, text: word[test.outcome] })),
      el('td', {}, el('div', { text: test.description }), el('code', { class: 'hint', text: test.name }), detail,
        test.outcome === 'failed' && test.error ? el('pre', { class: 'code', text: test.error }) : null),
      el('td', { class: 'nowrap', text: dur(test.ms) }));
  }

  function classCard(cls) {
    const shown = cls.tests.filter((t) => matches(t, cls));
    if (!shown.length) return null;
    const failed = cls.tests.filter((t) => t.outcome === 'failed').length;
    return el('details', { class: 'card flat', open: failed > 0 || st.text.trim() !== '' },
      el('summary', {}, el('b', { text: cls.name }), ' ',
        el('span', { class: `chip ${failed ? 'bad' : 'ok'}`, text: failed ? `${failed} failed` : `${cls.tests.length} passed` }), ' ',
        el('span', { class: 'chip', text: cls.area }), ' ',
        cls.tests.length !== shown.length ? el('span', { class: 'hint', text: `showing ${shown.length} of ${cls.tests.length}` }) : null),
      cls.summary ? el('p', { class: 'hint', text: cls.summary }) : null,
      cls.requirements.length ? el('div', { class: 'row wrap' }, el('span', { class: 'hint', text: 'Proves' }), cls.requirements.map((id) => el('span', { class: 'chip info', text: id }))) : null,
      cls.file ? el('code', { class: 'hint', text: cls.file }) : null,
      el('table', {}, el('thead', {}, el('tr', {}, el('th', { text: 'Result' }), el('th', { text: 'What it checks' }), el('th', { text: 'Time' }))),
        el('tbody', {}, shown.map(testRow))));
  }

  function render() {
    const suite = suiteOf();
    const areas = [...new Set(suite.classes.map((c) => c.area))];
    const cards = suite.classes.filter((c) => !st.area || c.area === st.area).map(classCard).filter(Boolean);

    const suiteTabs = el('div', { class: 'tabs', role: 'tablist' }, st.data.suites.map((s) => el('button', {
      type: 'button', class: 'tab', role: 'tab', 'aria-selected': String(s.id === st.suite),
      onclick: () => { st.suite = s.id; st.area = ''; render(); },
      text: `${s.name} (${s.passed}/${s.total})`,
    })));
    const outcomes = el('div', { class: 'row wrap' }, ['all', 'passed', 'failed', 'skipped'].map((o) => el('button', {
      type: 'button', class: 'seg-btn', 'aria-pressed': String(st.outcome === o), onclick: () => { st.outcome = o; render(); },
      text: o === 'all' ? 'All' : word[o],
    })));
    const area = el('select', { 'aria-label': 'Area', onchange: (e) => { st.area = e.target.value; render(); } },
      el('option', { value: '', text: 'Every area' }), areas.map((a) => el('option', { value: a, text: a, selected: a === st.area })));
    const search = el('input', { type: 'search', class: 'mono', placeholder: 'Find a test, a class, a word or a requirement id…', value: st.text, 'aria-label': 'Find a test' });
    search.addEventListener('input', () => { st.text = search.value; clearTimeout(st.timer); st.timer = setTimeout(() => { render(); document.querySelector('#view-testing input[type=search]')?.focus(); }, 200); });

    fill(ui.root, summary(), suiteTabs,
      el('p', { class: 'hint' }, suite.about, ' Run on its own: ', el('code', { text: suite.command })),
      el('div', { class: 'row wrap' }, outcomes, area, search),
      cards.length ? el('div', { class: 'col' }, cards) : el('p', { class: 'hint', text: 'No test matches that filter.' }));
  }

  async function build(container) {
    ui.root = el('div', { class: 'col' }, el('p', { class: 'hint', text: 'Loading the recorded test run…' }));
    container.append(ui.root);
    try {
      const response = await fetch('/test-results.json', { cache: 'no-store' });
      if (!response.ok) throw new Error(String(response.status));
      st.data = await response.json();
      render();
    } catch {
      fill(ui.root, el('div', { class: 'banner bad', text: 'No recorded test run yet. Run bash tools/testing/run.sh to create one.' }));
    }
  }

  Eng.register('testing', {
    title: 'Testing',
    subtitle: 'Every test, what it checks, and whether it passed in the last recorded run.',
    build,
  });
})();
