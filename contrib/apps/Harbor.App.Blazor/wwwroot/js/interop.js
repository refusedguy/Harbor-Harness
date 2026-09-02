/* ============================================================
   Harbor Blazor — Monaco Editor loader + JS interop shim.
   Exposes two global objects:
     - harborMonaco: editor lifecycle (init / getValue / setValue /
                     setLanguage / dispose)
     - harborCharts: Chart.js renderer (render / update / dispose)
     - harborInterop: misc DOM helpers (copyToClipboard, scrollToBottom,
                      applyTheme)
   The C# HarborJsInterop class calls into these via IJSRuntime.
   ============================================================ */

(function () {
    'use strict';

    // Track initialised editors by container id so Blazor re-renders
    // don't leak Monaco instances.
    const editors = new Map();
    let monacoReady = null;

    function loadMonaco() {
        if (monacoReady) return monacoReady;
        monacoReady = new Promise((resolve, reject) => {
            // Try the bundled loader first; fall back to CDN if missing.
            function tryRequire() {
                if (typeof require !== 'undefined' && typeof require.config === 'function') {
                    require.config({paths: {vs: '_content/monaco-editor/min/vs'}});
                    require(['vs/editor/editor.main'], function () {
                        resolve(window.monaco);
                    }, function (err) {
                        reject(err);
                    });
                } else {
                    reject(new Error('requirejs not present'));
                }
            }

            tryRequire();
        });
        return monacoReady;
    }

    window.harborMonaco = {
        async init(containerId, initialValue, language, theme) {
            const el = document.getElementById(containerId);
            if (!el) return;
            // Skip if already initialised on this element.
            if (editors.has(containerId)) return;
            try {
                const monaco = await loadMonaco();
                const editor = monaco.editor.create(el, {
                    value: initialValue || '',
                    language: language || 'markdown',
                    theme: theme || 'vs-dark',
                    automaticLayout: true,
                    fontSize: 13,
                    fontFamily: "'JetBrains Mono', 'Fira Code', monospace",
                    minimap: {enabled: false},
                    scrollBeyondLastLine: false,
                    wordWrap: 'on',
                    tabSize: 4,
                    renderWhitespace: 'selection',
                    smoothScrolling: true,
                    cursorBlinking: 'smooth',
                    cursorSmoothCaretAnimation: true
                });
                editors.set(containerId, editor);
            } catch (err) {
                // Fall back to a plain <textarea> overlay so the page is
                // still usable when Monaco can't load (offline, CSP).
                console.warn('[harborMonaco] Monaco failed to load, falling back to textarea:', err);
                el.innerHTML = '<textarea style="width:100%;height:100%;background:#11111b;color:#cdd6f4;border:none;font-family:monospace;padding:8px;"></textarea>';
                const ta = el.querySelector('textarea');
                ta.value = initialValue || '';
                editors.set(containerId, {fallback: ta});
            }
        },
        getValue(containerId) {
            const e = editors.get(containerId);
            if (!e) return '';
            if (e.fallback) return e.fallback.value;
            return e.getValue();
        },
        setValue(containerId, value) {
            const e = editors.get(containerId);
            if (!e) return;
            if (e.fallback) {
                e.fallback.value = value;
                return;
            }
            e.setValue(value);
        },
        setLanguage(containerId, language) {
            const e = editors.get(containerId);
            if (!e || e.fallback) return;
            const model = e.getModel();
            if (model && window.monaco) {
                window.monaco.editor.setModelLanguage(model, language);
            }
        },
        dispose(containerId) {
            const e = editors.get(containerId);
            if (!e) return;
            if (e.fallback) {
                e.fallback.remove();
            } else {
                e.dispose();
            }
            editors.delete(containerId);
        }
    };

    // ============== Chart.js renderer ==============
    // Lazy-loads Chart.js from CDN. Each canvas is bound to a Chart instance
    // stored in `charts` keyed by canvas id.
    const charts = new Map();
    let chartjsReady = null;

    function loadChartJs() {
        if (chartjsReady) return chartjsReady;
        chartjsReady = new Promise((resolve, reject) => {
            if (window.Chart) {
                resolve(window.Chart);
                return;
            }
            const s = document.createElement('script');
            s.src = 'https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js';
            s.onload = () => resolve(window.Chart);
            s.onerror = () => reject(new Error('Chart.js failed to load'));
            document.head.appendChild(s);
        });
        return chartjsReady;
    }

    window.harborCharts = {
        async render(canvasId, configJson) {
            const el = document.getElementById(canvasId);
            if (!el) return;
            try {
                const Chart = await loadChartJs();
                const config = JSON.parse(configJson);
                if (charts.has(canvasId)) {
                    charts.get(canvasId).destroy();
                }
                const chart = new Chart(el, config);
                charts.set(canvasId, chart);
            } catch (err) {
                console.warn('[harborCharts] failed to render chart:', err);
                // Draw a plain fallback so the user sees something.
                const ctx = el.getContext('2d');
                ctx.fillStyle = '#a6adc8';
                ctx.font = '14px sans-serif';
                ctx.fillText('Chart unavailable (offline)', 20, 30);
            }
        },
        dispose(canvasId) {
            const c = charts.get(canvasId);
            if (c) {
                c.destroy();
                charts.delete(canvasId);
            }
        }
    };

    // ============== Misc interop ==============
    window.harborInterop = {
        async copyToClipboard(text) {
            try {
                if (navigator.clipboard) {
                    await navigator.clipboard.writeText(text);
                    return true;
                }
            } catch (e) { /* fall through */
            }
            // Legacy fallback for non-secure contexts.
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.left = '-9999px';
            document.body.appendChild(ta);
            ta.select();
            try {
                document.execCommand('copy');
            } catch (e) { /* ignore */
            }
            document.body.removeChild(ta);
            return true;
        },
        scrollToBottom(elementId) {
            const el = document.getElementById(elementId);
            if (el) el.scrollTop = el.scrollHeight;
        },
        applyTheme(themeName) {
            document.documentElement.setAttribute('data-theme', themeName.toLowerCase());
        }
    };

    // Keyboard shortcut for the command palette (Ctrl+P / Cmd+P).
    document.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key === 'p') {
            e.preventDefault();
            // The Blazor layout also handles this — but we dispatch a custom
            // event so any listener can react (used as a backup).
            document.dispatchEvent(new CustomEvent('harbor:commandpalette'));
        }
    });
})();
