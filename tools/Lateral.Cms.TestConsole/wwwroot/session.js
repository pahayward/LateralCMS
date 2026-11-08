'use strict';

// Shows who is signed in on every page, with a way to sign out, and hides what that person may not open.
(async () => {
  let session;
  try {
    const response = await fetch('/api/session', { headers: { 'X-Console-Request': '1' } });
    if (response.status === 401) { location.replace('/login?next=' + encodeURIComponent(location.pathname)); return; }
    session = await response.json();
  } catch { return; }

  const bar = document.querySelector('.site-bar');
  if (!bar) return;
  if (session.role !== 'admin') document.querySelectorAll('.site-bar nav a[href="/engineering"]').forEach((a) => a.remove());
  if (session.enabled === false) return;

  const who = document.createElement('span');
  who.className = 'who';
  const label = document.createElement('span');
  label.textContent = `${session.name} (${session.role === 'admin' ? 'admin' : 'photos only'})`;
  const out = document.createElement('button');
  out.type = 'button';
  out.textContent = 'Sign out';
  out.addEventListener('click', async () => {
    await fetch('/api/logout', { method: 'POST', headers: { 'X-Console-Request': '1' } });
    location.replace('/login');
  });
  who.append(label, out);
  bar.append(who);
})();
