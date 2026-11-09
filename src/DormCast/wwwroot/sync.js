'use strict';
((root) => {
  class SourceClock {
    constructor() { this.samples = []; }
    observe(t0, t1, t2, t3) {
      if (![t0, t1, t2, t3].every(Number.isFinite) || t3 < t0 || t2 < t1) return;
      const rtt = (t3 - t0) - (t2 - t1);
      if (rtt < -1 || rtt > 1000) return;
      this.samples.push({ at: t3, rtt: Math.max(0, rtt), offset: ((t1 - t0) + (t2 - t3)) / 2 });
      this.samples = this.samples.filter(s => t3 - s.at < 30000).slice(-24);
    }
    get best() { return [...this.samples].sort((a, b) => a.rtt - b.rtt)[0] || null; }
    get ready() { return this.samples.length >= 3 && this.best?.rtt <= 100; }
  }
  function sourceLatency(source, clock, now, timeOrigin, outputMs, timelineStartMs = 40) {
    if (!clock.ready || !source || !Number.isFinite(source.rtpTimestamp) || !Number.isFinite(source.timestamp) || !Number.isFinite(outputMs)) return null;
    // W3C defines synchronization-source timestamps as timeOrigin + performance.now().
    const deliveredAt = source.timestamp > 1e10 ? source.timestamp - timeOrigin : source.timestamp;
    if (now - deliveredAt > 500 || now - deliveredAt < -50) return null;
    const serverReference = now + clock.best.offset - timelineStartMs;
    const wrapMs = 4294967296 / 48;
    const rawMs = (source.rtpTimestamp >>> 0) / 48;
    const sampleMs = rawMs + Math.round((serverReference - rawMs) / wrapMs) * wrapMs;
    const latency = deliveredAt + clock.best.offset - (sampleMs + timelineStartMs) + outputMs;
    return latency >= -10 && latency <= 3000 ? Math.max(0, latency) : null;
  }
  function delayPlan(targetMs, naturalMs, correctionMs, currentSeconds) {
    if (![targetMs, naturalMs, correctionMs, currentSeconds].every(Number.isFinite)) return null;
    const seconds = Math.max(0, Math.min(2, (targetMs - naturalMs - correctionMs) / 1000));
    return { seconds, duration: Math.max(0.35, Math.abs(seconds - currentSeconds) / 0.03) };
  }
  root.DormCastSync = { SourceClock, sourceLatency, delayPlan };
})(typeof window === 'undefined' ? globalThis : window);
