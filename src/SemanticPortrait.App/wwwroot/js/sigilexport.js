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
