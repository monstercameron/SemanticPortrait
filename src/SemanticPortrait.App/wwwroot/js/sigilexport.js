// Rasterize the Sigil SVG to a PNG data URL at wallpaper resolution. The deep-space background
// lives as CSS on the stage div (not inside the SVG), so it's painted onto the canvas first from
// the same hue/light values. Returns base64 (no data: prefix); C# writes the file — the Sigil is
// the app's ONLY designed egress, and it leaves solely through this user-initiated path.
window.spExportSigil = async function (svgId, size, bgHue, bgLight) {
    const el = document.getElementById(svgId);
    if (!el) return null;

    const xml = new XMLSerializer().serializeToString(el);
    const img = new Image();
    const url = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(xml);
    await new Promise((res, rej) => { img.onload = res; img.onerror = rej; img.src = url; });

    const c = document.createElement('canvas');
    c.width = c.height = size;
    const ctx = c.getContext('2d');

    // the stage's radial deep-space gradient, reproduced
    const g = ctx.createRadialGradient(size / 2, size * 0.42, 0, size / 2, size * 0.42, size * 0.85);
    g.addColorStop(0, `hsl(${bgHue} 45% ${bgLight + 6}%)`);
    g.addColorStop(0.55, `hsl(${bgHue} 40% ${bgLight}%)`);
    g.addColorStop(1, `hsl(${bgHue} 45% 2.5%)`);
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, size, size);

    ctx.drawImage(img, 0, 0, size, size);
    return c.toDataURL('image/png').split(',')[1];
};

// Rasterize the Constellation SVG to a 2x-scale PNG data URL (ConstellationView.razor, save path
// mirrors spExportSigil above). Unlike the Sigil, the constellation leans on its scoped .razor.css
// stylesheet for most of its polish (LoD display toggles, edge/label opacity, hull fog opacity) —
// that stylesheet lives OUTSIDE the <svg> element, so a plain XMLSerializer.serializeToString(el)
// would drop all of it and render flat/unstyled. Fix: clone the tree, then for every element read
// getComputedStyle from its still-attached LIVE counterpart (so animations/LoD are resolved to
// their current values) and bake the result onto the clone as an inline style attribute — a frozen
// snapshot of exactly what's on screen right now. cloneNode(true) already copies class/viewBox/etc
// verbatim, so whatever the user is currently panned/zoomed to is exactly what gets exported.
window.spExportConstellation = async function (svgEl) {
    if (!svgEl) return null;

    const PROPS = ['opacity', 'fill', 'fill-opacity', 'stroke', 'stroke-opacity', 'stroke-width',
        'stroke-dasharray', 'display', 'filter', 'font-size', 'font-weight', 'letter-spacing',
        'mix-blend-mode', 'text-anchor', 'transform'];
    function bake(liveEl, cloneEl) {
        const cs = getComputedStyle(liveEl);
        const decls = [];
        for (const p of PROPS) {
            const v = cs.getPropertyValue(p);
            if (v) decls.push(`${p}:${v}`);
        }
        const prior = cloneEl.getAttribute('style');
        cloneEl.setAttribute('style', prior ? `${prior};${decls.join(';')}` : decls.join(';'));
    }

    const clone = svgEl.cloneNode(true);
    bake(svgEl, clone);
    const liveEls = svgEl.querySelectorAll('*');
    const cloneEls = clone.querySelectorAll('*');
    for (let i = 0; i < liveEls.length; i++) bake(liveEls[i], cloneEls[i]);

    // Render at 2x the panel's current on-screen pixel size (task: "2x-scale PNG").
    const rect = svgEl.getBoundingClientRect();
    const w = Math.max(1, Math.round(rect.width * 2));
    const h = Math.max(1, Math.round(rect.height * 2));
    clone.setAttribute('width', w);
    clone.setAttribute('height', h);

    const xml = new XMLSerializer().serializeToString(clone);
    const img = new Image();
    const url = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(xml);
    await new Promise((res, rej) => { img.onload = res; img.onerror = rej; img.src = url; });

    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    const ctx = c.getContext('2d');
    // the stage's deep-space backdrop (.cstage CSS gradient), reproduced — the svg itself is
    // transparent, and a bare-black export would lose the atmosphere entirely.
    const g = ctx.createRadialGradient(w * 0.5, h * 0.45, 0, w * 0.5, h * 0.45, Math.max(w, h) * 0.85);
    g.addColorStop(0, '#141426');
    g.addColorStop(0.55, '#0b0b16');
    g.addColorStop(1, '#06060d');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, w, h);
    ctx.drawImage(img, 0, 0, w, h);
    return c.toDataURL('image/png').split(',')[1];
};
