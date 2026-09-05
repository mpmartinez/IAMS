// Loads a third-party script the first time something actually needs it, and only once.
//
// html5-qrcode used to sit in index.html as a plain <script>, so every visitor fetched it
// cross-origin from unpkg on the boot path - competing with the Blazor _framework payload -
// even though only /scan ever uses it. Callers now await loadScriptOnce() at their entry
// point instead; see js/qrScanner.js.
//
// The promise is cached per URL so concurrent callers share one fetch, and a failed load is
// evicted so a later attempt can retry rather than resolving against a script that never ran.
window.loadScriptOnce = (function () {
    const pending = new Map();

    return function (src) {
        if (pending.has(src)) return pending.get(src);

        const promise = new Promise((resolve, reject) => {
            const el = document.createElement('script');
            el.src = src;
            el.async = true;
            el.onload = () => resolve();
            el.onerror = () => {
                pending.delete(src);
                reject(new Error('Failed to load ' + src));
            };
            document.head.appendChild(el);
        });

        pending.set(src, promise);
        return promise;
    };
})();
