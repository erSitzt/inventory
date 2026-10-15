// Geführte Tour: hebt nacheinander Elemente der Oberfläche hervor und erklärt sie in einer Sprechblase.
// Schritte ohne sichtbares Element (z. B. Menüpunkte, die die eigene Rolle nicht sieht) werden übersprungen.
window.inventoryTour = (() => {
    let steps = [], labels = {}, index = 0, mask = null, bubble = null;

    const visible = step => {
        if (!step.selector) return true;
        const el = document.querySelector(step.selector);
        return !!el && el.getClientRects().length > 0;
    };

    function close(finished) {
        if (!mask) return;
        mask.remove(); bubble?.remove(); mask = bubble = null;
        window.removeEventListener('resize', place);
        document.removeEventListener('keydown', key, true);
        // Einmal gesehen (auch abgebrochen) = nicht wieder von selbst starten.
        try { localStorage.setItem('inventory.tour', finished ? 'done' : 'closed'); } catch { }
    }

    function key(e) {
        if (e.key === 'Escape') { e.preventDefault(); close(false); }
        else if (e.key === 'ArrowRight' || e.key === 'Enter') { e.preventDefault(); go(1); }
        else if (e.key === 'ArrowLeft') { e.preventDefault(); go(-1); }
    }

    function go(delta) {
        let next = index + delta;
        while (next >= 0 && next < steps.length && !visible(steps[next])) next += delta;
        if (next < 0) return;
        if (next >= steps.length) { close(true); return; }
        index = next;
        render();
    }

    function place() {
        const step = steps[index];
        const el = step.selector ? document.querySelector(step.selector) : null;
        const pad = 6, gap = 14, width = Math.min(360, window.innerWidth - 32);
        bubble.style.width = width + 'px';
        if (!el) {
            mask.style.cssText += ';left:50%;top:50%;width:0;height:0';
            bubble.style.left = (window.innerWidth - width) / 2 + 'px';
            bubble.style.top = Math.max(16, (window.innerHeight - bubble.offsetHeight) / 2) + 'px';
            return;
        }
        const r = el.getBoundingClientRect();
        Object.assign(mask.style, { left: r.left - pad + 'px', top: r.top - pad + 'px', width: r.width + 2 * pad + 'px', height: r.height + 2 * pad + 'px' });
        const h = bubble.offsetHeight;
        let left, top;
        if (r.right + gap + width < window.innerWidth) { left = r.right + gap; top = r.top + r.height / 2 - h / 2; }
        else if (r.left - gap - width > 0) { left = r.left - gap - width; top = r.top + r.height / 2 - h / 2; }
        else { left = r.left + r.width / 2 - width / 2; top = r.bottom + gap; if (top + h > window.innerHeight - 16) top = r.top - gap - h; }
        bubble.style.left = Math.min(Math.max(16, left), window.innerWidth - width - 16) + 'px';
        bubble.style.top = Math.min(Math.max(16, top), window.innerHeight - h - 16) + 'px';
    }

    function render() {
        const step = steps[index];
        const el = step.selector ? document.querySelector(step.selector) : null;
        el?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        const shown = steps.filter(visible), position = shown.indexOf(step) + 1;
        const last = !steps.slice(index + 1).some(visible);
        bubble.innerHTML = '';
        const head = document.createElement('div'); head.className = 'tour-head';
        const title = document.createElement('strong'); title.textContent = step.title;
        const count = document.createElement('span'); count.className = 'tour-count'; count.textContent = `${position} / ${shown.length}`;
        head.append(title, count);
        const text = document.createElement('p'); text.textContent = step.text;
        const actions = document.createElement('div'); actions.className = 'tour-actions';
        const button = (label, onClick, primary) => {
            const b = document.createElement('button'); b.type = 'button'; b.textContent = label;
            b.className = primary ? 'tour-primary' : ''; b.onclick = onClick; return b;
        };
        actions.append(button(labels.close, () => close(false)));
        const spacer = document.createElement('span'); spacer.style.flex = '1'; actions.append(spacer);
        if (position > 1) actions.append(button(labels.back, () => go(-1)));
        const next = button(last ? labels.done : labels.next, () => go(1), true);
        actions.append(next);
        bubble.append(head, text, actions);
        place();
        next.focus();
    }

    return {
        seen() {
            try { return localStorage.getItem('inventory.tour') !== null; } catch { return true; }
        },
        start(tourSteps, tourLabels) {
            close(false);
            steps = tourSteps; labels = tourLabels; index = -1;
            mask = document.createElement('div'); mask.className = 'tour-mask';
            bubble = document.createElement('div'); bubble.className = 'tour-bubble';
            bubble.setAttribute('role', 'dialog'); bubble.setAttribute('aria-live', 'polite');
            document.body.append(mask, bubble);
            window.addEventListener('resize', place);
            document.addEventListener('keydown', key, true);
            go(1);
        },
    };
})();
