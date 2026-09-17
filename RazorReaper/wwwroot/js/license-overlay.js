/*
 * Full-window overlay runtime
 * ---------------------------
 * Pairs with Components/Shared/LicenseOverlay.razor and WhatsNewOverlay.razor. Blazor
 * renders each overlay; this only does the two things markup cannot: lock the page
 * behind it against scrolling (a class on <body>, see the overlay's stylesheet) and
 * move focus in on open and back to whatever opened it on close.
 *
 * One runtime per overlay, each with its own body class and its own "where focus came
 * from", so opening one never restores the other's focus target.
 */
(function () {
    'use strict';

    function createOverlayRuntime(bodyClass) {
        let restoreFocusTo = null;

        return {
            open: function (root) {
                const active = document.activeElement;
                restoreFocusTo = active instanceof HTMLElement ? active : null;
                document.body.classList.add(bodyClass);
                if (root && typeof root.focus === 'function') {
                    root.focus({ preventScroll: true });
                }
            },

            close: function () {
                document.body.classList.remove(bodyClass);
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
