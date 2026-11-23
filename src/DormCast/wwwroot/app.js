'use strict';
(() => {
  const el = id => document.getElementById(id);
  const tokenStorageKey = `DormCast.joinToken.${location.host}`;
  function readCookie(name) {
    const prefix = `${name}=`;
    for (const part of document.cookie.split(';')) {
      const trimmed = part.trim();
      if (trimmed.startsWith(prefix)) return decodeURIComponent(trimmed.slice(prefix.length));
    }
    return '';
  }
  function saveToken(value) {
    if (!value) return;
    try { localStorage.setItem(tokenStorageKey, value); } catch {}
    try { document.cookie = `DormCastJoin=${encodeURIComponent(value)}; path=/; max-age=31536000; samesite=strict`; } catch {}
  }
  function clearSavedToken() {
    try { localStorage.removeItem(tokenStorageKey); } catch {}
    try { document.cookie = 'DormCastJoin=; path=/; max-age=0; samesite=strict'; } catch {}
  }
  let token = new URLSearchParams(location.hash.slice(1)).get('join') || '';
  if (!token) { try { token = localStorage.getItem(tokenStorageKey) || ''; } catch {} }
  if (!token) token = readCookie('DormCastJoin');
  const clientName = window.DormCastAndroid ? 'Android 安卓客户端' : /iPhone|iPad/.test(navigator.userAgent) ? 'iPhone / iPad' : /Android/.test(navigator.userAgent) ? 'Android 浏览器' : /Macintosh/.test(navigator.userAgent) ? 'Mac 浏览器' : '电脑浏览器';
  const { SourceClock, sourceLatency, delayPlan } = window.DormCastSync;
  let ws, pc, audioContext, source, gain, fallback, stream, silentSink, delayNode, mediaOutput, mediaDestination;
  let generation = 0, retryTimer, statsTimer, pingTimer, syncTimer, retries = 0, ended = false, paused = false;
  let previous = null, candidates = [], targetMs = 60, connected = false, hinted = false;
  let sourceClock = new SourceClock(), syncEnabled = true, commonTargetMs = 250, playoutTargetMs = 250, timelineStartMs = 40;
  let naturalWindow = [], naturalMs = null, syncReady = false, ramp = null, plannedDelay = null, lastSyncReport = 0, lastPing = 0;
  let playbackStartedAt = 0, bufferEstimateMs = 60, fallbackTiming = false, referenceDevice = '等待测量';
  let localMonitorOnly = false;
  let syncDirection = 'stable', desiredTargetMs = 250;
  let hostReadyAt = 0, displayError = null;
  let serverCompensation = false, senderDelayMs = 0, measuredAt = 0;
  let wsReady = false, remoteControl = { enabled: false, volume: { available: false, level: 0.5, muted: false, deviceName: null } };
  let hostNowPlaying = { Playing: false, HasSession: false };
  let volumeDragging = false, lastVolumeInputAt = 0, pendingVolumeLevel = null, volumeTimer = null, feedbackTimer = null, feedbackActive = false;
  let backgroundMode = Boolean(window.DormCastAndroid) || /Android|iPhone|iPad|iPod/.test(navigator.userAgent) || (/Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1);
  try { const saved = localStorage.getItem('DormCast.backgroundMode'); if (saved !== null) backgroundMode = saved === 'true'; } catch {}
  el('backgroundMode').checked = backgroundMode;
  const media = window.DormCastMedia;
  let hardwareMs = 0;
  try { hardwareMs = Math.max(0, Math.min(1000, Number(localStorage.getItem('DormCast.hardwareOffsetMs')) || 0)); } catch {}
  el('hardwareOffset').value = hardwareMs;
  function useDirectBackgroundAudio() { return backgroundMode && serverCompensation; }
  function initIosPwaGuide() {
    const guide = el('iosPwaGuide');
    if (!guide) return;
    const isIos = /iPhone|iPad|iPod/.test(navigator.userAgent) || (/Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1);
    const isStandalone = window.navigator.standalone === true || Boolean(window.matchMedia?.('(display-mode: standalone)').matches);
    let dismissed = false;
    try { dismissed = localStorage.getItem('DormCast.iosPwaDismissed') === 'true'; } catch {}
    if (isIos && !isStandalone && !window.DormCastAndroid && !dismissed) guide.hidden = false;
    el('iosPwaClose')?.addEventListener('click', () => { guide.hidden = true; });
    el('iosPwaDismiss')?.addEventListener('click', () => {
      guide.hidden = true;
      try { localStorage.setItem('DormCast.iosPwaDismissed', 'true'); } catch {}
    });
  }
  initIosPwaGuide();
  function addedDelaySeconds() {
    if (!ramp || !audioContext) return 0;
    const fraction = Math.max(0, Math.min(1, (audioContext.currentTime - ramp.at) / ramp.duration));
    return ramp.from + (ramp.to - ramp.from) * fraction;
  }
  function syncTick() {
    if (!syncEnabled) { el('syncStatus').textContent = '主机未启用统一同步。'; return; }
    el('groupTarget').textContent = `${Math.round(commonTargetMs)} ms`;
    const directAudio = useDirectBackgroundAudio();
    if (!sourceClock.ready) return;
    if (directAudio) { if (!isPlaying()) return; }
    else if (!audioContext || audioContext.state !== 'running') return;
    const receiver = pc?.getReceivers().find(r => r.track.kind === 'audio');
    const point = receiver?.getSynchronizationSources?.()[0];
    const outputMs = directAudio ? 0 : ((audioContext.baseLatency || 0) + (audioContext.outputLatency || 0)) * 1000;
    let measurement = sourceLatency(point, sourceClock, performance.now(), performance.timeOrigin, outputMs, timelineStartMs);
    fallbackTiming = measurement === null;
    if (fallbackTiming) {
      if (serverCompensation && document.hidden) { el('syncStatus').textContent = '后台由主机持续补偿，本机测量等待恢复前台。'; return; }
      if (performance.now() - playbackStartedAt < 4000) { el('syncStatus').textContent = '正在等待源播放时间戳。'; return; }
      // Compatibility playback must remain audible even when a browser omits RTP playout timestamps.
      // Its buffer estimate never participates in choosing the slowest measured device.
      measurement = bufferEstimateMs + sourceClock.best.rtt / 2 + outputMs;
    }
    naturalWindow.push(measurement); if (naturalWindow.length > 3) naturalWindow.shift();
    if (!fallbackTiming) measuredAt = performance.now();
    const sorted = [...naturalWindow].sort((a, b) => a - b); naturalMs = sorted[Math.floor(sorted.length / 2)];
    const corrected = naturalMs + hardwareMs;
    const reportInterval = syncReady ? 500 : 250;
    if (performance.now() - lastSyncReport > reportInterval) {
      send({ type: 'sync-report', data: { naturalMs: corrected, uncertaintyMs: sourceClock.best.rtt / 2, playing: connected && isPlaying() && !fallbackTiming } });
      lastSyncReport = performance.now();
    }
    if (corrected - (serverCompensation ? senderDelayMs : 0) > 1940) {
      if (directAudio && mediaOutput) mediaOutput.muted = true;
      else if (gain && audioContext) gain.gain.setTargetAtTime(0, audioContext.currentTime, 0.03);
      el('syncStatus').textContent = '此设备超过 2 秒同步上限，请重连或检查蓝牙/网络。';
      return;
    }
    if (naturalWindow.length < 3 || (!directAudio && !delayNode)) return;
    if (serverCompensation && !fallbackTiming && (!hostReadyAt || performance.now() < hostReadyAt)) return;
    if (directAudio) {
      if (!syncReady) {
        syncReady = true;
        if (!paused && mediaOutput) { mediaOutput.muted = false; mediaOutput.volume = 1; }
        if (connected && !paused) el('message').textContent = '主机播放什么，这里就听到什么。';
      }
    } else {
      const current = addedDelaySeconds(), plan = serverCompensation ? { seconds: 0, duration: 0.25 } : delayPlan(commonTargetMs, naturalMs, hardwareMs, current);
      if (!syncReady) {
        delayNode.delayTime.setValueAtTime(plan.seconds, audioContext.currentTime);
        ramp = { from: plan.seconds, to: plan.seconds, at: audioContext.currentTime, duration: 1 };
        plannedDelay = plan.seconds; syncReady = true;
        if (!paused) gain.gain.setTargetAtTime(1, audioContext.currentTime, 0.04);
        if (connected && !paused) el('message').textContent = '主机播放什么，这里就听到什么。';
      } else if (Math.abs(plan.seconds - plannedDelay) > 0.008) {
        delayNode.delayTime.cancelScheduledValues(audioContext.currentTime);
        delayNode.delayTime.setValueAtTime(current, audioContext.currentTime);
        delayNode.delayTime.linearRampToValueAtTime(plan.seconds, audioContext.currentTime + plan.duration);
        ramp = { from: current, to: plan.seconds, at: audioContext.currentTime, duration: plan.duration }; plannedDelay = plan.seconds;
      }
    }
    const instantaneousError = corrected + (directAudio ? 0 : addedDelaySeconds() * 1000) - playoutTargetMs;
    displayError = displayError === null ? instantaneousError : displayError + (instantaneousError - displayError) * 0.25;
    const error = displayError;
    el('syncStatus').textContent = document.hidden && serverCompensation ? '后台由主机持续补偿；实际播放测量需恢复前台确认。' : fallbackTiming ? '兼容播放：浏览器缺少源时间戳，缓冲估计无法保证同步。' : `${Math.abs(error) > 15 ? '正在平滑对齐' : '已按源时间线补偿'} · 平滑估计偏差 ${error > 0 ? '+' : ''}${Math.round(error)}ms`;
    const direction = syncDirection === 'decreasing' ? `正在下调至 ${Math.round(desiredTargetMs)}ms。` : syncDirection === 'settling' ? `观察稳定后下调至 ${Math.round(desiredTargetMs)}ms。` : '';
    el('syncDetail').textContent = serverCompensation ? `${direction}主机补偿 ${Math.round(senderDelayMs)}ms · 参考：${referenceDevice}。${directAudio ? '已直连原生音频播放器以增强后台保活。' : backgroundMode ? '后台媒体输出的额外缓冲可在详情中校准。' : ''}` : `${direction}基础 ${Math.round(naturalMs)}ms + 硬件校准 ${hardwareMs}ms + 自动补偿 ${Math.round(addedDelaySeconds() * 1000)}ms。参考：${referenceDevice}。`;
  }
  function status(text, message, live = false) {
    el('connection').textContent = text;
    if (message !== undefined) el('message').textContent = message;
    el('dot').classList.toggle('live', live);
    document.body.classList.toggle('playing', live && !paused && isPlaying());
    media.state(isPlaying() && live ? 'playing' : connected ? 'paused' : 'none');
  }
  function send(value) { if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(value)); }
  function normalizeControl(raw) {
    if (!raw) return null;
    const v = raw.Volume ?? raw.volume ?? {};
    return {
      enabled: (raw.Enabled ?? raw.enabled) === true,
      volume: {
        available: (v.Available ?? v.available) === true,
        level: Math.max(0, Math.min(1, Number(v.Level ?? v.level ?? 0) || 0)),
        muted: (v.Muted ?? v.muted) === true,
        deviceName: v.DeviceName ?? v.deviceName ?? null
      }
    };
  }
  function defaultRemoteFeedback() {
    if (!wsReady || ended) return '连接后可控制主机播放、切歌与音量';
    if (!remoteControl.enabled) return '主机已在设置中关闭浏览器远程控制';
    if (remoteControl.volume.available && remoteControl.volume.deviceName) return `主机扬声器：${remoteControl.volume.deviceName}`;
    return '可控制主机播放、暂停与切歌';
  }
  function showRemoteFeedback(text, isError = false) {
    if (!text) return;
    clearTimeout(feedbackTimer);
    feedbackActive = true;
    const node = el('remoteFeedback');
    node.textContent = text;
    node.classList.toggle('error', isError);
    feedbackTimer = setTimeout(() => {
      feedbackActive = false;
      node.classList.remove('error');
      node.textContent = defaultRemoteFeedback();
    }, 2600);
  }
  function renderRemoteControl() {
    const active = wsReady && !ended && remoteControl.enabled;
    const volActive = active && remoteControl.volume.available;
    el('remoteControl').classList.toggle('is-disabled', !active);
    el('hostPrev').disabled = !active;
    el('hostPlayPause').disabled = !active;
    el('hostNext').disabled = !active;
    el('hostMute').disabled = !volActive;
    el('hostVolDown').disabled = !volActive;
    el('hostVolUp').disabled = !volActive;
    el('hostVolume').disabled = !volActive;

    const playing = Boolean(hostNowPlaying.Playing);
    const hasSession = Boolean(hostNowPlaying.HasSession);
    const badge = el('hostPlaybackBadge');
    badge.classList.toggle('playing', active && playing);
    badge.textContent = !wsReady || ended ? '未连接' : !remoteControl.enabled ? '主机已关闭' : playing ? '主机播放中' : hasSession ? '主机已暂停' : '已连接';

    el('hostPlayIcon').hidden = playing;
    el('hostPauseIcon').hidden = !playing;
    el('hostPlayLabel').textContent = playing ? '暂停' : hasSession ? '播放' : '播放/暂停';
    el('hostPlayPause').classList.toggle('is-playing', active && playing);
    el('hostPlayPause').setAttribute('aria-pressed', String(playing));
    el('hostPlayPause').setAttribute('aria-label', playing ? '暂停主机播放' : hasSession ? '继续主机播放' : '切换主机播放或暂停');

    if (remoteControl.volume.available) {
      if (!volumeDragging && performance.now() - lastVolumeInputAt > 600) {
        const pct = Math.round(remoteControl.volume.level * 100);
        el('hostVolume').value = String(pct);
        el('hostVolumeValue').textContent = remoteControl.volume.muted ? '静音' : `${pct}%`;
        el('hostMute').classList.toggle('is-muted', remoteControl.volume.muted);
        el('hostVolIcon').hidden = remoteControl.volume.muted;
        el('hostMuteIcon').hidden = !remoteControl.volume.muted;
      }
    } else {
      el('hostVolumeValue').textContent = '—';
      el('hostMute').classList.remove('is-muted');
      el('hostVolIcon').hidden = false;
      el('hostMuteIcon').hidden = true;
    }
    if (!feedbackActive) {
      el('remoteFeedback').classList.remove('error');
      el('remoteFeedback').textContent = defaultRemoteFeedback();
    }
  }
  function applyHostState(nextPlaying, nextControl) {
    if (nextPlaying) {
      hostNowPlaying = {
        Playing: Boolean(nextPlaying.Playing ?? nextPlaying.playing),
        HasSession: Boolean(nextPlaying.HasSession ?? nextPlaying.hasSession)
      };
    }
    const normalized = normalizeControl(nextControl);
    if (normalized) remoteControl = normalized;
    renderRemoteControl();
  }
  function sendHostControl(payload) {
    if (!wsReady || ended || !remoteControl.enabled) return;
    send({ type: 'control', ...payload });
  }
  function flushVolume() {
    clearTimeout(volumeTimer); volumeTimer = null;
    if (pendingVolumeLevel === null) return;
    const level = pendingVolumeLevel;
    pendingVolumeLevel = null;
    sendHostControl({ action: 'volume', level });
  }
  function isPlaying() {
    if (paused) return false;
    if (useDirectBackgroundAudio()) return Boolean(mediaOutput && !mediaOutput.paused);
    return Boolean(audioContext ? audioContext.state === 'running' && silentSink && !silentSink.paused && (!mediaOutput || !mediaOutput.paused) : fallback && !fallback.paused);
  }
  function audioElement() {
    const audio = new Audio(); audio.setAttribute('playsinline', ''); audio.className = 'audio-output'; document.body.append(audio); return audio;
  }
  function interrupted() {
    if (!connected || paused || !playbackStartedAt || isPlaying()) return;
    send({ type: 'sync-report', data: { playing: false } });
    el('listen').textContent = '继续收听 ↗';
    status('播放被系统暂停', '返回页面后点「继续收听」。请检查省电设置；部分系统会暂停后台网页。');
  }
  async function play() {
    if (localMonitorOnly) { status('主机网页预览', '主机声音由桌面软件同步播放，网页不重复出声，避免回授。', connected); return; }
    if (!stream) { status('等待音频', '正在连接主机，连接成功后即可开始收听。'); return; }
    try {
      if (useDirectBackgroundAudio()) {
        // Bypass AudioContext entirely when host handles delay compensation, keeping native media playback alive in mobile background/lock-screen.
        if (!mediaOutput) {
          mediaOutput = audioElement();
          mediaOutput.srcObject = stream;
          mediaOutput.muted = syncEnabled && !syncReady;
          mediaOutput.volume = 1;
          mediaOutput.addEventListener('pause', interrupted);
        }
        await mediaOutput.play();
      } else {
        // Chromium needs a playing media element to pull remote WebRTC audio into Web Audio.
        // The original track remains silent; only the compensated graph is audible.
        if (!silentSink) {
          silentSink = audioElement(); silentSink.muted = true; silentSink.volume = 0;
          silentSink.autoplay = true; silentSink.srcObject = stream;
        }
        const sinkPlayback = silentSink.play();
        if (!audioContext && !fallback) {
          const AC = window.AudioContext || window.webkitAudioContext;
          if (AC) {
            audioContext = new AC({ latencyHint: 'interactive' });
            source = audioContext.createMediaStreamSource(stream);
            gain = audioContext.createGain();
            gain.gain.value = syncEnabled ? 0 : 1;
            delayNode = audioContext.createDelay(2); source.connect(delayNode); delayNode.connect(gain);
            if (backgroundMode) {
              mediaDestination = audioContext.createMediaStreamDestination(); gain.connect(mediaDestination);
              mediaOutput = audioElement(); mediaOutput.srcObject = mediaDestination.stream;
              mediaOutput.addEventListener('pause', interrupted);
            } else gain.connect(audioContext.destination);
            audioContext.addEventListener('statechange', interrupted);
          } else {
            fallback = audioElement(); fallback.srcObject = stream; fallback.addEventListener('pause', interrupted);
          }
        }
        if (audioContext) {
          if (audioContext.state !== 'running' && syncReady) {
            source.disconnect(); delayNode.disconnect(); delayNode = audioContext.createDelay(2);
            source.connect(delayNode); delayNode.connect(gain); gain.gain.value = 0;
            syncReady = false; ramp = null; plannedDelay = null; naturalWindow = [];
          }
          const resume = audioContext.resume(); await Promise.all([resume, sinkPlayback, mediaOutput?.play()]);
        } else { await sinkPlayback; await fallback.play(); }
      }
      paused = false;
      if (!playbackStartedAt) playbackStartedAt = performance.now();
      if (useDirectBackgroundAudio()) {
        if (mediaOutput && (!syncEnabled || syncReady)) { mediaOutput.muted = false; mediaOutput.volume = 1; }
      } else if (gain && (!syncEnabled || syncReady)) {
        gain.gain.setTargetAtTime(1, audioContext.currentTime, 0.03);
      }
      if (!isPlaying()) throw new Error('autoplay');
      el('listen').textContent = '暂停本机收听';
      status(connected ? '正在收听' : '正在连接', syncEnabled && !syncReady ? '正在快速对齐统一延迟，约需 1 秒后开始出声。' : '主机播放什么，这里就听到什么。', connected);
      send({ type: 'visibility', hidden: document.hidden });
    } catch {
      el('listen').textContent = '开始收听 ↗';
      status('等待点击收听', '浏览器需要你点一次「开始收听」，允许播放声音。');
    }
  }
  function applyBuffer() {
    hinted = false;
    for (const receiver of pc?.getReceivers() || []) {
      if (receiver.track.kind !== 'audio') continue;
      try {
        if ('jitterBufferTarget' in receiver) { receiver.jitterBufferTarget = targetMs; hinted = true; }
      } catch { /* The browser may reject or ignore a target. */ }
    }
    const owner = serverCompensation ? '主机发送补偿' : '本机播放补偿';
    el('bufferSupport').textContent = hinted ? `原生抖动缓冲目标 ${targetMs}ms；统一同步另由${owner}执行。` : `使用浏览器默认抖动缓冲，统一同步由${owner}执行。`;
  }
  async function cleanup() {
    clearInterval(statsTimer); clearInterval(pingTimer); clearInterval(syncTimer);
    clearTimeout(volumeTimer); clearTimeout(feedbackTimer); volumeTimer = feedbackTimer = null;
    wsReady = false; volumeDragging = false; pendingVolumeLevel = null; feedbackActive = false;
    hostNowPlaying = { Playing: false, HasSession: false };
    remoteControl = { enabled: false, volume: { available: false, level: 0.5, muted: false, deviceName: null } };
    renderRemoteControl();
    const oldWs = ws, oldPc = pc, oldContext = audioContext, oldFallback = fallback, oldSink = silentSink, oldOutput = mediaOutput;
    ws = pc = audioContext = source = gain = fallback = stream = silentSink = delayNode = mediaOutput = mediaDestination = null;
    connected = false; previous = null; candidates = []; sourceClock = new SourceClock();
    naturalWindow = []; naturalMs = null; syncReady = false; ramp = null; plannedDelay = null; lastSyncReport = 0;
    playbackStartedAt = 0; fallbackTiming = false; senderDelayMs = 0; measuredAt = 0;
    if (oldWs) { oldWs.onclose = oldWs.onmessage = oldWs.onerror = null; oldWs.close(); }
    if (oldPc) { oldPc.onconnectionstatechange = oldPc.onicecandidate = oldPc.ontrack = null; oldPc.close(); }
    for (const audio of [oldFallback, oldSink, oldOutput]) if (audio) { audio.removeEventListener('pause', interrupted); audio.pause(); audio.srcObject = null; audio.remove(); }
    oldContext?.removeEventListener('statechange', interrupted);
    media.reset();
    if (oldContext) await oldContext.close().catch(() => {});
  }
  function reconnect(message) {
    if (ended || retryTimer) return;
    generation++;
    const delay = Math.min(1000 * 2 ** Math.min(retries++, 5), 15000);
    status('正在重连', message || '连接暂时中断，正在自动回到实时音频。');
    void cleanup();
    retryTimer = setTimeout(() => { retryTimer = null; void connect(); }, delay);
  }
  async function stats() {
    const current = pc;
    if (!current || current.connectionState !== 'connected') return;
    try {
      const report = await current.getStats();
      if (current !== pc) return;
      let inbound, pair;
      report.forEach(r => {
        if (r.type === 'inbound-rtp' && (r.kind === 'audio' || r.mediaType === 'audio')) inbound = r;
        if (r.type === 'candidate-pair' && r.state === 'succeeded' && r.nominated) pair = r;
      });
      if (!inbound) return;
      const received = inbound.packetsReceived || 0, lost = inbound.packetsLost || 0;
      let loss = null, buffer = null;
      if (previous && received >= previous.received) {
        const dr = received - previous.received, dl = Math.max(0, lost - previous.lost);
        if (dr + dl > 0) loss = dl * 100 / (dr + dl);
        const emitted = (inbound.jitterBufferEmittedCount || 0) - previous.emitted;
        if (emitted > 0) buffer = ((inbound.jitterBufferDelay || 0) - previous.delay) * 1000 / emitted;
      }
      previous = { received, lost, emitted: inbound.jitterBufferEmittedCount || 0, delay: inbound.jitterBufferDelay || 0 };
      if (buffer !== null) bufferEstimateMs = buffer;
      const data = {
        rttMs: pair?.currentRoundTripTime !== undefined ? pair.currentRoundTripTime * 1000 : null,
        jitterMs: inbound.jitter !== undefined ? inbound.jitter * 1000 : null,
        bufferMs: buffer, lossPercent: loss, packetsReceived: received, packetsLost: lost,
        concealedSamples: inbound.concealedSamples ?? null, totalSamplesReceived: inbound.totalSamplesReceived ?? null,
        audioEnergy: inbound.totalAudioEnergy ?? null, targetMs, bufferHintSupported: hinted,
        outputLatencyMs: audioContext?.outputLatency !== undefined ? audioContext.outputLatency * 1000 : null, browserBaseLatencyMs: audioContext?.baseLatency !== undefined ? audioContext.baseLatency * 1000 : null, clockRttMs: sourceClock.best?.rtt ?? null, clockUncertaintyMs: sourceClock.best ? sourceClock.best.rtt / 2 : null,
        visibility: document.visibilityState, backgroundMode, mediaPlaying: isPlaying(), naturalMs, extraDelayMs: serverCompensation ? senderDelayMs : addedDelaySeconds() * 1000, commonTargetMs, hardwareCorrectionMs: hardwareMs, sourceTimingAvailable: !fallbackTiming, measurementAgeMs: measuredAt ? performance.now() - measuredAt : null, compensationOwner: serverCompensation ? 'host' : 'browser',
        estimatedAlignmentErrorMs: naturalMs === null ? null : naturalMs + hardwareMs + addedDelaySeconds() * 1000 - commonTargetMs
      };
      el('rtt').textContent = data.rttMs === null ? '—' : `${Math.round(data.rttMs)} ms`;
      el('buffer').textContent = buffer === null ? '—' : `${Math.round(buffer)} ms`;
      el('loss').textContent = loss === null ? '—' : `${loss.toFixed(1)} %`;
      send({ type: 'metrics', data });
      // A stalled transport rejoins live instead of replaying a growing backlog.
      if (received === stats.lastPackets) stats.stalled = (stats.stalled || 0) + 1; else stats.stalled = 0;
      stats.lastPackets = received;
      if (stats.stalled >= 4) reconnect('音频传输中断，正在重新连接。请检查 Wi-Fi、防火墙或 AP 隔离。');
    } catch { /* Connection changes while collecting stats are expected. */ }
  }
  async function receive(message, currentGeneration) {
    if (currentGeneration !== generation) return;
    switch (message.type) {
      case 'welcome': {
        wsReady = true;
        saveToken(token);
        el('title').textContent = message.name; document.title = `${message.name} · 一起听`;
        void media.update(message.nowPlaying, token, message.name);
        applyHostState(message.nowPlaying, message.hostControl);
        targetMs = message.bufferMs; syncEnabled = message.syncEnabled !== false; commonTargetMs = message.syncTargetMs || 250; playoutTargetMs = commonTargetMs; hostReadyAt = 0; displayError = null; timelineStartMs = message.timelineStartMs ?? 40;
        serverCompensation = message.serverCompensation === true;
        localMonitorOnly = message.localMonitorOnly === true;
        if (localMonitorOnly) {
          el('listen').disabled = true; el('listen').textContent = '由桌面统一播放';
          el('syncStatus').textContent = '本机网页只查看状态，不参与最慢接收端计算。';
        }
        el('tech').textContent = `Opus · 48kHz · 立体声 · ${message.frameMs}ms 帧 · 局域网直连`;
        const peer = new RTCPeerConnection({ iceServers: [], bundlePolicy: 'max-bundle' }); pc = peer;
        const transceiver = peer.addTransceiver('audio', { direction: 'recvonly' });
        const opus = RTCRtpReceiver.getCapabilities?.('audio')?.codecs?.filter(c => c.mimeType.toLowerCase() === 'audio/opus');
        if (opus?.length && transceiver.setCodecPreferences) transceiver.setCodecPreferences(opus);
        peer.onicecandidate = e => { if (e.candidate && currentGeneration === generation) send({ type: 'candidate', candidate: e.candidate.toJSON() }); };
        peer.ontrack = e => {
          if (currentGeneration !== generation) return;
          stream = e.streams[0] || new MediaStream([e.track]); applyBuffer();
          if (!paused) void play();
        };
        peer.onconnectionstatechange = () => {
          if (currentGeneration !== generation) return;
          connected = peer.connectionState === 'connected';
          if (connected) {
            retries = 0;
            if (localMonitorOnly) status('主机网页预览', '主机声音由桌面软件同步播放，网页不重复出声，避免回授。', true);
            else status(isPlaying() && !paused ? '正在收听' : '已连接', paused ? '已暂停本机，广播仍在继续。' : isPlaying() ? '主机播放什么，这里就听到什么。' : '点「开始收听」即可播放声音。', true);
          } else if (['failed', 'disconnected'].includes(peer.connectionState)) reconnect();
        };
        const offer = await peer.createOffer();
        // Request stereo explicitly; opus/48000/2 alone does not enable stereo receive in Chromium.
        offer.sdp = offer.sdp.replace(/(a=fmtp:\d+[^\r\n]*useinbandfec=1[^\r\n]*)/g, '$1;stereo=1;sprop-stereo=1;maxaveragebitrate=128000');
        await peer.setLocalDescription(offer);
        send({ type: 'offer', sdp: offer.sdp });
        send({ type: 'visibility', hidden: document.hidden });
        statsTimer = setInterval(stats, 2000); syncTimer = setInterval(syncTick, 200);
        pingTimer = setInterval(() => {
          if (sourceClock.samples.length < 6 || performance.now() - lastPing >= 2000) { lastPing = performance.now(); send({ type: 'ping', clientMs: lastPing }); }
        }, 250);
        send({ type: 'ping', clientMs: performance.now() });
        break;
      }
      case 'answer':
        await pc.setRemoteDescription({ type: 'answer', sdp: message.sdp });
        for (const c of candidates) await pc.addIceCandidate(c); candidates = []; applyBuffer(); break;
      case 'candidate':
        if (pc?.remoteDescription) await pc.addIceCandidate(message.candidate); else candidates.push(message.candidate); break;
      case 'pong': {
        const now = performance.now(); sourceClock.observe(message.clientMs, message.serverReceiveMs, message.serverSendMs, now);
        const best = sourceClock.best;
        if (best) el('clock').textContent = `四时间戳校时往返 ${best.rtt.toFixed(1)}ms，时钟估计不确定度至少 ±${(best.rtt / 2).toFixed(1)}ms。此值不包含未报告的硬件延迟。`;
        break;
      }
      case 'timeline':
        if (message.compensation) { senderDelayMs = message.compensation.senderMs; if (message.compensation.ready && !hostReadyAt) hostReadyAt = performance.now() + 200; }
        if (message.sync) { commonTargetMs = message.sync.targetMs; playoutTargetMs = message.sync.playoutTargetMs ?? commonTargetMs; desiredTargetMs = message.sync.desiredMs; syncDirection = message.sync.direction; referenceDevice = message.sync.referenceDevice; el('groupTarget').textContent = `${Math.round(commonTargetMs)} ms`; }
        void media.update(message.nowPlaying, token);
        applyHostState(message.nowPlaying, message.hostControl);
        if (!localMonitorOnly) syncTick();
        if (message.audio?.error) status('主机音频异常', '主机音频编码已停止，请主机重新开启广播。');
        else if (message.audio?.capture?.includes('重试')) status('等待主机音频设备', '主机输出设备暂不可用，正在自动恢复。');
        break;
      case 'host-state':
        if (message.nowPlaying) void media.update(message.nowPlaying, token);
        applyHostState(message.nowPlaying, message.hostControl);
        break;
      case 'control-result':
        if (message.nowPlaying) void media.update(message.nowPlaying, token);
        applyHostState(message.nowPlaying, message.hostControl);
        if (message.message) showRemoteFeedback(message.message, !message.ok);
        break;
      case 'ended':
        ended = true; generation++; clearTimeout(retryTimer); retryTimer = null;
        await cleanup(); el('listen').textContent = '广播已结束'; el('listen').disabled = true;
        status('广播已结束', '主机已停止广播。下次使用同一个收藏链接，或点「重新连接」即可加入。'); break;
    }
  }
  async function connect() {
    if (ended) return;
    const currentGeneration = ++generation;
    await cleanup();
    if (!token.match(/^[a-f0-9]{64}$/i)) {
      status('需要加入链接', '请扫描主机上的二维码，或粘贴主机提供的完整加入链接。'); el('listen').disabled = true; return;
    }
    if (!window.RTCPeerConnection) {
      status('浏览器不支持连接', '请更新 Safari、Chrome、Edge 或 Firefox。此浏览器可能限制 HTTP 下的 WebRTC。'); el('listen').disabled = true; return;
    }
    status('正在连接', '请确保所有设备连接在同一个 Wi-Fi / 局域网中，才能正常收听。');
    el('listen').disabled = false; el('listen').textContent = '开始收听 ↗';
    const socket = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/signal`); ws = socket;
    let chain = Promise.resolve();
    socket.onopen = () => { if (currentGeneration === generation) send({ type: 'hello', token, name: clientName }); };
    socket.onmessage = e => { chain = chain.then(() => receive(JSON.parse(e.data), currentGeneration)).catch(error => { if (currentGeneration !== generation) return; console.warn('Broadcast negotiation:', error); reconnect('音频连接失败，正在重试。请确保所有设备连接同一 Wi-Fi，并检查防火墙或 AP 隔离。'); }); };
    socket.onclose = e => {
      if (currentGeneration !== generation) return;
      if (e.code === 1008) {
        clearSavedToken();
        ended = true; void cleanup(); status('加入链接已失效', '请重新扫描主机当前二维码。'); el('listen').disabled = true;
      } else reconnect('主机连接中断，正在重试。请确保与广播主机连接在同一个 Wi-Fi / 局域网中。');
    };
    socket.onerror = () => { /* onclose schedules bounded-backoff reconnection. */ };
  }
  function pauseListening() {
    paused = true;
    if (audioContext && gain) gain.gain.setTargetAtTime(0, audioContext.currentTime, 0.03);
    if (mediaOutput) { mediaOutput.muted = false; mediaOutput.pause(); }
    fallback?.pause();
    send({ type: 'sync-report', data: { playing: false } });
    el('listen').textContent = '继续收听 ↗'; status('本机已暂停', '点击继续后将收听当前实时音频。', connected);
  }
  window.DormCastPlayer = { play: () => void play(), pause: pauseListening, toggle: () => { if (isPlaying()) pauseListening(); else void play(); } };
  media.init(() => void play(), pauseListening);
  el('listen').addEventListener('click', async () => { if (isPlaying()) pauseListening(); else await play(); });
  el('hostPrev').addEventListener('click', () => sendHostControl({ action: 'previous' }));
  el('hostPlayPause').addEventListener('click', () => {
    const action = hostNowPlaying.Playing ? 'pause' : hostNowPlaying.HasSession ? 'play' : 'toggle';
    sendHostControl({ action });
  });
  el('hostNext').addEventListener('click', () => sendHostControl({ action: 'next' }));
  el('hostMute').addEventListener('click', () => {
    const nextMuted = !remoteControl.volume.muted;
    lastVolumeInputAt = performance.now();
    remoteControl.volume.muted = nextMuted;
    el('hostMute').classList.toggle('is-muted', nextMuted);
    el('hostVolIcon').hidden = nextMuted;
    el('hostMuteIcon').hidden = !nextMuted;
    el('hostVolumeValue').textContent = nextMuted ? '静音' : `${el('hostVolume').value}%`;
    sendHostControl({ action: 'mute', muted: nextMuted });
  });
  el('hostVolDown').addEventListener('click', () => {
    const nextPct = Math.max(0, Number(el('hostVolume').value) - 5);
    lastVolumeInputAt = performance.now();
    el('hostVolume').value = String(nextPct);
    el('hostVolumeValue').textContent = `${nextPct}%`;
    sendHostControl({ action: 'volume-step', delta: -0.05 });
  });
  el('hostVolUp').addEventListener('click', () => {
    const nextPct = Math.min(100, Number(el('hostVolume').value) + 5);
    lastVolumeInputAt = performance.now();
    el('hostVolume').value = String(nextPct);
    el('hostVolumeValue').textContent = `${nextPct}%`;
    el('hostMute').classList.remove('is-muted');
    el('hostVolIcon').hidden = false;
    el('hostMuteIcon').hidden = true;
    sendHostControl({ action: 'volume-step', delta: 0.05 });
  });
  el('hostVolume').addEventListener('pointerdown', () => { volumeDragging = true; });
  el('hostVolume').addEventListener('pointerup', () => { volumeDragging = false; });
  el('hostVolume').addEventListener('pointercancel', () => { volumeDragging = false; });
  el('hostVolume').addEventListener('input', e => {
    const pct = Math.max(0, Math.min(100, Math.round(Number(e.target.value) || 0)));
    lastVolumeInputAt = performance.now();
    el('hostVolumeValue').textContent = `${pct}%`;
    if (pct > 0) {
      remoteControl.volume.muted = false;
      el('hostMute').classList.remove('is-muted');
      el('hostVolIcon').hidden = false;
      el('hostMuteIcon').hidden = true;
    }
    pendingVolumeLevel = pct / 100;
    if (!volumeTimer) volumeTimer = setTimeout(flushVolume, 60);
  });
  el('hostVolume').addEventListener('change', e => {
    volumeDragging = false;
    const pct = Math.max(0, Math.min(100, Math.round(Number(e.target.value) || 0)));
    lastVolumeInputAt = performance.now();
    pendingVolumeLevel = pct / 100;
    flushVolume();
  });
  el('backgroundMode').addEventListener('change', async e => {
    backgroundMode = e.target.checked;
    try { localStorage.setItem('DormCast.backgroundMode', String(backgroundMode)); } catch {}
    const oldContext = audioContext, oldOutput = mediaOutput, oldSink = silentSink, oldFallback = fallback;
    audioContext = source = gain = delayNode = mediaOutput = mediaDestination = silentSink = fallback = null;
    for (const audio of [oldOutput, oldSink, oldFallback]) {
      if (audio) { audio.removeEventListener('pause', interrupted); audio.pause(); audio.srcObject = null; audio.remove(); }
    }
    oldContext?.removeEventListener('statechange', interrupted);
    if (oldContext) await oldContext.close().catch(() => {});
    syncReady = false; ramp = null; plannedDelay = null; naturalWindow = []; playbackStartedAt = 0;
    if (!paused && !localMonitorOnly) await play();
  });
  el('hardwareOffset').addEventListener('change', e => {
    hardwareMs = Math.max(0, Math.min(1000, Number(e.target.value) || 0)); e.target.value = hardwareMs;
    try { localStorage.setItem('DormCast.hardwareOffsetMs', String(hardwareMs)); } catch {}
    syncTick();
  });
  el('reconnect').addEventListener('click', () => { clearTimeout(retryTimer); retryTimer = null; ended = false; retries = 0; paused = false; void connect(); });
  window.addEventListener('online', () => { if (!connected && !ended) reconnect(); });
  window.addEventListener('hashchange', () => {
    const next = new URLSearchParams(location.hash.slice(1)).get('join') || '';
    if (next !== token) { token = next; ended = false; retries = 0; clearTimeout(retryTimer); retryTimer = null; void connect(); }
  });
  window.addEventListener('pagehide', () => { generation++; clearTimeout(retryTimer); void cleanup(); });
  window.addEventListener('pageshow', e => { if (e.persisted && !ended) void connect(); });
  document.addEventListener('visibilitychange', () => {
    send({ type: 'visibility', hidden: document.hidden });
    if (!document.hidden) {
      naturalWindow = []; sourceClock = new SourceClock(); lastPing = 0;
      send({ type: 'ping', clientMs: performance.now() });
      el('syncStatus').textContent = '已恢复前台，正在重新校时与测量。';
      if (!connected && !ended) reconnect(); else interrupted();
    } else if (serverCompensation) el('syncStatus').textContent = '后台由主机持续补偿，本机测量暂停时不显示已对齐。';
  });
  void connect();
})();
