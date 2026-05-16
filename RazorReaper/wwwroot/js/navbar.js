/*
 * RazorReaper navbar runtime
 * ---------------------------
 * - Drag-to-resize the sidebar; dragging past COLLAPSE_THRESHOLD snaps to rail
 *   (collapsed icon-only) mode, and dragging a collapsed rail back right
 *   re-expands it.
 * - State persisted in localStorage:
 *     rr.navbar.width     : integer px, MIN_WIDTH..MAX_WIDTH
 *     rr.navbar.collapsed : "1" | "0"
 *     rr.navbar.groups    : JSON { "<group>": true } (true = collapsed)
 * - Ctrl+B toggles collapsed.
 * - Rail-mode flyouts are positioned via JS (position: fixed) so the sidebar
 *   itself can keep overflow management without clipping them.
 */
(function () {
    'use strict';

    const STORAGE_WIDTH = 'rr.navbar.width';
    const STORAGE_COLLAPSED = 'rr.navbar.collapsed';
    const STORAGE_GROUPS = 'rr.navbar.groups';
    const STORAGE_VERSION_KEY = 'rr.navbar.stateVersion';
    const NAVBAR_STATE_VERSION = '2';

    const MIN_WIDTH = 180;
    const MAX_WIDTH = 360;
    const DEFAULT_WIDTH = 240;
    const RAIL_WIDTH = 64;
    const COLLAPSE_THRESHOLD = 140;
    const FLYOUT_HIDE_DELAY_MS = 140;
    const FLYOUT_GAP_PX = 6;

    const root = document.documentElement;

    // ---- One-shot schema reset ----
    try {
        if (localStorage.getItem(STORAGE_VERSION_KEY) !== NAVBAR_STATE_VERSION) {
            localStorage.removeItem(STORAGE_WIDTH);
            localStorage.removeItem(STORAGE_COLLAPSED);
            localStorage.removeItem(STORAGE_GROUPS);
            localStorage.setItem(STORAGE_VERSION_KEY, NAVBAR_STATE_VERSION);
        }
    } catch (e) { /* ignore */ }

    function clampWidth(n) {
        if (!Number.isFinite(n)) return DEFAULT_WIDTH;
        if (n < MIN_WIDTH) return MIN_WIDTH;
        if (n > MAX_WIDTH) return MAX_WIDTH;
        return Math.round(n);
    }

    function readStoredWidth() {
        try {
            const raw = localStorage.getItem(STORAGE_WIDTH);
            return raw ? clampWidth(parseInt(raw, 10)) : DEFAULT_WIDTH;
        } catch (e) { return DEFAULT_WIDTH; }
    }
    function readStoredCollapsed() {
        try { return localStorage.getItem(STORAGE_COLLAPSED) === '1'; } catch (e) { return false; }
    }
    function readStoredGroups() {
        try {
            const raw = localStorage.getItem(STORAGE_GROUPS);
            const parsed = raw ? JSON.parse(raw) : null;
            return parsed && typeof parsed === 'object' ? parsed : {};
        } catch (e) { return {}; }
    }
    function persistWidth(w) { try { localStorage.setItem(STORAGE_WIDTH, String(w)); } catch (e) { /* */ } }
    function persistCollapsed(c) { try { localStorage.setItem(STORAGE_COLLAPSED, c ? '1' : '0'); } catch (e) { /* */ } }
    function persistGroups(g) { try { localStorage.setItem(STORAGE_GROUPS, JSON.stringify(g)); } catch (e) { /* */ } }

    function applyWidth(w, collapsed) {
        const effective = collapsed ? RAIL_WIDTH : w;
        root.style.setProperty('--sidebar-width', effective + 'px');
        root.style.setProperty('--sidebar-expanded-width', w + 'px');
    }
    function applyCollapsed(c) {
        if (c) {
            root.setAttribute('data-sidebar-collapsed', '');
        } else {
            root.removeAttribute('data-sidebar-collapsed');
        }
        hideActiveFlyout(0);
    }

    function notifyBlazor() {
        if (window._navbarBlazorRef) {
            window._navbarBlazorRef.invokeMethodAsync('OnExternalCollapseToggle').catch(function () { /* */ });
        }
    }

    // ====================================================================
    //  Flyout positioning state — declared BEFORE the initial paint because
    //  applyCollapsed() transitively calls hideActiveFlyout() which reads
    //  these `let` bindings. If declared later, they'd be in the temporal
    //  dead zone during initial paint and abort the whole IIFE (silently
    //  preventing registerNavbar / razorReaperNavbar from ever being set).
    // ====================================================================
    let activeFlyoutGroup = null;
    let flyoutHideTimer = null;

    // ---- Initial paint ----
    applyWidth(readStoredWidth(), readStoredCollapsed());
    applyCollapsed(readStoredCollapsed());

    function isCollapsed() {
        return root.hasAttribute('data-sidebar-collapsed');
    }

    function positionFlyout(group) {
        const flyout = group.querySelector('.nav-group-flyout');
        if (!flyout) return;
        const rect = group.getBoundingClientRect();
        const viewportH = window.innerHeight;
        const desiredTop = rect.top;
        flyout.style.left = (rect.right + FLYOUT_GAP_PX) + 'px';
        flyout.style.top = desiredTop + 'px';
        const flyoutHeight = flyout.offsetHeight;
        if (desiredTop + flyoutHeight > viewportH - 8) {
            flyout.style.top = Math.max(8, viewportH - flyoutHeight - 8) + 'px';
        }
    }

    function showFlyoutFor(group) {
        if (!isCollapsed() || !group) return;
        clearTimeout(flyoutHideTimer);
        flyoutHideTimer = null;
        if (activeFlyoutGroup === group) return;
        if (activeFlyoutGroup) {
            const prev = activeFlyoutGroup.querySelector('.nav-group-flyout');
            if (prev) prev.classList.remove('flyout-visible');
        }
        const flyout = group.querySelector('.nav-group-flyout');
        if (!flyout) return;
        activeFlyoutGroup = group;
        flyout.classList.add('flyout-visible');
        positionFlyout(group);
    }

    function hideActiveFlyout(delayMs) {
        if (delayMs === undefined) delayMs = FLYOUT_HIDE_DELAY_MS;
        clearTimeout(flyoutHideTimer);
        if (!activeFlyoutGroup) return;
        if (delayMs <= 0) {
            const flyout = activeFlyoutGroup.querySelector('.nav-group-flyout');
            if (flyout) flyout.classList.remove('flyout-visible');
            activeFlyoutGroup = null;
            return;
        }
        flyoutHideTimer = setTimeout(function () {
            if (activeFlyoutGroup) {
                const flyout = activeFlyoutGroup.querySelector('.nav-group-flyout');
                if (flyout) flyout.classList.remove('flyout-visible');
                activeFlyoutGroup = null;
            }
            flyoutHideTimer = null;
        }, delayMs);
    }

    document.addEventListener('mouseover', function (e) {
        if (!isCollapsed()) return;
        const target = e.target;
        if (!(target instanceof Element)) return;
        const sidebar = target.closest('.sidebar');
        if (sidebar) {
            const group = target.closest('.nav-group');
            if (group) { showFlyoutFor(group); return; }
        }
        if (activeFlyoutGroup) {
            const flyout = activeFlyoutGroup.querySelector('.nav-group-flyout');
            if (flyout && flyout.contains(target)) {
                clearTimeout(flyoutHideTimer);
                flyoutHideTimer = null;
            }
        }
    }, true);

    document.addEventListener('mouseout', function (e) {
        if (!activeFlyoutGroup) return;
        if (!isCollapsed()) { hideActiveFlyout(0); return; }
        const related = e.relatedTarget;
        const flyout = activeFlyoutGroup.querySelector('.nav-group-flyout');
        if (!flyout) return;
        const movedIntoGroup = related instanceof Element && activeFlyoutGroup.contains(related);
        const movedIntoFlyout = related instanceof Element && flyout.contains(related);
        if (!movedIntoGroup && !movedIntoFlyout) hideActiveFlyout();
    }, true);

    document.addEventListener('scroll', function () {
        if (activeFlyoutGroup && isCollapsed()) positionFlyout(activeFlyoutGroup);
    }, true);
    window.addEventListener('resize', function () {
        if (activeFlyoutGroup) positionFlyout(activeFlyoutGroup);
    });

    // ====================================================================
    //  Drag-to-resize (INLINE — no api indirection, no Blazor round-trip)
    //
    //  Single document-level mousedown listener (capture phase). When the click
    //  target is the resize handle, we install document-level mousemove/mouseup
    //  listeners and resize the sidebar by writing the CSS var directly.
    //  Crossing COLLAPSE_THRESHOLD snaps to rail; releasing persists state.
    // ====================================================================
    let dragState = null;

    // Internal debug breadcrumbs for the runtime test runner. Tests read
    // window.__navbarDebug to figure out what the drag pipeline actually did.
    // No-op in production (the array just stays empty).
    window.__navbarDebug = [];
    function dbg(tag, info) { window.__navbarDebug.push(tag + ' ' + (info || '')); }
    window.__navbarDebugClear = function () { window.__navbarDebug.length = 0; };

    function onDragMove(e) {
        dbg('onDragMove', 'clientX=' + e.clientX + ' dragState=' + !!dragState);
        if (!dragState) return;
        const delta = e.clientX - dragState.startX;
        const rawTarget = dragState.startEffectiveWidth + delta;
        if (rawTarget < COLLAPSE_THRESHOLD) {
            if (!dragState.currentCollapsed) {
                dragState.currentCollapsed = true;
                applyCollapsed(true);
                applyWidth(dragState.lastExpandedWidth, true);
                notifyBlazor();
            }
        } else {
            const clamped = clampWidth(rawTarget);
            dragState.lastExpandedWidth = clamped;
            if (dragState.currentCollapsed) {
                dragState.currentCollapsed = false;
                applyCollapsed(false);
                notifyBlazor();
            }
            applyWidth(clamped, false);
            dbg('applyWidth', clamped + ' (delta=' + delta + ')');
        }
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
        persistWidth(dragState.lastExpandedWidth);
        persistCollapsed(dragState.currentCollapsed);
        dragState = null;
    }

    function beginDrag(startX) {
        if (dragState) { dbg('beginDrag', 'already dragging'); return; }
        const startCollapsed = readStoredCollapsed();
        const startExpandedWidth = readStoredWidth();
        dragState = {
            startX: startX,
            startEffectiveWidth: startCollapsed ? RAIL_WIDTH : startExpandedWidth,
            lastExpandedWidth: startExpandedWidth,
            currentCollapsed: startCollapsed
        };
        document.body.style.cursor = 'ew-resize';
        document.body.style.userSelect = 'none';
        root.setAttribute('data-sidebar-resizing', '');
        hideActiveFlyout(0);
        document.addEventListener('mousemove', onDragMove, true);
        document.addEventListener('mouseup', onDragUp, true);
        document.addEventListener('pointermove', onDragMove, true);
        document.addEventListener('pointerup', onDragUp, true);
        document.addEventListener('pointercancel', onDragUp, true);
        dbg('beginDrag', 'startX=' + startX + ' startW=' + startExpandedWidth);
    }

    function maybeStartDrag(e) {
        const target = e.target;
        if (!(target instanceof Element)) { dbg('maybeStart', 'no target'); return; }
        const targetClass = (target.className && target.className.toString) ? target.className.toString() : '(none)';
        if (!target.classList.contains('sidebar-resize-handle')) {
            dbg('maybeStart', e.type + ' bail-notHandle target=' + targetClass);
            return;
        }
        if (e.button !== undefined && e.button !== 0) {
            dbg('maybeStart', e.type + ' bail-button=' + e.button);
            return;
        }
        dbg('maybeStart', e.type + ' OK clientX=' + e.clientX);
        beginDrag(e.clientX);
    }

    document.addEventListener('mousedown', maybeStartDrag, true);
    document.addEventListener('pointerdown', maybeStartDrag, true);

    // ---- Ctrl/Cmd + B toggles the sidebar ----
    document.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && (e.key === 'b' || e.key === 'B')) {
            const target = e.target;
            if (target instanceof HTMLElement) {
                const tag = target.tagName;
                if (tag === 'INPUT' || tag === 'TEXTAREA' || target.isContentEditable) return;
            }
            e.preventDefault();
            const next = !readStoredCollapsed();
            persistCollapsed(next);
            applyCollapsed(next);
            applyWidth(readStoredWidth(), next);
            notifyBlazor();
        }
    }, true);

    // ====================================================================
    //  Public API for Blazor (group state + collapse toggle for the burger)
    // ====================================================================
    window.razorReaperNavbar = {
        getState: function () {
            return {
                width: readStoredWidth(),
                collapsed: readStoredCollapsed(),
                groups: readStoredGroups(),
                minWidth: MIN_WIDTH,
                maxWidth: MAX_WIDTH,
                railWidth: RAIL_WIDTH,
                collapseThreshold: COLLAPSE_THRESHOLD
            };
        },
        setCollapsed: function (collapsed) {
            const c = !!collapsed;
            persistCollapsed(c);
            applyCollapsed(c);
            applyWidth(readStoredWidth(), c);
        },
        toggleCollapsed: function () {
            const next = !readStoredCollapsed();
            this.setCollapsed(next);
            return next;
        },
        setGroupCollapsed: function (groupName, collapsed) {
            if (!groupName) return;
            const groups = readStoredGroups();
            if (collapsed) groups[groupName] = true; else delete groups[groupName];
            persistGroups(groups);
        },
        isGroupCollapsed: function (groupName) {
            return !!readStoredGroups()[groupName];
        },
        // attachResizeHandle is a no-op now (drag is bound at document level).
        attachResizeHandle: function () { /* no-op */ }
    };

    window.registerNavbar = function (dotNetRef) { window._navbarBlazorRef = dotNetRef; };
    window.unregisterNavbar = function () { window._navbarBlazorRef = null; };
})();
