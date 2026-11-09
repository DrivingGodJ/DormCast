'use strict';
(() => {
  const el = id => document.getElementById(id);
  let token = new URLSearchParams(location.hash.slice(1)).get('join') || '';
  const clientName = /iPhone|iPad/.test(navigator.userAgent) ? 'iPhone / iPad' : /Android/.test(navigator.userAgent) ? 'Android 浏览器' : /Macintosh/.test(navigator.userAgent) ? 'Mac 浏览器' : '电脑浏览器';
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
  let backgroundMode = /Android|iPhone|iPad|iPod/.test(navigator.userAgent) || (/Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1);
  try { const saved = localStorage.getItem('DormCast.backgroundMode'); if (saved !== null) backgroundMode = saved === 'true'; } catch {}
  el('backgroundMode').checked = backgroundMode;
  const media = window.DormCastMedia;
  let hardwareMs = 0;
  try { hardwareMs = Math.max(0, Math.min(1000, Number(localStorage.getItem('DormCast.hardwareOffsetMs')) || 0)); } catch {}
  el('hardwareOffset').value = hardwareMs;
  function addedDelaySeconds() {
    if (!ramp || !audioContext) return 0;
    const fraction = Math.max(0, Math.min(1, (audioContext.currentTime - ramp.at) / ramp.duration));
    return ramp.from + (ramp.to - ramp.from) * fraction;
  }
  function syncTick() {
    if (!syncEnabled) { el('syncStatus').textContent = '主机未启用统一同步。'; return; }
    el('groupTarget').textContent = `${Math.round(commonTargetMs)} ms`;
    if (!audioContext || audioContext.state !== 'running' || !sourceClock.ready) return;
    const receiver = pc?.getReceivers().find(r => r.track.kind === 'audio');
    const point = receiver?.getSynchronizationSources?.()[0];
    const outputMs = ((audioContext.baseLatency || 0) + (audioContext.outputLatency || 0)) * 1000;
    let measurement = sourceLatency(point, sourceClock, performance.now(), performance.timeOrigin, outputMs, timelineStartMs);
    fallbackTiming = measurement === null;
    if (fallbackTiming) {
      if (serverCompensation && document.hidden) { el('syncStatus').textContent = '后台由主机持续补偿，本机测量等待恢复前台。'; return; }
      if (performance.now() - playbackStartedAt < 6000) { el('syncStatus').textContent = '正在等待源播放时间戳。'; return; }
      // Compatibility playback must remain audible even when a browser omits RTP playout timestamps.
      // Its buffer estimate never participates in choosing the slowest measured device.
      measurement = bufferEstimateMs + sourceClock.best.rtt / 2 + outputMs;
    }
    naturalWindow.push(measurement); if (naturalWindow.length > 5) naturalWindow.shift();
    if (!fallbackTiming) measuredAt = performance.now();
    const sorted = [...naturalWindow].sort((a, b) => a - b); naturalMs = sorted[Math.floor(sorted.length / 2)];
    const corrected = naturalMs + hardwareMs;
    if (performance.now() - lastSyncReport > 900) {
      send({ type: 'sync-report', data: { naturalMs: corrected, uncertaintyMs: sourceClock.best.rtt / 2, playing: connected && isPlaying() && !fallbackTiming } });
      lastSyncReport = performance.now();
    }
    if (corrected - (serverCompensation ? senderDelayMs : 0) > 1940) { gain.gain.setTargetAtTime(0, audioContext.currentTime, 0.03); el('syncStatus').textContent = '此设备超过 2 秒同步上限，请重连或检查蓝牙/网络。'; return; }
    if (naturalWindow.length < 3 || !delayNode) return;
    if (serverCompensation && !fallbackTiming && (!hostReadyAt || performance.now() < hostReadyAt)) return;
    const current = addedDelaySeconds(), plan = serverCompensation ? { seconds: 0, duration: 0.35 } : delayPlan(commonTargetMs, naturalMs, hardwareMs, current);
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
    const instantaneousError = corrected + addedDelaySeconds() * 1000 - playoutTargetMs;
    displayError = displayError === null ? instantaneousError : displayError + (instantaneousError - displayError) * 0.05;
    const error = displayError;
    el('syncStatus').textContent = document.hidden && serverCompensation ? '后台由主机持续补偿；实际播放测量需恢复前台确认。' : fallbackTiming ? '兼容播放：浏览器缺少源时间戳，缓冲估计无法保证同步。' : `${Math.abs(error) > 15 ? '正在平滑对齐' : '已按源时间线补偿'} · 平滑估计偏差 ${error > 0 ? '+' : ''}${Math.round(error)}ms`;
    const direction = syncDirection === 'decreasing' ? `正在下调至 ${Math.round(desiredTargetMs)}ms。` : syncDirection === 'settling' ? `观察稳定后下调至 ${Math.round(desiredTargetMs)}ms。` : '';
    el('syncDetail').textContent = serverCompensation ? `${direction}主机补偿 ${Math.round(senderDelayMs)}ms · 参考：${referenceDevice}。${backgroundMode ? '后台媒体输出的额外缓冲可在详情中校准。' : ''}` : `${direction}基础 ${Math.round(naturalMs)}ms + 硬件校准 ${hardwareMs}ms + 自动补偿 ${Math.round(addedDelaySeconds() * 1000)}ms。参考：${referenceDevice}。`;
  }
  function status(text, message, live = false) {
    el('connection').textContent = text;
    if (message !== undefined) el('message').textContent = message;
    el('dot').classList.toggle('live', live);
    document.body.classList.toggle('playing', live && !paused && isPlaying());
    media.state(isPlaying() && live ? 'playing' : connected ? 'paused' : 'none');
  }
  function send(value) { if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(value)); }
  function isPlaying() { return !paused && (audioContext ? audioContext.state === 'running' && silentSink && !silentSink.paused && (!mediaOutput || !mediaOutput.paused) : fallback && !fallback.paused); }
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
      paused = false;
      if (!playbackStartedAt) playbackStartedAt = performance.now();
      if (gain && (!syncEnabled || syncReady)) gain.gain.setTargetAtTime(1, audioContext.currentTime, 0.03);
      if (!isPlaying()) throw new Error('autoplay');
      el('listen').textContent = '暂停本机收听';
      status(connected ? '正在收听' : '正在连接', syncEnabled && !syncReady ? '正在测量统一延迟，约需 1–3 秒后开始出声。' : '主机播放什么，这里就听到什么。', connected);
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
        el('title').textContent = message.name; document.title = `${message.name} · 一起听`;
        void media.update(message.nowPlaying, token, message.name);
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
        statsTimer = setInterval(stats, 2000); syncTimer = setInterval(syncTick, 250);
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
        if (message.compensation) { senderDelayMs = message.compensation.senderMs; if (message.compensation.ready && !hostReadyAt) hostReadyAt = performance.now() + 500; }
        if (message.sync) { commonTargetMs = message.sync.targetMs; playoutTargetMs = message.sync.playoutTargetMs ?? commonTargetMs; desiredTargetMs = message.sync.desiredMs; syncDirection = message.sync.direction; referenceDevice = message.sync.referenceDevice; el('groupTarget').textContent = `${Math.round(commonTargetMs)} ms`; }
        void media.update(message.nowPlaying, token);
        if (!localMonitorOnly) syncTick();
        if (message.audio?.error) status('主机音频异常', '主机音频编码已停止，请主机重新开启广播。');
        else if (message.audio?.capture?.includes('重试')) status('等待主机音频设备', '主机输出设备暂不可用，正在自动恢复。');
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
    status('正在连接', '保持和广播主机连接同一个局域网。');
    el('listen').disabled = false; el('listen').textContent = '开始收听 ↗';
    const socket = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/signal`); ws = socket;
    let chain = Promise.resolve();
    socket.onopen = () => { if (currentGeneration === generation) send({ type: 'hello', token, name: clientName }); };
    socket.onmessage = e => { chain = chain.then(() => receive(JSON.parse(e.data), currentGeneration)).catch(error => { if (currentGeneration !== generation) return; console.warn('Broadcast negotiation:', error); reconnect('音频连接失败，正在重试。请检查浏览器兼容性、防火墙和 AP 隔离。'); }); };
    socket.onclose = e => {
      if (currentGeneration !== generation) return;
      if (e.code === 1008) {
        ended = true; void cleanup(); status('加入链接已失效', '请重新扫描主机当前二维码。'); el('listen').disabled = true;
      } else reconnect('主机连接中断，正在用同一个地址等待广播恢复。');
    };
    socket.onerror = () => { /* onclose schedules bounded-backoff reconnection. */ };
  }
  function pauseListening() {
    paused = true;
    if (audioContext) gain.gain.setTargetAtTime(0, audioContext.currentTime, 0.03);
    mediaOutput?.pause(); fallback?.pause();
    send({ type: 'sync-report', data: { playing: false } });
    el('listen').textContent = '继续收听 ↗'; status('本机已暂停', '点击继续后将收听当前实时音频。', connected);
  }
  media.init(() => void play(), pauseListening);
  el('listen').addEventListener('click', async () => { if (isPlaying()) pauseListening(); else await play(); });
  el('backgroundMode').addEventListener('change', async e => {
    backgroundMode = e.target.checked;
    try { localStorage.setItem('DormCast.backgroundMode', String(backgroundMode)); } catch {}
    const oldContext = audioContext, oldOutput = mediaOutput;
    audioContext = source = gain = delayNode = mediaOutput = mediaDestination = null;
    if (oldOutput) { oldOutput.removeEventListener('pause', interrupted); oldOutput.pause(); oldOutput.srcObject = null; oldOutput.remove(); }
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
