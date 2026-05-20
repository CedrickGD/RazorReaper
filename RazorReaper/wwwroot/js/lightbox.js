// Global image lightbox. Pair with <ImageLightbox /> (mounted via MainLayout)
// and css/shared/lightbox.css. Any page can call:
//   <img src="..." onclick="rrZoom(this)" />
(function () {
    function getOverlay() { return document.getElementById('rr-lightbox'); }
    function getImg() { return document.getElementById('rr-lightbox-img'); }

    window.rrZoom = function (el) {
        if (!el) return;
        var overlay = getOverlay();
        var img = getImg();
        if (!overlay || !img) return;
        img.src = el.src || el.currentSrc || '';
        img.alt = el.alt || '';
        overlay.classList.add('open');
        document.body.style.overflow = 'hidden';
    };

    // Callable from Blazor (JSRuntime.InvokeVoidAsync("rrZoomUrl", src, alt)).
    window.rrZoomUrl = function (src, alt) {
        if (!src) return;
        var overlay = getOverlay();
        var img = getImg();
        if (!overlay || !img) return;
        img.src = src;
        img.alt = alt || '';
        overlay.classList.add('open');
        document.body.style.overflow = 'hidden';
    };

    window.rrCloseZoom = function (e) {
        // Clicking the actual <img> inside the overlay should not close.
        if (e && e.target && e.target.id === 'rr-lightbox-img') return;
        var overlay = getOverlay();
        if (!overlay) return;
        overlay.classList.remove('open');
        document.body.style.overflow = '';
    };

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') {
            window.rrCloseZoom();
        }
    });
})();
