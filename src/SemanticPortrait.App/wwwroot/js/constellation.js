// Animated lock-screen constellation. Nodes drift; line endpoints follow their nodes so the
// network stays connected. Driven by requestAnimationFrame against the .sp-lock-net SVG.
(function () {
    const NS = "http://www.w3.org/2000/svg";

    // base layout (matches the design); coords in the 1200x700 viewBox
    const NODES = [
        [120, 90, 3], [260, 180, 2], [80, 300, 2.5], [200, 420, 3.5], [140, 560, 2],
        [330, 520, 2.5], [380, 300, 2], [420, 120, 2.5], [520, 300, 3.5], [610, 200, 2],
        [700, 460, 2.5], [640, 620, 2], [880, 120, 3], [980, 300, 2.5], [1080, 200, 3],
        [1020, 520, 3.5], [1140, 420, 2]
    ];
    const EDGES = [
        [0, 1], [1, 3], [2, 3], [3, 4], [3, 5], [5, 6], [6, 8], [7, 8], [8, 9], [9, 7],
        [8, 10], [10, 11], [12, 13], [13, 14], [12, 14], [13, 15], [14, 16], [15, 16]
    ];

    let raf = 0, lineEls = [], dotEls = [], nodes = [];

    function build(svg) {
        const gLines = svg.querySelector(".sp-net-lines");
        const gDots = svg.querySelector(".sp-net-dots");
        if (!gLines || !gDots) return false;
        gLines.innerHTML = ""; gDots.innerHTML = "";

        nodes = NODES.map(([x, y, r], i) => ({
            x, y, r,
            ax: 7 + (i % 3) * 4, ay: 6 + (i % 4) * 3,            // drift amplitude px
            phx: i * 0.7, phy: i * 1.3,                          // phase offset
            sx: 0.00022 + (i % 5) * 0.00006,                     // angular speed
            sy: 0.00019 + (i % 4) * 0.00007,
            cx: x, cy: y
        }));

        lineEls = EDGES.map(([a, b]) => {
            const l = document.createElementNS(NS, "line");
            gLines.appendChild(l);
            return { l, a, b };
        });
        dotEls = nodes.map(n => {
            const c = document.createElementNS(NS, "circle");
            c.setAttribute("r", n.r);
            gDots.appendChild(c);
            return c;
        });
        return true;
    }

    function frame(t) {
        for (const n of nodes) {
            n.cx = n.x + Math.sin(t * n.sx + n.phx) * n.ax;
            n.cy = n.y + Math.cos(t * n.sy + n.phy) * n.ay;
        }
        for (let i = 0; i < dotEls.length; i++) {
            dotEls[i].setAttribute("cx", nodes[i].cx.toFixed(2));
            dotEls[i].setAttribute("cy", nodes[i].cy.toFixed(2));
        }
        for (const o of lineEls) {
            o.l.setAttribute("x1", nodes[o.a].cx.toFixed(2));
            o.l.setAttribute("y1", nodes[o.a].cy.toFixed(2));
            o.l.setAttribute("x2", nodes[o.b].cx.toFixed(2));
            o.l.setAttribute("y2", nodes[o.b].cy.toFixed(2));
        }
        raf = requestAnimationFrame(frame);
    }

    function start() {
        const svg = document.querySelector(".sp-lock-net");
        if (!svg || raf) return;
        if (!build(svg)) return;
        raf = requestAnimationFrame(frame);
    }

    function stop() {
        if (raf) { cancelAnimationFrame(raf); raf = 0; }
    }

    window.spConstellation = { start, stop };
})();

// Camera fast-path: pan/zoom set the viewBox attribute directly — no Blazor re-render. A trackpad
// emits wheel events at 60-120Hz; re-diffing the whole SVG per event floods the renderer (observed
// as a silent webview freeze/crash). Attribute writes are nearly free.
window.spSetViewBox = (el, vb) => { if (el) el.setAttribute('viewBox', vb); };

// Container aspect (width/height) so the constellation world can match its real panel shape.
window.spAspect = (el) => {
    if (!el) return 0;
    const r = el.getBoundingClientRect();
    return r.height > 0 ? r.width / r.height : 0;
};

// Re-measure when the panel actually changes size (window resize/maximize) — a world built
// for one aspect letterboxes or crops in another. Debounced; detached via spUnwatchResize.
window.spWatchResize = (el, dotnetRef) => {
    if (!el) return;
    let t = 0;
    const ro = new ResizeObserver(() => {
        clearTimeout(t);
        t = setTimeout(() => dotnetRef.invokeMethodAsync('OnHostResized'), 220);
    });
    ro.observe(el);
    el._spRo = ro;
};
window.spUnwatchResize = (el) => { if (el && el._spRo) { el._spRo.disconnect(); delete el._spRo; } };
