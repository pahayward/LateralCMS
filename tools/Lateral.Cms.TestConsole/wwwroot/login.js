'use strict';

const form = document.getElementById('form');
const error = document.getElementById('error');
const go = document.getElementById('go');

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  error.textContent = '';
  go.disabled = true;
  try {
    const response = await fetch('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Console-Request': '1' },
      body: JSON.stringify({ username: form.username.value, password: form.password.value }),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) { error.textContent = body.error || `Sign-in failed (${response.status}).`; return; }
    const next = new URLSearchParams(location.search).get('next') || '/';
    // Only ever go to a page on this site, and send the photo-library person to the library.
    const safe = next.startsWith('/') && !next.startsWith('//') ? next : '/';
    location.replace(body.role === 'admin' ? safe : '/');
  } catch {
    error.textContent = 'Could not reach the console.';
  } finally {
    go.disabled = false;
  }
});
