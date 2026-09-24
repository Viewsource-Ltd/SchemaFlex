// Points the hero download button at the right release asset for the visitor's OS,
// based on data-win/data-linux/data-mac attributes set in index.html. Falls back to
// the GitHub releases page (the button's plain href) if the OS can't be determined -
// e.g. no JS, or a platform we don't build for.
(function () {
    var button = document.getElementById('primary-download');
    if (!button) {
        return;
    }

    var platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || navigator.userAgent || '';

    var target = null;
    var label = 'Download';

    if (/win/i.test(platform)) {
        target = button.getAttribute('data-win');
        label = 'Download for Windows';
    } else if (/linux/i.test(platform) && !/android/i.test(platform)) {
        target = button.getAttribute('data-linux');
        label = 'Download for Linux';
    } else if (/mac|iphone|ipad/i.test(platform)) {
        // Apple Silicon only for now - no Intel Mac build is published yet.
        target = button.getAttribute('data-mac');
        label = 'Download for macOS';
    }

    if (target) {
        button.setAttribute('href', target);
        button.textContent = label;
    }
})();
