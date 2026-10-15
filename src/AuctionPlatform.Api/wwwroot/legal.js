/*
 * DealGo — the legal pages.
 *
 * The documents themselves are written in the HTML, because a policy has to read the same whether
 * or not a script runs. This only fills in the contact details the administrator keeps in the
 * console, so an e-mail address changed there does not leave a stale one in the policy. Each
 * placeholder already says where to look if the request fails.
 */

'use strict';

(async () => {
  try {
    const res = await fetch('/api/v1/site/content', { headers: { Accept: 'application/json' } });
    if (!res.ok) return;
    const s = (await res.json()).settings || {};

    const email = (s.contactEmail || '').trim();
    const phone = (s.contactPhone || '').trim();

    document.querySelectorAll('[data-site="email"]').forEach((el) => {
      if (!email.includes('@') || /\s/.test(email)) return;
      const a = document.createElement('a');
      a.href = `mailto:${email}`;
      a.textContent = email;
      el.replaceChildren(a);
    });

    document.querySelectorAll('[data-site="phone"]').forEach((el) => {
      if (!/^[+0-9()\-\s]{5,}$/.test(phone)) return;
      const a = document.createElement('a');
      a.href = `tel:${phone.replace(/\s/g, '')}`;
      a.textContent = phone;
      el.replaceChildren(a);
    });
  } catch {
    // The page is complete without the details; the fallbacks stay in place.
  }
})();
