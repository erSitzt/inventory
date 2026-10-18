// Netzwerkkarte. Zuerst nur die Übersicht: Router (die zentrale Firewall in der Mitte) und die Subnetze sternförmig darum, jedes
// Subnetz als Karte mit der Zahl seiner Geräte und Rechner. Ein Klick klappt ein Subnetz auf (Geräte einzeln, Rechner als Gruppe,
// die sich ihrerseits aufklappen lässt), ein Klick auf den Rahmen klappt es wieder zu. Zeichnet mit Cytoscape.js
// (wwwroot/lib/cytoscape, MIT); die Daten baut der Server (NetworkMapBuilder).
window.inventoryMap = (() => {
    let cy = null, map = null, icons = {}, paths = {}, labels = {}, dotnet = null, tip = null;
    let openSubnets = new Set(), openGroups = new Set(), individually = false, hiddenKinds = new Set(), onlySubnet = null;
    let centers = {};

    const colors = {
        Router: '#ef6c00', Firewall: '#c62828', Gateway: '#8d6e63', Switch: '#1565c0', AccessPoint: '#0277bd', Printer: '#00897b',
        Storage: '#6a1b9a', Linux: '#37474f', Windows: '#1e88e5', Hypervisor: '#5e35b1', Camera: '#ad1457', Phone: '#2e7d32',
        Ups: '#f9a825', Management: '#546e7a', Iot: '#00838f', Apple: '#616161', Unknown: '#9e9e9e',
        Desktop: '#3949ab', Laptop: '#3949ab', Server: '#283593', VirtualMachine: '#5c6bc0', Tablet: '#3949ab',
    };
    const css = (name, fallback) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
    const color = n => n.type === 'gateway' ? colors.Gateway : colors[n.icon] || colors.Unknown;

    // Maße für die Anordnung.
    const card = { w: 220, h: 112 }, routerSize = { w: 90, h: 100 }, cell = { w: 108, h: 84 }, maxCols = 7, padding = 34, margin = 60;
    // Zeilen in einem aufgeklappten Subnetz, wie in üblichen Topologie-Ansichten nach Rolle: Infrastruktur, Server, Peripherie, Rechner.
    const tiers = {
        Switch: 0, AccessPoint: 0, Firewall: 0, Router: 0, Management: 1, Hypervisor: 1, Storage: 1, Linux: 1, Ups: 1, Windows: 1,
        Printer: 2, Phone: 2, Camera: 2, Iot: 2, Apple: 2, Unknown: 2,
    };
    const tierOf = n => n.type === 'device' ? tiers[n.icon] ?? 2 : 3;

    // Verschobene Router und Subnetze merkt sich der Browser (nur für diesen Betrachter); "Anordnung zurücksetzen" vergisst sie.
    const storeKey = 'inventory.netmap.positions';
    const saved = () => { try { return JSON.parse(localStorage.getItem(storeKey) || '{}'); } catch { return {}; } };
    const save = positions => { try { localStorage.setItem(storeKey, JSON.stringify(positions)); } catch { } };

    // --- Was sichtbar ist -------------------------------------------------------------------------------------------------

    const subnets = () => map.nodes.filter(n => n.type === 'subnet' && (!onlySubnet || n.id === onlySubnet));
    const isRouter = n => n.type === 'router' || n.type === 'gateway' || n.type === 'server';
    const routers = () => map.nodes.filter(n => isRouter(n)
        && (!onlySubnet || map.edges.some(e => e.source === n.id && e.target === onlySubnet)));

    function members(subnetId) {
        return map.nodes.filter(n => n.parent === subnetId && (
            n.type === 'device' ? !hiddenKinds.has(n.icon)
                : n.type === 'group' ? !individually && !openGroups.has(n.id)
                    : n.type === 'computer' ? individually || openGroups.has(n.group) : false));
    }

    function summary(subnetId) {
        const devices = map.nodes.filter(n => n.parent === subnetId && n.type === 'device' && !hiddenKinds.has(n.icon)).length;
        const computers = map.nodes.filter(n => n.parent === subnetId && n.type === 'computer').length;
        const parts = [];
        if (devices) parts.push(labels.devices.replace('{0}', devices));
        if (computers) parts.push(labels.computers.replace('{0}', computers));
        return parts.length ? parts.join(' · ') : labels.empty;
    }

    const isOpen = id => openSubnets.has(id) && members(id).length > 0;

    /** Symbolleiste für die zugeklappte Karte: je Geräteart (und für die Rechner) ein farbiger Kreis mit Anzahl. */
    function strip(subnetId) {
        const counts = new Map();
        map.nodes.filter(n => n.parent === subnetId && n.type === 'device' && !hiddenKinds.has(n.icon))
            .forEach(n => counts.set(n.icon, (counts.get(n.icon) || 0) + 1));
        const computers = map.nodes.filter(n => n.parent === subnetId && n.type === 'computer').length;
        const items = [...counts.entries()].sort((a, b) => b[1] - a[1]);
        if (computers) items.unshift(['Desktop', computers]);
        if (!items.length) return null;
        const shown = items.slice(0, 7), step = 28, width = shown.length * step;
        const textColor = css('--mud-palette-text-secondary', '#757575');
        const parts = shown.map(([kind, count], i) => {
            const x = i * step + step / 2;
            // MudBlazor-Symbole beginnen mit einem unsichtbaren Rahmenpfad (fill="none"); der darf nicht weiß gefüllt werden.
            const path = (paths[kind] || paths.Unknown || '').replace(/<path[^>]*fill="none"[^>]*\/>/g, '');
            return `<circle cx="${x}" cy="11" r="10" fill="${colors[kind] || colors.Unknown}"/>`
                + `<g transform="translate(${x - 7} 4) scale(0.583)" fill="#fff">${path}</g>`
                + `<text x="${x}" y="33" font-family="Roboto, Arial, sans-serif" font-size="10" text-anchor="middle" fill="${textColor}">${count}</text>`;
        });
        const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="38" viewBox="0 0 ${width} 38">${parts.join('')}</svg>`;
        return { url: 'data:image/svg+xml;utf8,' + encodeURIComponent(svg), width };
    }

    function elements() {
        const els = [];
        const node = (n, extra = {}) => ({
            group: 'nodes',
            data: { id: n.id, type: n.type, href: n.href, detail: n.detail, color: color(n), icon: icons[n.type === 'gateway' ? 'Router' : n.icon] || icons.Unknown, raw: n, ...extra },
            classes: [n.type, n.online === false ? 'offline' : ''].join(' '),
        });
        // Gateways ohne Namen: mit einem Subnetz die Adresse, mit mehreren (zentraler Router) nur "Gateway".
        const gatewayLabel = r => r.named ? r.label : r.count > 1 ? labels.gateway : `${labels.gateway}\n${r.label}`;
        for (const r of routers())
            els.push(node(r, { label: r.type === 'gateway' ? gatewayLabel(r) : r.type === 'server' ? labels.server : r.label }));
        for (const s of subnets()) {
            const open = isOpen(s.id);
            const remote = s.id === 'remote';
            const title = remote ? labels.remote : s.detail ? `${s.label} · ${s.detail}` : s.label;
            const bar = open ? null : strip(s.id);
            const label = open ? title : remote ? labels.remote : [s.label, s.detail, bar ? null : labels.empty].filter(Boolean).join('\n');
            const el = node(s, { label, strip: bar?.url ?? 'none', stripWidth: bar?.width ?? 1 });
            el.classes += (open ? ' open' : ' closed') + (s.assumed || remote ? ' assumed' : '');
            els.push(el);
            if (!open) continue;
            for (const m of members(s.id)) {
                const text = m.type === 'group' ? labels.computers.replace('{0}', m.count) : m.label;
                els.push(node(m, { label: text, parent: s.id }));
            }
        }
        const ids = new Set(els.map(e => e.data.id));
        for (const e of map.edges)
            if (ids.has(e.source) && ids.has(e.target))
                els.push({ group: 'edges', data: { id: `${e.source}->${e.target}`, source: e.source, target: e.target },
                    classes: map.nodes.some(n => n.id === e.target && n.type === 'subnet') ? '' : 'backbone' });
        return els;
    }

    // --- Anordnung ---------------------------------------------------------------------------------------------------------

    /** Zeilen der aufgeklappten Mitglieder eines Subnetzes. */
    function rowsOf(subnetId) {
        const byTier = [[], [], [], []];
        members(subnetId).forEach(m => byTier[tierOf(m)].push(m));
        const rows = [];
        byTier.forEach(list => {
            list.sort((a, b) => a.icon.localeCompare(b.icon) || a.label.localeCompare(b.label));
            for (let i = 0; i < list.length; i += maxCols) rows.push(list.slice(i, i + maxCols));
        });
        return rows;
    }

    function extent(id) {
        if (!isOpen(id)) return card;
        const rows = rowsOf(id);
        return { w: Math.max(card.w, Math.max(...rows.map(r => r.length)) * cell.w + 2 * padding), h: rows.length * cell.h + 2 * padding + 10 };
    }

    /** Zahl für die Sortierung nach Adresse ("10.0.1.0/24" → 0x0A000100). */
    const addressKey = cidr => cidr.split('/')[0].split('.').reduce((v, o) => v * 256 + (+o || 0), 0);

    /**
     * Mittelpunkte der Router und Subnetze. Jede zusammenhängende Gruppe (Router und ihre Netze) wird für sich als Stern gelegt:
     * der Router mit den meisten Netzen in die Mitte, im Ring darum seine Netze und die Router, mit denen er verbunden ist.
     * Die Gruppen werden dann in Reihen gepackt – die größte zuerst, danach die kleinen nach Adresse, damit Netze aus demselben
     * Adressraum (Firma, Heimnetze im Homeoffice) beieinander liegen.
     */
    function plan() {
        const size = {};
        subnets().forEach(s => { size[s.id] = extent(s.id); });
        const links = r => map.edges.filter(e => e.source === r.id && size[e.target]).map(e => e.target);
        const routerList = routers().sort((a, b) => links(b).length - links(a).length);
        routerList.forEach(r => { size[r.id] = routerSize; });
        const routerIds = new Set(routerList.map(r => r.id));
        const peers = r => map.edges.filter(e => (e.source === r.id && routerIds.has(e.target)) || (e.target === r.id && routerIds.has(e.source)))
            .map(e => e.source === r.id ? e.target : e.source);

        // Zusammenhängende Gruppen.
        const neighbours = new Map(Object.keys(size).map(id => [id, []]));
        map.edges.forEach(e => {
            if (neighbours.has(e.source) && neighbours.has(e.target)) {
                neighbours.get(e.source).push(e.target);
                neighbours.get(e.target).push(e.source);
            }
        });
        const seen = new Set(), groups = [];
        for (const id of [...routerList.map(r => r.id), ...Object.keys(size)]) {
            if (seen.has(id)) continue;
            const group = [], queue = [id];
            seen.add(id);
            while (queue.length) {
                const current = queue.shift();
                group.push(current);
                neighbours.get(current).forEach(n => { if (!seen.has(n)) { seen.add(n); queue.push(n); } });
            }
            groups.push(group);
        }

        const laid = groups.map(group => {
            const members = new Set(group);
            const pos = {};
            const pending = routerList.filter(r => members.has(r.id));
            let first = true;
            while (pending.length) {
                let index = first ? 0 : pending.findIndex(r => pos[r.id]);
                if (index < 0) index = pending.findIndex(r => links(r).some(id => pos[id]) || peers(r).some(id => pos[id]));
                if (index < 0) index = 0;
                const [r] = pending.splice(index, 1);
                if (!pos[r.id]) {
                    const known = [...links(r), ...peers(r)].filter(id => pos[id]);
                    const avg = known.length
                        ? known.reduce((a, id) => ({ x: a.x + pos[id].x / known.length, y: a.y + pos[id].y / known.length }), { x: 0, y: 0 })
                        : { x: 0, y: 0 };
                    const len = Math.hypot(avg.x, avg.y) || 1;
                    pos[r.id] = first ? { x: 0, y: 0 } : { x: avg.x + avg.x / len * 260, y: avg.y + avg.y / len * 260 };
                }
                const ring = [...links(r), ...peers(r)].filter(id => !pos[id]);
                if (ring.length) {
                    const span = ring.reduce((sum, id) => sum + Math.max(size[id].w, size[id].h) + margin, 0);
                    // Ein einzelnes Netz unter seinem Gateway statt auf einem großen Kreis.
                    const radius = ring.length === 1 && first ? 170 : Math.max(first ? 300 : 260, span / (2 * Math.PI));
                    const center = pos[r.id];
                    const outward = Math.atan2(center.y, center.x);
                    const arc = first ? 2 * Math.PI : Math.min(1.4 * Math.PI, Math.max(span / radius, 0.8));
                    ring.sort((a, b) => (routerIds.has(b) ? 1 : 0) - (routerIds.has(a) ? 1 : 0));
                    const order = first ? interleave(ring, id => routerIds.has(id)) : ring;
                    order.forEach((id, k) => {
                        const angle = first ? (order.length === 1 ? Math.PI / 2 : -Math.PI / 2 + k * arc / order.length)
                            : outward - arc / 2 + (k + 0.5) * arc / order.length;
                        pos[id] = { x: center.x + radius * Math.cos(angle), y: center.y + radius * Math.sin(angle) };
                    });
                }
                first = false;
            }
            group.filter(id => !pos[id]).forEach((id, k) => { pos[id] = { x: k * (card.w + margin), y: 0 }; });
            separate(pos, size, new Set([group[0]]));
            const box = group.reduce((b, id) => ({
                x1: Math.min(b.x1, pos[id].x - size[id].w / 2), y1: Math.min(b.y1, pos[id].y - size[id].h / 2),
                x2: Math.max(b.x2, pos[id].x + size[id].w / 2), y2: Math.max(b.y2, pos[id].y + size[id].h / 2),
            }), { x1: Infinity, y1: Infinity, x2: -Infinity, y2: -Infinity });
            const firstSubnet = group.filter(id => !routerIds.has(id)).sort((a, b) => addressKey(a) - addressKey(b))[0];
            // Heim- und Fremdnetze ans Ende.
            return { pos, box, count: group.length, key: group.includes('remote') ? Infinity : firstSubnet ? addressKey(firstSubnet) : 0 };
        });

        laid.sort((a, b) => (a.count > 3) !== (b.count > 3) ? (a.count > 3 ? -1 : 1) : a.count > 3 ? b.count - a.count : a.key - b.key);
        const gap = margin * 1.5;
        const area = laid.reduce((sum, g) => sum + (g.box.x2 - g.box.x1 + gap) * (g.box.y2 - g.box.y1 + gap), 0);
        const rowWidth = Math.max(...laid.map(g => g.box.x2 - g.box.x1), Math.sqrt(area) * 1.6);
        const pos = {};
        let x = 0, y = 0, rowHeight = 0;
        laid.forEach(g => {
            const w = g.box.x2 - g.box.x1, h = g.box.y2 - g.box.y1;
            if (x > 0 && x + w > rowWidth) { x = 0; y += rowHeight + gap; rowHeight = 0; }
            Object.entries(g.pos).forEach(([id, p]) => { pos[id] = { x: p.x - g.box.x1 + x, y: p.y - g.box.y1 + y }; });
            x += w + gap;
            rowHeight = Math.max(rowHeight, h);
        });

        const fixed = saved();
        const pinned = Object.keys(fixed).filter(id => pos[id]);
        pinned.forEach(id => { pos[id] = { ...fixed[id] }; });
        if (pinned.length)
            separate(pos, size, new Set(pinned));
        return pos;
    }

    /** Verteilt die markierten Einträge (Router) gleichmäßig zwischen die übrigen (Netze). */
    function interleave(items, marked) {
        const special = items.filter(marked), rest = items.filter(i => !marked(i));
        if (!special.length || !rest.length) return items;
        const result = [], every = (special.length + rest.length) / special.length;
        let s = 0, r = 0;
        for (let k = 0; k < items.length; k++)
            result.push(s < special.length && k >= Math.round(s * every) ? special[s++] : rest[r++] ?? special[s++]);
        return result;
    }

    /** Schiebt überlappende Router und Subnetze auseinander, jeweils das äußere weiter nach außen. */
    function separate(pos, size, pinned) {
        const ids = Object.keys(pos);
        for (let round = 0; round < 80; round++) {
            let moved = false;
            for (let i = 0; i < ids.length; i++) {
                for (let j = i + 1; j < ids.length; j++) {
                    const a = ids[i], b = ids[j];
                    const dx = pos[b].x - pos[a].x, dy = pos[b].y - pos[a].y;
                    const ox = (size[a].w + size[b].w) / 2 + margin / 2 - Math.abs(dx);
                    const oy = (size[a].h + size[b].h) / 2 + margin / 2 - Math.abs(dy);
                    if (ox <= 0 || oy <= 0) continue;
                    const pinA = pinned.has(a), pinB = pinned.has(b);
                    if (pinA && pinB) continue;
                    const outer = pinA ? b : pinB ? a : Math.hypot(pos[a].x, pos[a].y) > Math.hypot(pos[b].x, pos[b].y) ? a : b;
                    const other = outer === a ? b : a;
                    // Entlang der kleineren Überlappung schieben, weg vom anderen.
                    const sx = Math.sign(pos[outer].x - pos[other].x) || 1, sy = Math.sign(pos[outer].y - pos[other].y) || 1;
                    if (ox < oy) pos[outer].x += sx * (ox + 1); else pos[outer].y += sy * (oy + 1);
                    moved = true;
                }
            }
            if (!moved) break;
        }
    }

    function arrange(animate, focus) {
        const pos = plan();
        const target = {};
        Object.entries(pos).forEach(([id, p]) => { if (!isOpen(id)) target[id] = { ...p }; });
        subnets().filter(s => isOpen(s.id)).forEach(s => {
            const rows = rowsOf(s.id);
            const cols = Math.max(...rows.map(r => r.length));
            const left = pos[s.id].x - cols * cell.w / 2, top = pos[s.id].y - rows.length * cell.h / 2 + 5;
            rows.forEach((row, r) => {
                const offset = (cols - row.length) * cell.w / 2;
                row.forEach((m, i) => { target[m.id] = { x: left + offset + i * cell.w + cell.w / 2, y: top + r * cell.h + cell.h / 2 }; });
            });
        });
        centers = pos;
        const layout = cy.layout({
            name: 'preset', positions: n => target[n.id()], fit: !focus, padding: 50,
            animate: animate && cy.nodes().length < 500, animationDuration: 450, animationEasing: 'ease-in-out-cubic',
        });
        if (focus)
            layout.one('layoutstop', () => cy.animate({ fit: { eles: focus(), padding: 80 } }, { duration: 400 }));
        layout.run();
    }

    function redraw(animate = true, focusId = null) {
        if (!cy) return;
        const before = {};
        cy.nodes().forEach(n => { before[n.id()] = { ...n.position() }; });
        cy.elements().remove();
        // Neue Knoten erscheinen dort, wo vorher ihr Subnetz stand, und wandern von da an ihren Platz.
        cy.add(elements().map(e => {
            if (e.group === 'nodes')
                // Kopie: Cytoscape übernimmt das Objekt als Position, mehrere Knoten dürfen es nicht teilen.
                e.position = { ...(before[e.data.id] ?? before[e.data.parent] ?? centers[e.data.parent] ?? centers[e.data.id] ?? { x: 0, y: 0 }) };
            return e;
        }));
        const focus = focusId ? () => cy.getElementById(focusId).union(cy.getElementById(focusId).neighborhood()) : null;
        arrange(animate, focus);
    }

    // --- Aussehen ----------------------------------------------------------------------------------------------------------

    function style() {
        const text = css('--mud-palette-text-primary', '#212121');
        const secondary = css('--mud-palette-text-secondary', '#757575');
        const surface = css('--mud-palette-surface', '#ffffff');
        const primary = css('--mud-palette-primary', '#594ae2');
        const lines = css('--mud-palette-lines-default', '#bdbdbd');
        const warning = css('--mud-palette-warning', '#ff9800');
        return [
            { selector: 'node', style: {
                'label': 'data(label)', 'color': text, 'font-size': 11, 'font-family': 'Roboto, Helvetica, Arial, sans-serif',
                'text-valign': 'bottom', 'text-margin-y': 5, 'text-wrap': 'wrap', 'text-max-width': 110,
                'background-color': 'data(color)', 'background-image': 'data(icon)', 'background-width': '58%', 'background-height': '58%',
                'width': 38, 'height': 38, 'border-width': 3, 'border-color': surface,
                'text-background-color': surface, 'text-background-opacity': 0.85, 'text-background-padding': 2, 'text-background-shape': 'roundrectangle',
                'transition-property': 'opacity', 'transition-duration': 200 } },
            { selector: 'node.router, node.gateway', style: {
                'width': 62, 'height': 62, 'font-size': 13, 'font-weight': 'bold', 'border-width': 4,
                'underlay-color': 'data(color)', 'underlay-opacity': 0.18, 'underlay-padding': 10, 'underlay-shape': 'ellipse' } },
            { selector: 'node.server', style: { 'shape': 'round-rectangle', 'width': 56, 'height': 56 } },
            { selector: 'edge.backbone', style: { 'width': 5, 'line-color': secondary, 'opacity': 0.75 } },
            { selector: 'node.gateway', style: { 'border-style': 'dashed', 'border-color': secondary, 'background-opacity': 0.8 } },
            { selector: 'node.subnet.closed', style: {
                'shape': 'round-rectangle', 'width': card.w, 'height': card.h, 'background-color': surface,
                'border-width': 2, 'border-color': primary, 'text-valign': 'center', 'text-margin-y': -22, 'text-max-width': card.w - 20,
                'font-size': 13, 'font-weight': 'bold', 'line-height': 1.4, 'text-background-opacity': 0,
                'background-image': 'data(strip)', 'background-fit': 'none', 'background-width': 'data(stripWidth)', 'background-height': 38,
                'background-position-y': '88%', 'background-clip': 'none', 'background-image-containment': 'over',
                'underlay-color': primary, 'underlay-opacity': 0.08, 'underlay-padding': 7, 'underlay-shape': 'round-rectangle' } },
            { selector: 'node.subnet.open', style: {
                'shape': 'round-rectangle', 'background-color': primary, 'background-opacity': 0.05, 'background-image': 'none',
                'border-width': 2, 'border-color': primary, 'border-opacity': 0.6, 'padding': padding,
                'text-valign': 'top', 'text-halign': 'center', 'text-margin-y': -6, 'font-size': 14, 'font-weight': 'bold',
                'text-background-opacity': 0 } },
            { selector: 'node.subnet.assumed', style: { 'border-style': 'dashed' } },
            { selector: 'node.group', style: { 'shape': 'round-rectangle', 'width': 58, 'height': 44, 'font-weight': 'bold' } },
            { selector: 'node.offline', style: { 'background-opacity': 0.45 } },
            { selector: 'edge', style: {
                'width': 3, 'line-color': lines, 'curve-style': 'straight', 'opacity': 0.9,
                'transition-property': 'line-color, width, opacity', 'transition-duration': 200 } },
            { selector: '.dim', style: { 'opacity': 0.25 } },
            { selector: 'edge.hot', style: { 'line-color': primary, 'width': 4, 'opacity': 1 } },
            { selector: 'node.match', style: { 'border-color': warning, 'border-width': 5 } },
            { selector: 'node:selected', style: { 'border-color': primary, 'border-width': 5 } },
        ];
    }

    // --- Bedienung ---------------------------------------------------------------------------------------------------------

    function showTip(node, event) {
        const raw = node.data('raw');
        if (!raw) return;
        const lines = [];
        if (raw.type === 'subnet') {
            lines.push(raw.id === 'remote' ? [labels.remote, labels.remoteHint, raw.detail].filter(Boolean).join('\n') : raw.detail ? `${raw.label} · ${raw.detail}` : raw.label,
                summary(raw.id), isOpen(raw.id) ? labels.collapse : labels.expandSubnet);
            if (raw.assumed) lines.push(labels.assumedHint);
        } else if (raw.type === 'group') {
            lines.push(labels.computers.replace('{0}', raw.count), labels.expand);
        } else {
            lines.push(raw.label);
            if (raw.detail) lines.push(raw.detail);
            if (raw.type === 'computer' && !individually) lines.push(labels.collapseComputers);
            if (raw.href) lines.push(labels.open);
            else if (raw.type === 'gateway' && labels.rename) lines.push(labels.rename);
        }
        tip.textContent = lines.join('\n');
        tip.style.left = `${event.renderedPosition.x + 16}px`;
        tip.style.top = `${event.renderedPosition.y + 16}px`;
        tip.style.display = 'block';
    }

    function hover(node) {
        const keep = node.closedNeighborhood().union(node.ancestors()).union(node.descendants()).union(node.ancestors().closedNeighborhood());
        cy.elements().not(keep).addClass('dim');
        node.connectedEdges().union(node.ancestors().connectedEdges()).addClass('hot');
    }

    function unhover() {
        cy.elements().removeClass('dim hot');
        tip.style.display = 'none';
    }

    // Cytoscape wird erst geladen, wenn die Karte geöffnet wird; die übrigen Seiten bleiben schlank.
    let loading = null;
    function load(source) {
        if (window.cytoscape) return Promise.resolve();
        loading ??= new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = source;
            script.onload = resolve;
            script.onerror = reject;
            document.head.appendChild(script);
        });
        return loading;
    }

    function find(text) {
        const query = text.trim().toLowerCase();
        return query ? map.nodes.filter(n => n.type !== 'subnet' && n.type !== 'group' && `${n.label} ${n.detail || ''}`.toLowerCase().includes(query)) : [];
    }

    return {
        async render(element, data, iconSet, texts, reference, library) {
            await load(library);
            this.dispose();
            map = data; icons = iconSet.icons; paths = iconSet.paths; labels = texts; dotnet = reference;
            openSubnets = new Set(); openGroups = new Set(); individually = false; hiddenKinds = new Set(); onlySubnet = null; centers = {};
            tip = document.createElement('div');
            tip.className = 'netmap-tip';
            element.appendChild(tip);
            cy = cytoscape({ container: element, style: style(), wheelSensitivity: 0.3, minZoom: 0.08, maxZoom: 2.5, boxSelectionEnabled: false });
            redraw(false);

            cy.on('tap', 'node', e => {
                const raw = e.target.data('raw');
                if (!raw) return;
                unhover();
                if (raw.type === 'subnet') {
                    if (openSubnets.has(raw.id)) openSubnets.delete(raw.id); else openSubnets.add(raw.id);
                    redraw(true, openSubnets.has(raw.id) ? raw.id : null);
                } else if (raw.type === 'group') {
                    openGroups.add(raw.id);
                    redraw(true, raw.parent);
                } else if (raw.href) {
                    dotnet.invokeMethodAsync('Open', raw.href);
                } else if (raw.type === 'gateway' && labels.rename) {
                    dotnet.invokeMethodAsync('Rename', raw.id);
                }
            });
            cy.on('cxttap', 'node.computer', e => { unhover(); openGroups.delete(e.target.data('raw').group); redraw(true); });
            cy.on('mouseover', 'node', e => { hover(e.target); showTip(e.target, e); });
            cy.on('mouseout', 'node', unhover);
            cy.on('pan zoom drag', () => { tip.style.display = 'none'; });
            cy.on('dragfree', 'node', e => {
                if (e.target.isChild()) return;
                const positions = saved();
                positions[e.target.id()] = { ...e.target.position() };
                save(positions);
            });
        },
        expandAll() { subnets().forEach(s => openSubnets.add(s.id)); redraw(true); },
        collapseAll() { openSubnets.clear(); openGroups.clear(); redraw(true); },
        setIndividually(value) { individually = value; openGroups.clear(); redraw(true); },
        setHiddenKinds(kinds) { hiddenKinds = new Set(kinds); redraw(true); },
        setSubnet(cidr) { onlySubnet = cidr || null; if (onlySubnet) openSubnets.add(onlySubnet); redraw(true); },
        resetLayout() { save({}); redraw(true); },
        search(text) {
            if (!cy) return;
            const hits = find(text || '');
            if (!hits.length) { cy.nodes().removeClass('match'); return; }
            // Treffer in zugeklappten Subnetzen und Gruppen werden sichtbar gemacht.
            hits.forEach(n => {
                if (n.parent) openSubnets.add(n.parent);
                if (n.type === 'computer' && !individually) openGroups.add(n.group);
            });
            redraw(false);
            const found = cy.collection(hits.map(n => cy.getElementById(n.id)).filter(n => n.nonempty()));
            found.addClass('match');
            cy.animate({ fit: { eles: found, padding: 120 } }, { duration: 400 });
        },
        focus(id) {
            if (!cy) return;
            const node = map.nodes.find(n => n.id === id || n.id.startsWith(`${id}@`));
            if (!node) return;
            if (node.parent) openSubnets.add(node.parent);
            if (node.type === 'computer' && !individually) openGroups.add(node.group);
            redraw(false);
            const target = cy.getElementById(node.id);
            target.select();
            cy.animate({ center: { eles: target }, zoom: 1.3 }, { duration: 500 });
        },
        /** Für Tests und Fehlersuche im Browser. */
        instance() { return cy; },
        fit() { cy?.animate({ fit: { eles: cy.elements(), padding: 50 } }, { duration: 300 }); },
        png() {
            if (!cy) return;
            const link = document.createElement('a');
            link.href = cy.png({ full: true, scale: 2, bg: css('--mud-palette-surface', '#ffffff') });
            link.download = 'network-map.png';
            link.click();
        },
        dispose() {
            cy?.destroy();
            cy = null;
            tip?.remove();
            tip = null;
        },
    };
})();
