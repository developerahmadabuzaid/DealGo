/*
 * The app's own service worker.
 *
 * Flutter no longer ships a caching one — the file it generates only unregisters itself — so the
 * installed app would otherwise show the browser's error page the moment the network dips. This
 * keeps the app's own files (the shell: the page, the engine, the fonts and the icons) in a cache
 * and serves them from there, which also makes a cold start noticeably faster.
 *
 * What it never caches: anything under /api/ and /hubs/. Prices, balances, stock and auction state
 * must always come from the server — a stale price is worse than no price. Those requests go
 * straight to the network and fail honestly when it is down.
 *
 * CACHE_VERSION is rewritten on every build, so a new release takes over and the old files are
 * dropped rather than lingering.
 */

const CACHE_VERSION = '__BUILD_ID__';
const CACHE_NAME = `dealgo-app-${CACHE_VERSION}`;

// Enough to start the app offline. Everything else is cached the first time it is fetched.
const SHELL = [
  './',
  'index.html',
  'flutter_bootstrap.js',
  'flutter.js',
  'main.dart.js',
  'manifest.json',
  'favicon.png',
  'icons/Icon-192.png',
  'icons/Icon-512.png',
];

self.addEventListener('install', (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(CACHE_NAME);
      // One missing file must not fail the whole install, so each is added on its own.
      await Promise.all(
        SHELL.map((path) => cache.add(new Request(path, { cache: 'reload' })).catch(() => {})),
      );
      await self.skipWaiting();
    })(),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      const names = await caches.keys();
      await Promise.all(
        names
          .filter((name) => name.startsWith('dealgo-app-') && name !== CACHE_NAME)
          .map((name) => caches.delete(name)),
      );
      await self.clients.claim();
    })(),
  );
});

/** Server state, never served from a cache. */
function isServerData(url) {
  return url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs/');
}

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  if (isServerData(url)) return;

  // Opening the app: the newest page when the network allows, the cached one when it does not.
  if (request.mode === 'navigate') {
    event.respondWith(
      (async () => {
        try {
          const response = await fetch(request);
          const cache = await caches.open(CACHE_NAME);
          cache.put('index.html', response.clone());
          return response;
        } catch (error) {
          const cached = await caches.match('index.html');
          return cached ?? Response.error();
        }
      })(),
    );
    return;
  }

  // The app's own files: serve the cached copy at once, and refresh it in the background.
  event.respondWith(
    (async () => {
      const cache = await caches.open(CACHE_NAME);
      const cached = await cache.match(request);

      const network = fetch(request)
        .then((response) => {
          if (response && response.status === 200 && response.type === 'basic') {
            cache.put(request, response.clone());
          }
          return response;
        })
        .catch(() => null);

      return cached ?? (await network) ?? Response.error();
    })(),
  );
});
