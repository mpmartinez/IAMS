// Bump this whenever a precached asset below changes. The activate handler deletes every cache
// whose key differs, so a new name is what actually evicts the old copies.
const cacheName = 'iams-cache-v4';
const offlineUrl = 'offline.html';

self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(cacheName).then(cache => {
            return cache.addAll([
                offlineUrl,
                // Must match the ?v= in index.html exactly. A cache entry is keyed by full URL,
                // so precaching the bare path would store something the page never asks for and
                // leave the stylesheet unavailable offline.
                'css/app.css?v=3',
                'manifest.webmanifest'
            ]);
        })
    );
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys().then(keys => {
            return Promise.all(
                keys.filter(key => key !== cacheName).map(key => caches.delete(key))
            );
        })
    );
    self.clients.claim();
});

self.addEventListener('fetch', event => {
    if (event.request.method !== 'GET') return;

    const url = new URL(event.request.url);

    // Don't cache _framework files - they have hash-based versioning
    if (url.pathname.startsWith('/_framework/')) {
        event.respondWith(fetch(event.request));
        return;
    }

    event.respondWith(
        fetch(event.request)
            .then(response => {
                const responseClone = response.clone();
                caches.open(cacheName).then(cache => {
                    cache.put(event.request, responseClone);
                });
                return response;
            })
            .catch(() => {
                return caches.match(event.request).then(response => {
                    return response || caches.match(offlineUrl);
                });
            })
    );
});
