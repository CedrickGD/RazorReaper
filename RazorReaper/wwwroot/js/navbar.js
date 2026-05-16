/*
 * RazorReaper navbar runtime
 * ---------------------------
 * - Drag the right edge to resize the sidebar (clamped 180..360px).
 * - Drag past COLLAPSE_THRESHOLD snaps to a 64px icon-only rail; drag back
 *   right past it re-expands. The threshold snap is animated via a brief CSS
 *   transition window so it doesn't pop visually.
 * - Width and overall collapsed state are intentionally NOT persisted —
 *   every launch starts in the standard expanded layout.
 * - Per-category collapse (the chevron toggles inside each group) IS
 *   persisted so users don't have to re-collapse "Help & About" etc. every
 *   launch.
 * - Ctrl+B toggles collapsed.
 * - In rail mode, hovering an icon shows just the page name via a pure CSS
 *   tooltip (no flyout panel, no JS positioning).
 */
(function () {
    'use strict';

    const STORAGE_GROUPS = 'rr.navbar.groups';
    const STORAGE_VERSION_KEY = 'rr.navbar.stateVersion';
    const NAVBAR_STATE_VERSION = '4';

    const MIN_WIDTH = 180;
    const MAX_WIDTH = 360;
    const DEFAULT_WIDTH = 240;
    const RAIL_WIDTH = 64;
    const COLLAPSE_THRESHOLD = 140;
    const SNAP_ANIM_MS = 220;

    const root = document.documentElement;

    // One-shot cleanup of obsolete keys from older builds.
    try {
        if (localStorage.getItem(STORAGE_VERSION_KEY) !== NAVBAR_STATE_VERSION) {
            localStorage.removeItem('rr.navbar.width');
            localStorage.removeItem('rr.navbar.collapsed');
            localStorage.setItem(STORAGE_VERSION_KEY, NAVBAR_STATE_VERSION);
        }
    } catch (e) { /* ignore */ }

    // ---- In-memory state (no persistence for width/collapsed) ----
    let currentWidth = DEFAULT_WIDTH;
    let currentCollapsed = false;

    function clampWidth(n) {
        if (!Number.isFinite(n)) return DEFAULT_WIDTH;
        if (n < MIN_WIDTH) return MIN_WIDTH;
        if (n > MAX_WIDTH) return MAX_WIDTH;
        return Math.round(n);
    }

    function readStoredGroups() {
        try {
            const raw = localStorage.getItem(STORAGE_GROUPS);
            const parsed = raw ? JSON.parse(raw) : null;
            return parsed && typeof parsed === 'object' ? parsed : {};
        } catch (e) { return {}; }
    }
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
    }

    function notifyBlazor() {
        if (window._navbarBlazorRef) {
            window._navbarBlazorRef.invokeMethodAsync('OnExternalCollapseToggle').catch(function () { /* */ });
        }
    }

    // ---- Initial paint ----
    applyWidth(currentWidth, currentCollapsed);
    applyCollapsed(currentCollapsed);

    // ====================================================================
    //  Rail-mode tooltip
    //
    //  A single themed tooltip element appended to <body>. We can't use a
    //  ::before pseudo-element because .nav-link / .nav-content / .sidebar
    //  have overflow rules that would clip it. Body-level position: fixed
    //  escapes all of them; JS positions it on hover.
    //
    //  Source of the label text: the `.nav-link-label` span inside each link
    //  (not the `title` attribute) so we don't fight with the native OS
    //  tooltip the browser would otherwise show.
    // ====================================================================
    let railTooltipEl = null;
    let railTooltipHideTimer = null;

    function ensureTooltipEl() {
        if (railTooltipEl && railTooltipEl.isConnected) return railTooltipEl;
        railTooltipEl = document.createElement('div');
        railTooltipEl.className = 'rail-tooltip';
        railTooltipEl.setAttribute('role', 'tooltip');
        document.body.appendChild(railTooltipEl);
        return railTooltipEl;
    }

    function showRailTooltip(link) {
        if (!root.hasAttribute('data-sidebar-collapsed')) return;
        const labelEl = link.querySelector('.nav-link-label');
        const text = (labelEl ? labelEl.textContent : link.getAttribute('title') || '').trim();
        if (!text) return;

        const t = ensureTooltipEl();
        t.textContent = text;

        const rect = link.getBoundingClientRect();
        t.style.left = (rect.right + 8) + 'px';
        t.style.top = (rect.top + rect.height / 2) + 'px';

        clearTimeout(railTooltipHideTimer);
        railTooltipHideTimer = null;
        t.classList.add('visible');
    }

    function hideRailTooltip() {
        if (!railTooltipEl) return;
        railTooltipEl.classList.remove('visible');
    }

    // Event delegation — works regardless of when Blazor renders the navbar.
    document.addEventListener('mouseover', function (e) {
        if (!root.hasAttribute('data-sidebar-collapsed')) return;
        const target = e.target;
        if (!(target instanceof Element)) return;
        const link = target.closest('.nav-link');
        if (link && link.closest('.sidebar')) {
            showRailTooltip(link);
        }
    }, true);

    document.addEventListener('mouseout', function (e) {
        if (!railTooltipEl || !railTooltipEl.classList.contains('visible')) return;
        const target = e.target;
        const related = e.relatedTarget;
        if (!(target instanceof Element)) return;
        const leavingLink = target.closest('.nav-link');
        if (!leavingLink) return;
        // If the cursor is moving within the same link (e.g. icon → label), keep showing.
        if (related instanceof Element && related.closest('.nav-link') === leavingLink) return;
        hideRailTooltip();
    }, true);

    // Reposition if user scrolls the nav list while hovering, or resizes window.
    document.addEventListener('scroll', function () {
        if (!railTooltipEl || !railTooltipEl.classList.contains('visible')) return;
        // Cheapest: just hide. The user is moving the cursor again anyway.
        hideRailTooltip();
    }, true);
    window.addEventListener('resize', function () {
        if (railTooltipEl) hideRailTooltip();
    });

    // ====================================================================
    //  Drag-to-resize
    // ====================================================================
    let dragState = null;

    // Internal breadcrumbs for the runtime test runner. Cheap; useful when a
    // test fails because you can see exactly which events fired.
    window.__navbarDebug = [];
    function dbg(tag, info) { window.__navbarDebug.push(tag + ' ' + (info || '')); }
    window.__navbarDebugClear = function () { window.__navbarDebug.length = 0; };

    // When the drag crosses COLLAPSE_THRESHOLD we briefly re-enable CSS
    // transitions so the rail<->expanded jump animates smoothly instead of
    // popping. After SNAP_ANIM_MS we re-disable transitions so post-snap
    // cursor tracking stays responsive.
    let snapTimer = null;
    function withSnapAnimation(fn) {
        root.removeAttribute('data-sidebar-resizing');
        fn();
        clearTimeout(snapTimer);
        snapTimer = setTimeout(function () {
            if (dragState) root.setAttribute('data-sidebar-resizing', '');
            snapTimer = null;
        }, SNAP_ANIM_MS);
    }

    function onDragMove(e) {
        dbg('onDragMove', 'clientX=' + e.clientX + ' dragState=' + !!dragState);
        if (!dragState) return;
        const delta = e.clientX - dragState.startX;
        const rawTarget = dragState.startEffectiveWidth + delta;
        if (rawTarget < COLLAPSE_THRESHOLD) {
            if (!dragState.currentCollapsed) {
                dragState.currentCollapsed = true;
                withSnapAnimation(function () {
                    applyCollapsed(true);
                    applyWidth(dragState.lastExpandedWidth, true);
                });
                notifyBlazor();
                dbg('snap', 'collapse');
            }
        } else {
            const clamped = clampWidth(rawTarget);
            dragState.lastExpandedWidth = clamped;
            if (dragState.currentCollapsed) {
                dragState.currentCollapsed = false;
                withSnapAnimation(function () {
                    applyCollapsed(false);
                    applyWidth(clamped, false);
                });
                notifyBlazor();
                dbg('snap', 'expand');
            } else {
                applyWidth(clamped, false);
                dbg('applyWidth', clamped + ' (delta=' + delta + ')');
            }
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
        currentWidth = dragState.lastExpandedWidth;
        currentCollapsed = dragState.currentCollapsed;
        dragState = null;
    }

    function beginDrag(startX) {
        if (dragState) { dbg('beginDrag', 'already dragging'); return; }
        dragState = {
            startX: startX,
            startEffectiveWidth: currentCollapsed ? RAIL_WIDTH : currentWidth,
            lastExpandedWidth: currentWidth,
            currentCollapsed: currentCollapsed
        };
        document.body.style.cursor = 'ew-resize';
        document.body.style.userSelect = 'none';
        root.setAttribute('data-sidebar-resizing', '');
        document.addEventListener('mousemove', onDragMove, true);
        document.addEventListener('mouseup', onDragUp, true);
        document.addEventListener('pointermove', onDragMove, true);
        document.addEventListener('pointerup', onDragUp, true);
        document.addEventListener('pointercancel', onDragUp, true);
        dbg('beginDrag', 'startX=' + startX + ' startW=' + currentWidth);
    }

    function maybeStartDrag(e) {
        const target = e.target;
        if (!(target instanceof Element)) { dbg('maybeStart', 'no target'); return; }
        if (!target.classList.contains('sidebar-resize-handle')) {
            dbg('maybeStart', e.type + ' bail-notHandle');
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

    // ---- Double-click on the resize handle → reset to DEFAULT_WIDTH ----
    // Excel-style: double-click the column edge to snap back to default.
    // Works from any state: oversized, undersized, or fully collapsed rail.
    function maybeResetToDefault(e) {
        const target = e.target;
        if (!(target instanceof Element)) return;
        if (!target.classList.contains('sidebar-resize-handle')) return;
        if (e.button !== undefined && e.button !== 0) return;
        e.preventDefault();
        e.stopPropagation();
        // No `data-sidebar-resizing` set here — that means the standard CSS
        // `transition: width 0.18s ease` on .sidebar plays, giving us a
        // smooth animation back to default width.
        currentWidth = DEFAULT_WIDTH;
        if (currentCollapsed) {
            currentCollapsed = false;
            applyCollapsed(false);
            notifyBlazor();
        }
        applyWidth(currentWidth, false);
        dbg('dblclick', 'reset to ' + DEFAULT_WIDTH);
    }

    document.addEventListener('dblclick', maybeResetToDefault, true);

    // ---- Ctrl/Cmd + B toggles the sidebar ----
    document.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && (e.key === 'b' || e.key === 'B')) {
            const target = e.target;
            if (target instanceof HTMLElement) {
                const tag = target.tagName;
                if (tag === 'INPUT' || tag === 'TEXTAREA' || target.isContentEditable) return;
            }
            e.preventDefault();
            currentCollapsed = !currentCollapsed;
            applyCollapsed(currentCollapsed);
            applyWidth(currentWidth, currentCollapsed);
            notifyBlazor();
        }
    }, true);

    // ====================================================================
    //  Public API for Blazor (group state + collapse toggle)
    // ====================================================================
    window.razorReaperNavbar = {
        getState: function () {
            return {
                width: currentWidth,
                collapsed: currentCollapsed,
                groups: readStoredGroups(),
                minWidth: MIN_WIDTH,
                maxWidth: MAX_WIDTH,
                railWidth: RAIL_WIDTH,
                collapseThreshold: COLLAPSE_THRESHOLD
            };
        },
        setCollapsed: function (collapsed) {
            currentCollapsed = !!collapsed;
            applyCollapsed(currentCollapsed);
            applyWidth(currentWidth, currentCollapsed);
        },
        toggleCollapsed: function () {
            this.setCollapsed(!currentCollapsed);
            return currentCollapsed;
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
        attachResizeHandle: function () { /* no-op, drag is bound at document level */ }
    };

    window.registerNavbar = function (dotNetRef) { window._navbarBlazorRef = dotNetRef; };
    window.unregisterNavbar = function () { window._navbarBlazorRef = null; };
})();
