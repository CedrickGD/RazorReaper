/*
 * RazorReaper navbar runtime
 * ---------------------------
 * - Drag the right edge to resize the sidebar. --sidebar-width is the TOTAL of the
 *   icon rail plus the page panel; the rail is a fixed 54px, so a drag only ever
 *   changes how much room the page list gets.
 * - Double-click the edge resets to the default width.
 * - There is no collapsed / icon-only mode. The rail is always paired with an open
 *   panel, so there is no collapse state to track, persist, or tell Blazor about.
 * - Width is intentionally NOT persisted — every launch starts at the default.
 *
 * The only thing persisted is the recently-visited list, which the command palette
 * shows before you've typed anything.
 */
(function () {
    'use strict';

    const STORAGE_RECENT = 'rr.navbar.recent';
    const STORAGE_VERSION_KEY = 'rr.navbar.stateVersion';
    const NAVBAR_STATE_VERSION = '7';

    /* Total width = 54px rail + panel. 226 is measured, not guessed: it's the exact
       point where every one of the 40 page names fits without an ellipsis (at 220
       "Underwater Drops" clips, at 200 six labels do). So it's both the default and
       the floor — dragging narrower would only ever hide text. */
    const MIN_WIDTH = 226;
    const MAX_WIDTH = 320;
    const DEFAULT_WIDTH = 226;

    const MAX_RECENT = 5;

    const root = document.documentElement;

    // One-shot cleanup of keys from older sidebar layouts (per-category collapse
    // map, pinned list, open-group, and the collapsed/width pair).
    try {
        if (localStorage.getItem(STORAGE_VERSION_KEY) !== NAVBAR_STATE_VERSION) {
            ['rr.navbar.width', 'rr.navbar.collapsed', 'rr.navbar.groups',
             'rr.navbar.openGroup', 'rr.navbar.pinned'].forEach(function (k) {
                localStorage.removeItem(k);
            });
            localStorage.setItem(STORAGE_VERSION_KEY, NAVBAR_STATE_VERSION);
        }
    } catch (e) { /* ignore */ }

    let currentWidth = DEFAULT_WIDTH;

    function clampWidth(n) {
        if (!Number.isFinite(n)) return DEFAULT_WIDTH;
        if (n < MIN_WIDTH) return MIN_WIDTH;
        if (n > MAX_WIDTH) return MAX_WIDTH;
        return Math.round(n);
    }

    function applyWidth(w) {
        root.style.setProperty('--sidebar-width', w + 'px');
    }

    /** Trims slashes and any query/fragment so one page is always one entry. */
    function normalizeRoute(route) {
        if (typeof route !== 'string') return '';
        return route.split('?')[0].split('#')[0].replace(/^\/+|\/+$/g, '');
    }

    function readRecent() {
        try {
            const parsed = JSON.parse(localStorage.getItem(STORAGE_RECENT) || '[]');
            if (!Array.isArray(parsed)) return [];
            return parsed.filter(function (r) { return typeof r === 'string' && r.length > 0; })
                         .slice(0, MAX_RECENT);
        } catch (e) { return []; }
    }

    applyWidth(currentWidth);

    // ====================================================================
    //  Drag-to-resize
    // ====================================================================
    let dragState = null;

    // Internal breadcrumbs for the runtime test runner. Cheap; useful when a
    // test fails because you can see exactly which events fired.
    window.__navbarDebug = [];
    function dbg(tag, info) { window.__navbarDebug.push(tag + ' ' + (info || '')); }
    window.__navbarDebugClear = function () { window.__navbarDebug.length = 0; };

    function onDragMove(e) {
        if (!dragState) return;
        const clamped = clampWidth(dragState.startWidth + (e.clientX - dragState.startX));
        dragState.width = clamped;
        applyWidth(clamped);
        dbg('applyWidth', clamped);
    }

    function onDragUp() {
        if (!dragState) return;
        document.removeEventListener('mousemove', onDragMove, true);
        document.removeEventListener('mouseup', onDragUp, true);
        document.removeEventListener('pointermove', onDragMove, true);
        document.removeEventListener('pointerup', onDragUp, true);
        document.removeEventListener('pointercancel', onDragUp, true);
        document.body.style.cursor = '';
        document.body.style.userSelect = '';
        root.removeAttribute('data-sidebar-resizing');
        currentWidth = dragState.width;
        dragState = null;
    }

    function beginDrag(startX) {
        if (dragState) { dbg('beginDrag', 'already dragging'); return; }
        dragState = { startX: startX, startWidth: currentWidth, width: currentWidth };
        document.body.style.cursor = 'ew-resize';
        document.body.style.userSelect = 'none';
        // Disables the width transition so the edge tracks the cursor exactly, and
        // keeps the grip lit for the whole drag (see .resize-grip in navbar.css).
        root.setAttribute('data-sidebar-resizing', '');
        document.addEventListener('mousemove', onDragMove, true);
        document.addEventListener('mouseup', onDragUp, true);
        document.addEventListener('pointermove', onDragMove, true);
        document.addEventListener('pointerup', onDragUp, true);
        document.addEventListener('pointercancel', onDragUp, true);
        dbg('beginDrag', 'startX=' + startX + ' startW=' + currentWidth);
    }

    /** The grip inside the handle is pointer-events:none, so the target is always the handle. */
    function isHandle(target) {
        return target instanceof Element && target.classList.contains('sidebar-resize-handle');
    }

    function maybeStartDrag(e) {
        if (!isHandle(e.target)) { dbg('maybeStart', e.type + ' bail-notHandle'); return; }
        if (e.button !== undefined && e.button !== 0) { dbg('maybeStart', 'bail-button'); return; }
        beginDrag(e.clientX);
    }

    document.addEventListener('mousedown', maybeStartDrag, true);
    document.addEventListener('pointerdown', maybeStartDrag, true);

    // ---- Double-click on the resize handle → reset to DEFAULT_WIDTH ----
    // Excel-style: double-click the column edge to snap back to default. No
    // data-sidebar-resizing here, so the CSS width transition plays.
    document.addEventListener('dblclick', function (e) {
        if (!isHandle(e.target)) return;
        if (e.button !== undefined && e.button !== 0) return;
        e.preventDefault();
        e.stopPropagation();
        currentWidth = DEFAULT_WIDTH;
        applyWidth(currentWidth);
        dbg('dblclick', 'reset to ' + DEFAULT_WIDTH);
    }, true);

    // ====================================================================
    //  Public API for Blazor
    // ====================================================================
    window.razorReaperNavbar = {
        getState: function () {
            return {
                width: currentWidth,
                recent: readRecent(),
                minWidth: MIN_WIDTH,
                maxWidth: MAX_WIDTH
            };
        },

        /** Recently visited routes, newest first. Consumed by the command palette. */
        getRecent: readRecent,

        /** Moves a route to the front of the recents list. */
        pushRecent: function (route) {
            const key = normalizeRoute(route);
            if (!key) return;
            const list = readRecent();
            const at = list.indexOf(key);
            if (at >= 0) list.splice(at, 1);
            list.unshift(key);
            try {
                localStorage.setItem(STORAGE_RECENT, JSON.stringify(list.slice(0, MAX_RECENT)));
            } catch (e) { /* */ }
        },

        attachResizeHandle: function () { /* no-op, drag is bound at document level */ }
    };

    window.registerNavbar = function (dotNetRef) { window._navbarBlazorRef = dotNetRef; };
    window.unregisterNavbar = function () { window._navbarBlazorRef = null; };
})();
