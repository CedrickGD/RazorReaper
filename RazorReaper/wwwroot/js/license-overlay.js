/*
 * Full-window overlay runtime
 * ---------------------------
 * Pairs with Components/Shared/LicenseOverlay.razor and WhatsNewOverlay.razor. Blazor
 * renders each overlay; this only does the things markup cannot: lock the page behind
 * it against scrolling (a class on <body>, see the overlay's stylesheet), move focus in
 * on open and back to whatever opened it on close, and keep Tab and Shift+Tab inside
 * the overlay while it is open — without the trap, Tab walked out into the sidebar
 * under the dialog.
 *
 * One runtime per overlay, each with its own body class and its own "where focus came
 * from", so opening one never restores the other's focus target.
 */
(function () {
    'use strict';

    const FOCUSABLE = [
        'a[href]',
        'button:not([disabled])',
        'input:not([disabled])',
        'select:not([disabled])',
        'textarea:not([disabled])',
        '[tabindex]:not([tabindex="-1"])'
    ].join(', ');

    function createOverlayRuntime(bodyClass) {
        let restoreFocusTo = null;
        let root = null;

        function focusableIn(container) {
            return Array.prototype.filter.call(container.querySelectorAll(FOCUSABLE), function (el) {
                // Skips anything display:none. The overlay root is position:fixed, so its
                // visible descendants all have an offsetParent.
                return el.offsetParent !== null;
            });
        }

        function trapTab(event) {
            if (event.key !== 'Tab' || !root || !root.isConnected) return;

            const items = focusableIn(root);
            if (items.length === 0) {
                event.preventDefault();
                root.focus({ preventScroll: true });
                return;
            }

            const first = items[0];
            const last = items[items.length - 1];
            const active = document.activeElement;
            const outside = !root.contains(active) || active === root;

            if (event.shiftKey) {
                if (outside || active === first) {
                    event.preventDefault();
                    last.focus();
                }
            } else if (outside || active === last) {
                event.preventDefault();
                first.focus();
            }
        }

        return {
            open: function (element) {
                const active = document.activeElement;
                restoreFocusTo = active instanceof HTMLElement ? active : null;
                root = element || null;
                document.body.classList.add(bodyClass);
                document.addEventListener('keydown', trapTab, true);
                if (root && typeof root.focus === 'function') {
                    root.focus({ preventScroll: true });
                }
            },

            close: function () {
                document.body.classList.remove(bodyClass);
                document.removeEventListener('keydown', trapTab, true);
                root = null;
                const target = restoreFocusTo;
                restoreFocusTo = null;
                if (target && target.isConnected && typeof target.focus === 'function') {
                    target.focus({ preventScroll: true });
                }
            }
        };
    }

    window.razorReaperLicenseOverlay = createOverlayRuntime('license-overlay-open');
    window.razorReaperWhatsNewOverlay = createOverlayRuntime('whats-new-overlay-open');
})();
