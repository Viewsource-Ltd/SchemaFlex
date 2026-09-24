// Scroll-spy for the sticky top bar: highlights the nav link for whichever section
// is currently under the bar. The only script on the site - everything else here is
// plain static HTML.
(function () {
    var bar = document.querySelector('.bar');
    var links = Array.prototype.slice.call(document.querySelectorAll('.bar-nav a[href*="#"]'));
    if (!bar || !links.length || !('IntersectionObserver' in window)) {
        return;
    }

    var sections = [];
    links.forEach(function (link) {
        var hash = link.getAttribute('href').split('#')[1];
        var section = hash && document.getElementById(hash);
        if (section) {
            sections.push({ link: link, section: section });
        }
    });
    if (!sections.length) {
        return;
    }

    function setActive(link) {
        links.forEach(function (l) {
            l.classList.toggle('is-active', l === link);
        });
    }

    // A thin band just under the sticky bar: a section only counts as "current" once
    // its top has passed the bar, and stops counting well before it reaches the
    // bottom of the viewport, so the highlight tracks reading position rather than
    // merely "is any part of this section visible".
    var barHeight = bar.getBoundingClientRect().height;
    var observer = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
            if (!entry.isIntersecting) {
                return;
            }
            var match = sections.find(function (s) { return s.section === entry.target; });
            if (match) {
                setActive(match.link);
            }
        });
    }, {
        rootMargin: '-' + Math.ceil(barHeight + 1) + 'px 0px -65% 0px',
        threshold: 0
    });

    sections.forEach(function (s) { observer.observe(s.section); });
})();
