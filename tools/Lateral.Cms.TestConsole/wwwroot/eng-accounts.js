'use strict';

// People and sign-in: who can sign in, what they may do, and how to get back in when nobody can. Every change goes to the CMS's
// audit log. Acts as the console's admin account; a person made here can sign in with the same name and password anywhere the
// CMS is reached.

(() => {
  const ui = {};
  const st = { people: [] };

  const when = (iso) => (iso ? new Date(iso).toLocaleString() : 'never');

  function say(node, tone, text) {
    node.hidden = !text;
    node.className = `banner ${tone}`;
    node.textContent = text ?? '';
  }

  const problem = (entry) => `${entry.status || 'No response'}: ${entry.json?.detail ?? entry.error ?? 'Refused.'}`;

  /** A temporary password is shown once, here, because the service never shows it again. */
  function showSecret(name, password, why) {
    ui.secret.hidden = false;
    fill(ui.secret,
      el('strong', { text: `${name}: temporary password` }),
      el('div', { class: 'row wrap' },
        el('code', { class: 'secret', text: password }),
        el('button', { type: 'button', class: 'btn small', text: 'Copy', onclick: () => { navigator.clipboard?.writeText(password); Eng.toast('Copied'); } })),
      el('div', { class: 'hint', text: `${why} It is not shown again and is not stored anywhere readable. They must replace it the first time they sign in.` }));
  }

  async function refresh() {
    const entry = await Eng.call({ path: 'api/admin/users', as: 'admin', quiet: true });
    if (entry.status !== 200 || !entry.json) {
      say(ui.message, 'bad', entry.status === 401 || entry.status === 403 ? 'The console’s admin account is not allowed to manage people (is it configured?).' : `Could not read the list: ${problem(entry)}`);
      return;
    }

    say(ui.message, '', '');
    st.people = entry.json.users;
    const admins = st.people.filter((p) => p.role === 'Admin' && !p.disabled);
    ui.count.textContent = `${st.people.length} account${st.people.length === 1 ? '' : 's'}, ${admins.length} enabled administrator${admins.length === 1 ? '' : 's'}`;
    fill(ui.body, st.people.map(row));
  }

  async function act(label, request, onDone) {
    const entry = await Eng.call({ ...request, as: 'admin' });
    if (entry.status >= 200 && entry.status < 300) {
      Eng.toast(label);
      await onDone?.(entry);
      await refresh();
    } else {
      say(ui.message, 'bad', problem(entry));
    }

    return entry;
  }

  const path = (name, tail = '') => `api/admin/users/${encodeURIComponent(name)}${tail}`;

  function row(person) {
    const configured = person.source === 'config';
    const state = person.disabled ? el('span', { class: 'chip bad', text: 'disabled' })
      : person.mustChangePassword ? el('span', { class: 'chip warn', text: 'must choose a new password' })
        : el('span', { class: 'chip ok', text: 'active' });

    if (configured) {
      return el('tr', {},
        el('td', {}, el('code', { text: person.username })),
        el('td', { text: person.role === 'CmsIngest' ? 'CMS service' : person.role }),
        el('td', {}, el('span', { class: 'chip info', text: 'configuration' })),
        el('td', {}, el('span', { class: 'chip', text: 'always on' })),
        el('td', { text: '-' }),
        el('td', {}, el('span', { class: 'hint', text: 'Defined in the settings file or environment; change it there.' })));
    }

    const role = el('select', { 'aria-label': `Role of ${person.username}` },
      el('option', { value: 'User', text: 'User' }), el('option', { value: 'Admin', text: 'Admin' }));
    role.value = person.role;
    role.addEventListener('change', async () => {
      const entry = await act(`${person.username} is now ${role.value}`, { method: 'PATCH', path: path(person.username), body: JSON.stringify({ role: role.value }) });
      if (entry.status >= 400) role.value = person.role;
    });

    return el('tr', {},
      el('td', {}, el('code', { text: person.username })),
      el('td', {}, role),
      el('td', {}, el('span', { class: 'chip', text: 'database' })),
      el('td', {}, state),
      el('td', { text: when(person.lastSignInAt) }),
      el('td', {}, el('div', { class: 'row wrap' },
        el('button', {
          type: 'button', class: 'btn small', text: 'Reset password',
          onclick: () => act(`Password reset for ${person.username}`, { method: 'POST', path: path(person.username, '/password'), body: '{}' },
            (entry) => showSecret(person.username, entry.json.temporaryPassword, 'Every session they had has ended.')),
        }),
        el('button', {
          type: 'button', class: 'btn small', text: 'Sign out everywhere',
          onclick: () => act(`${person.username} signed out`, { method: 'POST', path: path(person.username, '/revoke-sessions') }),
        }),
        el('button', {
          type: 'button', class: 'btn small', text: person.disabled ? 'Enable' : 'Disable',
          onclick: () => act(`${person.username} ${person.disabled ? 'enabled' : 'disabled'}`, { method: 'PATCH', path: path(person.username), body: JSON.stringify({ disabled: !person.disabled }) }),
        }),
        el('button', {
          type: 'button', class: 'btn small danger', text: 'Delete',
          onclick: () => { if (window.confirm(`Delete ${person.username}? Their sessions end now. The audit log keeps what they did.`)) act(`${person.username} deleted`, { method: 'DELETE', path: path(person.username) }); },
        }))));
  }

  async function add(event) {
    event.preventDefault();
    const username = ui.name.value.trim();
    const body = { username, role: ui.role.value };
    if (ui.password.value) body.password = ui.password.value;
    const entry = await Eng.call({ method: 'POST', path: 'api/admin/users', as: 'admin', body: JSON.stringify(body) });
    if (entry.status === 201) {
      say(ui.addResult, 'ok', `${username} was added as ${ui.role.value}.`);
      if (entry.json.temporaryPassword) showSecret(username, entry.json.temporaryPassword, 'You left the password empty, so one was made.');
      ui.name.value = '';
      ui.password.value = '';
      await refresh();
    } else {
      say(ui.addResult, 'bad', problem(entry));
    }
  }

  async function tryIn(event) {
    event.preventDefault();
    // Sent as a page with its own form would: no browser password box on a 401. The cookie the CMS sets is not kept by this console.
    const entry = await Eng.call({
      method: 'POST', path: 'api/auth/login', as: 'anonymous', headers: { 'X-Requested-With': 'console' },
      body: JSON.stringify({ username: ui.tryName.value, password: ui.tryPassword.value }),
    });
    ui.tryPassword.value = '';
    if (entry.status === 200) say(ui.tryResult, 'ok', `Signed in as ${entry.json.username} (${entry.json.role})${entry.json.mustChangePassword ? '. They must choose a new password before anything else is allowed' : ''}. Nothing was kept: this only checks the credentials.`);
    else if (entry.status === 429) say(ui.tryResult, 'warn', 'Too many failed sign-ins from this address. The CMS says to wait; this is the throttle working.');
    else say(ui.tryResult, 'bad', problem(entry));
  }

  const code = (text) => el('pre', { class: 'code wrap', text });

  function build(container) {
    ui.message = el('div', { class: 'banner', hidden: true });
    ui.count = el('span', { class: 'hint' });
    ui.body = el('tbody');
    ui.secret = el('div', { class: 'banner warn secret-box', hidden: true });

    ui.name = el('input', { class: 'mono', 'aria-label': 'Username', placeholder: 'name or email', autocomplete: 'off', spellcheck: 'false', required: true });
    ui.role = el('select', { 'aria-label': 'Role' }, el('option', { value: 'User', text: 'User (reads)' }), el('option', { value: 'Admin', text: 'Admin (manages)' }));
    ui.password = el('input', { type: 'password', 'aria-label': 'Password', placeholder: 'empty = make a temporary one', autocomplete: 'new-password' });
    ui.addResult = el('div', { class: 'banner', hidden: true });

    ui.tryName = el('input', { class: 'mono', 'aria-label': 'Username to check', placeholder: 'username', autocomplete: 'off', spellcheck: 'false', required: true });
    ui.tryPassword = el('input', { type: 'password', 'aria-label': 'Password to check', placeholder: 'password', autocomplete: 'off', required: true });
    ui.tryResult = el('div', { class: 'banner', hidden: true });

    fill(container,
      ui.message,
      el('div', { class: 'card' },
        el('div', { class: 'card-head' }, el('h2', { text: 'People' }), el('span', { class: 'row' }, ui.count, el('button', { type: 'button', class: 'btn small', text: 'Refresh', onclick: refresh }))),
        el('p', { class: 'hint', text: 'People are kept in the database with a salted, slow hash of their password. The CMS service account and any accounts in the settings file are listed too, read-only. The last enabled administrator, and yourself, cannot be removed, disabled or demoted.' }),
        ui.secret,
        el('div', { class: 'table-wrap' }, el('table', { class: 'people' },
          el('thead', {}, el('tr', {}, ['Name', 'Role', 'Kept in', 'State', 'Last sign-in', ''].map((h) => el('th', { text: h })))), ui.body))),
      el('div', { class: 'grid two' },
        el('div', { class: 'card' },
          el('h2', { text: 'Add a person' }),
          el('form', { class: 'fields', onsubmit: add },
            el('label', {}, 'Username', ui.name), el('label', {}, 'Role', ui.role), el('label', {}, 'Password', ui.password),
            el('div', {}, el('button', { type: 'submit', class: 'btn primary', text: 'Add person' }))),
          el('p', { class: 'hint', text: 'At least 12 characters, not their name, not a common password. Leave it empty and the service makes a temporary one, shown once, that they must replace at their first sign-in.' }),
          ui.addResult),
        el('div', { class: 'card' },
          el('h2', { text: 'Check a sign-in' }),
          el('form', { class: 'fields', onsubmit: tryIn }, el('label', {}, 'Username', ui.tryName), el('label', {}, 'Password', ui.tryPassword),
            el('div', {}, el('button', { type: 'submit', class: 'btn', text: 'Try signing in' }))),
          el('p', { class: 'hint', text: 'Sends the real sign-in request and reports what the CMS says. A wrong password, an unknown name and a disabled account all answer the same. Ten wrong tries in a minute from this address are throttled.' }),
          ui.tryResult)),
      el('div', { class: 'card' },
        el('div', { class: 'card-head' }, el('h2', { text: 'Getting in, and getting back in' })),
        el('div', { class: 'grid two' },
          el('div', {},
            el('h3', { text: 'The first time' }),
            el('p', { text: 'A fresh install has nobody. It makes one administrator, called admin, with a generated temporary password written to a file beside the database (readable only by the account that runs the service) and not to the log. Sign in with it and you are made to choose your own; the file is then deleted.' }),
            code('cat /var/lib/lateral/initial-admin-password.txt'),
            el('h3', { text: 'Passwords' }),
            el('p', { text: 'At least 12 and at most 128 characters, not your name, not a common one. They are stored as PBKDF2-SHA256 with a random salt and 600000 iterations, so a copy of the database does not give them away. Nothing, including this page, can show an existing password; the most anyone can do is replace it.' }),
            el('h3', { text: 'Sessions' }),
            el('p', { text: 'Signing in gives a cookie that JavaScript cannot read and other sites cannot send. It lasts 12 hours without use. Changing or resetting a password, disabling the person or choosing Sign out everywhere ends every session at once, and a role change applies to a session already open.' })),
          el('div', {},
            el('h3', { text: 'Locked out? Over SSH' }),
            el('p', { text: 'Whoever can log in to the machine can already read the database, so the same authority can reset any password. This works with the service stopped or running, and uses the same rules.' }),
            code('sudo systemd-run --pty --wait --collect -q -p User=lateral \\\n  -p EnvironmentFile=/etc/lateral/lateral.env -p WorkingDirectory=/opt/lateral \\\n  /usr/bin/dotnet /opt/lateral/Lateral.Cms.dll admin set-password admin'),
            el('p', { class: 'hint', text: 'That runs the tool as the service’s own user with the service’s own environment, so it opens the same database. Swap the last words for list-users, set-password admin --generate, create-user rescue --role admin --generate, or any other command from admin help. set-password asks twice and shows nothing as you type; --generate prints a temporary password once.' }),
            el('p', { text: 'Without systemd, from the install folder, as the account that owns the database and with the service’s environment variables set:' }),
            code('cd /opt/lateral\ndotnet Lateral.Cms.dll admin set-password admin'),
            code('dotnet Lateral.Cms.dll admin help'),
            el('h3', { text: 'The CMS service account' }),
            el('p', { text: 'The CMS signs in with Basic credentials from the settings file or the environment, not from the database, so it still works when everyone else is locked out. To rotate: add a new GUID next to the old one in Auth:Cms:Passwords, switch the CMS over, then remove the old one.' }),
            code('dotnet Lateral.Cms.dll admin new-service-secret')))));
  }

  Eng.register('accounts', {
    title: 'People and sign-in',
    subtitle: 'Who can sign in and what they may do. Every change is in the audit log.',
    build,
    show() { refresh(); },
  });
})();
