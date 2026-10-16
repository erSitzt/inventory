// Netzwerkkarte: Subnetze als Container, Router dazwischen, Geräte einzeln, Rechner je Subnetz als aufklappbarer Sammelknoten.
// Zeichnet mit Cytoscape.js (wwwroot/lib/cytoscape, MIT); die Daten baut der Server (NetworkMapBuilder).
window.inventoryMap = (() => {
    let cy = null, map = null, icons = {}, labels = {}, dotnet = null, tip = null;
    let expanded = new Set(), individually = false, hiddenKinds = new Set(), onlySubnet = null, query = '';

    const colors = {
        Router: '#ef6c00', Firewall: '#c62828', Gateway: '#8d6e63', Switch: '#1565c0', AccessPoint: '#0277bd', Printer: '#00897b',
        Storage: '#6a1b9a', Linux: '#37474f', Windows: '#1e88e5', Hypervisor: '#5e35b1', Camera: '#ad1457', Phone: '#2e7d32',
        Ups: '#f9a825', Management: '#546e7a', Iot: '#00838f', Apple: '#616161', Unknown: '#9e9e9e',
        Desktop: '#3949ab', Laptop: '#3949ab', Server: '#283593', VirtualMachine: '#5c6bc0', Tablet: '#3949ab',
    };
    const css = (name, fallback) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
    const color = n => n.type === 'gateway' ? colors.Gateway : colors[n.icon] || colors.Unknown;

    function visible(n) {
        if (n.type === 'device' && hiddenKinds.has(n.icon)) return false;
        if (n.type === 'computer') return individually || expanded.has(n.group);
        if (n.type === 'group') return !individually && !expanded.has(n.id);
        return true;
    }

    function elements() {
        let nodes = map.nodes.filter(visible);
        if (onlySubnet) {
            // Nur das Subnetz, seine Router und deren übrige Subnetze als leere Hüllen.
            const routers = new Set(map.edges.filter(e => e.target === onlySubnet).map(e => e.source));
            nodes = nodes.filter(n => n.id === onlySubnet || n.parent === onlySubnet || routers.has(n.id));
        }
        const ids = new Set(nodes.map(n => n.id));
        const used = new Set(nodes.map(n => n.parent).filter(Boolean));
        map.edges.forEach(e => { if (ids.has(e.source)) used.add(e.target); });
        // Subnetze ohne sichtbaren Inhalt und ohne Router bleiben weg, damit Filter die Karte wirklich aufräumen.
        nodes = nodes.filter(n => n.type !== 'subnet' || used.has(n.id));
        const kept = new Set(nodes.map(n => n.id));
        return [
            ...nodes.map(n => ({
                group: 'nodes',
                data: {
                    id: n.id, parent: n.parent && kept.has(n.parent) ? n.parent : undefined, type: n.type, href: n.href,
                    label: n.type === 'subnet' ? (n.detail ? `${n.label}\n${n.detail}` : n.label) + (n.assumed ? ` (${labels.assumed})` : '')
                        : n.type === 'group' ? labels.computers.replace('{0}', n.count) : n.label,
                    detail: n.detail, color: color(n), icon: icons[n.type === 'gateway' ? 'Router' : n.icon] || icons.Unknown,
                    online: n.online, raw: n,
                },
                classes: [n.type, n.assumed ? 'assumed' : '', n.online ? '' : 'offline'].join(' '),
            })),
            ...map.edges.filter(e => kept.has(e.source) && kept.has(e.target)).map(e => ({ group: 'edges', data: { id: `${e.source}->${e.target}`, source: e.source, target: e.target } })),
        ];
    }

    function style() {
        const text = css('--mud-palette-text-primary', '#212121');
        const secondary = css('--mud-palette-text-secondary', '#757575');
        const surface = css('--mud-palette-surface', '#ffffff');
        const primary = css('--mud-palette-primary', '#594ae2');
        const lines = css('--mud-palette-lines-default', '#bdbdbd');
        return [
            { selector: 'node', style: { 'label': 'data(label)', 'color': text, 'font-size': 12, 'text-valign': 'bottom', 'text-margin-y': 4,
                'text-wrap': 'wrap', 'text-max-width': 120, 'background-color': 'data(color)', 'background-image': 'data(icon)',
                'background-width': '60%', 'background-height': '60%', 'width': 34, 'height': 34, 'text-background-color': surface,
                'text-background-opacity': 0.8, 'text-background-padding': 2, 'text-background-shape': 'roundrectangle' } },
            { selector: 'node.router, node.gateway', style: { 'shape': 'round-diamond', 'width': 46, 'height': 46, 'font-weight': 'bold' } },
            { selector: 'node.gateway', style: { 'border-width': 2, 'border-style': 'dashed', 'border-color': secondary } },
            { selector: 'node.group', style: { 'shape': 'round-rectangle', 'width': 52, 'height': 40, 'border-width': 3, 'border-color': surface,
                'border-opacity': 1, 'font-weight': 'bold' } },
            { selector: 'node.offline', style: { 'opacity': 0.55 } },
            { selector: ':parent', style: { 'shape': 'round-rectangle', 'background-color': primary, 'background-opacity': 0.06,
                'background-image': 'none', 'border-width': 1.5, 'border-color': primary, 'border-opacity': 0.5, 'padding': 18,
                'text-valign': 'top', 'text-halign': 'center', 'text-margin-y': -4, 'font-size': 13, 'font-weight': 'bold',
                'text-background-opacity': 0 } },
            { selector: 'node.subnet.assumed', style: { 'border-style': 'dashed' } },
            { selector: 'node.subnet:childless', style: { 'width': 120, 'height': 50, 'background-image': 'none', 'shape': 'round-rectangle',
                'background-opacity': 0.06, 'background-color': primary, 'border-width': 1.5, 'border-color': primary, 'text-valign': 'center', 'text-margin-y': 0 } },
            { selector: 'edge', style: { 'width': 2.5, 'line-color': lines, 'curve-style': 'bezier' } },
            { selector: '.faded', style: { 'opacity': 0.15 } },
            { selector: 'node.match', style: { 'border-width': 4, 'border-color': css('--mud-palette-warning', '#ff9800'), 'border-opacity': 1 } },
            { selector: 'node:selected', style: { 'border-width': 4, 'border-color': primary } },
        ];
    }

    // Festes Layout wie ein Netzplan (wie bei üblichen Topologie-Ansichten nach Rolle geschichtet): Router oben, darunter die
    // Subnetze nebeneinander (umbrechend). In jedem Subnetz eine Zeile je Rolle: Netzinfrastruktur, Server und Speicher,
    // Peripherie, zuletzt die Rechner. Kräftebasierte Layouts kommen mit verschachtelten Subnetzen und kantenlosen Geräten nicht zurecht.
    const cell = { w: 104, h: 78 }, gap = 70, routerGap = 150, maxCols = 8, subnetsPerRow = 4;
    const tiers = {
        Switch: 0, AccessPoint: 0, Firewall: 0, Router: 0, Management: 1, Hypervisor: 1, Storage: 1, Linux: 1, Ups: 1, Windows: 1,
        Printer: 2, Phone: 2, Camera: 2, Iot: 2, Apple: 2, Unknown: 2,
    };
    const tier = n => n.data('type') === 'device' ? tiers[n.data('raw').icon] ?? 2 : 3;
    // Verschobene Knoten merkt sich der Browser (nur für diesen Betrachter); "Anordnung zurücksetzen" vergisst sie.
    const storeKey = 'inventory.netmap.positions';
    const saved = () => { try { return JSON.parse(localStorage.getItem(storeKey) || '{}'); } catch { return {}; } };
    const save = positions => { try { localStorage.setItem(storeKey, JSON.stringify(positions)); } catch { } };

    function layout(animate) {
        const positions = {};
        let x = 0, y = 0, rowHeight = 0, inRow = 0;
        const centers = {};
        cy.nodes('.subnet').forEach(subnet => {
            const byTier = [[], [], [], []];
            subnet.children().forEach(c => byTier[tier(c)].push(c));
            const rows = [];
            byTier.forEach(list => {
                list.sort((a, b) => a.data('raw').icon.localeCompare(b.data('raw').icon) || a.data('label').localeCompare(b.data('label')));
                for (let i = 0; i < list.length; i += maxCols) rows.push(list.slice(i, i + maxCols));
            });
            const cols = Math.max(1, ...rows.map(r => r.length));
            const width = cols * cell.w, height = Math.max(1, rows.length) * cell.h;
            // Subnetze nebeneinander, damit die Kanten der Router nicht durch andere Subnetze laufen; ab vier eine neue Reihe.
            if (inRow === subnetsPerRow) { x = 0; y += rowHeight + gap + routerGap; rowHeight = 0; inRow = 0; }
            inRow++;
            rows.forEach((row, r) => {
                // Jede Zeile mittig, damit kurze Zeilen nicht links kleben.
                const offset = (cols - row.length) * cell.w / 2;
                row.forEach((child, i) => { positions[child.id()] = { x: x + offset + i * cell.w + cell.w / 2, y: y + r * cell.h + cell.h / 2 }; });
            });
            if (rows.length === 0) positions[subnet.id()] = { x: x + width / 2, y: y + height / 2 };
            centers[subnet.id()] = { x: x + width / 2, top: y };
            x += width + gap;
            rowHeight = Math.max(rowHeight, height);
        });
        // Router über die Mitte ihrer Subnetze; mehrere auf derselben Höhe rücken auseinander.
        const taken = [];
        cy.nodes('.router, .gateway').sort((a, b) => b.connectedEdges().length - a.connectedEdges().length).forEach(router => {
            const linked = router.connectedEdges().connectedNodes('.subnet').map(n => centers[n.id()]).filter(Boolean);
            let rx = linked.length ? linked.reduce((sum, c) => sum + c.x, 0) / linked.length : 0;
            const ry = (linked.length ? Math.min(...linked.map(c => c.top)) : 0) - routerGap;
            while (taken.some(t => Math.abs(t.x - rx) < 140 && t.y === ry)) rx += 150;
            taken.push({ x: rx, y: ry });
            positions[router.id()] = { x: rx, y: ry };
        });
        const moved = saved();
        cy.layout({
            name: 'preset', positions: node => moved[node.id()] ?? positions[node.id()], fit: true, padding: 40,
            animate: animate && cy.nodes().length < 400, animationDuration: 350,
        }).run();
    }

    function redraw(animate) {
        cy.elements().remove();
        cy.add(elements());
        layout(animate);
        applySearch();
    }

    function applySearch() {
        cy.elements().removeClass('faded match');
        const text = query.trim().toLowerCase();
        if (!text) return;
        const hits = cy.nodes().filter(n => n.data('type') !== 'subnet'
            && `${n.data('label')} ${n.data('detail') || ''}`.toLowerCase().includes(text));
        if (hits.length === 0) return;
        cy.elements().addClass('faded');
        hits.removeClass('faded').addClass('match');
        hits.ancestors().removeClass('faded');
        hits.connectedEdges().removeClass('faded');
    }

    function showTip(node, event) {
        const raw = node.data('raw');
        if (!raw || raw.type === 'subnet') return;
        const parts = [raw.type === 'group' ? labels.computers.replace('{0}', raw.count) : raw.label];
        if (raw.detail) parts.push(raw.detail);
        if (raw.type === 'group') parts.push(labels.expand);
        else if (raw.href) parts.push(labels.open);
        tip.textContent = parts.join('\n');
        tip.style.left = `${event.renderedPosition.x + 14}px`;
        tip.style.top = `${event.renderedPosition.y + 14}px`;
        tip.style.display = 'block';
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

    return {
        async render(element, data, iconSet, texts, reference, library) {
            await load(library);
            this.dispose();
            map = data; icons = iconSet; labels = texts; dotnet = reference;
            expanded = new Set(); individually = false; hiddenKinds = new Set(); onlySubnet = null; query = '';
            tip = document.createElement('div');
            tip.className = 'netmap-tip';
            element.appendChild(tip);
            cy = cytoscape({ container: element, elements: elements(), style: style(), wheelSensitivity: 0.3, minZoom: 0.1, maxZoom: 3 });
            layout(true);
            cy.on('tap', 'node', e => {
                const n = e.target, raw = n.data('raw');
                if (!raw) return;
                if (raw.type === 'group') { expanded.add(raw.id); redraw(true); }
                else if (raw.href) dotnet.invokeMethodAsync('Open', raw.href);
            });
            cy.on('cxttap', 'node.computer', e => { expanded.delete(e.target.data('raw').group); redraw(true); });
            cy.on('mouseover', 'node', e => showTip(e.target, e));
            cy.on('mouseout', 'node', () => { tip.style.display = 'none'; });
            cy.on('pan zoom', () => { tip.style.display = 'none'; });
            cy.on('dragfree', 'node', e => {
                const positions = saved();
                // Ein verschobenes Subnetz nimmt seinen Inhalt mit.
                e.target.union(e.target.descendants()).filter(n => !n.isParent()).forEach(n => { positions[n.id()] = { ...n.position() }; });
                save(positions);
            });
        },
        setIndividually(value) { individually = value; expanded.clear(); redraw(true); },
        resetLayout() { save({}); layout(true); },
        collapseAll() { individually = false; expanded.clear(); redraw(true); },
        setHiddenKinds(kinds) { hiddenKinds = new Set(kinds); redraw(false); },
        setSubnet(cidr) { onlySubnet = cidr || null; redraw(true); },
        search(text) { query = text || ''; applySearch(); },
        focus(id) {
            if (!cy) return;
            const node = map.nodes.find(n => n.id === id || n.id.startsWith(`${id}@`));
            if (!node) return;
            if (node.type === 'computer') { expanded.add(node.group); redraw(false); }
            const target = cy.getElementById(node.id);
            cy.animate({ center: { eles: target }, zoom: 1.4 }, { duration: 500 });
            target.select();
        },
        /** Für Tests und Fehlersuche im Browser. */
        instance() { return cy; },
        fit() { cy?.animate({ fit: { padding: 30 } }, { duration: 300 }); },
        png() {
            if (!cy) return;
            const url = cy.png({ full: true, scale: 2, bg: css('--mud-palette-background', '#ffffff') });
            const link = document.createElement('a');
            link.href = url;
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
