/*
 * Auction Platform admin dashboard.
 *
 * A dependency-free SPA served straight out of wwwroot, so the admin panel ships with the API and
 * needs no separate build step or deployment. It calls the same /api/v1 surface as the mobile
 * client; authorisation is enforced server side on every request.
 */
'use strict';

const API = '/api/v1';

const state = {
  auth: null,          // { accessToken, refreshToken, user }
  route: 'dashboard',
  cache: {},
};

// ---------------------------------------------------------------- storage

const store = {
  load() {
    try {
      const raw = localStorage.getItem('auction.admin.auth');
      return raw ? JSON.parse(raw) : null;
    } catch { return null; }
  },
  save(auth) {
    try { localStorage.setItem('auction.admin.auth', JSON.stringify(auth)); } catch { /* private mode */ }
  },
  clear() {
    try { localStorage.removeItem('auction.admin.auth'); } catch { /* ignore */ }
  },
};

// ---------------------------------------------------------------- api client

/**
 * Calls the API with the current access token, transparently refreshing it once on a 401 so a
 * short-lived token never interrupts an admin mid-task.
 */
async function api(path, options = {}, retry = true) {
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  if (state.auth?.accessToken) headers.Authorization = `Bearer ${state.auth.accessToken}`;

  const res = await fetch(`${API}${path}`, { ...options, headers });

  if (res.status === 401 && retry && state.auth?.refreshToken) {
    const refreshed = await tryRefresh();
    if (refreshed) return api(path, options, false);
    logout();
    throw new Error('Session expired.');
  }

  if (res.status === 204) return null;

  const text = await res.text();
  const body = text ? JSON.parse(text) : null;

  if (!res.ok) {
    const err = new Error(body?.message || `Request failed (${res.status})`);
    err.code = body?.code;
    err.details = body?.details || [];
    throw err;
  }

  return body;
}

