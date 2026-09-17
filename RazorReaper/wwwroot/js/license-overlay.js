/*
 * License overlay runtime
 * -----------------------
 * Pair with Components/Shared/LicenseOverlay.razor. Blazor renders the overlay; this
 * only does the two things markup cannot: lock the page behind it against scrolling
 * and move focus in on open and back to whatever opened it on close.
 */
(function () {
    'use strict';

    const BODY_CLASS = 'license-overlay-open';
    let restoreFocusTo = null;

    window.razorReaperLicenseOverlay = {
        open: function (root) {
            const active = document.activeElement;
            restoreFocusTo = active instanceof HTMLElement ? active : null;
            document.body.classList.add(BODY_CLASS);
            if (root && typeof root.focus === 'function') {
                root.focus({ preventScroll: true });
            }
        },

        close: function () {
            document.body.classList.remove(BODY_CLASS);
            const target = restoreFocusTo;
            restoreFocusTo = null;
            if (target && target.isConnected && typeof target.focus === 'function') {
                target.focus({ preventScroll: true });
            }
        }
    };
})();
