// Keeps --rr-fill in sync with every range input's value. CSS alone can't read a form
// control's value, so the filled part of the track has to be written from script.
//
// Delegated + observed rather than wired per control: Blazor replaces DOM nodes on
// re-render, so anything bound at startup would go stale the first time a page updates.
(function () {
    'use strict';

    function paint(el) {
        var min = parseFloat(el.min);
        var max = parseFloat(el.max);
        if (!isFinite(min)) min = 0;
        if (!isFinite(max)) max = 100;
        var span = max - min;
        var value = parseFloat(el.value);
        if (!isFinite(value)) value = min;
        var pct = span > 0 ? ((value - min) / span) * 100 : 0;
        el.style.setProperty('--rr-fill', Math.max(0, Math.min(100, pct)) + '%');
    }

    function paintAll(root) {
        var nodes = (root || document).querySelectorAll('input[type="range"]');
        for (var i = 0; i < nodes.length; i++) paint(nodes[i]);
    }

    // 'input' fires while dragging, so the fill tracks the thumb instead of snapping
    // into place on release.
    document.addEventListener('input', function (e) {
        if (e.target && e.target.type === 'range') paint(e.target);
    }, true);

    document.addEventListener('change', function (e) {
        if (e.target && e.target.type === 'range') paint(e.target);
    }, true);

    // Blazor can also change a value without any user event (a Reset button, a value
    // pushed from C#), and those arrive as attribute or subtree mutations.
    var observer = new MutationObserver(function (records) {
        for (var i = 0; i < records.length; i++) {
            var r = records[i];
            if (r.type === 'attributes') {
                if (r.target.type === 'range') paint(r.target);
                continue;
            }
            for (var j = 0; j < r.addedNodes.length; j++) {
                var n = r.addedNodes[j];
                if (n.nodeType !== 1) continue;
                if (n.type === 'range') paint(n);
                else paintAll(n);
            }
        }
    });

    // Blazor sets element.value as a DOM *property*. That fires no 'input' event and
    // produces no mutation record, so neither hook below sees it and the fill freezes at
    // whatever the last drag left — a preset would move the thumb while the coloured part
    // of the track stayed put. Wrapping the setter is the only place that observes it.
    // The native setter still does the work; we only repaint after it.
    function hookValueSetter() {
        var proto = window.HTMLInputElement && HTMLInputElement.prototype;
        var desc = proto && Object.getOwnPropertyDescriptor(proto, 'value');
        if (!desc || !desc.set || !desc.configurable) return;

        Object.defineProperty(proto, 'value', {
            configurable: true,
            enumerable: desc.enumerable,
            get: desc.get,
            set: function (v) {
                desc.set.call(this, v);
                if (this.type === 'range') paint(this);
            }
        });
    }

    function start() {
        hookValueSetter();
        paintAll(document);
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['value', 'min', 'max']
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