async function tryRefresh() {
  try {
    const res = await fetch(`${API}/auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: state.auth.refreshToken }),
    });
    if (!res.ok) return false;

    const auth = await res.json();
    state.auth = auth;
    store.save(auth);
    return true;
  } catch { return false; }
}

function logout() {
  state.auth = null;
  store.clear();
  render();
}

// ---------------------------------------------------------------- helpers

const h = (tag, attrs = {}, ...children) => {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'html') el.innerHTML = v;
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
};

const money = (v, c = 'USD') =>
  v === null || v === undefined ? '—' : `${Number(v).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${c}`;

const num = (v) => (v === null || v === undefined ? '—' : Number(v).toLocaleString());

const when = (v) => (v ? new Date(v).toLocaleString() : '—');

const shortId = (v) => (v ? String(v).slice(0, 8) : '—');

function badgeClass(status) {
  const s = String(status || '').toLowerCase();
  if (['live', 'finalphase', 'active', 'completed', 'paid', 'delivered', 'ok'].includes(s)) return 'badge live';
  if (['ready', 'waitingforstart', 'registrationopen', 'waitingforparticipants', 'processing', 'shipped', 'paymentrequired'].includes(s)) return 'badge info';
  if (['draft', 'pending', 'hidden', 'outofstock', 'scheduled'].includes(s)) return 'badge warn';
  if (['cancelled', 'expired', 'refunded', 'archived', 'failed'].includes(s)) return 'badge danger';
  return 'badge';
}

function toast(message, kind = '') {
  document.querySelectorAll('.toast').forEach((t) => t.remove());
  const el = h('div', { class: `toast ${kind}` }, message);
  document.body.append(el);
  setTimeout(() => el.remove(), 4200);
}

function confirmDialog(title, message, onConfirm, { danger = false, needsReason = false } = {}) {
  const reason = h('input', { placeholder: 'Reason (recorded in the audit log)' });
  const back = h('div', { class: 'modal-back' });

  const submit = async () => {
    if (needsReason && !reason.value.trim()) { reason.focus(); return; }
    back.remove();
    await onConfirm(reason.value.trim());
  };

  back.append(h('div', { class: 'modal' },
    h('h3', {}, title),
    h('p', { class: 'sub' }, message),
    needsReason ? reason : null,
    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Cancel'),
      h('button', { class: danger ? 'btn danger' : 'btn', onclick: submit }, 'Confirm'))));

  document.body.append(back);
  if (needsReason) reason.focus();
}

function formModal(title, subtitle, fields, onSubmit) {
  const inputs = {};
  const back = h('div', { class: 'modal-back' });
  const error = h('div', { class: 'err-text' });

  const body = fields.map((f) => {
    if (f.type === 'select') {
      const sel = h('select', {}, ...f.options.map((o) =>
        h('option', { value: o.value, selected: o.value === f.value ? '' : null }, o.label)));
      inputs[f.name] = sel;
      return h('div', {}, h('label', {}, f.label), sel);
    }
    const input = h('input', {
      type: f.type || 'text',
      value: f.value ?? '',
      placeholder: f.placeholder || '',
      step: f.step || null,
    });
    inputs[f.name] = input;
    return h('div', {}, h('label', {}, f.label), input, f.hint ? h('div', { class: 'hint' }, f.hint) : null);
  });

  const save = async () => {
    const values = {};
    for (const [k, el] of Object.entries(inputs)) values[k] = el.value;
    error.textContent = '';
    try {
      await onSubmit(values);
      back.remove();
    } catch (e) {
      error.textContent = e.message + (e.details?.length ? ` (${e.details.join('; ')})` : '');
    }
  };

  back.append(h('div', { class: 'modal' },
    h('h3', {}, title),
    subtitle ? h('p', { class: 'sub' }, subtitle) : null,
    ...body,
    error,
    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Cancel'),
      h('button', { class: 'btn', onclick: save }, 'Save'))));

  document.body.append(back);
}

/** Renders a paged table with a shared toolbar, so every list page behaves the same way. */
function pagedTable({ columns, rows, page, totalPages, totalCount, onPage }) {
  if (!rows.length) return h('div', { class: 'empty' }, 'Nothing to show.');

  const table = h('table', {},
    h('thead', {}, h('tr', {}, ...columns.map((c) => h('th', { class: c.num ? 'num' : '' }, c.label)))),
    h('tbody', {}, ...rows.map((r) => h('tr', {}, ...columns.map((c) => {
      const cell = c.render(r);
      return h('td', { class: [c.num ? 'num' : '', c.mono ? 'mono' : ''].filter(Boolean).join(' ') },
        cell instanceof Node ? cell : String(cell ?? '—'));
    })))));

  return h('div', {},
    h('div', { class: 'table-wrap' }, table),
    h('div', { class: 'pager' },
      `${num(totalCount)} total`,
      h('button', { class: 'btn ghost sm', disabled: page <= 1 ? '' : null, onclick: () => onPage(page - 1) }, 'Prev'),
      `Page ${page} / ${Math.max(totalPages, 1)}`,
      h('button', { class: 'btn ghost sm', disabled: page >= totalPages ? '' : null, onclick: () => onPage(page + 1) }, 'Next')));
}

// ---------------------------------------------------------------- login

function renderLogin() {
  const email = h('input', { type: 'email', placeholder: 'admin@auctions.local', value: '' });
  const password = h('input', { type: 'password', placeholder: '••••••••' });
  const error = h('div', { class: 'err-text' });
  const button = h('button', { class: 'btn', style: 'width:100%;margin-top:16px' }, 'Sign in');

  const submit = async () => {
    error.textContent = '';
    button.disabled = true;
    try {
      const auth = await api('/auth/login', {
        method: 'POST',
        body: JSON.stringify({ email: email.value.trim(), password: password.value }),
      });

      const isAdmin = (auth.user.roles || []).some((r) => r !== 'USER');
      if (!isAdmin) throw new Error('This account does not have administrative access.');

      state.auth = auth;
      store.save(auth);
      render();
    } catch (e) {
      error.textContent = e.message;
    } finally {
      button.disabled = false;
    }
  };

  button.addEventListener('click', submit);
  password.addEventListener('keydown', (e) => { if (e.key === 'Enter') submit(); });

  return h('div', { class: 'login-wrap' },
    h('div', { class: 'login-card' },
      h('h1', {}, 'Auction Platform'),
      h('p', { class: 'sub' }, 'Administration'),
      h('label', {}, 'Email'), email,
      h('label', {}, 'Password'), password,
      button,
      error));
}

// ---------------------------------------------------------------- shell

const NAV = [
  { group: 'Overview', items: [{ id: 'dashboard', label: 'Dashboard' }] },
  {
    group: 'Auctions',
    items: [
      { id: 'auctions', label: 'All auctions' },
      { id: 'auction-new', label: 'Create auction' },
    ],
  },
  {
    group: 'Store',
    items: [
      { id: 'store-overview', label: 'Overview' },
      { id: 'store-products', label: 'Products' },
      { id: 'store-categories', label: 'Categories' },
      { id: 'store-inventory', label: 'Inventory' },
      { id: 'store-orders', label: 'Orders' },
      { id: 'store-settings', label: 'Settings' },
    ],
  },
  {
    group: 'Finance',
    items: [
      { id: 'wallets', label: 'Wallets' },
      { id: 'shopping-credits', label: 'Shopping credits' },
    ],
  },
  {
    group: 'System',
    items: [
      { id: 'users', label: 'Users' },
      { id: 'audit', label: 'Audit log' },
    ],
  },
];

function renderShell() {
  const nav = h('nav', { class: 'sidebar' },
    h('div', { class: 'brand' }, 'Auction Platform', h('small', {}, 'Admin console')));

  for (const section of NAV) {
    nav.append(h('div', { class: 'nav-group' }, section.group));
    for (const item of section.items) {
      nav.append(h('button', {
        class: `nav-item ${state.route === item.id ? 'active' : ''}`,
        onclick: () => { state.route = item.id; render(); },
      }, item.label));
    }
  }

  nav.append(h('div', { class: 'sidebar-footer' },
    h('div', { class: 'who' }, state.auth.user.fullName, h('br'), state.auth.user.roles.join(', ')),
    h('button', { class: 'nav-item', onclick: logout }, 'Sign out')));

  const main = h('main', { class: 'main' }, h('div', { class: 'empty' }, 'Loading…'));

  routeView(state.route)
    .then((view) => { main.replaceChildren(view); })
    .catch((e) => {
      main.replaceChildren(h('div', { class: 'card' },
        h('h3', {}, 'Could not load this page'),
        h('div', { class: 'err-text' }, e.message)));
    });

  return h('div', { class: 'shell' }, nav, main);
}

function routeView(route) {
  const views = {
    'dashboard': viewDashboard,
    'auctions': viewAuctions,
    'auction-new': viewCreateAuction,
    'store-overview': viewStoreOverview,
    'store-products': viewStoreProducts,
    'store-categories': viewStoreCategories,
    'store-inventory': viewInventory,
    'store-orders': viewOrders,
    'store-settings': viewStoreSettings,
    'wallets': viewWallets,
    'shopping-credits': viewShoppingCredits,
    'users': viewUsers,
    'audit': viewAudit,
  };
  return (views[route] || viewDashboard)();
}

function pageHead(title, subtitle, ...actions) {
  return h('div', { class: 'page-head' },
    h('div', {}, h('h2', {}, title), subtitle ? h('div', { class: 'sub' }, subtitle) : null),
    h('div', { class: 'row' }, ...actions));
}

function kpi(label, value, isMoney = false) {
  return h('div', { class: 'kpi' },
    h('div', { class: 'label' }, label),
    h('div', { class: `value ${isMoney ? 'money' : ''}` }, value));
}

// ---------------------------------------------------------------- dashboard

async function viewDashboard() {
  const d = await api('/admin/dashboard');

  return h('div', {},
    pageHead('Dashboard', 'Platform-wide figures, straight from the database.'),

    h('div', { class: 'kpi-grid' },
      kpi('Active auctions', num(d.activeAuctions)),
      kpi('Upcoming auctions', num(d.upcomingAuctions)),
      kpi('Completed auctions', num(d.completedAuctions)),
      kpi('Total participants', num(d.totalParticipants))),

    h('div', { class: 'card' },
      h('h3', {}, 'Entry fees & prizes'),
      h('div', { class: 'kpi-grid' },
        kpi("Today's entry revenue", money(d.todayEntryRevenue, d.currency), true),
        kpi('Total entry revenue', money(d.totalEntryRevenue, d.currency), true),
        kpi('Pending prize payments', num(d.pendingPrizePayments)),
        kpi('Pending prize value', money(d.pendingPrizeValue, d.currency), true))),

    h('div', { class: 'card' },
      h('h3', {}, 'Shopping credit (closed loop)'),
      h('div', { class: 'kpi-grid' },
        kpi('Issued', money(d.shoppingCreditIssued, d.currency), true),
        kpi('Used in store', money(d.shoppingCreditUsed, d.currency), true),
        kpi('Outstanding liability', money(d.shoppingCreditOutstanding, d.currency), true))),

    h('div', { class: 'card' },
      h('h3', {}, 'Store & wallets'),
      h('div', { class: 'kpi-grid' },
        kpi('Store orders', num(d.storeOrders)),
        kpi('Pending orders', num(d.pendingStoreOrders)),
        kpi('Store revenue', money(d.storeRevenue, d.currency), true),
        kpi('Main wallet deposits', money(d.mainWalletDeposits, d.currency), true),
        kpi('Main wallet withdrawals', money(d.mainWalletWithdrawals, d.currency), true))));
}

// ---------------------------------------------------------------- auctions

async function viewAuctions() {
  const q = state.cache.auctions || (state.cache.auctions = { page: 1, group: '', search: '' });

  const load = async () => {
    const params = new URLSearchParams({ page: q.page, pageSize: 20 });
    if (q.group) params.set('group', q.group);
    if (q.search) params.set('search', q.search);
    return api(`/admin/auctions?${params}`);
  };

  const data = await load();

  const search = h('input', { placeholder: 'Search title or number', value: q.search });
  const group = h('select', {},
    ...[['', 'All'], ['live', 'Live'], ['upcoming', 'Upcoming'], ['completed', 'Completed']]
      .map(([v, l]) => h('option', { value: v, selected: v === q.group ? '' : null }, l)));

  const apply = () => { q.search = search.value; q.group = group.value; q.page = 1; render(); };

  return h('div', {},
    pageHead('Auctions', 'Publish, schedule and start auctions. Every action is audited.',
      h('button', { class: 'btn', onclick: () => { state.route = 'auction-new'; render(); } }, 'Create auction')),

    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search, group,
        h('button', { class: 'btn ghost sm', onclick: apply }, 'Apply')),

      pagedTable({
        columns: [
          { label: 'Number', mono: true, render: (a) => a.auctionNumber },
          { label: 'Title', render: (a) => a.title },
          { label: 'Status', render: (a) => h('span', { class: badgeClass(a.status) }, a.status) },
          { label: 'Participants', num: true, render: (a) => `${a.participantCount}/${a.requiredParticipants}` },
          { label: 'Price', num: true, render: (a) => money(a.currentPrice, a.currency) },
          { label: 'Target', num: true, render: (a) => money(a.targetPrice, a.currency) },
          { label: 'Entry fee', num: true, render: (a) => money(a.entryFee, a.currency) },
          { label: 'Scheduled', render: (a) => when(a.scheduledStartAt) },
          { label: '', render: (a) => h('button', { class: 'btn ghost sm', onclick: () => openAuction(a.id) }, 'Manage') },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function openAuction(id) {
  const a = await api(`/admin/auctions/${id}`);
  const back = h('div', { class: 'modal-back' });

  const act = async (fn, successMessage) => {
    try {
      await fn();
      back.remove();
      toast(successMessage, 'ok');
      render();
    } catch (e) {
      toast(e.message + (e.details?.length ? ` — ${e.details.join('; ')}` : ''), 'err');
    }
  };

  const canPublish = a.status === 'Draft' || a.status === 'Scheduled';
  const canStart = a.status === 'Ready' || a.status === 'WaitingForStart';
  const canPause = a.status === 'Live' || a.status === 'FinalPhase';
  const canResume = a.status === 'Paused';
  const canCancel = !['Completed', 'Cancelled'].includes(a.status);

  const readyPanel = canStart
    ? h('div', { class: 'card', style: 'background:var(--surface-2)' },
        h('h3', {}, 'Auction ready'),
        h('div', {}, `${a.participantCount} / ${a.requiredParticipants} participants`),
        h('div', { class: 'hint' }, `Participants completed: ${when(a.participantsCompletedAt)}`),
        h('div', { class: 'hint' }, `Scheduled start: ${when(a.scheduledStartAt)}`))
    : null;

  back.append(h('div', { class: 'modal' },
    h('h3', {}, a.title),
    h('p', { class: 'sub' }, `${a.auctionNumber} · `, h('span', { class: badgeClass(a.status) }, a.status)),

    readyPanel,

    h('div', { class: 'grid-2' },
      h('div', {}, h('label', {}, 'Original price'), h('div', {}, money(a.originalPrice, a.currency))),
      h('div', {}, h('label', {}, 'Starting price'), h('div', {}, money(a.startingPrice, a.currency))),
      h('div', {}, h('label', {}, 'Current price'), h('div', {}, money(a.currentPrice, a.currency))),
      h('div', {}, h('label', {}, `Target (${a.targetPercentage}%)`), h('div', {}, money(a.targetPrice, a.currency))),
      h('div', {}, h('label', {}, `Final phase (${a.finalPhasePercentage}%)`), h('div', {}, money(a.finalPhasePrice, a.currency))),
      h('div', {}, h('label', {}, 'Bid increment'), h('div', {}, money(a.bidIncrement, a.currency))),
      h('div', {}, h('label', {}, 'Entry fee'), h('div', {}, money(a.entryFee, a.currency))),
      h('div', {}, h('label', {}, 'Duration'), h('div', {}, `${a.durationSeconds}s`)),
      h('div', {}, h('label', {}, 'Start delay'), h('div', {}, `${a.startDelayAfterParticipantsSeconds}s`)),
      h('div', {}, h('label', {}, 'Expiration policy'), h('div', {}, a.expirationPolicy))),

    h('label', {}, `Prizes (${a.prizes.length})`),
    h('div', { class: 'table-wrap' },
      h('table', {},
        h('thead', {}, h('tr', {}, h('th', {}, 'Rank'), h('th', {}, 'Product'), h('th', { class: 'num' }, 'Value'))),
        h('tbody', {}, ...a.prizes.map((p) =>
          h('tr', {}, h('td', {}, `#${p.rank}`), h('td', {}, p.productName),
            h('td', { class: 'num' }, money(p.originalPrice, p.currency))))))),

    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'),

      h('button', { class: 'btn ghost sm', onclick: () => { back.remove(); openParticipants(a.id); } }, 'Participants'),
      h('button', { class: 'btn ghost sm', onclick: () => { back.remove(); openWinners(a.id); } }, 'Winners'),
      h('button', { class: 'btn ghost sm', onclick: () => { back.remove(); openEvents(a.id); } }, 'Event log'),

      canPublish ? h('button', {
        class: 'btn', onclick: () => act(() => api(`/admin/auctions/${a.id}/publish`, { method: 'POST' }), 'Auction published.'),
      }, 'Publish') : null,

      canStart ? h('button', {
        class: 'btn ok', onclick: () => act(() => api(`/admin/auctions/${a.id}/start`, { method: 'POST' }), 'Auction started.'),
      }, 'Start now') : null,

      canStart ? h('button', {
        class: 'btn ghost', onclick: () => { back.remove(); editSchedule(a); },
      }, 'Edit start time') : null,

      canPause ? h('button', {
        class: 'btn ghost', onclick: () => act(() => api(`/admin/auctions/${a.id}/pause`, { method: 'POST' }), 'Auction paused.'),
      }, 'Pause') : null,

      canResume ? h('button', {
        class: 'btn ok', onclick: () => act(() => api(`/admin/auctions/${a.id}/resume`, { method: 'POST' }), 'Auction resumed.'),
      }, 'Resume') : null,

      canCancel ? h('button', {
        class: 'btn danger',
        onclick: () => {
          back.remove();
          confirmDialog('Cancel auction', 'Participants will be notified.', (reason) =>
            act(() => api(`/admin/auctions/${a.id}/cancel`, {
              method: 'POST', body: JSON.stringify({ reason }),
            }), 'Auction cancelled.'), { danger: true, needsReason: true });
        },
      }, 'Cancel') : null)));

  document.body.append(back);
}

