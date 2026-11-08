'use strict';

// When the site is served under a path prefix (for example https://example.ngrok-free.dev/lateral/, with another site at the root),
// every root-absolute URL the pages use ("/api/...", "/media/...") must carry that prefix. The prefix is read from where this
// script was loaded from, so there is nothing to configure in the browser and a site at the root is untouched.
(function () {
  const BASE = new URL(document.currentScript.src).pathname.replace(/\/base\.js$/, '');
  const fix = (url) => (BASE && typeof url === 'string' && url.startsWith('/') && !url.startsWith('//') && url !== BASE && !url.startsWith(BASE + '/') ? BASE + url : url);
  window.AppBase = BASE;
  window.AppPath = fix;
  /** The path of this page without the prefix, for "?next=" values. */
  window.AppRelativePath = () => (BASE && location.pathname.startsWith(BASE) ? location.pathname.slice(BASE.length) || '/' : location.pathname);
  if (!BASE) return;

  const realFetch = window.fetch;
  window.fetch = (input, init) => realFetch(typeof input === 'string' ? fix(input) : input, init);

  const realOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, url, ...rest) { return realOpen.call(this, method, fix(String(url)), ...rest); };

  const realSetAttribute = Element.prototype.setAttribute;
  Element.prototype.setAttribute = function (name, value) {
    return realSetAttribute.call(this, name, ['href', 'src', 'action', 'data'].includes(String(name).toLowerCase()) ? fix(String(value)) : value);
  };

  for (const [proto, property] of [[HTMLImageElement.prototype, 'src'], [HTMLAnchorElement.prototype, 'href'], [HTMLSourceElement.prototype, 'src'], [HTMLVideoElement.prototype, 'src']]) {
    const original = Object.getOwnPropertyDescriptor(proto, property);
    if (original?.set) Object.defineProperty(proto, property, { ...original, set(value) { original.set.call(this, fix(String(value))); } });
  }

  document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('[href^="/"],[src^="/"]').forEach((node) => {
      for (const attribute of ['href', 'src']) {
        const value = node.getAttribute(attribute);
        if (value) node.setAttribute(attribute, value);
      }
    });
  });
})();
