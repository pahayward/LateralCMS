'use strict';

// Requirements: the original brief, item by item, with how each item was met and the proof; every requirement the project tracks,
// filterable; and what was asked for afterwards. The brief's words and the answers are in brief-trace.json (hand-written, kept
// honest by a test); the rows, tests and statuses are generated from docs/REQUIREMENTS.md into requirements.json.

(() => {
  const st = { tab: 'brief', status: 'all', trace: null, reqs: null, byId: new Map(), filter: { text: '', area: '', pri: '', kind: '', origin: '' } };
  const ui = {};

  const EVIDENCE = {
    S: 'static analysis (warnings are errors)', U: 'unit test', P: 'property test (permutations, oracle)', I: 'integration test (real HTTP and SQLite, rows checked with raw SQL)',
    F: 'fault injection (a real fault, no test hook)', C: 'concurrency test', Q: 'query counts and plans', A: 'authorisation matrix', L: 'log content checked',
    V: 'golden or contract test', M: 'mutation: a deliberate defect was caught', B: 'benchmark', O: 'operated (CI, README run)', D: 'documented',
  };
  const TABS = [['brief', 'The brief, item by item'], ['all', 'Every requirement tracked'], ['beyond', 'Beyond the brief']];
  const tone = { met: 'ok', partly: 'warn', open: 'bad', done: 'ok' };
  const word = { met: 'Met', partly: 'Partly met', open: 'Not met yet', done: 'Done' };

  const row = (id) => st.byId.get(id);
  const unique = (list) => [...new Set(list)];

  /** What proves an item: the rows it points at, their test classes and their caught mutations. */
  function proof(item) {
    const rows = (item.reqs ?? []).map(row).filter(Boolean);
    const tests = new Map();
    for (const r of rows) for (const t of r.tests) tests.set(t.name, (tests.get(t.name) ?? 0) + (t.count ?? 0));
    return { rows, tests, mutations: unique(rows.flatMap((r) => r.mutations)) };
  }

  // ---------------------------------------------------------------- the summary

  function summary() {
    const items = st.trace.sections.flatMap((s) => s.items);
    const count = (s) => items.filter((i) => i.status === s).length;
    const rows = st.reqs.rows;
    const done = rows.filter((r) => r.statusKind === 'done').length;
    const must = rows.filter((r) => r.pri === 'MUST');
    const stat = (value, label, toneName = '', note = '') => el('div', { class: `stat ${toneName}` }, el('div', { class: 'value', text: value }), el('div', { class: 'label', text: label }), note ? el('div', { class: 'hint', text: note }) : null);
    const everyTest = unique(rows.flatMap((r) => r.tests.map((t) => t.name))).length;
    const everyMutation = unique(rows.flatMap((r) => r.mutations)).length;
    return el('div', { class: 'grid four' },
      stat(`${count('met')} of ${items.length}`, 'Original brief items met', count('met') === items.length ? 'ok' : '', `${count('partly')} partly met, ${count('open')} not met yet`),
      stat(`${done} of ${rows.length}`, 'Tracked requirements done', done === rows.length ? 'ok' : '', `${rows.length - done} partly or open, all listed below`),
      stat(`${must.filter((r) => r.statusKind === 'done').length} of ${must.length}`, 'MUST requirements done', '', 'every one needs a named test'),
      stat(`${everyTest} test classes`, 'Behind the requirements', '', `and ${everyMutation} deliberate defects each had to be caught`));
  }

  // ---------------------------------------------------------------- the brief table

  function chips(ids) {
    return el('div', { class: 'row wrap' }, ids.map((id) => el('button', { type: 'button', class: `chip ${row(id) ? tone[row(id).statusKind] : 'bad'}`, title: row(id)?.text ?? 'Not found', onclick: () => open(id), text: id })));
  }

  function itemRow(item, kind) {
    const p = proof(item);
    const where = item.where?.length ? el('details', {}, el('summary', { text: `Where in the code (${item.where.length})` }), el('div', { class: 'col' }, item.where.map((w) => el('code', { text: w })))) : null;
    const tests = p.tests.size ? el('details', {}, el('summary', { text: `${p.tests.size} test classes${p.mutations.length ? `, ${p.mutations.length} defects caught` : ''}` }),
      el('div', { class: 'row wrap' }, [...p.tests].map(([name, n]) => el('span', { class: 'chip', text: n ? `${name} (${n})` : name }))),
      p.mutations.length ? el('p', { class: 'hint', text: `Deliberate defects caught: ${p.mutations.join(', ')}` }) : null) : null;
    const live = item.live ? (item.live.href
      ? el('a', { class: 'btn small', href: item.live.href, text: item.live.label })
      : el('button', { type: 'button', class: 'btn small', text: item.live.label, onclick: () => Eng.show(item.live.view, item.live.params ?? '') })) : null;
    return el('tr', { id: `req-${item.id}` },
      el('td', { class: 'nowrap', text: item.id }),
      el('td', {}, el('blockquote', { class: 'brief-quote', text: kind === 'beyond' ? item.quote : `“${item.quote}”` })),
      el('td', {}, el('p', { text: item.how }), item.gap ? el('div', { class: `meaning ${tone[item.status]}` }, el('b', { text: 'Not complete: ' }), item.gap) : null, where),
      el('td', {}, item.reqs?.length ? chips(item.reqs) : el('span', { class: 'hint', text: 'no matrix row' }), tests, live),
      el('td', {}, el('span', { class: `chip ${tone[item.status]}`, text: word[item.status] })));
  }

  function table(items, kind, head = 'The brief says') {
    return el('div', { class: 'table-wrap tall' }, el('table', { class: 'trace' },
      el('thead', {}, el('tr', {}, ['#', head, 'How we met it', 'Proof', 'Status'].map((h) => el('th', { text: h })))),
      el('tbody', {}, items.map((i) => itemRow(i, kind)))));
  }

  function briefTab() {
    const chipsFor = [['all', 'All'], ['met', 'Met'], ['partly', 'Partly met'], ['open', 'Not met yet']];
    const bar = el('div', { class: 'row wrap' }, el('span', { class: 'hint', text: 'Show' }), chipsFor.map(([key, label]) => el('button', {
      type: 'button', class: 'seg-btn', 'aria-pressed': String(st.status === key), text: label, onclick: () => { st.status = key; draw(); },
    })));
    return el('div', { class: 'col' },
      el('p', { class: 'hint', text: 'The left-hand column is the brief as the user pasted it, word for word. The matrix rows in the Proof column are the same requirements broken down and traced to tests; click one to see it.' }),
      bar,
      st.trace.sections.map((section) => {
        const items = section.items.filter((i) => st.status === 'all' || i.status === st.status);
        if (items.length === 0) return null;
        return el('div', { class: 'card' }, el('div', { class: 'card-head' }, el('h2', { text: section.title }), el('span', { class: 'hint', text: section.blurb })), table(items, 'brief'));
      }));
  }

  // ---------------------------------------------------------------- every requirement

  function filtered() {
    const f = st.filter;
    const text = f.text.trim().toLowerCase();
    return st.reqs.rows.filter((r) => (!f.area || r.area === f.area) && (!f.pri || r.pri === f.pri) && (!f.kind || r.statusKind === f.kind) && (!f.origin || r.origin === f.origin)
      && (!text || `${r.id} ${r.text} ${r.status} ${r.limit} ${r.mutations.join(' ')} ${r.tests.map((t) => t.name).join(' ')}`.toLowerCase().includes(text)));
  }

  function allTab() {
    const select = (key, options, label) => {
      const input = el('select', { 'aria-label': label }, options.map(([v, l]) => el('option', { value: v, text: l, selected: st.filter[key] === v })));
      input.addEventListener('change', () => { st.filter[key] = input.value; drawRows(); });
      return input;
    };
    const search = el('input', { type: 'search', class: 'mono', placeholder: 'Find an id, a word, a test or a mutation…', value: st.filter.text, 'aria-label': 'Find a requirement' });
    search.addEventListener('input', () => { st.filter.text = search.value; drawRows(); });
    ui.count = el('span', { class: 'hint' });
    ui.rows = el('tbody');
    const legend = el('details', {}, el('summary', { text: 'What the evidence letters mean' }), el('dl', { class: 'kv' }, Object.entries(EVIDENCE).flatMap(([k, v]) => [el('dt', { text: k }), el('dd', { text: v })])));
    ui.search = search;
    const wrap = el('div', { class: 'col' },
      el('div', { class: 'card' },
        el('div', { class: 'row wrap' }, search,
          select('area', [['', 'Any area'], ...st.reqs.areas.map((a) => [a, a])], 'Area'),
          select('pri', [['', 'Any priority'], ['MUST', 'MUST'], ['SHOULD', 'SHOULD'], ['COULD', 'COULD']], 'Priority'),
          select('kind', [['', 'Any status'], ['done', 'Done'], ['partly', 'Partly'], ['open', 'Open']], 'Status'),
          select('origin', [['', 'Brief or added'], ['brief', 'In the brief\'s matrix'], ['added', 'Added later']], 'Origin'), ui.count),
        legend,
        el('div', { class: 'table-wrap tall' }, el('table', { class: 'reqs' },
          el('thead', {}, el('tr', {}, ['ID', 'Requirement', 'Pri', 'Evidence', 'Tests', 'Status'].map((h) => el('th', { text: h })))), ui.rows))));
    drawRows();
    return wrap;
  }

  function drawRows() {
    const rows = filtered();
    ui.count.textContent = `${rows.length} of ${st.reqs.rows.length}`;
    fill(ui.rows, rows.map((r) => el('tr', { id: `row-${r.id}` },
      el('td', { class: 'nowrap' }, el('b', { text: r.id }), r.origin === 'added' ? el('div', { class: 'hint', text: 'added later' }) : null),
      el('td', {}, el('p', { text: r.text.replace(/`/g, '') }), r.limit ? el('button', { type: 'button', class: 'chip warn', title: 'A known limit bounds this requirement: open the limits', onclick: () => Eng.show('design', 'limits'), text: `limit ${r.limit}` }) : null,
        r.priNote ? el('span', { class: 'hint', text: ` priority: ${r.priNote}` }) : null),
      el('td', { text: r.pri }),
      el('td', {}, el('div', { class: 'row wrap' }, r.evidence.map((c) => el('span', { class: 'chip', title: EVIDENCE[c] ?? c, text: c })))),
      el('td', {}, r.tests.length ? el('div', { class: 'col' }, r.tests.map((t) => el('span', { class: 'chip', text: t.count ? `${t.name} (${t.count})` : t.name })), r.mutations.length ? el('span', { class: 'hint', text: `defects caught: ${r.mutations.join(' ')}` }) : null) : el('span', { class: 'hint', text: r.mutations.length ? `defects caught: ${r.mutations.join(' ')}` : 'no test class (see status)' })),
      el('td', {}, el('span', { class: `chip ${tone[r.statusKind]}`, text: r.statusKind === 'done' ? 'Done' : r.statusKind === 'open' ? 'Open' : 'Partly' }), r.status !== 'Done' ? el('p', { class: 'hint', text: r.status.replace(/^Done;?\s*/, '') }) : null))));
  }

  function open(id) {
    st.tab = 'all';
    st.filter = { text: id, area: '', pri: '', kind: '', origin: '' };
    draw();
    setTimeout(() => $(`#row-${id}`, ui.root)?.scrollIntoView({ block: 'center' }), 50);
  }

  // ---------------------------------------------------------------- the page

  /** The assignment exactly as it was sent, above everything that interprets it. */
  function originalBrief() {
    return el('details', { class: 'card', open: true },
      el('summary', {}, el('b', { text: 'The original requirements, word for word' }), ' ',
        el('span', { class: 'hint', text: 'as sent by the recruiter; everything below answers this' })),
      el('p', { class: 'hint' }, 'A printout of the email is in the repository: ',
        el('a', { href: 'https://github.com/pahayward/LateralCMS/blob/main/docs/Lateral.pdf', target: '_blank', rel: 'noopener', text: 'docs/Lateral.pdf' }),
        '. Its sample JSON is not valid JSON (typographic quotes, a trailing comma); it is reproduced here as given.'),
      el('pre', { class: 'code brief-original', text: st.reqs.original }));
  }

  function draw() {
    const tabs = el('div', { class: 'tabs', role: 'tablist' }, TABS.map(([key, label]) => el('button', {
      type: 'button', class: 'tab', role: 'tab', 'aria-selected': String(st.tab === key), text: label, onclick: () => { st.tab = key; draw(); },
    })));
    const body = st.tab === 'brief' ? briefTab()
      : st.tab === 'all' ? allTab()
        : el('div', { class: 'col' }, el('p', { class: 'hint', text: 'Things asked for after the brief, in the order they came. The left-hand column is what was asked for, as recorded in the brief or in the user\'s own words.' }),
          el('div', { class: 'card' }, el('div', { class: 'card-head' }, el('h2', { text: st.trace.beyond.title }), el('span', { class: 'hint', text: st.trace.beyond.blurb })), table(st.trace.beyond.items, 'beyond', 'What was asked for')));
    fill(ui.root, summary(), originalBrief(), tabs, body);
  }

  function build(container) {
    ui.root = container;
    fill(container, el('p', { class: 'hint pad', text: 'Loading the requirements…' }));
    Promise.all([fetch('/brief-trace.json').then((r) => r.json()), fetch('/requirements.json').then((r) => r.json())]).then(([trace, reqs]) => {
      st.trace = trace;
      st.reqs = reqs;
      st.byId = new Map(reqs.rows.map((r) => [r.id, r]));
      draw();
      if (st.pending) { const p = st.pending; st.pending = null; route(p); }
    }).catch((error) => fill(container, el('div', { class: 'banner bad', text: `Could not load the requirements: ${error.message}` })));
  }

  function route(params) {
    if (!st.trace) { st.pending = params; return; }
    if (params?.startsWith('req/')) open(decodeURIComponent(params.slice(4)));
    else if (TABS.some(([k]) => k === params)) { st.tab = params; draw(); }
  }

  Eng.register('requirements', {
    title: 'Requirements',
    subtitle: 'What was asked for, how each item was met, and the proof: the original brief word for word beside the answer, the tests and the live checks.',
    build,
    show: route,
  });
})();
