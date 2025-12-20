// Development service worker - minimal caching for faster development
// The published version (service-worker.published.js) has full offline support

self.addEventListener('install', event => {
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(self.clients.claim());
});

self.addEventListener('fetch', event => {
    // In development, just pass through to network
    // This allows hot reload and fresh content
});
