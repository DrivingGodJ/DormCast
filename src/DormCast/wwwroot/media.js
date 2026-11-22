'use strict';
(() => {
  let info = {}, artworkUrl = null, revision = null, request = 0, station = '宿舍之音', playback = 'none';
  const el = id => document.getElementById(id);
  const theme = () => window.DormCastTheme;
  function metadata() {
    if (!('mediaSession' in navigator) || !window.MediaMetadata) return;
    try {
      navigator.mediaSession.metadata = new MediaMetadata({ title: info.Title || station, artist: info.Artist || '局域网实时广播', album: info.Album || '', artwork: artworkUrl ? [{ src: artworkUrl }] : [] });
      navigator.mediaSession.playbackState = playback;
    } catch { /* Media controls are optional in embedded browsers. */ }
  }
  function clearCover(resetTheme = true) {
    el('artwork').hidden = true; el('artwork').removeAttribute('src');
    el('record').classList.remove('has-cover');
    if (artworkUrl) URL.revokeObjectURL(artworkUrl);
    artworkUrl = null;
    if (resetTheme) theme()?.reset();
  }
  async function update(next, token, name) {
    info = next || {}; station = name || station;
    const text = (id, value) => { if (el(id).textContent !== value) el(id).textContent = value; };
    text('song', info.Title || '正在广播系统声音');
    text('artist', info.Artist || (info.Title ? 'Windows 媒体信息' : '播放器未提供歌曲信息'));
    text('album', info.Album || '');
    metadata();
    const wanted = /^[a-f0-9]{16}$/.test(info.CoverRevision || '') ? info.CoverRevision : null;
    if (wanted === revision) return;
    revision = wanted; const current = ++request;
    clearCover(!wanted); metadata();
    if (!wanted) return;
    try {
      const response = await fetch(`/cover/${wanted}`, { headers: { Authorization: `Bearer ${token}` }, cache: 'no-store' });
      if (!response.ok) throw new Error('cover unavailable');
      const blob = await response.blob();
      if (current !== request) return;
      if (blob.size > 2 * 1024 * 1024 || !/^image\/(jpeg|png|webp|gif)$/.test(blob.type)) throw new Error('unsupported cover');
      const img = el('artwork');
      artworkUrl = URL.createObjectURL(blob); img.src = artworkUrl;
      img.hidden = false; el('record').classList.add('has-cover'); metadata();
    } catch {
      if (current === request) { revision = null; clearCover(true); metadata(); }
    }
  }
  function init(play, pause) {
    try { if (navigator.audioSession) navigator.audioSession.type = 'playback'; } catch {}
    if (!('mediaSession' in navigator)) return;
    for (const [action, handler] of [['play', play], ['pause', pause]]) {
      try { navigator.mediaSession.setActionHandler(action, handler); } catch {}
    }
    // Receiver controls never seek, skip tracks, or control the host player.
    for (const action of ['seekbackward', 'seekforward', 'seekto', 'previoustrack', 'nexttrack']) {
      try { navigator.mediaSession.setActionHandler(action, null); } catch {}
    }
  }
  function state(value) { playback = value; if ('mediaSession' in navigator) { try { navigator.mediaSession.playbackState = value; } catch {} } }
  function reset() { request++; revision = null; info = {}; clearCover(true); state('none'); metadata(); }
  const artwork = el('artwork');
  artwork.addEventListener('load', () => {
    if (!artworkUrl || artwork.hidden || !artwork.naturalWidth) return;
    theme()?.applyFromImage(artwork);
  });
  artwork.addEventListener('error', () => {
    if (!artworkUrl) return;
    clearCover(true); metadata();
  });
  window.DormCastMedia = { update, init, state, reset };
})();
