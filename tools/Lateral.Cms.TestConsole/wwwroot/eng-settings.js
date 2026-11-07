'use strict';

// Settings and storage: what the CMS is configured with right now (and where each value came from), where it keeps its data,
// and how to run it at home. All read-only: the service reads its configuration when it starts, so changing a value means editing
// the settings file or environment and restarting, and this page writes the lines to paste.

(() => {
  const ui = {};
  const st = { tab: 'settings', settings: null, storage: null, filter: '', changedOnly: false };

  const code = (text) => el('pre', { class: 'code wrap', text });

  function show(value, key) {
    if (value === null || value === undefined || value === '') return '(empty)';
    if (Array.isArray(value)) return value.length ? value.join(', ') : '(none)';
    if (typeof value === 'number' && /Bytes$/.test(key) && value >= 1024) return `${value} (${fmtBytes(value)})`;
    return String(value);
  }

  // ---------------------------------------------------------------- settings

  function settingRow(s) {
    return el('tr', {},
      el('td', {}, el('code', { text: s.key }), el('div', { class: 'hint', text: s.description }),
        s.example ? el('div', { class: 'hint' }, 'For example ', el('code', { text: s.example })) : null),
      el('td', {}, s.secret ? el('span', { class: `chip ${s.value === 'set' ? 'ok' : ''}`, text: String(s.value) }) : el('code', { text: show(s.value, s.key) })),
      el('td', {}, s.secret ? el('span', { class: 'hint', text: 'hidden' }) : el('code', { class: 'muted', text: show(s.default, s.key) })),
      el('td', {}, el('span', { class: `chip ${s.isDefault ? '' : 'info'}`, text: s.source })),
      el('td', {}, el('code', { text: s.env })));
  }

  function renderSettings() {
    const report = st.settings;
    if (!report) return;
    const text = st.filter.toLowerCase();
    const keep = (s) => (!st.changedOnly || !s.isDefault) && (!text || `${s.key} ${s.description} ${s.source}`.toLowerCase().includes(text));
    const groups = report.groups.map((g) => ({ ...g, settings: g.settings.filter(keep) })).filter((g) => g.settings.length > 0);

    fill(ui.settingsBody,
      groups.length === 0 ? el('p', { class: 'hint', text: 'Nothing matches.' }) : null,
      groups.map((g) => el('div', { class: 'card' },
        el('div', { class: 'card-head' }, el('h2', { text: g.title }), el('span', { class: 'hint', text: g.about })),
        el('div', { class: 'table-wrap tall' }, el('table', { class: 'settings' },
          el('thead', {}, el('tr', {}, ['Setting', 'Now', 'Default', 'Comes from', 'Environment variable'].map((h) => el('th', { text: h })))),
          el('tbody', {}, g.settings.map(settingRow)))))));
  }

  /** What has been set away from its default, as lines to paste: environment variables, or an appsettings file. */
  function changed() {
    return st.settings.groups.flatMap((g) => g.settings).filter((s) => !s.isDefault && !s.secret && s.source !== 'not set' && !/^(Urls|Logging)/.test(s.key));
  }

  function asEnvironment() {
    return changed().map((s) => `${s.env}=${Array.isArray(s.value) ? s.value.join(',') : s.value}`).join('\n');
  }

  function asAppSettings() {
    const root = {};
    for (const s of changed()) {
      const parts = s.key.split(':');
      let node = root;
      for (const part of parts.slice(0, -1)) node = (node[part] ??= {});
      node[parts.at(-1)] = s.value;
    }

    return pretty(root);
  }

  function settingsTab() {
    ui.search = el('input', { type: 'search', 'aria-label': 'Search settings', placeholder: 'Search settings', class: 'search' });
    ui.search.addEventListener('input', () => { st.filter = ui.search.value; renderSettings(); });
    const only = el('input', { type: 'checkbox', 'aria-label': 'Only settings that are not at their default' });
    only.addEventListener('change', () => { st.changedOnly = only.checked; renderSettings(); });
    ui.settingsBody = el('div');
    ui.snippet = el('div', { hidden: true });

    const snippet = (title, text) => {
      ui.snippet.hidden = false;
      fill(ui.snippet, el('div', { class: 'card' },
        el('div', { class: 'card-head' }, el('h2', { text: title }), el('button', { type: 'button', class: 'btn small', text: 'Copy', onclick: () => { navigator.clipboard?.writeText(text); Eng.toast('Copied'); } })),
        el('p', { class: 'hint', text: 'Only the values that differ from the default, and never a secret. Paste into the service’s environment file or appsettings.Production.json and restart the service.' }),
        code(text || '(nothing is set away from its default)')));
    };

    return el('div', {},
      el('div', { class: 'banner info' }, 'The service reads its configuration once, when it starts. Later sources win: ', el('code', { text: 'appsettings.json' }), ', then ', el('code', { text: 'appsettings.<Environment>.json' }), ', then environment variables, then the command line. To change a value, set it in one of those and restart the service. Secrets are never shown; only whether they are set.'),
      el('div', { class: 'row wrap' }, ui.search, el('label', { class: 'inline' }, only, 'Only what is not at its default'),
        el('button', { type: 'button', class: 'btn small', text: 'Write what I’ve changed as environment variables', onclick: () => snippet('Environment variables', asEnvironment()) }),
        el('button', { type: 'button', class: 'btn small', text: 'Write it as appsettings.json', onclick: () => snippet('appsettings.Production.json', asAppSettings()) })),
      ui.snippet, ui.settingsBody);
  }

  // ---------------------------------------------------------------- storage

  function bar(used, total) {
    const inner = el('div', {});
    inner.style.setProperty('width', `${total ? Math.min(100, (used / total) * 100).toFixed(1) : 0}%`);
    return el('div', { class: 'progress' }, inner);
  }

  function disk(info) {
    return info ? `${fmtBytes(info.freeBytes)} free of ${fmtBytes(info.totalBytes)} on ${info.volume}` : 'unknown';
  }

  function renderStorage() {
    const r = st.storage;
    if (!r) return;
    const stat = (value, label, extra, tone = '') => el('div', { class: `stat ${tone}` }, el('div', { class: 'value', text: value }), el('div', { class: 'label', text: label }), extra);
    const where = (title, path, lines) => el('tr', {}, el('td', { text: title }), el('td', {}, el('code', { text: path })), el('td', {}, lines));
    const low = (info) => info && info.freeBytes < 2 * 1024 * 1024 * 1024;

    fill(ui.storageBody,
      el('div', { class: 'grid four' },
        stat(fmtBytes(r.database.bytes ?? 0), `Database${r.database.walBytes ? ` (+ ${fmtBytes(r.database.walBytes)} write-ahead log)` : ''}`),
        stat(fmtBytes(r.assets.storedBytes), `Assets, of a ${fmtBytes(r.assets.quotaBytes)} quota`, bar(r.assets.storedBytes, r.assets.quotaBytes)),
        stat(r.assets.disk ? fmtBytes(r.assets.disk.freeBytes) : '?', 'Free where assets are kept', null, low(r.assets.disk) ? 'bad' : 'ok'),
        stat(String(r.counts.users), r.counts.users === 1 ? 'Person in the database' : 'People in the database')),
      r.firstRunPasswordFile.exists
        ? el('div', { class: 'banner warn' }, 'The first-run administrator’s temporary password is still in ', el('code', { text: r.firstRunPasswordFile.path }), '. Sign in as admin and choose a new one, and the file is deleted.')
        : null,
      el('div', { class: 'card' },
        el('h2', { text: 'Where things are' }),
        el('div', { class: 'table-wrap tall' }, el('table', {},
          el('thead', {}, el('tr', {}, ['What', 'Where', 'Notes'].map((h) => el('th', { text: h })))),
          el('tbody', {},
            where('Database', r.database.path, [r.database.engine, el('div', { class: 'hint', text: disk(r.database.disk) })]),
            where('Asset store', r.assets.root, [r.assets.store, el('div', { class: 'hint', text: disk(r.assets.disk) }),
              ...r.assets.folders.map((f) => el('div', { class: 'hint', text: `${f.name}: ${f.files}${f.atLeast ? '+' : ''} files, ${fmtBytes(f.bytes)}${f.name === 'quarantine' ? ' (waiting for the scanner)' : f.name === 'parts' ? ' (uploads in flight)' : ''}` }))]),
            where('Session keys', r.sessionKeys.path, `${r.sessionKeys.files} key file${r.sessionKeys.files === 1 ? '' : 's'}. They sign the session cookies; lose them and everyone signs in again.`),
            where('Logs', '(standard output)', r.logs))))),
      el('div', { class: 'grid two' },
        el('div', { class: 'card' },
          el('h2', { text: 'What is stored' }),
          el('div', { class: 'table-wrap tall' }, el('table', {},
            el('thead', {}, el('tr', {}, ['Kind', 'Rows'].map((h) => el('th', { text: h })))),
            el('tbody', {}, Object.entries(r.counts).map(([k, v]) => el('tr', {}, el('td', { text: k }), el('td', { class: 'num', text: String(v) })))))),
          r.assets.byState.length ? el('p', { class: 'hint', text: `Assets by state: ${r.assets.byState.map((s) => `${s.count} ${s.state} (${fmtBytes(s.bytes)})`).join(', ')}.` }) : null),
        el('div', { class: 'card' },
          el('h2', { text: 'What to back up' }),
          el('div', { class: 'table-wrap tall' }, el('table', {},
            el('thead', {}, el('tr', {}, ['What', 'How'].map((h) => el('th', { text: h })))),
            el('tbody', {}, r.backup.map((b) => el('tr', {}, el('td', {}, b.what, el('div', {}, el('code', { text: b.path }))), el('td', { class: 'hint', text: b.how })))))))));
  }

  function storageTab() {
    ui.storageBody = el('div');
    return el('div', {},
      el('div', { class: 'banner info', text: 'Everything is kept on this machine, in two places: one SQLite database file, and one folder of asset bytes. Nothing is sent anywhere. Assets are stored by their SHA-256, so the same file uploaded twice is kept once. The store sits behind an interface, so an S3-compatible backend could replace the folder; none is built.' }),
      ui.storageBody);
  }

  // ---------------------------------------------------------------- run it at home

  function homeTab() {
    const step = (n, title, ...body) => el('div', { class: 'card' }, el('h2', {}, el('span', { class: 'step-no', text: String(n) }), ' ', title), ...body);
    return el('div', {},
      el('div', { class: 'banner', text: 'For a home server running Linux with systemd, behind a reverse proxy. The service speaks plain HTTP and sends passwords with every Basic request, so it should listen only on the loopback address and be reached over HTTPS through the proxy.' }),
      step(1, 'Install it',
        code('dotnet publish src/Lateral.Cms -c Release -o /opt/lateral\nsudo useradd --system --home /var/lib/lateral --shell /usr/sbin/nologin lateral\nsudo install -d -o lateral -g lateral -m 700 /var/lib/lateral /var/lib/lateral/assets'),
        el('p', { class: 'hint', text: 'Needs the .NET 9 runtime. The data folder is outside the install so an upgrade never touches it, and private to the service account.' })),
      step(2, 'Tell it where things live',
        code('# /etc/lateral/lateral.env  (chmod 600, owned by root)\nASPNETCORE_ENVIRONMENT=Production\nASPNETCORE_URLS=http://127.0.0.1:5080\nAllowedHosts=cms.home.lan\nDatabase__ConnectionString=Data Source=/var/lib/lateral/cms.db\nAssets__Root=/var/lib/lateral/assets\nAssets__MaxBytes=200000000000\nAuth__Cms__Username=cms-ingest-svc\nAuth__Cms__Passwords__0=<run: dotnet Lateral.Cms.dll admin new-service-secret>\nProxy__KnownProxies__0=127.0.0.1'),
        el('p', { class: 'hint', text: 'Leave Auth__Users unset: people live in the database. Production refuses to start with the reset route switched on, and plain HTTP is only served behind the proxy that names itself in Proxy__KnownProxies.' })),
      step(3, 'Run it as a service',
        code('# /etc/systemd/system/lateral.service\n[Unit]\nDescription=Lateral CMS\nAfter=network.target\n\n[Service]\nUser=lateral\nWorkingDirectory=/opt/lateral\nEnvironmentFile=/etc/lateral/lateral.env\nExecStart=/usr/bin/dotnet /opt/lateral/Lateral.Cms.dll\nRestart=on-failure\nNoNewPrivileges=true\nProtectSystem=strict\nReadWritePaths=/var/lib/lateral\n\n[Install]\nWantedBy=multi-user.target'),
        code('sudo systemctl enable --now lateral\njournalctl -u lateral -f')),
      step(4, 'Put HTTPS in front',
        code('# Caddyfile\ncms.home.lan {\n    reverse_proxy 127.0.0.1:5080\n}'),
        el('p', { class: 'hint', text: 'Any reverse proxy will do. For uploads of many gigabytes, raise or remove the proxy’s own request-size and timeout limits.' })),
      step(5, 'Sign in for the first time',
        code('sudo cat /var/lib/lateral/initial-admin-password.txt'),
        el('p', { class: 'hint', text: 'Sign in as admin with that temporary password. You are made to choose a new one, and the file is deleted. Then add the people who need access under People and sign-in.' })),
      step(6, 'Back it up',
        code('sqlite3 /var/lib/lateral/cms.db ".backup /backup/cms.db"\nrsync -a /var/lib/lateral/assets/ /backup/assets/'),
        el('p', { class: 'hint', text: 'The database copy is safe while the service runs. Asset files never change once written, so a plain copy is enough. To restore, stop the service, put both back, start it.' })),
      step(7, 'Upgrade it',
        code('sudo systemctl stop lateral\ndotnet publish src/Lateral.Cms -c Release -o /opt/lateral\nsudo systemctl start lateral'),
        el('p', { class: 'hint', text: 'The database is migrated when the service starts. Back it up first.' })));
  }

  // ---------------------------------------------------------------- the page

  async function load() {
    const [settings, storage] = await Promise.all([
      Eng.call({ path: 'api/admin/settings', as: 'admin', quiet: true }),
      Eng.call({ path: 'api/admin/storage', as: 'admin', quiet: true }),
    ]);
    const ok = settings.status === 200 && storage.status === 200;
    ui.message.hidden = ok;
    if (!ok) {
      ui.message.className = 'banner bad';
      ui.message.textContent = [settings, storage].some((e) => e.status === 401 || e.status === 403)
        ? 'The console’s admin account is not allowed to read this (is it configured?).'
        : `Could not read the CMS: ${settings.status || settings.error || 'no response'}.`;
      return;
    }

    st.settings = settings.json;
    st.storage = storage.json;
    ui.environment.textContent = `${st.settings.environment} environment, content root ${st.settings.contentRoot}`;
    renderSettings();
    renderStorage();
  }

  function pick(tab) {
    st.tab = tab;
    for (const [name, button, pane] of ui.tabs) {
      button.setAttribute('aria-selected', String(name === tab));
      pane.hidden = name !== tab;
    }
  }

  function build(container) {
    ui.message = el('div', { class: 'banner', hidden: true });
    ui.environment = el('span', { class: 'hint' });
    const panes = [['settings', 'Settings', settingsTab()], ['storage', 'Where data is kept', storageTab()], ['home', 'Run it at home', homeTab()]];
    ui.tabs = panes.map(([name, label, pane]) => [name, el('button', { type: 'button', role: 'tab', class: 'tab', text: label, onclick: () => pick(name) }), el('div', { hidden: true }, pane)]);

    fill(container,
      ui.message,
      el('div', { class: 'row wrap between' },
        el('div', { class: 'tabs', role: 'tablist' }, ui.tabs.map(([, button]) => button)),
        el('span', { class: 'row' }, ui.environment, el('button', { type: 'button', class: 'btn small', text: 'Refresh', onclick: load }))),
      ui.tabs.map(([, , pane]) => pane));
    pick('settings');
  }

  Eng.register('settings', {
    title: 'Settings and storage',
    subtitle: 'What the CMS is configured with, where it keeps things, and how to run it at home.',
    build,
    show(params) { if (['settings', 'storage', 'home'].includes(params)) pick(params); load(); },
  });
})();
