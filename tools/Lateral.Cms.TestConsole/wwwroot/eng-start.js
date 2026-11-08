'use strict';

// Loads what the console needs to know, builds the navigation, and opens the page named in the address.

const NAV = [
  { group: 'Understand', items: [['overview', 'Overview', 'home'], ['design', 'Design and architecture', 'book'], ['requirements', 'Requirements', 'list']] },
  { group: 'Explore', items: [['records', 'Records', 'records'], ['assets', 'Assets', 'image']] },
  { group: 'Act', items: [['events', 'Send events', 'send'], ['workbench', 'API workbench', 'tool']] },
  { group: 'Operate', items: [['ops', 'Queue and admin', 'pulse'], ['accounts', 'People and sign-in', 'users'], ['settings', 'Settings and storage', 'gear']] },
  { group: 'Verify', items: [['testing', 'Testing', 'check'], ['verify', 'Security and limits', 'shield']] },
];

function buildNav() {
  fill($('#nav'), NAV.map((group) => el('div', { class: 'nav-group' },
    el('h4', { text: group.group }),
    group.items.map(([name, label, glyph]) => el('button', {
      type: 'button', class: 'nav-item', 'data-view': name, 'aria-current': 'false',
      onclick: () => Eng.show(name),
    }, icon(glyph), el('span', { text: label }))))));
}

function renderAccounts() {
  const missing = ['cms', 'user', 'admin'].filter((id) => !Eng.config.accounts.includes(id));
  fill($('#nav-foot'),
    el('div', { class: 'hint', text: 'Accounts the console can act as' }),
    ['cms', 'user', 'admin'].map((id) => el('div', { class: 'account-line' },
      el('span', { class: `dot ${Eng.config.accounts.includes(id) ? 'ok' : 'bad'}` }), el('span', { text: id === 'cms' ? 'cms (webhook sender)' : id }))));

  if (missing.length) {
    const banner = $('#setup-banner');
    banner.hidden = false;
    banner.textContent = `No credentials configured for: ${missing.join(', ')}. In Development the console reads them from the CMS's App_Data/dev-credentials.json (the CMS writes it the first time it runs); otherwise set Console:Accounts. Those accounts are disabled below.`;
  }
}

function openFromAddress() {
  const [name, ...rest] = location.hash.replace(/^#\/?/, '').split('/');
  Eng.show(name || 'overview', decodeURIComponent(rest.join('/')));
}

async function start() {
  try {
    const response = await fetch('/api/config', { headers: { 'X-Console-Request': '1' } });
    if (response.ok) {
      const config = await response.json();
      Eng.config = { ...Eng.config, ...config, limits: { ...Eng.config.limits, ...config.limits } };
    }
  } catch { /* the banner explains */ }

  try {
    const catalogue = await (await fetch('/endpoints.json')).json();
    Eng.catalogue = catalogue;
  } catch { /* the workbench says so */ }

  $('#cms-url').textContent = Eng.config.cmsBaseUrl || '(unknown)';
  buildNav();
  renderAccounts();
  Eng.logWatchers.add(renderDock);
  renderDock();
  $('#dock-clear').addEventListener('click', () => { Eng.log.length = 0; renderDock(); });
  $('#health-btn').addEventListener('click', () => Eng.checkHealth());
  window.addEventListener('hashchange', openFromAddress);

  openFromAddress();
  // Ask every couple of seconds while the CMS is not answering (it may still be starting), and every 20 seconds once it is.
  const watch = async () => setTimeout(watch, (await Eng.checkHealth()) ? 20000 : 2000);
  watch();
}

start();
