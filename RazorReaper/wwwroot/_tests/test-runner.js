/*
 * RazorReaper runtime test runner
 * --------------------------------
 * Loaded by index.html. Reads test-config.json and runs the enabled tests
 * after Blazor has rendered. Results go to console AND to
 *   %LOCALAPPDATA%\RazorReaper\Logs\test-results.log
 * via SharedNavbar's WriteDiagnostic JSInvokable bridge.
 *
 * See ../../tests/README.md for how to add a test.
 */
(function () {
    'use strict';

    // ====================================================================
    //  Helpers
    // ====================================================================
    function sleep(ms) {
        return new Promise(function (resolve) { setTimeout(resolve, ms); });
    }

    function nextFrame() {
        return new Promise(function (resolve) { requestAnimationFrame(function () { resolve(); }); });
    }

    function getCssVarPx(name) {
        const raw = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        const n = parseInt(raw, 10);
        return Number.isFinite(n) ? n : null;
    }

    function dispatchMouse(target, type, x, y, button) {
        target.dispatchEvent(new MouseEvent(type, {
            bubbles: true, cancelable: true, view: window,
            clientX: x, clientY: y, button: button || 0
        }));
    }

    function dispatchPointer(target, type, x, y) {
        try {
            target.dispatchEvent(new PointerEvent(type, {
                bubbles: true, cancelable: true,
                clientX: x, clientY: y, button: 0, pointerId: 1, pointerType: 'mouse'
            }));
        } catch (e) {
            // PointerEvent constructor may not exist in some older WebView2 builds.
        }
    }

    async function waitForBridge(timeoutMs) {
        const startedAt = Date.now();
        while (Date.now() - startedAt < timeoutMs) {
            if (window._navbarBlazorRef) return true;
            await sleep(100);
        }
        return false;
    }

    async function report(line) {
        console.log('[TEST]', line);
        // Use the per-component DotNetObjectReference registered by SharedNavbar
        // (via registerNavbar in navbar.js). This works in MAUI Blazor Hybrid
        // where static DotNet.invokeMethodAsync is not reliable.
        if (window._navbarBlazorRef) {
            try {
                await window._navbarBlazorRef.invokeMethodAsync('WriteDiagnostic', line);
            } catch (e) {
                console.warn('[TEST] WriteDiagnostic failed:', e);
            }
        }
    }

    // ====================================================================
    //  Test registry
    // ====================================================================
    const TESTS = {
        // -------------------------------------------------------------
        // navbar-drag : dispatches a synthetic drag on the resize handle
        //   and verifies that --sidebar-width grows.
        // -------------------------------------------------------------
        'navbar-drag': async function () {
            const handle = document.querySelector('.sidebar-resize-handle');
            if (!handle) return { pass: false, reason: 'handle element not found' };

            const rect = handle.getBoundingClientRect();
            if (rect.width === 0 || rect.height === 0) {
                return { pass: false, reason: 'handle has zero dimensions ' + JSON.stringify(rect) };
            }

            const initialW = getCssVarPx('--sidebar-width');
            if (initialW === null) return { pass: false, reason: '--sidebar-width unreadable' };

            // Clear navbar breadcrumbs so we only see the events from THIS test.
            if (typeof window.__navbarDebugClear === 'function') window.__navbarDebugClear();

            const cx = rect.left + rect.width / 2;
            const cy = rect.top + rect.height / 2;

            dispatchPointer(handle, 'pointerdown', cx, cy);
            dispatchMouse(handle, 'mousedown', cx, cy, 0);

            await nextFrame();
            dispatchPointer(document, 'pointermove', cx + 80, cy);
            dispatchMouse(document, 'mousemove', cx + 80, cy);
            await nextFrame();
            await nextFrame();

            const midW = getCssVarPx('--sidebar-width');

            dispatchPointer(document, 'pointerup', cx + 80, cy);
            dispatchMouse(document, 'mouseup', cx + 80, cy, 0);

            const finalW = getCssVarPx('--sidebar-width');

            const trace = Array.isArray(window.__navbarDebug)
                ? window.__navbarDebug.slice(0, 20).join(' | ')
                : '(no breadcrumbs)';

            return {
                pass: finalW > initialW,
                reason: 'handle@(' + Math.round(cx) + ',' + Math.round(cy) + ') ' +
                        'w=' + Math.round(rect.width) + 'x' + Math.round(rect.height) + ' ' +
                        'initial=' + initialW + ' mid=' + midW + ' final=' + finalW +
                        ' | TRACE: ' + trace
            };
        },

        // -------------------------------------------------------------
        // navbar-drag-collapse : drag far left, verify rail mode triggers.
        // -------------------------------------------------------------
        'navbar-drag-collapse': async function () {
            const handle = document.querySelector('.sidebar-resize-handle');
            if (!handle) return { pass: false, reason: 'handle element not found' };

            // Make sure we start expanded.
            document.documentElement.removeAttribute('data-sidebar-collapsed');

            const rect = handle.getBoundingClientRect();
            const cx = rect.left + rect.width / 2;
            const cy = rect.top + rect.height / 2;

            dispatchPointer(handle, 'pointerdown', cx, cy);
            dispatchMouse(handle, 'mousedown', cx, cy, 0);

            // Drag 200px to the LEFT — way past the collapse threshold.
            await nextFrame();
            dispatchPointer(document, 'pointermove', cx - 200, cy);
            dispatchMouse(document, 'mousemove', cx - 200, cy);
            await nextFrame();
            await nextFrame();

            const collapsedDuringDrag = document.documentElement.hasAttribute('data-sidebar-collapsed');

            dispatchPointer(document, 'pointerup', cx - 200, cy);
            dispatchMouse(document, 'mouseup', cx - 200, cy, 0);

            const collapsedAfterDrag = document.documentElement.hasAttribute('data-sidebar-collapsed');

            return {
                pass: collapsedDuringDrag && collapsedAfterDrag,
                reason: 'collapsedDuringDrag=' + collapsedDuringDrag +
                        ' collapsedAfterDrag=' + collapsedAfterDrag
            };
        }
    };

    // ====================================================================
    //  Runner
    // ====================================================================
    async function runAll(config) {
        await report('=== RazorReaper runtime tests starting ===');
        // Give Blazor an additional moment to render the navbar before we
        // poke at it (DotNet being ready doesn't mean the component tree is).
        await sleep(800);

        const enabledNames = Object.keys(config.tests).filter(function (k) { return config.tests[k]; });
        if (enabledNames.length === 0) {
            await report('No tests enabled. Exiting.');
            return;
        }

        let passCount = 0;
        let failCount = 0;

        for (const name of enabledNames) {
            const fn = TESTS[name];
            if (!fn) {
                await report('[SKIP] ' + name + ' — not registered');
                continue;
            }
            let result;
            try { result = await fn(); }
            catch (e) { result = { pass: false, reason: 'threw: ' + (e && e.message ? e.message : String(e)) }; }
            const tag = result.pass ? 'PASS' : 'FAIL';
            await report('[' + tag + '] ' + name + ' — ' + (result.reason || ''));
            if (result.pass) passCount++; else failCount++;

            // Give the page a moment to settle between tests.
            await sleep(200);
        }

        await report('=== ' + passCount + ' passed, ' + failCount + ' failed ===');
    }

    // ====================================================================
    //  Bootstrap
    // ====================================================================
    (async function bootstrap() {
        // Log proof-of-life as soon as the JS runtime starts executing,
        // BEFORE any fetch / DOM wait / bridge dependency. If this line
        // never appears in test-results.log, the runner script isn't even
        // being loaded (check script tag / build output).
        const flushBacklog = [];
        flushBacklog.push('runner: script loaded, readyState=' + document.readyState);

        let config = null;
        let fetchError = null;
        try {
            const resp = await fetch('_tests/test-config.json', { cache: 'no-store' });
            if (!resp.ok) {
                fetchError = 'fetch returned status ' + resp.status;
            } else {
                config = await resp.json();
            }
        } catch (e) {
            fetchError = 'fetch threw: ' + (e && e.message ? e.message : String(e));
        }

        if (fetchError) {
            flushBacklog.push('runner: config fetch FAILED — ' + fetchError);
        } else if (!config || !config.enabled) {
            flushBacklog.push('runner: config loaded but disabled (skipping)');
        } else if (!config.tests || typeof config.tests !== 'object') {
            flushBacklog.push('runner: config has no tests block (skipping)');
        }

        // Wait for DOM ready so the Blazor bridge has a chance to register.
        if (document.readyState === 'loading') {
            await new Promise(function (resolve) {
                document.addEventListener('DOMContentLoaded', resolve, { once: true });
            });
        }

        const blazorReady = await waitForBridge(20000);
        flushBacklog.push('runner: bridgeReady=' + blazorReady);

        // Flush proof-of-life lines now that the bridge is (hopefully) up.
        for (const line of flushBacklog) {
            await report(line);
        }

        if (!config || !config.enabled) return;
        if (!config.tests || typeof config.tests !== 'object') return;

        try { await runAll(config); }
        catch (e) {
            await report('runner: crashed — ' + (e && e.message ? e.message : String(e)));
        }
    })();
})();
