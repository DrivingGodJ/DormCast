'use strict';
((root) => {
  const DEFAULT_THEME_COLOR = '#101612';
  const SAMPLE_SIZE = 48;
  const MIN_ACCENT_Y = 0.31;
  const MAX_ACCENT_Y = 0.74;
  const THEME_VARS = [
    '--bg',
    '--bg-translucent',
    '--surface',
    '--raised',
    '--raised-hover',
    '--raised-active',
    '--line',
    '--line-strong',
    '--line-focus',
    '--text',
    '--muted',
    '--dot-idle',
    '--accent',
    '--accent-hover',
    '--accent-active',
    '--accent-soft',
    '--accent-soft-hover',
    '--accent-border',
    '--ink',
    '--ambient',
    '--cover-glow'
  ];

  let sampleCanvas = null;
  let sampleCtx = null;

  function clamp(v, min, max) {
    return v < min ? min : v > max ? max : v;
  }

  function rgbToHsl(r, g, b) {
    const rn = clamp(r, 0, 255) / 255;
    const gn = clamp(g, 0, 255) / 255;
    const bn = clamp(b, 0, 255) / 255;
    const max = Math.max(rn, gn, bn);
    const min = Math.min(rn, gn, bn);
    const l = (max + min) / 2;
    const d = max - min;
    if (d < 1e-6) return { h: 0, s: 0, l };
    const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    let h;
    if (max === rn) h = ((gn - bn) / d + (gn < bn ? 6 : 0)) * 60;
    else if (max === gn) h = ((bn - rn) / d + 2) * 60;
    else h = ((rn - gn) / d + 4) * 60;
    return { h: (h + 360) % 360, s, l };
  }

  function hslToRgb(h, s, l) {
    const hue = ((h % 360) + 360) % 360;
    const sat = clamp(s, 0, 1);
    const light = clamp(l, 0, 1);
    const c = (1 - Math.abs(2 * light - 1)) * sat;
    const x = c * (1 - Math.abs(((hue / 60) % 2) - 1));
    const m = light - c / 2;
    let r1 = 0, g1 = 0, b1 = 0;
    if (hue < 60) [r1, g1, b1] = [c, x, 0];
    else if (hue < 120) [r1, g1, b1] = [x, c, 0];
    else if (hue < 180) [r1, g1, b1] = [0, c, x];
    else if (hue < 240) [r1, g1, b1] = [0, x, c];
    else if (hue < 300) [r1, g1, b1] = [x, 0, c];
    else [r1, g1, b1] = [c, 0, x];
    return {
      r: Math.round((r1 + m) * 255),
      g: Math.round((g1 + m) * 255),
      b: Math.round((b1 + m) * 255)
    };
  }

  function toHex(rgb) {
    const part = n => clamp(Math.round(n), 0, 255).toString(16).padStart(2, '0');
    return `#${part(rgb.r)}${part(rgb.g)}${part(rgb.b)}`;
  }

  function toRgba(rgb, alpha) {
    return `rgba(${clamp(Math.round(rgb.r), 0, 255)},${clamp(Math.round(rgb.g), 0, 255)},${clamp(Math.round(rgb.b), 0, 255)},${alpha})`;
  }

  function relativeLuminance(rgb) {
    const lin = c => {
      const n = clamp(c, 0, 255) / 255;
      return n <= 0.03928 ? n / 12.92 : ((n + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * lin(rgb.r) + 0.7152 * lin(rgb.g) + 0.0722 * lin(rgb.b);
  }

  function contrastRatio(a, b) {
    const ya = relativeLuminance(a);
    const yb = relativeLuminance(b);
    return (Math.max(ya, yb) + 0.05) / (Math.min(ya, yb) + 0.05);
  }

  function calibrateAccentLightness(h, s, baseL) {
    let l = baseL;
    const y0 = relativeLuminance(hslToRgb(h, s, l));
    if (y0 < MIN_ACCENT_Y) {
      let lo = l, hi = 0.92;
      for (let i = 0; i < 14; i++) {
        const mid = (lo + hi) / 2;
        if (relativeLuminance(hslToRgb(h, s, mid)) < MIN_ACCENT_Y) lo = mid;
        else hi = mid;
      }
      l = hi;
    } else if (y0 > MAX_ACCENT_Y) {
      let lo = 0.35, hi = l;
      for (let i = 0; i < 14; i++) {
        const mid = (lo + hi) / 2;
        if (relativeLuminance(hslToRgb(h, s, mid)) > MAX_ACCENT_Y) hi = mid;
        else lo = mid;
      }
      l = lo;
    }
    return l;
  }

  function extractDominantColor(rgba, width, height) {
    if (!rgba || !width || !height || rgba.length < width * height * 4) return null;
    const bins = Array.from({ length: 36 }, () => ({ r: 0, g: 0, b: 0, w: 0, count: 0 }));
    let validPixels = 0;
    let chromaticCount = 0;
    let allR = 0, allG = 0, allB = 0, allW = 0;

    for (let y = 0; y < height; y++) {
      const dy = (y + 0.5) / height - 0.5;
      for (let x = 0; x < width; x++) {
        const idx = (y * width + x) * 4;
        const a = rgba[idx + 3];
        if (a < 128) continue;
        const r = rgba[idx], g = rgba[idx + 1], b = rgba[idx + 2];
        const dx = (x + 0.5) / width - 0.5;
        const dist = Math.min(1, Math.hypot(dx, dy) * 2);
        const centerWeight = 0.72 + 0.28 * (1 - dist * dist);

        validPixels++;
        allR += r * centerWeight;
        allG += g * centerWeight;
        allB += b * centerWeight;
        allW += centerWeight;

        const { h, s, l } = rgbToHsl(r, g, b);
        if (s < 0.12 || l < 0.04 || l > 0.94) continue;

        chromaticCount++;
        const lPref = 1 - Math.pow(Math.abs(l - 0.5) * 2, 2.2) * 0.55;
        const sPref = 0.25 + 0.75 * Math.pow(s, 0.85);
        const w = centerWeight * lPref * sPref;
        const bin = Math.floor(h / 10) % 36;
        bins[bin].r += r * w;
        bins[bin].g += g * w;
        bins[bin].b += b * w;
        bins[bin].w += w;
        bins[bin].count++;
      }
    }

    if (validPixels === 0 || allW <= 0) return null;

    if (chromaticCount / validPixels < 0.04) {
      return {
        r: Math.round(allR / allW),
        g: Math.round(allG / allW),
        b: Math.round(allB / allW),
        monochrome: true
      };
    }

    let bestBin = 0;
    let bestScore = -1;
    for (let i = 0; i < 36; i++) {
      const prev = bins[(i + 35) % 36];
      const curr = bins[i];
      const next = bins[(i + 1) % 36];
      const score = prev.w * 0.5 + curr.w + next.w * 0.5;
      if (score > bestScore) {
        bestScore = score;
        bestBin = i;
      }
    }

    const cluster = [
      [bins[(bestBin + 35) % 36], 0.6],
      [bins[bestBin], 1.0],
      [bins[(bestBin + 1) % 36], 0.6]
    ];
    let sumR = 0, sumG = 0, sumB = 0, sumW = 0;
    for (const [b, factor] of cluster) {
      if (b.w <= 0) continue;
      sumR += b.r * factor;
      sumG += b.g * factor;
      sumB += b.b * factor;
      sumW += b.w * factor;
    }
    if (sumW <= 0) return null;

    return {
      r: Math.round(sumR / sumW),
      g: Math.round(sumG / sumW),
      b: Math.round(sumB / sumW),
      monochrome: false
    };
  }

  function buildPalette(dominant) {
    if (!dominant || !Number.isFinite(dominant.r) || !Number.isFinite(dominant.g) || !Number.isFinite(dominant.b)) {
      return null;
    }
    const hsl = rgbToHsl(dominant.r, dominant.g, dominant.b);
    const isMono = Boolean(dominant.monochrome) || hsl.s < 0.08;
    const h = isMono ? 215 : hsl.h;

    const surfS = isMono ? 0.04 : clamp(0.14 + hsl.s * 0.14, 0.14, 0.28);
    const lineS = isMono ? 0.05 : clamp(0.14 + hsl.s * 0.16, 0.14, 0.30);
    const textS = isMono ? 0.04 : clamp(0.10 + hsl.s * 0.12, 0.10, 0.22);

    const bgRgb = hslToRgb(h, surfS, 0.075);
    const surfaceRgb = hslToRgb(h, surfS, 0.118);
    const raisedRgb = hslToRgb(h, surfS, 0.155);
    const raisedHoverRgb = hslToRgb(h, surfS, 0.200);
    const raisedActiveRgb = hslToRgb(h, surfS, 0.132);
    const lineRgb = hslToRgb(h, lineS, 0.238);
    const lineStrongRgb = hslToRgb(h, lineS, 0.305);
    const lineFocusRgb = hslToRgb(h, lineS, 0.425);
    const textRgb = hslToRgb(h, textS, 0.950);
    const mutedRgb = hslToRgb(h, isMono ? 0.04 : clamp(surfS * 0.72, 0.08, 0.18), 0.725);
    const dotIdleRgb = hslToRgb(h, isMono ? 0.03 : 0.08, 0.565);

    let accentRgb, accentHoverRgb, accentActiveRgb, inkRgb;
    if (isMono) {
      accentRgb = hslToRgb(h, 0.08, 0.84);
      accentHoverRgb = hslToRgb(h, 0.08, 0.91);
      accentActiveRgb = hslToRgb(h, 0.08, 0.74);
      inkRgb = hslToRgb(h, 0.14, 0.10);
    } else {
      const accentS = clamp(0.48 + hsl.s * 0.40, 0.50, 0.88);
      const baseL = clamp(0.65 + (hsl.l - 0.5) * 0.16, 0.58, 0.73);
      const accentL = calibrateAccentLightness(h, accentS, baseL);
      accentRgb = hslToRgb(h, accentS, accentL);
      accentHoverRgb = hslToRgb(h, clamp(accentS + 0.06, 0.50, 0.94), Math.min(0.92, accentL + 0.07));
      accentActiveRgb = hslToRgb(h, clamp(accentS - 0.08, 0.42, 0.84), Math.max(0.38, accentL - 0.07));
      inkRgb = hslToRgb(h, clamp(accentS * 0.55, 0.22, 0.48), 0.09);
    }

    const bgHex = toHex(bgRgb);
    const vars = {
      '--bg': bgHex,
      '--bg-translucent': toRgba(bgRgb, 0.48),
      '--surface': toHex(surfaceRgb),
      '--raised': toHex(raisedRgb),
      '--raised-hover': toHex(raisedHoverRgb),
      '--raised-active': toHex(raisedActiveRgb),
      '--line': toHex(lineRgb),
      '--line-strong': toHex(lineStrongRgb),
      '--line-focus': toHex(lineFocusRgb),
      '--text': toHex(textRgb),
      '--muted': toHex(mutedRgb),
      '--dot-idle': toHex(dotIdleRgb),
      '--accent': toHex(accentRgb),
      '--accent-hover': toHex(accentHoverRgb),
      '--accent-active': toHex(accentActiveRgb),
      '--accent-soft': toRgba(accentRgb, 0.14),
      '--accent-soft-hover': toRgba(accentRgb, 0.24),
      '--accent-border': toRgba(accentRgb, 0.44),
      '--ink': toHex(inkRgb),
      '--ambient': toRgba(accentRgb, isMono ? 0.08 : 0.12),
      '--cover-glow': toRgba(accentRgb, isMono ? 0.16 : 0.24)
    };

    return {
      vars,
      themeColor: bgHex,
      dominant: { r: dominant.r, g: dominant.g, b: dominant.b, monochrome: isMono },
      accentRgb,
      surfaceRgb,
      inkRgb
    };
  }

  function setMetaThemeColor(color) {
    if (typeof document === 'undefined') return;
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta && meta.getAttribute('content') !== color) meta.setAttribute('content', color);
  }

  function applyPalette(palette) {
    if (typeof document === 'undefined' || !palette?.vars) return null;
    const style = document.documentElement.style;
    for (const key of THEME_VARS) {
      if (palette.vars[key] !== undefined) style.setProperty(key, palette.vars[key]);
    }
    document.documentElement.dataset.themeSource = 'cover';
    setMetaThemeColor(palette.themeColor || DEFAULT_THEME_COLOR);
    return palette;
  }

  function applyColor(dominant) {
    const palette = buildPalette(dominant);
    if (!palette) {
      reset();
      return null;
    }
    return applyPalette(palette);
  }

  function reset() {
    if (typeof document === 'undefined') return;
    const style = document.documentElement.style;
    for (const key of THEME_VARS) style.removeProperty(key);
    document.documentElement.dataset.themeSource = 'default';
    setMetaThemeColor(DEFAULT_THEME_COLOR);
  }

  function applyFromImage(img) {
    if (typeof document === 'undefined' || !img || !img.naturalWidth || !img.naturalHeight) {
      reset();
      return null;
    }
    try {
      if (!sampleCanvas) {
        sampleCanvas = document.createElement('canvas');
        sampleCanvas.width = SAMPLE_SIZE;
        sampleCanvas.height = SAMPLE_SIZE;
        sampleCtx = sampleCanvas.getContext('2d', { willReadFrequently: true });
      }
      if (!sampleCtx) {
        reset();
        return null;
      }
      sampleCtx.clearRect(0, 0, SAMPLE_SIZE, SAMPLE_SIZE);
      sampleCtx.drawImage(img, 0, 0, SAMPLE_SIZE, SAMPLE_SIZE);
      const { data } = sampleCtx.getImageData(0, 0, SAMPLE_SIZE, SAMPLE_SIZE);
      const dominant = extractDominantColor(data, SAMPLE_SIZE, SAMPLE_SIZE);
      if (!dominant) {
        reset();
        return null;
      }
      return applyColor(dominant);
    } catch {
      reset();
      return null;
    }
  }

  root.DormCastTheme = {
    DEFAULT_THEME_COLOR,
    THEME_VARS,
    rgbToHsl,
    hslToRgb,
    relativeLuminance,
    contrastRatio,
    extractDominantColor,
    buildPalette,
    applyColor,
    applyFromImage,
    reset
  };
})(typeof window === 'undefined' ? globalThis : window);
