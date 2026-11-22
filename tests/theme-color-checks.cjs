const assert = require('node:assert/strict');
require('../src/DormCast/wwwroot/theme.js');
const {
  DEFAULT_THEME_COLOR,
  rgbToHsl,
  hslToRgb,
  contrastRatio,
  extractDominantColor,
  buildPalette
} = globalThis.DormCastTheme;

function makeBuffer(w, h, fn) {
  const out = new Uint8ClampedArray(w * h * 4);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const [r, g, b, a = 255] = fn(x, y, w, h);
      const i = (y * w + x) * 4;
      out[i] = r; out[i + 1] = g; out[i + 2] = b; out[i + 3] = a;
    }
  }
  return out;
}

assert.equal(DEFAULT_THEME_COLOR, '#101612');
assert.equal(extractDominantColor(new Uint8ClampedArray(16), 2, 2), null, 'fully transparent image yields null');

// Dominant red with dark border should pick red, not gray/black.
const redBuf = makeBuffer(32, 32, (x, y, w, h) =>
  (x < 4 || x >= w - 4 || y < 4 || y >= h - 4) ? [15, 15, 15] : [215, 48, 62]
);
const redDom = extractDominantColor(redBuf, 32, 32);
assert.ok(redDom && !redDom.monochrome, 'vivid crimson cover is chromatic');
assert.ok(redDom.r > 180 && redDom.g < 80 && redDom.b < 90, 'crimson dominant color stays red');

// Dominant cobalt blue with small white text strip should pick blue.
const blueBuf = makeBuffer(32, 32, (x, y) => y >= 26 ? [245, 245, 245] : [34, 112, 224]);
const blueDom = extractDominantColor(blueBuf, 32, 32);
assert.ok(blueDom && !blueDom.monochrome && blueDom.b > 190 && blueDom.b > blueDom.r * 2, 'blue cover extracts blue');

// Pure grayscale / monochrome cover should be flagged monochrome.
const monoBuf = makeBuffer(32, 32, (x, y) => (((x >> 2) + (y >> 2)) % 2 === 0 ? [230, 230, 230] : [25, 25, 25]));
const monoDom = extractDominantColor(monoBuf, 32, 32);
assert.ok(monoDom && monoDom.monochrome, 'black and white cover is detected as monochrome');
const monoPalette = buildPalette(monoDom);
assert.ok(contrastRatio(monoPalette.accentRgb, monoPalette.surfaceRgb) >= 5.5, 'monochrome accent keeps strong contrast on surface');
assert.ok(contrastRatio(monoPalette.accentRgb, monoPalette.inkRgb) >= 6.0, 'monochrome button ink keeps strong contrast on accent');

// Sweep all 360 degrees of hue across dark, mid, and bright covers to verify WCAG contrast invariants.
for (let h = 0; h < 360; h += 15) {
  for (const [s, l] of [[0.35, 0.35], [0.85, 0.50], [0.90, 0.78]]) {
    const rgb = hslToRgb(h, s, l);
    const palette = buildPalette(rgb);
    assert.ok(palette && palette.vars['--accent'].startsWith('#'), `palette built for h=${h}`);
    const onSurface = contrastRatio(palette.accentRgb, palette.surfaceRgb);
    const onAccent = contrastRatio(palette.accentRgb, palette.inkRgb);
    assert.ok(onSurface >= 5.0, `accent vs surface contrast >= 5.0 for h=${h}, s=${s}, l=${l} (got ${onSurface.toFixed(2)})`);
    assert.ok(onAccent >= 5.5, `ink vs accent contrast >= 5.5 for h=${h}, s=${s}, l=${l} (got ${onAccent.toFixed(2)})`);
    const outHue = rgbToHsl(palette.accentRgb.r, palette.accentRgb.g, palette.accentRgb.b).h;
    const hueDiff = Math.min(Math.abs(outHue - h), 360 - Math.abs(outHue - h));
    assert.ok(hueDiff < 4, `accent preserves dominant hue for h=${h} (got ${outHue.toFixed(1)})`);
  }
}

console.log('PASS dominant color extraction, monochrome detection, hue fidelity and WCAG contrast across all hues');
