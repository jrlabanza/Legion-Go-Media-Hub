// Volume boost for web pages. Controller Media injects this into every page and frame, after setting
// window.__cmBoostLevel (1 = normal, up to 2 = 200%). At level 1 it does nothing. Above 1, playing <video> and
// <audio> elements are routed through a Web Audio gain stage and a limiter.
(() => {
  'use strict';
  if (window.__cmBoost) return;
  const clamp = (v) => (typeof v === 'number' && v > 1) ? Math.min(2, v) : 1;
  let level = clamp(window.__cmBoostLevel);
  let ctx = null, limiter = null, patched = false;
  const wired = new WeakMap();   // element -> { src, gain }
  const skip = new WeakSet();    // elements that can't be routed (the page already uses Web Audio for them)
  const seen = new Set();        // media elements that have played

  // Sound from another site comes out silent through Web Audio unless that site allows it (CORS),
  // so those elements are left alone: they keep playing at normal volume.
  function readable(el) {
    const so = el.srcObject;
    if (so) return !(window.MediaStream && so instanceof MediaStream);   // leave calls and live captures alone
    const u = el.currentSrc || el.src;
    if (!u) return false;
    if (/^(blob|data):/i.test(u)) return true;                           // streaming sites (YouTube, Netflix, Twitch)
    try { if (new URL(u, location.href).origin === location.origin) return true; } catch (e) { }
    return el.crossOrigin != null;
  }

  // The amplification follows the same curve as VLC and Linux desktops: the level cubed (150% = 3.4x, 200% = 8x).
  // What no longer fits is turned down by a fast limiter rather than clipped, because clipping is what makes a
  // boost sound broken (measured: about 40% distortion at 200% on loud audio, against about 5% this way). Quiet
  // audio gets the full amplification; loud audio gets as much as fits. A soft clipper behind the limiter only
  // catches the brief peaks the limiter lets through.
  function loudnessChain(c) {
    const lim = c.createDynamicsCompressor();
    lim.threshold.value = -2; lim.knee.value = 2; lim.ratio.value = 20; lim.attack.value = 0.002; lim.release.value = 0.12;
    const RANGE = 2, KNEE = 0.9, N = 8193, curve = new Float32Array(N);   // the clipper's input range stands for -2..2
    for (let i = 0; i < N; i++) {
      const u = (i / (N - 1) * 2 - 1) * RANGE, a = Math.abs(u);
      curve[i] = Math.sign(u) * (a <= KNEE ? a : KNEE + (1 - KNEE) * Math.tanh((a - KNEE) / (1 - KNEE)));
    }
    const trim = c.createGain(); trim.gain.value = 1 / RANGE;
    const clip = c.createWaveShaper(); clip.curve = curve; clip.oversample = '4x';
    lim.connect(trim); trim.connect(clip); clip.connect(c.destination);
    return lim;   // connect sources here
  }

  function route(n) {
    try { n.src.disconnect(); } catch (e) { }
    n.gain.gain.value = level * level * level;
    n.src.connect(level > 1 ? n.gain : ctx.destination);
  }

  function wire(el) {
    if (level <= 1 || wired.has(el) || skip.has(el) || !readable(el)) return;
    if (!ctx) {
      const AC = window.AudioContext || window.webkitAudioContext;
      if (!AC) return;
      ctx = new AC();
      limiter = loudnessChain(ctx);
      ctx.addEventListener('statechange', refresh);
    }
    // An audio context only starts after the user has interacted with the page. Until it runs, don't touch
    // the element, or it would go silent.
    if (ctx.state !== 'running') { ctx.resume().catch(() => { }); return; }
    try {
      const n = { src: ctx.createMediaElementSource(el), gain: ctx.createGain() };
      n.gain.connect(limiter);
      wired.set(el, n);
      route(n);
    } catch (e) { skip.add(el); }
  }

  // Tell the app what the boost is doing here, so its menu can say so: 'on' (a playing element is boosted),
  // 'waiting' (the audio engine hasn't been allowed to start yet), 'blocked' (this player can't be boosted),
  // or 'idle' (nothing is playing).
  let lastState = '';
  function report() {
    let state = level > 1 ? 'idle' : 'off';
    if (level > 1) for (const el of seen) {
      if (el.paused || el.ended) continue;
      if (wired.has(el)) { state = (ctx && ctx.state === 'running') ? 'on' : 'waiting'; if (state === 'on') break; }
      else if (skip.has(el) || !readable(el)) { if (state === 'idle') state = 'blocked'; }
      else if (state !== 'on') state = 'waiting';
    }
    if (state === lastState) return;
    lastState = state;
    try { window.chrome.webview.postMessage({ cmBoost: 'status', state: state }); } catch (e) { }
  }

  function note(el) {
    if (!(el instanceof HTMLMediaElement)) return;
    if (seen.size > 40) for (const s of seen) if (!s.isConnected && s.paused) seen.delete(s);
    seen.add(el);
    const n = wired.get(el);
    if (n) route(n); else wire(el);
    report();
  }

  function refresh() {
    if (level > 1 && !patched) {
      // Also catch players that are not part of the page (new Audio()).
      patched = true;
      const play = HTMLMediaElement.prototype.play;
      HTMLMediaElement.prototype.play = function () { try { note(this); } catch (e) { } return play.apply(this, arguments); };
    }
    document.querySelectorAll('video, audio').forEach((el) => { if (!el.paused) seen.add(el); });
    for (const el of seen) {
      const n = wired.get(el);
      if (n) route(n); else if (!el.paused) wire(el);
    }
    report();
  }

  function setLevel(v) {
    level = clamp(v);
    refresh();
    for (let i = 0; i < window.frames.length; i++) {
      try { window.frames[i].postMessage({ __cmBoost: level }, '*'); } catch (e) { }
    }
  }

  Object.defineProperty(window, '__cmBoost', { value: setLevel });   // the app calls this on the main page
  addEventListener('message', (e) => {                               // frames get the level from their parent page
    if (e.source === window.parent && window.parent !== window && e.data && typeof e.data.__cmBoost === 'number') setLevel(e.data.__cmBoost);
  });
  document.addEventListener('playing', (e) => note(e.target), true);
  document.addEventListener('pause', () => setTimeout(report, 0), true);
  document.addEventListener('ended', () => setTimeout(report, 0), true);
  const kick = () => { if (ctx && ctx.state !== 'running') ctx.resume().catch(() => { }); };
  addEventListener('pointerdown', kick, true);
  addEventListener('keydown', kick, true);
  if (level > 1) refresh();
})();
