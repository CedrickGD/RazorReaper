// Places Components/Shared/Dropdown.razor's option list in the browser's top layer.
//
// Why it cannot just be a child of the trigger: every .content-card in this app carries
// backdrop-filter (server-styles.css sets it on the bare .content-card selector, so it
// reaches every page). backdrop-filter makes the card a stacking context and the containing
// block for fixed descendants, so a list painted inside a card is covered by the next card
// from that card's top edge downward — which reads as the menu being cut off at the bottom
// edge of its own card. Overflow never came into it, so lifting the card's overflow:hidden
// did nothing.
//
// showPopover() promotes the list to the top layer, which sits above every stacking context
// on the page, so no card, scroller or transform can clip or cover it. The element stays
// exactly where Blazor put it in the DOM — only its painting moves — so Blazor keeps owning
// its lifetime and nothing here ever adds or removes a node.
(function () {
    'use strict';

    // Distance from the trigger, and the smallest gap kept to the window edge.
    var GAP = 4;
    var MARGIN = 8;

    // Never squeeze the list below this, even in a cramped window: a menu two rows tall that
    // scrolls is still usable, a menu clipped to nothing is not.
    var MIN_HEIGHT = 72;

    var open = new Map();

    // Whether the trigger is still somewhere the user can see. The window rectangle is the
    // cheap half; the hit test is for a trigger scrolled out of an inner panel, whose
    // rectangle stays inside the window while the panel clips it away. Permissive on
    // purpose — an unanswerable hit test must not close a list the user is reading.
    function onScreen(state, rect, vh, vw) {
        if (rect.bottom < 0 || rect.top > vh || rect.right < 0 || rect.left > vw) return false;

        var x = Math.min(Math.max(rect.left + rect.width / 2, 1), vw - 1);
        var y = Math.min(Math.max(rect.top + rect.height / 2, 1), vh - 1);
        var hit = document.elementFromPoint(x, y);
        // The list itself counts as the trigger: in a cramped window it can sit over it.
        return !hit || state.trigger.contains(hit) || state.pop.contains(hit);
    }

    function place(state) {
        var pop = state.pop;
        var rect = state.trigger.getBoundingClientRect();
        var vh = window.innerHeight || document.documentElement.clientHeight;
        var vw = window.innerWidth || document.documentElement.clientWidth;

        // The trigger has been scrolled away. A list pinned to the window edge with nothing
        // to belong to is worse than no list, so close instead of chasing it.
        if (state.placed && !onScreen(state, rect, vh, vw)) {
            dismiss(state);
            return;
        }

        // Width is a floor and a ceiling, never a fixed value. The trigger is content-sized
        // around the selected label, so copying its width made the list as narrow as whatever
        // happened to be picked: on Settings, "中文 (简体)" was ellipsized to "中文 (…" while
        // English was selected, and the list changed width with the language. The stylesheet
        // keeps the list at width:max-content so it grows to its longest option; here it only
        // learns that it can never be narrower than the trigger, nor wider than the window.
        pop.style.minWidth = Math.round(rect.width) + 'px';
        pop.style.maxWidth = Math.max(0, Math.round(vw - 2 * MARGIN)) + 'px';

        // Measure unconstrained first, then cap to whichever side it opens on. state.cap is
        // the design cap read off the stylesheet, so the token stays in CSS.
        pop.style.maxHeight = '';
        var wanted = Math.min(pop.getBoundingClientRect().height, state.cap);

        var below = vh - rect.bottom - GAP - MARGIN;
        var above = rect.top - GAP - MARGIN;
        // Flip up only when below is genuinely too tight and above is the roomier side, so a
        // list that fits below never moves.
        var up = wanted > below && above > below;
        var room = Math.max(MIN_HEIGHT, up ? above : below);
        var height = Math.min(wanted, room);

        pop.style.maxHeight = height + 'px';

        // The width the list actually took, measured with min-width, max-width and the cap all
        // applied — a capped list grows its own scrollbar, which widens it. The trigger's width
        // is no longer the list's width, so everything below has to use this one.
        var width = pop.getBoundingClientRect().width;

        // Left edges aligned is the default. A list wider than its trigger grows to the right,
        // so a trigger near the right of the window — these pickers are right-aligned in their
        // rows, and the window narrows — would push it off; align the right edges instead, the
        // way a menu hangs from the control. The clamp is the last word either way.
        var left = rect.left;
        if (left + width > vw - MARGIN) left = rect.right - width;
        left = Math.min(Math.max(MARGIN, left), Math.max(MARGIN, vw - width - MARGIN));

        pop.style.left = Math.round(left) + 'px';
        pop.style.top = Math.round(up ? Math.max(MARGIN, rect.top - GAP - height) : rect.bottom + GAP) + 'px';
        pop.classList.toggle('flip-up', up);
        state.placed = true;
    }

    function dismiss(state) {
        if (!state.dotnet) return;
        try {
            state.dotnet.invokeMethodAsync('CloseFromLayer');
        } catch (e) {
            // The component went away first; its own disposal already closed this.
        }
    }

    window.rrDropdownLayer = {
        // id keys the state so close() works after Blazor has already taken the element out
        // of the DOM — an ElementReference to a removed node cannot be marshalled back.
        open: function (id, pop, trigger, dotnet) {
            if (!pop || !trigger) return;
            window.rrDropdownLayer.close(id);

            // Start from the stylesheet, not from whatever a previous opening left inline:
            // the cap below is read back through getComputedStyle, which would otherwise
            // return the last placement's height and shrink the list a little more each time.
            // The widths go with it — the placement measures the list's own max-content width,
            // and a min-width left over from the last trigger would silently widen it.
            pop.style.top = '';
            pop.style.left = '';
            pop.style.width = '';
            pop.style.minWidth = '';
            pop.style.maxWidth = '';
            pop.style.maxHeight = '';
            pop.classList.remove('flip-up');

            var declared = parseFloat(window.getComputedStyle(pop).maxHeight);
            var state = {
                pop: pop,
                trigger: trigger,
                dotnet: dotnet,
                cap: isNaN(declared) ? Infinity : declared
            };

            if (typeof pop.showPopover === 'function' && !pop.matches(':popover-open')) {
                try {
                    pop.showPopover();
                } catch (e) {
                    // Already shown, or the element left the DOM between render and here.
                }
            }

            state.reposition = function () { place(state); };
            // Only motion that actually carries the trigger: the app transitions rows and
            // buttons all over the page, and none of those move this list.
            state.onMotionEnd = function (event) {
                if (event.target instanceof Node && event.target.contains(trigger)) place(state);
            };
            state.onPointerDown = function (event) {
                var target = event.target;
                if (!(target instanceof Node)) return;
                // A click on the trigger is the trigger's own business: its click handler
                // toggles the list shut, and closing here first would let it reopen.
                if (pop.contains(target) || trigger.contains(target)) return;
                dismiss(state);
            };
            state.onKeyDown = function (event) {
                if (event.key === 'Escape') dismiss(state);
            };

            // Capture, so a scroll inside .main-content (the real scroller) is seen too.
            window.addEventListener('scroll', state.reposition, true);
            window.addEventListener('resize', state.reposition);
            // .content-card animates in on a page load and transitions on hover, and either can
            // still be moving the trigger when the list opens. Re-place when the motion lands.
            document.addEventListener('transitionend', state.onMotionEnd, true);
            document.addEventListener('animationend', state.onMotionEnd, true);
            document.addEventListener('pointerdown', state.onPointerDown, true);
            document.addEventListener('keydown', state.onKeyDown, true);

            open.set(id, state);
            place(state);

            // Cards animate in on a page load, and a web font can land a frame late; both move
            // the trigger under a menu that was measured once. One more pass on the next frame
            // settles it, and costs nothing when nothing moved.
            requestAnimationFrame(function () {
                if (open.get(id) === state) place(state);
            });
        },

        // Keeps the keyboard cursor inside a list that had to be capped to the window.
        reveal: function (id) {
            var state = open.get(id);
            if (!state) return;
            var active = state.pop.querySelector('.rr-dd-opt.active');
            // 'nearest' scrolls the minimum needed, so walking the list does not re-centre
            // and jump on every keypress.
            if (active) active.scrollIntoView({ block: 'nearest' });
        },

        close: function (id) {
            var state = open.get(id);
            if (!state) return;
            open.delete(id);

            window.removeEventListener('scroll', state.reposition, true);
            window.removeEventListener('resize', state.reposition);
            document.removeEventListener('transitionend', state.onMotionEnd, true);
            document.removeEventListener('animationend', state.onMotionEnd, true);
            document.removeEventListener('pointerdown', state.onPointerDown, true);
            document.removeEventListener('keydown', state.onKeyDown, true);

            var pop = state.pop;
            if (pop && pop.isConnected && typeof pop.hidePopover === 'function' && pop.matches(':popover-open')) {
                try {
                    pop.hidePopover();
                } catch (e) {
                    // Nothing to undo: leaving the DOM already took it out of the top layer.
                }
            }
        }
    };
})();
