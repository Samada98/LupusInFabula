// js/ptr.js
(function () {
    const scrollEl = document.querySelector('.app-shell') || document.documentElement;
    const ptr = document.getElementById('ptr');
    const text = document.getElementById('ptr-text');

    const THRESHOLD = 70;
    const MAX_PULL = 140;
    let startY = 0, pulling = false, armed = false;

    function getScrollTop() {
        return scrollEl === document.documentElement
            ? (document.scrollingElement || document.documentElement).scrollTop
            : scrollEl.scrollTop;
    }

    function setState(state) {
        ptr.classList.toggle('ptr--pulling', state === 'pulling');
        ptr.classList.toggle('ptr--armed', state === 'armed');
        ptr.classList.toggle('ptr--refreshing', state === 'refreshing');
    }

    function onTouchStart(e) {
        if (getScrollTop() > 0) return;
        const t = e.touches ? e.touches[0] : e;
        startY = t.clientY;
        pulling = true; armed = false;
        setState('pulling');
    }

    function onTouchMove(e) {
        if (!pulling) return;
        const t = e.touches ? e.touches[0] : e;
        const dy = Math.max(0, t.clientY - startY);
        if (dy <= 0) return;

        if (e.cancelable) e.preventDefault();

        const pull = Math.min(dy, MAX_PULL);
        ptr.style.height = pull + 'px';

        if (pull >= THRESHOLD && !armed) {
            armed = true; setState('armed');
            text.textContent = 'Rilascia per aggiornare';
        } else if (pull < THRESHOLD && armed) {
            armed = false; setState('pulling');
            text.textContent = 'Trascina per aggiornare…';
        }
    }

    function onTouchEnd() {
        if (!pulling) return;
        pulling = false;

        if (armed) {
            setState('refreshing');
            ptr.style.height = THRESHOLD + 'px';
            text.textContent = 'Aggiorno…';
            const url = new URL(location.href);
            url.searchParams.set('_r', Date.now());
            location.replace(url.toString());
            return;
        }

        ptr.style.height = '0px';
        text.textContent = 'Trascina per aggiornare…';
        setState('');
    }

    const opts = { passive: false };
    const target = scrollEl === document.documentElement ? window : scrollEl;
    target.addEventListener('touchstart', onTouchStart, opts);
    target.addEventListener('touchmove', onTouchMove, opts);
    target.addEventListener('touchend', onTouchEnd);
    target.addEventListener('touchcancel', onTouchEnd);
})();
