/* ============================================================
   Monaco Editor AMD loader shim.
   Tries to load Monaco from a bundled _content/monaco-editor path first;
   falls back to the CDN if the bundled copy is missing.
   Exposes `window.harborMonaco.init(containerId, value, lang, theme)`
   which the C# HarborJsInterop class calls.
   ============================================================ */

(function () {
    'use strict';

    // Inject requirejs if not already present so we can use AMD config.
    function ensureRequirejs() {
        return new Promise((resolve) => {
            if (typeof require !== 'undefined' && typeof require.config === 'function') {
                resolve();
                return;
            }
            const s = document.createElement('script');
            s.src = 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs/loader.min.js';
            s.onload = () => resolve();
            s.onerror = () => resolve(); // proceed — harborMonaco.init handles missing require
            document.head.appendChild(s);
        });
    }

    // Run on DOMContentLoaded so the shim is ready before interop.js calls it.
    document.addEventListener('DOMContentLoaded', ensureRequirejs);
})();
