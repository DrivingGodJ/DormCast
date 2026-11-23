const assert = require('node:assert/strict');
require('../src/DormCast/wwwroot/sync.js');
const { SourceClock, sourceLatency, delayPlan, evaluateSyncGate } = globalThis.DormCastSync;
const clock = new SourceClock();
for (let i = 0; i < 3; i++) clock.observe(1000 + i * 100, 1510 + i * 100, 1520 + i * 100, 1030 + i * 100);
assert.equal(clock.ready, true); assert.equal(clock.best.offset, 500); assert.equal(clock.best.rtt, 20);
clock.observe(1400, 1950, 1960, 1490);
assert.equal(clock.best.offset, 500, 'minimum network-RTT clock estimate ignores queued slow probes');
const origin = 1700000000000;
assert.equal(sourceLatency({ timestamp: origin + 2000, rtpTimestamp: 2300 * 48 }, clock, 2005, origin, 40), 200);
assert.equal(sourceLatency({ timestamp: origin + 1000, rtpTimestamp: 2300 * 48 }, clock, 2005, origin, 40), null);
const wrapMs = 4294967296 / 48;
assert.ok(Math.abs(sourceLatency({ timestamp: origin + wrapMs + 2000, rtpTimestamp: 2300 * 48 }, clock, wrapMs + 2005, origin, 40) - 200) < .001);
assert.equal(delayPlan(340, 100, 0, 0).seconds, .24);
assert.equal(delayPlan(340, 300, 0, 0).seconds, .04);
assert.equal(delayPlan(340, 100, 100, 0).seconds, .14);
assert.equal(delayPlan(100, 300, 0, 0).seconds, 0, 'negative compensating delay is never scheduled');
assert.equal(delayPlan(9000, 100, 0, 0).seconds, 2, 'output history remains bounded');
assert.ok(delayPlan(340, 100, 0, 0).duration >= 5.5 && delayPlan(340, 100, 0, 0).duration <= 6.5, 'large changes converge smoothly without provoking jitter buffer expansion');

// Sync-lock gate: wait until aligned before unmuting
let g = evaluateSyncGate({ sampleCount: 2, serverCompensation: true, hostReady: true, senderDelayMs: 140, plannedSenderDelayMs: 140, errorMs: 5, alignedSinceMs: null, nowMs: 1000, elapsedMs: 500 });
assert.equal(g.unlock, false); assert.equal(g.reason, 'measuring');
g = evaluateSyncGate({ sampleCount: 3, serverCompensation: true, hostReady: false, senderDelayMs: 140, plannedSenderDelayMs: 180, errorMs: 10, alignedSinceMs: null, nowMs: 1200, elapsedMs: 700 });
assert.equal(g.unlock, false); assert.equal(g.reason, 'compensating');
g = evaluateSyncGate({ sampleCount: 3, serverCompensation: true, hostReady: true, senderDelayMs: 175, plannedSenderDelayMs: 180, errorMs: 42, alignedSinceMs: null, nowMs: 1400, elapsedMs: 900 });
assert.equal(g.unlock, false); assert.equal(g.reason, 'aligning');
// First aligned tick starts confirmation window (stays muted even if called again 10ms later)
g = evaluateSyncGate({ sampleCount: 3, serverCompensation: true, hostReady: true, senderDelayMs: 176, plannedSenderDelayMs: 180, errorMs: 12, alignedSinceMs: null, nowMs: 1600, elapsedMs: 1100 });
assert.equal(g.unlock, false); assert.equal(g.reason, 'confirming'); assert.equal(g.alignedSinceMs, 1600);
g = evaluateSyncGate({ sampleCount: 3, serverCompensation: true, hostReady: true, senderDelayMs: 177, plannedSenderDelayMs: 180, errorMs: 10, alignedSinceMs: g.alignedSinceMs, nowMs: 1610, elapsedMs: 1110 });
assert.equal(g.unlock, false); assert.equal(g.reason, 'confirming');
// Jitter spike resets confirmation window
g = evaluateSyncGate({ sampleCount: 3, serverCompensation: true, hostReady: true, senderDelayMs: 178, plannedSenderDelayMs: 180, errorMs: -35, alignedSinceMs: g.alignedSinceMs, nowMs: 1800, elapsedMs: 1300 });
assert.equal(g.unlock, false); assert.equal(g.reason, 'aligning'); assert.equal(g.alignedSinceMs, null);
// Sustained alignment >= 300ms unlocks cleanly
g = evaluateSyncGate({ sampleCount: 3, serverCompensation: true, hostReady: true, senderDelayMs: 180, plannedSenderDelayMs: 180, errorMs: -8, alignedSinceMs: null, nowMs: 2000, elapsedMs: 1500 });
assert.equal(g.unlock, false); assert.equal(g.reason, 'confirming');
g = evaluateSyncGate({ sampleCount: 3, serverCompensation: true, hostReady: true, senderDelayMs: 180, plannedSenderDelayMs: 180, errorMs: 6, alignedSinceMs: g.alignedSinceMs, nowMs: 2300, elapsedMs: 1800 });
assert.equal(g.unlock, true); assert.equal(g.reason, 'locked');
// Bounded safety timeout prevents permanent silence on slow Bluetooth devices
g = evaluateSyncGate({ sampleCount: 3, serverCompensation: true, hostReady: true, senderDelayMs: 0, plannedSenderDelayMs: 0, errorMs: 140, alignedSinceMs: null, nowMs: 11000, elapsedMs: 10000 });
assert.equal(g.unlock, true); assert.equal(g.reason, 'timeout');
// Fallback timing mode (missing getSynchronizationSources) unlocks immediately once 3 samples collected
g = evaluateSyncGate({ sampleCount: 3, fallbackTiming: true, serverCompensation: true, hostReady: false, senderDelayMs: 0, plannedSenderDelayMs: 0, errorMs: Number.NaN, alignedSinceMs: null, nowMs: 5000, elapsedMs: 4500 });
assert.equal(g.unlock, true); assert.equal(g.reason, 'fallback');
console.log('PASS NTP four timestamps, RTP timeline/wrap, stale rejection, group compensation, smooth bounded delay and sync-locked unmute gate');
