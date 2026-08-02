/*
 * RazorReaper theme runtime
 * -------------------------
 * Interface scale only. The accent colour and the interface font each already have a
 * richer card on the Home page, so this deliberately touches neither — one owner per
 * value.
 *
 * Values are written as inline styles on documentElement, which beat the :root rules
 * in theme.css without needing !important anywhere.
 */
(function () {
    'use strict';

    window.razorReaperTheme = {
        apply: function (scalePercent) {
            const root = document.documentElement;

            // Every rem in the app scales off the root font size, so this is the whole
            // UI-scale implementation. 16px is the browser default we scale against.
            const pct = Number(scalePercent);
            if (Number.isFinite(pct) && pct > 0) {
                root.style.fontSize = (16 * pct / 100).toFixed(2) + 'px';
            }
        },

        /** Reverts to whatever theme.css declares. */
        reset: function () {
            const root = document.documentElement;
            root.style.removeProperty('font-size');
        }
    };
})();
