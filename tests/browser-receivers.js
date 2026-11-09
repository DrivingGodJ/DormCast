// Run inside the LAN client page with EgoLite's page.evaluate. Audio stays muted to avoid loopback feedback.
window.dormcastChecks = {
  receivers: [],
  create(token, name) {
    const receiver = { name, pc: null, ws: null, audio: null, errors: [], ended: false, last: null };
    const ws = new WebSocket(`ws://${location.host}/signal`); receiver.ws = ws;
    const send = value => { if (ws.readyState === 1) ws.send(JSON.stringify(value)); };
    let chain = Promise.resolve();
    ws.onopen = () => send({ type: 'hello', token, name });
    ws.onmessage = event => {
      chain = chain.then(async () => {
        const m = JSON.parse(event.data);
        if (m.type === 'welcome') {
          const pc = new RTCPeerConnection({ iceServers: [], bundlePolicy: 'max-bundle' }); receiver.pc = pc;
          pc.addTransceiver('audio', { direction: 'recvonly' });
          const pending = []; let offered = false;
          pc.onicecandidate = e => { if (e.candidate) { const value = { type: 'candidate', candidate: e.candidate.toJSON() }; if (offered) send(value); else pending.push(value); } };
          pc.ontrack = async e => {
            const sink = new Audio(); sink.muted = true; sink.volume = 0;
            sink.srcObject = e.streams[0] || new MediaStream([e.track]); receiver.audio = sink;
            try { await sink.play(); } catch (error) { receiver.errors.push(String(error)); }
          };
          const offer = await pc.createOffer();
          offer.sdp = offer.sdp.replace(/(a=fmtp:\d+[^\r\n]*useinbandfec=1[^\r\n]*)/g, '$1;stereo=1;sprop-stereo=1');
          await pc.setLocalDescription(offer); send({ type: 'offer', sdp: offer.sdp }); offered = true;
          for (const c of pending) send(c);
          receiver.ping = setInterval(() => send({ type: 'ping', clientMs: performance.now() }), 2000);
        } else if (m.type === 'answer') await receiver.pc.setRemoteDescription({ type: 'answer', sdp: m.sdp });
        else if (m.type === 'ended') { receiver.ended = true; receiver.close(); }
      }).catch(error => receiver.errors.push(String(error)));
    };
    receiver.close = () => {
      clearInterval(receiver.ping); receiver.pc?.close(); ws.close();
      if (receiver.audio) { receiver.audio.pause(); receiver.audio.srcObject = null; }
    };
    receiver.stats = async () => {
      const report = receiver.pc ? [...(await receiver.pc.getStats()).values()] : [];
      const inbound = report.find(s => s.type === 'inbound-rtp' && s.kind === 'audio');
      const pair = report.find(s => s.type === 'candidate-pair' && s.state === 'succeeded' && s.nominated);
      const codec = report.find(s => s.id === inbound?.codecId);
      const sample = { name, state: receiver.pc?.connectionState, ws: ws.readyState, errors: [...receiver.errors], codec,
        packetsReceived: inbound?.packetsReceived ?? 0, packetsLost: inbound?.packetsLost ?? 0,
        totalSamplesReceived: inbound?.totalSamplesReceived ?? 0, audioEnergy: inbound?.totalAudioEnergy ?? 0,
        jitterMs: inbound?.jitter !== undefined ? inbound.jitter * 1000 : null,
        cumulativeBufferMs: inbound?.jitterBufferEmittedCount ? inbound.jitterBufferDelay * 1000 / inbound.jitterBufferEmittedCount : null,
        rttMs: pair?.currentRoundTripTime !== undefined ? pair.currentRoundTripTime * 1000 : null };
      receiver.last = sample; return sample;
    };
    this.receivers.push(receiver); return receiver.name;
  },
  async stats() { return Promise.all(this.receivers.map(r => r.stats())); },
  closeAll() { this.receivers.forEach(r => r.close()); this.receivers = []; }
};
