// Convert page — the bits of the trimmer that have to talk to the media element.
// Everything else about the trim range lives in C#; this only seeks and plays.

window.rrConvert = (function () {
    'use strict';

    // Set by playRange so a second press cancels the first one's stop-watcher rather than
    // leaving two of them fighting over the same element.
    var stopAt = null;
    var watched = null;

    function el(id) {
        var node = document.getElementById(id);
        return node && typeof node.currentTime === 'number' ? node : null;
    }

    function clearWatch() {
        if (watched) {
            watched.removeEventListener('timeupdate', onTick);
            watched = null;
        }
        stopAt = null;
    }

    function onTick() {
        if (!watched || stopAt === null) return;
        if (watched.currentTime >= stopAt) {
            watched.pause();
            clearWatch();
        }
    }

    return {
        /// Moves the playhead so dragging a trim handle shows you the frame you are cutting at.
        seek: function (id, seconds) {
            var media = el(id);
            if (!media) return;
            clearWatch();
            try {
                media.currentTime = Math.max(0, seconds);
            } catch (e) {
                // Metadata may not be in yet; the next drag will land.
            }
        },

        /// Plays just the selected range, which is the only way to judge a cut without exporting.
        playRange: function (id, start, end) {
            var media = el(id);
            if (!media) return;
            clearWatch();
            try {
                media.currentTime = Math.max(0, start);
            } catch (e) {
                return;
            }
            if (end > start) {
                stopAt = end;
                watched = media;
                media.addEventListener('timeupdate', onTick);
            }
            var played = media.play();
            if (played && typeof played.catch === 'function') {
                played.catch(function () { clearWatch(); });
            }
        },

        /// Reads the playhead so "start here" / "end here" can use what you are looking at.
        currentTime: function (id) {
            var media = el(id);
            return media ? media.currentTime : 0;
        },
    };
})();
