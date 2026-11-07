'use strict';

// Design and architecture: the talking-point page. The diagrams are real files (also used by the documentation) with clickable
// boxes laid over them, and a walkthrough that lights the boxes one at a time with the words to say. The prose repeats
// docs/DESIGN.md in short; where they differ, DESIGN.md is right and this is a bug.

(() => {
  const ui = { section: 'idea', pages: {} };
  const SECTIONS = [
    ['idea', 'The idea'],
    ['architecture', 'Architecture'],
    ['event', 'Life of an event'],
    ['asset', 'Life of an asset'],
    ['rules', 'Reconciliation rules'],
    ['guarantees', 'Guarantees'],
    ['security', 'Security model'],
    ['limits', 'Limits and production'],
    ['talk', 'Talking points'],
    ['glossary', 'Glossary'],
  ];

  const laneName = { ingest: 'events in', asset: 'files', read: 'reads', admin: 'admin', store: 'storage', bad: 'rejected' };
  const laneTone = { ingest: 'info', asset: 'ok', read: 'warn', admin: 'info', store: '', bad: 'bad' };

  // ---------------------------------------------------------------- a diagram with clickable shapes and a walkthrough

  /** Lays hotspots over a diagram image, explains the clicked one, and runs the walkthroughs. */
  function diagram(name, { intro } = {}) {
    const holder = el('div', { class: 'col' });
    const panel = el('div', { class: 'card node-panel' }, el('p', { class: 'hint', text: 'Click any box in the diagram, or start a walkthrough.' }));
    const image = el('img', { src: `/diagrams/${name}.svg`, alt: '' });
    const frame = el('div', { class: 'diagram' }, image);
    const walkBox = el('div', { class: 'card walk' });
    let doc = null;
    let hotspots = new Map();
    let walk = null;
    let stepIndex = 0;

    const show = (node) => {
      hotspots.forEach((button, id) => button.setAttribute('aria-pressed', String(id === node.id)));
      fill(panel,
        el('div', { class: 'row' }, el('h2', { class: 'grow', text: node.title }), node.lane ? el('span', { class: `chip ${laneTone[node.lane] ?? ''}`, text: laneName[node.lane] ?? node.lane }) : null),
        node.text ? el('p', { text: node.text }) : null,
        node.points?.length ? el('ul', {}, node.points.map((p) => el('li', { text: p }))) : null,
        node.code?.length ? el('div', {}, el('h3', { text: 'In the code' }), el('div', { class: 'col' }, node.code.map((c) => el('code', { text: c })))) : null,
        node.tests?.length ? el('div', {}, el('h3', { text: 'Tested by' }), el('div', { class: 'row wrap' }, node.tests.map((t) => el('span', { class: 'chip', text: t })))) : null);
    };

    const light = (ids) => hotspots.forEach((button, id) => button.classList.toggle('lit', ids.includes(id)));

    const drawWalk = () => {
      if (!walk) { light([]); fill(walkBox, el('div', { class: 'row wrap' }, el('b', { text: 'Walk through it:' }), doc.walks.map((w) => el('button', { type: 'button', class: 'btn small', text: w.title, onclick: () => start(w) })))); return; }
      const step = walk.steps[stepIndex];
      light(step.nodes);
      const first = doc.nodes.find((n) => n.id === step.nodes[0]);
      if (first) show(first);
      fill(walkBox,
        el('div', { class: 'row wrap' }, el('b', { text: walk.title }), el('span', { class: 'grow' }),
          el('button', { type: 'button', class: 'btn small ghost', text: 'End the walkthrough', onclick: () => { walk = null; drawWalk(); } })),
        el('div', { class: 'walk-steps' }, walk.steps.map((_, i) => el('button', { type: 'button', 'aria-current': String(i === stepIndex), text: String(i + 1), onclick: () => { stepIndex = i; drawWalk(); } }))),
        el('p', { text: step.text }),
        el('div', { class: 'row' },
          el('button', { type: 'button', class: 'btn small', text: '← Back', disabled: stepIndex === 0, onclick: () => { stepIndex--; drawWalk(); } }),
          el('button', { type: 'button', class: 'btn small primary', text: stepIndex === walk.steps.length - 1 ? 'Finish' : 'Next →', onclick: () => { if (stepIndex === walk.steps.length - 1) { walk = null; } else { stepIndex++; } drawWalk(); } }),
          el('span', { class: 'hint', text: 'The arrow keys work too.' })));
    };

    const start = (w) => { walk = w; stepIndex = 0; drawWalk(); };

    holder.walkKey = (key) => {
      if (!walk) return;
      if (key === 'ArrowRight' && stepIndex < walk.steps.length - 1) { stepIndex++; drawWalk(); }
      if (key === 'ArrowLeft' && stepIndex > 0) { stepIndex--; drawWalk(); }
    };

    fetch(`/diagrams/${name}.json`).then((r) => r.json()).then((d) => {
      doc = d;
      for (const node of d.nodes) {
        const button = el('button', { type: 'button', class: 'hot', 'aria-label': node.title, 'aria-pressed': 'false', title: node.title, onclick: () => { walk = null; drawWalk(); show(node); } });
        button.style.setProperty('left', `${(node.x / d.width) * 100}%`);
        button.style.setProperty('top', `${(node.y / d.height) * 100}%`);
        button.style.setProperty('width', `${(node.w / d.width) * 100}%`);
        button.style.setProperty('height', `${(node.h / d.height) * 100}%`);
        hotspots.set(node.id, button);
        frame.append(button);
      }

      drawWalk();
    }).catch(() => fill(walkBox, el('div', { class: 'banner bad', text: 'Could not load the diagram data.' })));

    fill(holder,
      intro ? el('p', { class: 'hint', text: intro }) : null,
      frame, walkBox, panel,
      el('div', { class: 'row wrap' },
        el('a', { class: 'btn small', href: `/diagrams/${name}.svg`, target: '_blank', rel: 'noopener', text: 'Open the SVG' }),
        el('span', { class: 'hint', text: `The same file is used by the documentation: tools/Lateral.Cms.TestConsole/wwwroot/diagrams/${name}.svg (regenerate with node tools/diagrams/build.mjs).` })));
    return holder;
  }

  // ---------------------------------------------------------------- the text sections

  const P = (text) => el('p', { text });
  const H = (text) => el('h2', { text });
  const list = (items) => el('ul', {}, items.map((t) => el('li', {}, t)));
  const card = (title, ...body) => el('div', { class: 'card' }, el('b', { text: title }), ...body);

  function idea() {
    return el('div', { class: 'doc' },
      H('One sentence'),
      P('A CMS tells this service what changed; the service keeps the latest state of every record, whatever order, however many times and however late the news arrives, and serves it safely.'),
      el('div', { class: 'grid three' },
        card('Durable first', P('An acknowledgement (202) is only sent after the batch is safely stored. A crash can lose nothing the CMS was told was delivered. Applying happens afterwards, in the background.')),
        card('Correct whatever the order', P('Each event changes state only if it sorts after what is stored. Replays, old versions and out-of-order delivery change nothing, and the end state does not depend on arrival order. That is tested against an independent oracle.')),
        card('Nothing unsafe is served', P('Users see only published records an admin has not hidden, and only files a scanner has cleared. A hidden record looks exactly like a missing one; a quarantined file looks exactly like a missing one.'))),
      H('Why it is shaped this way'),
      list([
        'The caller is a webhook sender: it wants a fast answer and retries on anything that is not 2xx. So a 2xx means "do not retry", and a refusal (413, 429, 503) always means "nothing was stored, send it again".',
        'The CMS is the only source of truth, so there is deliberately no route that edits a record. Admins can hide one locally; they cannot change it.',
        'Large content is not carried in events. A file has its own path (assets), and an event only refers to it.',
        'Everything is plain HTTP: every call this console makes is shown as curl, so a conversation about behaviour can always end in "run this and see".',
      ]),
      H('Where to go next'),
      el('div', { class: 'row wrap' },
        el('button', { type: 'button', class: 'btn primary', text: 'Look at the architecture', onclick: () => go('architecture') }),
        el('button', { type: 'button', class: 'btn', text: 'Follow one event', onclick: () => go('event') }),
        el('button', { type: 'button', class: 'btn', text: 'Run the guided tour', onclick: () => Eng.show('overview') })));
  }

  const RULES = [
    ['publish', 'nothing stored', 'create, published'],
    ['publish', 'version > stored', 'replace payload and version, published'],
    ['publish', 'same version, newer timestamp', 'state changes (for example a republish after an unpublish)'],
    ['publish', 'same version, same timestamp, already published, same content', 'ignored_duplicate'],
    ['publish', 'version < stored, or same version and older timestamp', 'ignored_stale (older_version or older_timestamp)'],
    ['unPublish', 'nothing stored', 'created from the event\'s payload, unpublished'],
    ['unPublish', 'version ≥ stored and newer', 'overwrite payload and version, unpublished'],
    ['unPublish', 'older than stored', 'ignored_stale'],
    ['publish vs unPublish', 'same version and same timestamp', 'unPublish wins, in either arrival order'],
    ['same kind', 'same version and timestamp, different content', 'the greater payload text wins (same_version_different_content)'],
    ['delete', 'entity exists, delete timestamp ≥ its last event', 'hard delete; a tombstone is recorded or raised'],
    ['delete', 'nothing stored', 'tombstone recorded (tombstone_only)'],
    ['delete', 'older than the stored entity\'s last event', 'ignored_stale (newer_state_exists); the tombstone mark is still raised'],
    ['delete', 'at or below the existing tombstone', 'ignored_duplicate (equal) or ignored_stale (older)'],
    ['publish / unPublish', 'timestamp ≤ tombstone', 'ignored_stale (superseded_by_delete)'],
  ];

  function rules() {
    const scenario = (name, label) => el('button', {
      type: 'button', class: 'btn small', text: label,
      onclick: () => { const s = Eng.scenarios?.find((x) => x.name === name); if (s) { Eng.prefillEvents = s.build(Eng.scenarioContext()); Eng.show('events'); } },
    });
    return el('div', { class: 'doc' },
      P('An entity\'s events are ordered by (version, timestamp). An event changes state only if it sorts after what is stored. Deletes carry no version and compare by timestamp. The reducer is a pure function: current entity (or none), current tombstone (or none) and one event in; new entity, new tombstone, an outcome and a reason out.'),
      el('div', { class: 'table-wrap' }, el('table', { class: 'rule-table' }, el('thead', {}, el('tr', {}, ['Event', 'Situation', 'Result'].map((h) => el('th', { text: h })))),
        el('tbody', {}, RULES.map(([a, b, c]) => el('tr', {}, el('td', { text: a }), el('td', { text: b }), el('td', {}, el('code', { text: c }))))))),
      H('Try it'),
      el('div', { class: 'row wrap' }, scenario('Out-of-order delivery', 'Out of order'), scenario('Update, then replay', 'A replay'), scenario('Same version, different content', 'A conflict'), scenario('Hard delete', 'A delete')),
      H('How we know the order does not matter'),
      P('An independent oracle sees only the set of events for an id: the tombstone is the newest delete, live events are those after it, and the winner is the maximum of (version, timestamp, unPublish beating publish, payload text). The tests compare the incremental reducer with it on tens of thousands of seeded random timelines, each shuffled, and on every permutation of the curated ones.'),
      H('The one assumption'),
      P('Order independence holds only if, for one id, a higher version never carries an earlier timestamp, and ids are not reused with restarting versions. One CMS emitting its own events satisfies that. Outside it, arrival order can matter, and two tests pin the exact counterexamples. No cleverer reducer can fix it; the fix belongs at the source (a monotonic sequence per entity, or deletes that carry a version).'),
      el('p', { class: 'hint', text: 'Takedowns (unpublish and delete) are retried forever and never set aside: the item stays visible until it applies, so it must apply.' }));
  }

  const GUARANTEES = [
    ['An acknowledged batch is safe', '202 is sent only after the batch and its events are committed in one transaction. A restart finds the queue exactly as it was.', ['AcceptanceTests', 'InboxWorkerTests']],
    ['The end state does not depend on order', 'Replays, late and out-of-order events end in the same state as the same events in order.', ['ReducerTests', 'ReducerPropertyTests']],
    ['One bad event never spoils a batch', 'Each event is validated alone and rejected with its own stable reason code; the others are stored.', ['EventParserTests', 'IngestionEndpointTests']],
    ['A takedown cannot get stuck', 'Unpublish and delete batches are retried forever and never set aside, and are applied first.', ['InboxFailureTests']],
    ['A poisoned batch cannot block the queue', 'Other batches carry on; the failed one is set aside and an admin can requeue it.', ['InboxFailureTests', 'InboxWorkerTests']],
    ['Hidden looks the same as missing', 'A user gets the same 404 for an unpublished, disabled, deleted or never-existing record.', ['EntitiesEndpointTests', 'OverrideAndPolicyTests']],
    ['No route edits a record', 'The only writes are ingestion, the admin override, the queue and the asset routes, and a test lists them so a new one cannot appear unnoticed.', ['RouteAndTransportTests']],
    ['Nothing unscanned is ever served', 'A quarantined, rejected or set-aside file is the same 404 as a missing one, for users and admins alike. Scanner errors fail closed.', ['ScanPipelineTests', 'DownloadTests']],
    ['Delete is physical', 'After a hard delete the payload bytes are not in the database file (secure_delete) and an asset\'s bytes are overwritten before removal.', ['StorageGuaranteeTests', 'HousekeepingTests']],
    ['Memory and the queue are bounded', 'The declared length is checked before reading, concurrent accepts are capped, the queue has a size limit (429, nothing stored) and large files are streamed.', ['InboxBackpressureTests', 'IngestionLimitsTests', 'LargeAssetTests']],
    ['Secrets stay out of logs', 'No payload, password or Authorization header is ever logged; logs carry ids, counts and reason codes.', ['ObservabilityTests']],
    ['Bad input is a 400, never a 500', 'Unreadable queries, bodies and unknown values are a client error in every environment, including Development.', ['BadInputTests']],
  ];

  function guarantees() {
    return el('div', { class: 'doc' }, P('Each of these is a claim with a test that fails if it stops being true. The matrix of every requirement and its tests is in docs/REQUIREMENTS.md.'),
      el('div', { class: 'talk' }, GUARANTEES.map(([title, text, tests]) => el('div', { class: 'card' }, el('b', { text: title }), P(text), el('div', { class: 'row wrap' }, tests.map((t) => el('span', { class: 'chip', text: t })))))));
  }

  function security() {
    const callers = [['cms', 'CMS'], ['user', 'User'], ['admin', 'Admin'], ['anonymous', 'No credentials']];
    const rows = Eng.catalogue.endpoints.map((e) => el('tr', {}, el('td', {}, methodTag(Eng.routeParts(e.route).method, e.danger), ' ', el('code', { text: Eng.routeParts(e.route).display })),
      el('td', { text: e.title }), callers.map(([id]) => el('td', { class: e.who.includes(id) ? 'pass' : '', text: e.who.includes(id) ? '✓' : '' }))));
    return el('div', { class: 'doc' },
      el('div', { class: 'grid two' },
        card('Principles', list(['Deny by default: a fallback policy refuses anything not explicitly opened.', 'Least privilege: three roles, each with the smallest set of routes.', 'Fail closed: an unreadable input, an unreachable scanner or an unknown caller is a refusal, never a pass.', 'Say less: a hidden record and a missing one are the same answer; a quarantined file and a missing one are the same answer.', 'Constant-time credential comparison, with every account evaluated, and a throttle on repeated failed sign-ins.'])),
        card('Where it is deliberately weak', list(['Basic authentication with secrets in configuration, as the brief requires. Production would use a secret store, hashed credentials, request signing for the CMS and OIDC for people.', 'No encryption at rest; file permissions are the operating system\'s.', 'The scanner is a mock: it proves the pipeline, not malware protection.', 'The throttle is per instance and in memory.']))),
      H('Who may call what'),
      P('Generated from the same catalogue the API workbench uses, which a test keeps equal to the routes and roles the service really has.'),
      el('div', { class: 'table-wrap' }, el('table', {}, el('thead', {}, el('tr', {}, [el('th', { text: 'Route' }), el('th', { text: 'What' }), ...callers.map(([, l]) => el('th', { text: l }))])), el('tbody', {}, rows))),
      el('div', { class: 'row wrap' }, el('button', { type: 'button', class: 'btn', text: 'Prove it: run the matrix', onclick: () => Eng.show('verify') })));
  }

  const LIMITS = [
    ['Scale-out', 'One SQLite file, one writer, one instance.', 'A server database, several workers claiming batches, no in-process gate.'],
    ['Ordering', 'Correct only under the ordering assumption; both counterexamples are pinned by tests.', 'A CMS-assigned monotonic sequence per entity, or deletes that carry a version.'],
    ['Credentials', 'Plain text in configuration; Basic auth; a per-instance throttle.', 'A secret store, stored hashes, request signing, OIDC, gateway rate limiting.'],
    ['Deletion', 'The live file is wiped on delete. No encryption at rest, no retention for events, tombstones or batches.', 'Backups and replicas under the erasure policy; encryption at rest; crypto-shredding.'],
    ['Recovery', 'No resync or replay; a dropped batch shows only as a gap.', 'A sequence from the CMS, a replay endpoint, periodic reconciliation by checksum.'],
    ['Queue', 'A table in the same database, one worker.', 'A broker or partitioned queue keyed by entity id.'],
    ['Idempotency', 'A retried batch is stored and processed again (each event reports ignored_duplicate).', 'An idempotency key per batch and a completion callback.'],
    ['Integrity', 'Checksums detect corruption, not tampering.', 'An HMAC or signatures; RFC 8785 canonical JSON.'],
    ['Content scanning', 'A mock scanner that recognises obvious test patterns. Not malware protection.', 'A real engine behind IAssetScanner, signature updates with re-scan, sandboxed rendering.'],
    ['Asset store', 'One folder on one disk: no replication, versioning, lifecycle rules, pre-signed URLs or encryption.', 'An object store such as S3 behind the same interface.'],
    ['API surface', 'Paging by id only: no filtering, sorting or search. (The photo library reads everything and filters in the page for this reason.)', 'A prefix filter and the rest, with a version policy.'],
    ['SQLite connection pool', 'A race in Microsoft.Data.Sqlite\'s pool gave a rare HTTP 500 under many parallel requests; worked around by opening connections one at a time.', 'Remove the workaround when the library is fixed, or use a server database.'],
  ];

  function limits() {
    return el('div', { class: 'doc' },
      P('What this build knowingly leaves at demo quality, and what production would need. The complete list (K1 to K21) is in docs/DESIGN.md.'),
      el('div', { class: 'table-wrap' }, el('table', {}, el('thead', {}, el('tr', {}, ['Area', 'The demo does', 'Production needs'].map((h) => el('th', { text: h })))),
        el('tbody', {}, LIMITS.map(([a, b, c]) => el('tr', {}, el('td', { text: a }), el('td', { text: b }), el('td', { text: c })))))));
  }

  const TALK = [
    ['Why asynchronous?', 'A webhook sender wants a quick answer and retries on anything that is not 2xx. Applying a 500-event batch can take a while; making the sender wait invites timeouts and retries of work that actually succeeded. Acknowledging before storing would lose events in a crash. So: store durably, answer 202, apply in the background. The cost is that 202 means stored, not applied, and the queue has to be watched.'],
    ['What if the worker crashes mid-batch?', 'Applying a batch is one transaction: state, outcomes and completion commit together or not at all. A crash leaves the batch queued and the worker finds it on restart. Show it: Queue and admin.'],
    ['What if events arrive out of order, or twice?', 'The reducer only moves forward: an event applies only if it sorts after what is stored. Replays and old versions are ignored. Try the Out of order scenario and the guided tour.'],
    ['Why is there no PUT to change a record?', 'The CMS is the only source of truth. A route that edits content would let this service drift from it. Admins can hide a record locally; they cannot rewrite it.'],
    ['How do you stop a malicious upload?', 'Nothing is served until a scanner has cleared it. Uploads wait in quarantine; a flagged file is wiped and only the verdict is kept; a scanner that errors fails closed. The scanner here is a mock behind an interface, so a real engine drops in. Upload the mock malware sample on Assets.'],
    ['Why SQLite?', 'It keeps the demo self-contained and shows the real concerns (a single writer, WAL, physical delete). It is also the main scaling limit: production wants a server database and several workers. The seams for that exist (the queue is a table with a claim query).'],
    ['How do you know it is correct?', 'A pure reducer checked against an independent oracle on tens of thousands of shuffles; tests that run the real HTTP pipeline and a real SQLite file; a log of deliberate defects that each had to be caught by a named test; and a validation suite and auth matrix you can run live on the Security and limits page.'],
    ['What would you change for production?', 'The Limits section: secrets and request signing, a server database and several workers, a real scanner and object store, a monotonic sequence from the CMS, and alerting on queue age. All of it is listed with what it would take.'],
  ];

  function talk() {
    return el('div', { class: 'doc' }, P('Questions this design tends to provoke, with the short answer and where to show it.'),
      el('div', { class: 'talk' }, TALK.map(([q, a]) => el('div', { class: 'card' }, el('b', { text: q }), P(a)))));
  }

  const GLOSSARY = [
    ['Entity / record', 'The thing the CMS manages: an id, a version, a JSON payload. The CMS owns it; this service keeps the latest state.'],
    ['Event', 'One change the CMS reports: publish, unPublish or delete, with an id, a timestamp and (except delete) a version and payload.'],
    ['Batch', 'The array of events sent in one POST. Stored as a unit and applied as a unit.'],
    ['Reducer', 'The pure function that decides whether an event changes the stored state, and what the outcome is.'],
    ['Outcome', 'applied, ignored (duplicate or stale), rejected (with a reason code) or queued (not decided yet).'],
    ['Tombstone', 'What a delete leaves: the id and the time, no payload. It stops an older event bringing the entity back.'],
    ['Takedown', 'An unpublish or delete. These are applied first and retried forever, because the item stays visible until they apply.'],
    ['Checksum', 'A SHA-256 of the payload in a canonical form: the same content always gives the same value. It detects corruption, not tampering.'],
    ['Cursor', 'An opaque token for the next page of a list. The list is in id order, so the cost of a page does not grow with depth.'],
    ['Quarantine', 'Where an uploaded file waits until the scanner has decided. Nothing in it can be downloaded.'],
    ['Verdict', 'The scanner\'s decision: clean, infected or suspicious, with a signature name. A flagged file keeps only its verdict.'],
    ['Content-addressed', 'A file is stored under the SHA-256 of its content, so identical files are stored once.'],
    ['Correlation id', 'An id on each acknowledgement, to quote when asking about a request.'],
  ];

  function glossary() {
    return el('div', { class: 'doc' }, el('dl', { class: 'kv' }, GLOSSARY.flatMap(([t, d]) => [el('dt', { text: t }), el('dd', { text: d })])));
  }

  // ---------------------------------------------------------------- the page

  const PAGES = {
    idea: () => idea(),
    architecture: () => diagram('architecture', { intro: 'The whole service on one page. Blue is the events path, green the files, orange the reads, purple the admin side. Every box explains itself when clicked.' }),
    event: () => diagram('event-lifecycle', { intro: 'One batch from the webhook to a reader, in nine steps. The dashed line is the answer to the CMS.' }),
    asset: () => diagram('asset-lifecycle', { intro: 'Every state an uploaded file can be in, and what moves it. The only way to "clean" is through the scanner.' }),
    rules, guarantees, security, limits, talk, glossary,
  };

  function go(section) {
    ui.section = section;
    $$('button', ui.nav).forEach((b) => b.setAttribute('aria-current', String(b.dataset.section === section)));
    if (!ui.pages[section]) ui.pages[section] = PAGES[section]();
    fill(ui.body, ui.pages[section]);
    history.replaceState(null, '', `#/design/${section}`);
  }

  function build(container) {
    ui.nav = el('nav', { class: 'design-nav', 'aria-label': 'Design sections' }, SECTIONS.map(([key, label]) => el('button', { type: 'button', 'data-section': key, 'aria-current': 'false', text: label, onclick: () => go(key) })));
    ui.body = el('div', { class: 'col' });
    fill(container, el('div', { class: 'design' }, ui.nav, ui.body));
    document.addEventListener('keydown', (event) => {
      if (Eng.current?.title !== 'Design and architecture' || ['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName)) return;
      ui.pages[ui.section]?.walkKey?.(event.key);
    });
  }

  Eng.register('design', {
    title: 'Design and architecture',
    subtitle: 'How it is built and why: three diagrams you can click through, the rules, the guarantees and what production would need.',
    build,
    show(params) { go(SECTIONS.some(([k]) => k === params) ? params : ui.section); },
  });
})();