function editSchedule(a) {
  const current = a.scheduledStartAt ? new Date(a.scheduledStartAt) : new Date();
  const local = new Date(current.getTime() - current.getTimezoneOffset() * 60000).toISOString().slice(0, 16);

  formModal('Edit start time', `${a.auctionNumber} · times are stored in UTC`, [
    { name: 'scheduledStartAt', label: 'Scheduled start (local time)', type: 'datetime-local', value: local },
    { name: 'reason', label: 'Reason', placeholder: 'Recorded in the audit log' },
  ], async (v) => {
    await api(`/admin/auctions/${a.id}/schedule`, {
      method: 'PATCH',
      body: JSON.stringify({
        scheduledStartAt: new Date(v.scheduledStartAt).toISOString(),
        reason: v.reason,
      }),
    });
    toast('Start time updated. Participants were notified.', 'ok');
    render();
  });
}

async function openParticipants(auctionId) {
  const data = await api(`/admin/auctions/${auctionId}/participants?pageSize=50`);
  const back = h('div', { class: 'modal-back' });

  back.append(h('div', { class: 'modal' },
    h('h3', {}, 'Participants'),
    h('p', { class: 'sub' }, 'Bid history is immutable and cannot be edited from here.'),
    h('div', { class: 'table-wrap' },
      h('table', {},
        h('thead', {}, h('tr', {},
          h('th', {}, 'Rank'), h('th', {}, 'Participant'), h('th', {}, 'Joined'),
          h('th', { class: 'num' }, 'Entry fee'), h('th', { class: 'num' }, 'Highest'),
          h('th', { class: 'num' }, 'Bids'), h('th', {}, 'Status'))),
        h('tbody', {}, ...data.items.map((p) => h('tr', {},
          h('td', {}, p.currentRank || '—'),
          h('td', {}, p.fullName || shortId(p.userId)),
          h('td', {}, when(p.joinedAt)),
          h('td', { class: 'num' }, money(p.entryFeePaid, p.currency)),
          h('td', { class: 'num' }, money(p.highestPrice, p.currency)),
          h('td', { class: 'num' }, p.bidCount),
          h('td', {}, h('span', { class: badgeClass(p.status) }, p.status))))))),
    h('div', { class: 'modal-actions' }, h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'))));

  document.body.append(back);
}

async function openWinners(auctionId) {
  const winners = await api(`/admin/auctions/${auctionId}/winners`);
  const back = h('div', { class: 'modal-back' });

  back.append(h('div', { class: 'modal' },
    h('h3', {}, 'Winners'),
    h('p', { class: 'sub' }, 'Ranked by highest price, then by the earliest server timestamp at which it was reached.'),
    winners.length
      ? h('div', { class: 'table-wrap' }, h('table', {},
          h('thead', {}, h('tr', {}, h('th', {}, 'Rank'), h('th', {}, 'Winner'),
            h('th', { class: 'num' }, 'Highest'), h('th', {}, 'Prize'),
            h('th', { class: 'num' }, 'Prize value'), h('th', {}, 'Claim'))),
          h('tbody', {}, ...winners.map((w) => h('tr', {},
            h('td', {}, `#${w.rank}`),
            h('td', {}, w.displayName),
            h('td', { class: 'num' }, money(w.highestPrice, w.currency)),
            h('td', {}, w.prizeName),
            h('td', { class: 'num' }, money(w.prizeValue, w.currency)),
            h('td', {}, h('span', { class: badgeClass(w.claimStatus) }, w.claimStatus || '—')))))))
      : h('div', { class: 'empty' }, 'Winners have not been declared yet.'),
    h('div', { class: 'modal-actions' }, h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'))));

  document.body.append(back);
}

async function openEvents(auctionId) {
  const data = await api(`/admin/auctions/${auctionId}/events?pageSize=100`);
  const back = h('div', { class: 'modal-back' });

  back.append(h('div', { class: 'modal' },
    h('h3', {}, 'Auction event log'),
    h('p', { class: 'sub' }, 'Append-only record of everything that happened to this auction.'),
    h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, h('th', {}, 'When'), h('th', {}, 'Event'), h('th', {}, 'From'), h('th', {}, 'To'))),
      h('tbody', {}, ...data.items.map((e) => h('tr', {},
        h('td', {}, when(e.timestamp)),
        h('td', {}, h('span', { class: 'badge' }, e.eventType)),
        h('td', { class: 'mono' }, e.oldValue || '—'),
        h('td', { class: 'mono' }, e.newValue || '—')))))),
    h('div', { class: 'modal-actions' }, h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'))));

  document.body.append(back);
}

// ---------------------------------------------------------------- create auction

async function viewCreateAuction() {
  // The auction catalog is separate from the shop; prizes come from these products.
  const products = await api('/admin/catalog/products?pageSize=200');
  const opts = products.items.map((p) => ({ value: p.id, label: `${p.name} — ${money(p.price, p.currency)}` }));

  if (!opts.length) {
    return h('div', {},
      pageHead('Create auction'),
      h('div', { class: 'card' },
        h('h3', {}, 'No catalog products'),
        h('p', { class: 'hint' }, 'Add prize products first; an auction needs exactly four.')));
  }

  const f = {};
  const field = (name, label, attrs = {}, hint) => {
    const input = h('input', attrs);
    f[name] = input;
    return h('div', {}, h('label', {}, label), input, hint ? h('div', { class: 'hint' }, hint) : null);
  };
  const select = (name, label, options) => {
    const sel = h('select', {}, ...options.map((o) => h('option', { value: o.value }, o.label)));
    f[name] = sel;
    return h('div', {}, h('label', {}, label), sel);
  };

  const error = h('div', { class: 'err-text' });
  const preview = h('div', { class: 'hint' });

  const recompute = () => {
    const original = Number(f.originalPrice.value || 0);
    const target = original * Number(f.targetPercentage.value || 0) / 100;
    const finalPhase = original * Number(f.finalPhasePercentage.value || 0) / 100;
    preview.textContent =
      `Target price ${target.toFixed(2)} · final phase begins at ${finalPhase.toFixed(2)} ` +
      `· starting price must be below the target.`;
  };

  const create = async () => {
    error.textContent = '';
    try {
      const body = {
        title: f.title.value.trim(),
        description: f.description.value.trim() || null,
        mainProductId: f.mainProduct.value,
        originalPrice: Number(f.originalPrice.value),
        startingPrice: Number(f.startingPrice.value),
        targetPercentage: Number(f.targetPercentage.value),
        finalPhasePercentage: Number(f.finalPhasePercentage.value),
        bidIncrement: Number(f.bidIncrement.value),
        entryFee: Number(f.entryFee.value),
        currency: 'USD',
        requiredParticipants: Number(f.requiredParticipants.value),
        startDelayAfterParticipantsSeconds: Number(f.startDelay.value),
        durationSeconds: Number(f.duration.value),
        expirationPolicy: f.expirationPolicy.value,
        prizes: [
          { productId: f.mainProduct.value, rank: 1 },
          { productId: f.prize2.value, rank: 2 },
          { productId: f.prize3.value, rank: 3 },
          { productId: f.prize4.value, rank: 4 },
        ],
      };

      const created = await api('/admin/auctions', { method: 'POST', body: JSON.stringify(body) });
      toast(`Created ${created.auctionNumber}. Publish it when the prizes are correct.`, 'ok');
      state.route = 'auctions';
      render();
    } catch (e) {
      error.textContent = e.message + (e.details?.length ? ` — ${e.details.join('; ')}` : '');
    }
  };

  const view = h('div', {},
    pageHead('Create auction', 'An auction needs exactly four ranked prizes before it can be published.'),

    h('div', { class: 'card' },
      h('h3', {}, '1 · Basic information'),
      field('title', 'Title', { placeholder: 'Luxury SUV Grand Auction' }),
      field('description', 'Description', { placeholder: 'Optional' })),

    h('div', { class: 'card' },
      h('h3', {}, '2 · Prizes'),
      select('mainProduct', 'Prize #1 — main product (competes on price)', opts),
      select('prize2', 'Prize #2 — second place', opts),
      select('prize3', 'Prize #3 — third place', opts),
      select('prize4', 'Prize #4 — fourth place', opts)),

    h('div', { class: 'card' },
      h('h3', {}, '3 · Pricing'),
      h('div', { class: 'grid-3' },
        field('originalPrice', 'Original price', { type: 'number', value: '5000', step: '0.01' }),
        field('startingPrice', 'Starting price', { type: 'number', value: '1000', step: '0.01' }),
        field('bidIncrement', 'Bid increment', { type: 'number', value: '20', step: '0.01' },
          'Moves the price; it is not charged to the bidder.')),
      h('div', { class: 'grid-2' },
        field('targetPercentage', 'Target %', { type: 'number', value: '50', step: '0.01' }),
        field('finalPhasePercentage', 'Final phase %', { type: 'number', value: '40', step: '0.01' })),
      preview),

    h('div', { class: 'card' },
      h('h3', {}, '4 · Participation & timing'),
      h('div', { class: 'grid-3' },
        field('entryFee', 'Entry fee', { type: 'number', value: '20', step: '0.01' },
          'Charged once from the Main wallet.'),
        field('requiredParticipants', 'Required participants', { type: 'number', value: '400' }),
        field('duration', 'Duration (seconds)', { type: 'number', value: '180' })),
      h('div', { class: 'grid-2' },
        field('startDelay', 'Start delay after target reached (seconds)', { type: 'number', value: '7200' }),
        select('expirationPolicy', 'Expiration policy', [
          { value: 'EndAndDistribute', label: 'End and distribute (recommended)' },
          { value: 'Extend', label: 'Extend' },
          { value: 'Cancel', label: 'Cancel' },
          { value: 'Restart', label: 'Restart' },
        ]))),

    h('div', { class: 'card' },
      error,
      h('div', { class: 'row' },
        h('button', { class: 'btn', onclick: create }, 'Create as draft'),
        h('button', { class: 'btn ghost', onclick: () => { state.route = 'auctions'; render(); } }, 'Cancel'))));

  ['originalPrice', 'targetPercentage', 'finalPhasePercentage'].forEach((k) =>
    f[k].addEventListener('input', recompute));
  recompute();

  return view;
}

// ---------------------------------------------------------------- store

async function viewStoreOverview() {
  const o = await api('/admin/store/overview');

  return h('div', {},
    pageHead('Store overview'),
    h('div', { class: 'kpi-grid' },
      kpi('Products', num(o.totalProducts)),
      kpi('Active', num(o.activeProducts)),
      kpi('Out of stock', num(o.outOfStockProducts)),
      kpi('Low stock', num(o.lowStockProducts))),
    h('div', { class: 'kpi-grid' },
      kpi('Orders', num(o.totalOrders)),
      kpi('Pending', num(o.pendingOrders)),
      kpi('Completed', num(o.completedOrders)),
      kpi('Store revenue', money(o.storeRevenue, o.currency), true)),
    h('div', { class: 'card' },
      h('h3', {}, 'Shopping credit'),
      h('div', { class: 'kpi-grid' },
        kpi('Issued', money(o.shoppingCreditIssued, o.currency), true),
        kpi('Used', money(o.shoppingCreditUsed, o.currency), true),
        kpi('Outstanding', money(o.shoppingCreditOutstanding, o.currency), true))));
}

async function viewStoreProducts() {
  const q = state.cache.products || (state.cache.products = { page: 1, search: '', status: '' });

  const params = new URLSearchParams({ page: q.page, pageSize: 20 });
  if (q.search) params.set('search', q.search);
  if (q.status) params.set('status', q.status);

  const [data, categories] = await Promise.all([
    api(`/admin/store/products?${params}`),
    api('/admin/store/categories'),
  ]);

  const search = h('input', { placeholder: 'Name, SKU or category', value: q.search });
  const status = h('select', {}, ...[['', 'Any status'], ['Draft', 'Draft'], ['Active', 'Active'],
    ['OutOfStock', 'Out of stock'], ['Hidden', 'Hidden'], ['Archived', 'Archived']]
    .map(([v, l]) => h('option', { value: v, selected: v === q.status ? '' : null }, l)));

  const setStatus = async (id, action) => {
    try {
      await api(`/admin/store/products/${id}/${action}`, { method: 'POST' });
      toast('Product updated.', 'ok');
      render();
    } catch (e) { toast(e.message, 'err'); }
  };

  return h('div', {},
    pageHead('Store products', 'Products referenced by an order are archived rather than deleted.',
      h('button', { class: 'btn', onclick: () => productForm(null, categories) }, 'Add product')),

    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search, status,
        h('button', {
          class: 'btn ghost sm',
          onclick: () => { q.search = search.value; q.status = status.value; q.page = 1; render(); },
        }, 'Apply')),

      pagedTable({
        columns: [
          { label: 'Product', render: (p) => p.name },
          { label: 'SKU', mono: true, render: (p) => p.sku },
          { label: 'Category', render: (p) => p.categoryName || '—' },
          { label: 'Price', num: true, render: (p) => money(p.price, p.currency) },
          { label: 'Available', num: true, render: (p) => num(p.availableQuantity) },
          { label: 'Pays with', render: (p) => h('span', { class: 'badge info' }, p.paymentMode) },
          { label: 'Status', render: (p) => h('span', { class: badgeClass(p.status) }, p.status) },
          {
            label: '',
            render: (p) => h('div', { class: 'row' },
              h('button', { class: 'btn ghost sm', onclick: () => editProduct(p.id, categories) }, 'Edit'),
              p.status !== 'Active'
                ? h('button', { class: 'btn ghost sm', onclick: () => setStatus(p.id, 'activate') }, 'Activate')
                : h('button', { class: 'btn ghost sm', onclick: () => setStatus(p.id, 'hide') }, 'Hide'),
              h('button', { class: 'btn ghost sm', onclick: () => setStatus(p.id, 'archive') }, 'Archive')),
          },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function editProduct(id, categories) {
  const p = await api(`/admin/store/products/${id}`);
  productForm(p, categories);
}

function productForm(p, categories) {
  const isNew = !p;
  const catOptions = [{ value: '', label: '— none —' },
    ...categories.map((c) => ({ value: c.id, label: c.name }))];

  formModal(isNew ? 'Add product' : `Edit ${p.name}`,
    isNew ? 'New products start as drafts; activate them when they are ready to sell.'
          : 'Changing the price never rewrites existing orders: those hold their own snapshots.',
    [
      { name: 'name', label: 'Name', value: p?.name },
      { name: 'sku', label: 'SKU', value: p?.sku, placeholder: 'SKU-HEADPHONES-001' },
      { name: 'description', label: 'Description', value: p?.description },
      { name: 'categoryId', label: 'Category', type: 'select', options: catOptions, value: p?.categoryId || '' },
      { name: 'price', label: 'Price', type: 'number', step: '0.01', value: p?.price ?? 0 },
      ...(isNew ? [{ name: 'stockQuantity', label: 'Initial stock', type: 'number', value: 0 }] : []),
      {
        name: 'paymentMode', label: 'Accepted payment', type: 'select', value: p?.paymentMode || 'ShoppingWallet',
        options: [
          { value: 'ShoppingWallet', label: 'Shopping wallet only' },
          { value: 'MainWallet', label: 'Main wallet only' },
          { value: 'Both', label: 'Both wallets' },
        ],
      },
      { name: 'shippingFee', label: 'Shipping fee', type: 'number', step: '0.01', value: p?.shippingFee ?? 0 },
      { name: 'lowStockThreshold', label: 'Low stock threshold', type: 'number', value: p?.lowStockThreshold ?? 5 },
      { name: 'imageUrl', label: 'Primary image URL', value: p?.images?.[0]?.url || '' },
      {
        name: 'isFeatured', label: 'Featured', type: 'select', value: String(p?.isFeatured ?? false),
        options: [{ value: 'false', label: 'No' }, { value: 'true', label: 'Yes' }],
      },
    ],
    async (v) => {
      const body = {
        name: v.name,
        sku: v.sku,
        description: v.description || null,
        categoryId: v.categoryId || null,
        price: Number(v.price),
        paymentMode: v.paymentMode,
        shippingFee: Number(v.shippingFee),
        lowStockThreshold: Number(v.lowStockThreshold),
        isFeatured: v.isFeatured === 'true',
        requiresShipping: true,
        images: v.imageUrl ? [{ url: v.imageUrl, thumbnailUrl: null, isPrimary: true, displayOrder: 0 }] : null,
      };

      if (isNew) {
        await api('/admin/store/products', {
          method: 'POST',
          body: JSON.stringify({ ...body, stockQuantity: Number(v.stockQuantity), status: 'Draft', currency: 'USD' }),
        });
      } else {
        await api(`/admin/store/products/${p.id}`, { method: 'PUT', body: JSON.stringify(body) });
      }

      toast('Product saved.', 'ok');
      render();
    });
}

async function viewStoreCategories() {
  const categories = await api('/admin/store/categories');

  const save = (c) => formModal(c ? 'Edit category' : 'Add category', null, [
    { name: 'name', label: 'Name', value: c?.name },
    { name: 'description', label: 'Description', value: c?.description },
    { name: 'imageUrl', label: 'Image URL', value: c?.imageUrl },
    { name: 'displayOrder', label: 'Display order', type: 'number', value: c?.displayOrder ?? 0 },
  ], async (v) => {
    const body = {
      name: v.name,
      slug: null,
      description: v.description || null,
      imageUrl: v.imageUrl || null,
      displayOrder: Number(v.displayOrder),
    };
    if (c) await api(`/admin/store/categories/${c.id}`, { method: 'PUT', body: JSON.stringify({ ...body, status: c.status }) });
    else await api('/admin/store/categories', { method: 'POST', body: JSON.stringify(body) });
    toast('Category saved.', 'ok');
    render();
  });

  return h('div', {},
    pageHead('Categories', null, h('button', { class: 'btn', onclick: () => save(null) }, 'Add category')),
    h('div', { class: 'card' },
      categories.length
        ? h('div', { class: 'table-wrap' }, h('table', {},
            h('thead', {}, h('tr', {}, h('th', {}, 'Name'), h('th', {}, 'Slug'),
              h('th', { class: 'num' }, 'Products'), h('th', { class: 'num' }, 'Order'),
              h('th', {}, 'Status'), h('th', {}, ''))),
            h('tbody', {}, ...categories.map((c) => h('tr', {},
              h('td', {}, c.name),
              h('td', { class: 'mono' }, c.slug),
              h('td', { class: 'num' }, num(c.productCount)),
              h('td', { class: 'num' }, c.displayOrder),
              h('td', {}, h('span', { class: badgeClass(c.status) }, c.status)),
              h('td', {}, h('button', { class: 'btn ghost sm', onclick: () => save(c) }, 'Edit')))))))
        : h('div', { class: 'empty' }, 'No categories yet.')));
}

async function viewInventory() {
  const q = state.cache.inventory || (state.cache.inventory = { page: 1, search: '', lowOnly: false });

  const params = new URLSearchParams({ page: q.page, pageSize: 20 });
  if (q.search) params.set('search', q.search);
  if (q.lowOnly) params.set('lowStockOnly', 'true');

  const data = await api(`/admin/store/inventory?${params}`);

  const search = h('input', { placeholder: 'Name or SKU', value: q.search });
  const low = h('select', {},
    h('option', { value: '', selected: q.lowOnly ? null : '' }, 'All stock'),
    h('option', { value: '1', selected: q.lowOnly ? '' : null }, 'Low stock only'));

  const adjust = (row) => formModal(`Adjust stock — ${row.name}`,
    `Current stock ${row.stockQuantity}. Adjustments are audited and always leave a transaction.`, [
      { name: 'delta', label: 'Change (+ to restock, − to remove)', type: 'number', value: 0 },
      { name: 'reason', label: 'Reason (required)', placeholder: 'New inventory shipment' },
    ], async (v) => {
      await api(`/admin/store/inventory/${row.productId}/adjust`, {
        method: 'POST',
        body: JSON.stringify({ delta: Number(v.delta), reason: v.reason, type: null }),
      });
      toast('Stock adjusted.', 'ok');
      render();
    });

  return h('div', {},
    pageHead('Inventory', 'Stock only moves alongside an inventory transaction.'),
    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search, low,
        h('button', {
          class: 'btn ghost sm',
          onclick: () => { q.search = search.value; q.lowOnly = low.value === '1'; q.page = 1; render(); },
        }, 'Apply')),

      pagedTable({
        columns: [
          { label: 'Product', render: (r) => r.name },
          { label: 'SKU', mono: true, render: (r) => r.sku },
          { label: 'Stock', num: true, render: (r) => num(r.stockQuantity) },
          { label: 'Reserved', num: true, render: (r) => num(r.reservedQuantity) },
          { label: 'Available', num: true, render: (r) => num(r.availableQuantity) },
          { label: 'Sold', num: true, render: (r) => num(r.soldQuantity) },
          {
            label: 'Alert',
            render: (r) => r.isLowStock
              ? h('span', { class: 'badge warn' }, `Low (≤${r.lowStockThreshold})`)
              : h('span', { class: 'badge ok' }, 'OK'),
          },
          { label: '', render: (r) => h('button', { class: 'btn ghost sm', onclick: () => adjust(r) }, 'Adjust') },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function viewOrders() {
  const q = state.cache.orders || (state.cache.orders = { page: 1, search: '', status: '' });

  const params = new URLSearchParams({ page: q.page, pageSize: 20 });
  if (q.search) params.set('search', q.search);
  if (q.status) params.set('status', q.status);

  const data = await api(`/admin/store/orders?${params}`);

  const search = h('input', { placeholder: 'Order number or customer', value: q.search });
  const status = h('select', {}, ...[['', 'Any status'], ['Pending', 'Pending'], ['Paid', 'Paid'],
    ['Processing', 'Processing'], ['Shipped', 'Shipped'], ['Delivered', 'Delivered'],
    ['Completed', 'Completed'], ['Cancelled', 'Cancelled'], ['Refunded', 'Refunded']]
    .map(([v, l]) => h('option', { value: v, selected: v === q.status ? '' : null }, l)));

  return h('div', {},
    pageHead('Store orders'),
    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search, status,
        h('button', {
          class: 'btn ghost sm',
          onclick: () => { q.search = search.value; q.status = status.value; q.page = 1; render(); },
        }, 'Apply')),

      pagedTable({
        columns: [
          { label: 'Order', mono: true, render: (o) => o.orderNumber },
          { label: 'Customer', render: (o) => o.customerName || shortId(o.userId) },
          { label: 'Items', num: true, render: (o) => o.items.length },
          { label: 'Total', num: true, render: (o) => money(o.total, o.currency) },
          { label: 'Paid with', render: (o) => h('span', { class: 'badge info' }, o.paymentMethod) },
          { label: 'Status', render: (o) => h('span', { class: badgeClass(o.status) }, o.status) },
          { label: 'Created', render: (o) => when(o.createdAt) },
          { label: '', render: (o) => h('button', { class: 'btn ghost sm', onclick: () => openOrder(o.id) }, 'Open') },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function openOrder(id) {
  const o = await api(`/admin/store/orders/${id}`);
  const back = h('div', { class: 'modal-back' });

  const act = async (path, body, message) => {
    try {
      await api(`/admin/store/orders/${o.id}/${path}`, { method: 'POST', body: JSON.stringify(body || {}) });
      back.remove();
      toast(message, 'ok');
      render();
    } catch (e) { toast(e.message, 'err'); }
  };

  const next = {
    Paid: [['process', 'Mark processing']],
    Processing: [['ship', 'Mark shipped']],
    Shipped: [['deliver', 'Mark delivered']],
    Delivered: [['complete', 'Mark completed']],
  }[o.status] || [];

  back.append(h('div', { class: 'modal' },
    h('h3', {}, o.orderNumber),
    h('p', { class: 'sub' }, h('span', { class: badgeClass(o.status) }, o.status),
      ` · paid with ${o.paymentMethod}`),

    h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, h('th', {}, 'Product'), h('th', { class: 'num' }, 'Unit'),
        h('th', { class: 'num' }, 'Qty'), h('th', { class: 'num' }, 'Total'))),
      h('tbody', {}, ...o.items.map((i) => h('tr', {},
        h('td', {}, i.productName),
        h('td', { class: 'num' }, money(i.unitPrice, o.currency)),
        h('td', { class: 'num' }, i.quantity),
        h('td', { class: 'num' }, money(i.totalPrice, o.currency))))))),

    h('div', { class: 'grid-2', style: 'margin-top:12px' },
      h('div', {}, h('label', {}, 'Subtotal'), h('div', {}, money(o.subtotal, o.currency))),
      h('div', {}, h('label', {}, 'Shipping'), h('div', {}, money(o.shippingFee, o.currency))),
      h('div', {}, h('label', {}, 'Tax'), h('div', {}, money(o.tax, o.currency))),
      h('div', {}, h('label', {}, 'Total'), h('div', {}, money(o.total, o.currency)))),

    o.shippingAddress
      ? h('div', {}, h('label', {}, 'Shipping address'),
          h('div', { class: 'hint' },
            `${o.shippingAddress.fullName}, ${o.shippingAddress.addressLine1}, ` +
            `${o.shippingAddress.city}, ${o.shippingAddress.country} · ${o.shippingAddress.phone}`))
      : null,

    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'),
      ...next.map(([path, label]) =>
        h('button', {
          class: 'btn',
          onclick: () => {
            if (path === 'ship') {
              back.remove();
              formModal('Mark shipped', 'Adding a tracking number is optional.',
                [{ name: 'note', label: 'Tracking number' }],
                async (v) => {
                  await api(`/admin/store/orders/${o.id}/ship`, { method: 'POST', body: JSON.stringify({ note: v.note }) });
                  toast('Order marked shipped.', 'ok');
                  render();
                });
            } else {
              act(path, {}, label + '.');
            }
          },
        }, label)),

      ['Paid', 'Processing', 'Pending'].includes(o.status)
        ? h('button', {
            class: 'btn danger',
            onclick: () => {
              back.remove();
              confirmDialog('Cancel order',
                'Stock is returned and the money goes back to the wallet it came from.',
                (reason) => act('cancel', { note: reason }, 'Order cancelled and refunded.'),
                { danger: true, needsReason: true });
            },
          }, 'Cancel & refund')
        : null,

      ['Delivered', 'Completed'].includes(o.status)
        ? h('button', {
            class: 'btn danger',
            onclick: () => {
              back.remove();
              confirmDialog('Refund order',
                'A shopping-wallet purchase is refunded as shopping credit, never as cash.',
                async (reason) => {
                  try {
                    await api(`/admin/store/orders/${o.id}/refund`, {
                      method: 'POST', body: JSON.stringify({ reason }),
                    });
                    toast('Order refunded.', 'ok');
                    render();
                  } catch (e) { toast(e.message, 'err'); }
                }, { danger: true, needsReason: true });
            },
          }, 'Refund')
        : null)));

  document.body.append(back);
}

async function viewStoreSettings() {
  const s = await api('/admin/store/settings');

  const f = {};
  const bool = (name, label, value) => {
    const sel = h('select', {},
      h('option', { value: 'true', selected: value ? '' : null }, 'Enabled'),
      h('option', { value: 'false', selected: value ? null : '' }, 'Disabled'));
    f[name] = sel;
    return h('div', {}, h('label', {}, label), sel);
  };
  const numField = (name, label, value, step = '1') => {
    const input = h('input', { type: 'number', value, step });
    f[name] = input;
    return h('div', {}, h('label', {}, label), input);
  };

  const save = async () => {
    try {
      const body = {
        storeEnabled: f.storeEnabled.value === 'true',
        shoppingWalletEnabled: f.shoppingWalletEnabled.value === 'true',
        allowShoppingWalletPurchases: f.allowShopping.value === 'true',
        allowMainWalletPurchases: f.allowMain.value === 'true',
        defaultCurrency: s.defaultCurrency,
        taxEnabled: f.taxEnabled.value === 'true',
        taxPercentage: Number(f.taxPercentage.value),
        shippingEnabled: f.shippingEnabled.value === 'true',
        defaultShippingFee: Number(f.defaultShippingFee.value),
        minimumOrderAmount: Number(f.minimumOrderAmount.value),
        maximumOrderAmount: Number(f.maximumOrderAmount.value),
        shoppingCreditExpirationEnabled: f.creditExpiry.value === 'true',
        shoppingCreditExpirationDays: Number(f.creditExpiryDays.value),
        refundPolicy: s.refundPolicy,
        lowStockThreshold: Number(f.lowStockThreshold.value),
      };
      await api('/admin/store/settings', { method: 'PUT', body: JSON.stringify(body) });
      toast('Settings saved.', 'ok');
    } catch (e) { toast(e.message, 'err'); }
  };

  return h('div', {},
    pageHead('Store settings'),
    h('div', { class: 'card' },
      h('h3', {}, 'Availability'),
      h('div', { class: 'grid-2' },
        bool('storeEnabled', 'Store', s.storeEnabled),
        bool('shoppingWalletEnabled', 'Shopping wallet', s.shoppingWalletEnabled),
        bool('allowShopping', 'Allow shopping wallet purchases', s.allowShoppingWalletPurchases),
        bool('allowMain', 'Allow main wallet purchases', s.allowMainWalletPurchases))),

    h('div', { class: 'card' },
      h('h3', {}, 'Pricing & shipping'),
      h('div', { class: 'grid-2' },
        bool('taxEnabled', 'Tax', s.taxEnabled),
        numField('taxPercentage', 'Tax %', s.taxPercentage, '0.01'),
        bool('shippingEnabled', 'Shipping', s.shippingEnabled),
        numField('defaultShippingFee', 'Default shipping fee', s.defaultShippingFee, '0.01'),
        numField('minimumOrderAmount', 'Minimum order', s.minimumOrderAmount, '0.01'),
        numField('maximumOrderAmount', 'Maximum order', s.maximumOrderAmount, '0.01'))),

    h('div', { class: 'card' },
      h('h3', {}, 'Shopping credit & stock'),
      h('div', { class: 'grid-2' },
        bool('creditExpiry', 'Credit expiration', s.shoppingCreditExpirationEnabled),
        numField('creditExpiryDays', 'Expires after (days)', s.shoppingCreditExpirationDays),
        numField('lowStockThreshold', 'Low stock threshold', s.lowStockThreshold)),
      h('div', { class: 'hint', style: 'margin-top:8px' },
        'Credit is only ever expired through an explicit ledger entry, so a balance always matches its history.')),

    h('div', { class: 'card' }, h('button', { class: 'btn', onclick: save }, 'Save settings')));
}

// ---------------------------------------------------------------- finance

async function viewWallets() {
  const q = state.cache.wallets || (state.cache.wallets = { page: 1, search: '' });

  const params = new URLSearchParams({ page: q.page, pageSize: 20 });
  if (q.search) params.set('search', q.search);

  const data = await api(`/admin/wallets?${params}`);
  const search = h('input', { placeholder: 'Name or email', value: q.search });

  return h('div', {},
    pageHead('Wallets', 'Main and Shopping balances are always reported separately.'),
    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search,
        h('button', { class: 'btn ghost sm', onclick: () => { q.search = search.value; q.page = 1; render(); } }, 'Search')),

      pagedTable({
        columns: [
          { label: 'User', render: (w) => w.fullName },
          { label: 'Email', render: (w) => w.email || '—' },
          { label: 'Main (withdrawable)', num: true, render: (w) => money(w.mainBalance, w.currency) },
          { label: 'Shopping (store only)', num: true, render: (w) => money(w.shoppingBalance, w.currency) },
          { label: 'Auction refunds', num: true, render: (w) => money(w.totalAuctionRefunds, w.currency) },
          { label: 'Store spend', num: true, render: (w) => money(w.totalStorePurchases, w.currency) },
          { label: 'Last activity', render: (w) => when(w.lastTransactionAt) },
          { label: '', render: (w) => h('button', { class: 'btn ghost sm', onclick: () => openWallet(w) }, 'Open') },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function openWallet(w) {
  const tx = await api(`/admin/wallets/${w.userId}/transactions?pageSize=50`);
  const back = h('div', { class: 'modal-back' });

  const adjust = (walletType) => {
    back.remove();
    formModal(`Adjust ${walletType} wallet — ${w.fullName}`,
      'Positive credits, negative debits. A reason is required and the change is audited.', [
        { name: 'amount', label: 'Amount', type: 'number', step: '0.01', value: 0 },
        { name: 'reason', label: 'Reason (required)' },
      ], async (v) => {
        await api(`/admin/users/${w.userId}/wallet-adjustment`, {
          method: 'POST',
          body: JSON.stringify({
            amount: Number(v.amount),
            currency: w.currency,
            walletType,
            reason: v.reason,
            idempotencyKey: `adj-${Date.now()}-${Math.random().toString(36).slice(2)}`,
          }),
        });
        toast('Wallet adjusted.', 'ok');
        render();
      });
  };

  back.append(h('div', { class: 'modal' },
    h('h3', {}, w.fullName),
    h('p', { class: 'sub' }, w.email || shortId(w.userId)),

    h('div', { class: 'wallet-split' },
      h('div', { class: 'wallet-box' },
        h('div', { class: 't' }, 'Main wallet'),
        h('div', { class: 'v' }, money(w.mainBalance, w.currency)),
        h('div', { class: 'n' }, 'Withdrawable'),
        h('button', { class: 'btn ghost sm', style: 'margin-top:10px', onclick: () => adjust('Main') }, 'Adjust')),
      h('div', { class: 'wallet-box' },
        h('div', { class: 't' }, 'Shopping credit'),
        h('div', { class: 'v' }, money(w.shoppingBalance, w.currency)),
        h('div', { class: 'n' }, 'Store purchases only · never withdrawable'),
        h('button', { class: 'btn ghost sm', style: 'margin-top:10px', onclick: () => adjust('Shopping') }, 'Issue credit'))),

    h('label', { style: 'margin-top:16px' }, 'Ledger'),
    h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, h('th', {}, 'When'), h('th', {}, 'Wallet'), h('th', {}, 'Type'),
        h('th', { class: 'num' }, 'Amount'), h('th', { class: 'num' }, 'Balance after'), h('th', {}, 'Description'))),
      h('tbody', {}, ...tx.items.map((t) => h('tr', {},
        h('td', {}, when(t.createdAt)),
        h('td', {}, h('span', { class: t.walletType === 'Shopping' ? 'badge info' : 'badge' }, t.walletType)),
        h('td', {}, t.transactionType),
        h('td', { class: 'num' }, money(t.amount, t.currency)),
        h('td', { class: 'num' }, money(t.balanceAfter, t.currency)),
        h('td', {}, t.description || '—')))))),

    h('div', { class: 'modal-actions' }, h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'))));

  document.body.append(back);
}

async function viewShoppingCredits() {
  const q = state.cache.credits || (state.cache.credits = { page: 1 });

  const [summary, data] = await Promise.all([
    api('/admin/shopping-credits/summary?currency=USD'),
    api(`/admin/shopping-credits?page=${q.page}&pageSize=20`),
  ]);

  return h('div', {},
    pageHead('Shopping credits', 'The closed loop: entry fees in, store purchases out.'),

    h('div', { class: 'kpi-grid' },
      kpi('Issued', money(summary.totalIssued, summary.currency), true),
      kpi('Used', money(summary.totalUsed, summary.currency), true),
      kpi('Outstanding', money(summary.outstanding, summary.currency), true)),

    h('div', { class: 'card' },
      pagedTable({
        columns: [
          { label: 'User', render: (c) => c.fullName },
          { label: 'Email', render: (c) => c.email || '—' },
          { label: 'Issued', num: true, render: (c) => money(c.issued, c.currency) },
          { label: 'Used', num: true, render: (c) => money(c.used, c.currency) },
          { label: 'Remaining', num: true, render: (c) => money(c.remaining, c.currency) },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

// ---------------------------------------------------------------- system

async function viewUsers() {
  const q = state.cache.users || (state.cache.users = { page: 1, search: '' });

  const params = new URLSearchParams({ page: q.page, pageSize: 20 });
  if (q.search) params.set('search', q.search);

  const data = await api(`/admin/users?${params}`);
  const search = h('input', { placeholder: 'Name, email or phone', value: q.search });

  const setActive = (u) => confirmDialog(
    u.isActive ? 'Disable account' : 'Enable account',
    u.isActive ? 'Disabling revokes every active session.' : 'The user will be able to sign in again.',
    async (reason) => {
      try {
        await api(`/admin/users/${u.id}/active`, {
          method: 'PUT', body: JSON.stringify({ isActive: !u.isActive, reason }),
        });
        toast('User updated.', 'ok');
        render();
      } catch (e) { toast(e.message, 'err'); }
    }, { danger: u.isActive, needsReason: true });

  const editRoles = (u) => formModal(`Roles — ${u.fullName}`,
    'Comma-separated. Only SUPER_ADMIN may change roles.', [
      { name: 'roles', label: 'Roles', value: u.roles.join(', ') },
      { name: 'reason', label: 'Reason (required)' },
    ], async (v) => {
      await api(`/admin/users/${u.id}/roles`, {
        method: 'PUT',
        body: JSON.stringify({
          roles: v.roles.split(',').map((r) => r.trim()).filter(Boolean),
          reason: v.reason,
        }),
      });
      toast('Roles updated.', 'ok');
      render();
    });

  return h('div', {},
    pageHead('Users'),
    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search,
        h('button', { class: 'btn ghost sm', onclick: () => { q.search = search.value; q.page = 1; render(); } }, 'Search')),

      pagedTable({
        columns: [
          { label: 'Name', render: (u) => u.fullName },
          { label: 'Email', render: (u) => u.email },
          { label: 'Roles', render: (u) => h('div', { class: 'row' }, ...u.roles.map((r) => h('span', { class: 'badge' }, r))) },
          { label: 'Main', num: true, render: (u) => money(u.mainWalletBalance, u.currency) },
          { label: 'Shopping', num: true, render: (u) => money(u.shoppingWalletBalance, u.currency) },
          { label: 'Status', render: (u) => h('span', { class: u.isActive ? 'badge ok' : 'badge danger' }, u.isActive ? 'Active' : 'Disabled') },
          { label: 'Last login', render: (u) => when(u.lastLoginAt) },
          {
            label: '',
            render: (u) => h('div', { class: 'row' },
              h('button', { class: 'btn ghost sm', onclick: () => editRoles(u) }, 'Roles'),
              h('button', { class: 'btn ghost sm', onclick: () => setActive(u) }, u.isActive ? 'Disable' : 'Enable')),
          },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function viewAudit() {
  const q = state.cache.audit || (state.cache.audit = { page: 1, search: '' });

  const params = new URLSearchParams({ page: q.page, pageSize: 30 });
  if (q.search) params.set('search', q.search);

  const data = await api(`/admin/audit-logs?${params}`);
  const search = h('input', { placeholder: 'Actor email or reason', value: q.search });

  return h('div', {},
    pageHead('Audit log', 'Append-only record of every administrative and financial action.'),
    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search,
        h('button', { class: 'btn ghost sm', onclick: () => { q.search = search.value; q.page = 1; render(); } }, 'Search')),

      pagedTable({
        columns: [
          { label: 'When', render: (a) => when(a.createdAt) },
          { label: 'Action', render: (a) => h('span', { class: 'badge' }, a.action) },
          { label: 'Actor', render: (a) => a.actorEmail || 'system' },
          { label: 'Entity', render: (a) => `${a.entityType} ${shortId(a.entityId)}` },
          { label: 'Reason', render: (a) => a.reason || '—' },
          { label: 'IP', mono: true, render: (a) => a.ipAddress || '—' },
          {
            label: 'Change',
            render: (a) => h('button', {
              class: 'btn ghost sm',
              onclick: () => {
                const back = h('div', { class: 'modal-back' });
                back.append(h('div', { class: 'modal' },
                  h('h3', {}, a.action),
                  h('p', { class: 'sub' }, when(a.createdAt)),
                  h('label', {}, 'Before'),
                  h('pre', { class: 'mono', style: 'white-space:pre-wrap' }, a.oldValue || '—'),
                  h('label', {}, 'After'),
                  h('pre', { class: 'mono', style: 'white-space:pre-wrap' }, a.newValue || '—'),
                  h('div', { class: 'modal-actions' },
                    h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'))));
                document.body.append(back);
              },
            }, 'View'),
          },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

// ---------------------------------------------------------------- bootstrap

function render() {
  const app = document.getElementById('app');
  app.replaceChildren(state.auth ? renderShell() : renderLogin());
}

state.auth = store.load();
render();
