// A mouse wheel must never change a number field's value.
//
// Chromium spins input[type="number"] on wheel whenever the field is focused and the wheel
// lands on it. So scrolling a page with the pointer resting on a field the user had just
// typed into silently rewrote it — a click interval, a gamma preset, a stretched
// resolution — with no undo, and on the pages that apply on change the new value was
// already live before anyone noticed.
//
// Blurring during the capture phase is what stops it: the engine runs its own default
// action after dispatch and asks whether the element is focused, which it no longer is.
// preventDefault() would also stop the spin, but it takes the page scroll with it, and
// scrolling is the gesture the user actually made. Listening passively leaves that scroll
// on the compositor thread.
//
// Range inputs need no guard — Blink has no wheel handler for them, so a slider under the
// pointer already ignores the wheel.
(function () {
    'use strict';

    document.addEventListener('wheel', function (event) {
        var focused = document.activeElement;
        if (!focused || focused.type !== 'number') return;

        // Only the field under the pointer would have spun. A wheel anywhere else on the
        // page is none of this field's business, and taking its focus away there would be
        // the surprise this file exists to prevent.
        if (event.target !== focused) return;

        focused.blur();
    }, { passive: true, capture: true });
})();
