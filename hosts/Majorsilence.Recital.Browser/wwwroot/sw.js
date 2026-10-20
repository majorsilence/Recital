// Recital's service worker: lets the installed app start offline. It caches this site's own files and
// nothing else (no other origin is ever fetched or stored). Fingerprinted runtime files (_framework/x.<hash>.wasm)
// never change, so they come from the cache first; everything else is fetched first so updates arrive, with
// the cached copy as the fallback offline.
const CACHE = 'recital-v1';
const fingerprinted = /\/_framework\/.+\.[a-z0-9]{8,}\.[a-z]+$/i;

self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', event => event.waitUntil((async () => {
    for (const name of await caches.keys())
        if (name !== CACHE)
            await caches.delete(name);
    await self.clients.claim();
})()));

// The page sends the files it started from (the first visit loads them before this worker is in control), so the
// next start works offline too. Only this origin's files are taken.
self.addEventListener('message', event => {
    const urls = (event.data?.cache ?? []).filter(u => new URL(u).origin === self.location.origin);
    event.waitUntil(caches.open(CACHE).then(cache => Promise.all(urls.map(u =>
        cache.match(u).then(hit => hit ?? cache.add(u)).catch(() => {})))));
});

self.addEventListener('fetch', event => {
    const request = event.request;
    const url = new URL(request.url);
    if (request.method !== 'GET' || url.origin !== self.location.origin)
        return;
    event.respondWith((async () => {
        const cache = await caches.open(CACHE);
        if (fingerprinted.test(url.pathname)) {
            const hit = await cache.match(request);
            if (hit)
                return hit;
        }
        try {
            const response = await fetch(request);
            if (response.ok)
                await cache.put(request, response.clone());
            return response;
        } catch (err) {
            const hit = await cache.match(request);
            if (hit)
                return hit;
            throw err;
        }
    })());
});
