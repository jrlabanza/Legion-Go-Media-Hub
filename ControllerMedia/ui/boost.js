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

  function route(n) {
    try { n.src.disconnect(); } catch (e) { }
    n.gain.gain.value = level;
    n.src.connect(level > 1 ? n.gain : ctx.destination);
  }

  function wire(el) {
    if (level <= 1 || wired.has(el) || skip.has(el) || !readable(el)) return;
    if (!ctx) {
      const AC = window.AudioContext || window.webkitAudioContext;
      if (!AC) return;
      ctx = new AC();
      limiter = ctx.createDynamicsCompressor();
      limiter.threshold.value = -3; limiter.knee.value = 3; limiter.ratio.value = 20;
      limiter.attack.value = 0.002; limiter.release.value = 0.25;
      limiter.connect(ctx.destination);
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

  function note(el) {
    if (!(el instanceof HTMLMediaElement)) return;
    if (seen.size > 40) for (const s of seen) if (!s.isConnected && s.paused) seen.delete(s);
    seen.add(el);
    const n = wired.get(el);
    if (n) route(n); else wire(el);
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
  const kick = () => { if (ctx && ctx.state !== 'running') ctx.resume().catch(() => { }); };
  addEventListener('pointerdown', kick, true);
  addEventListener('keydown', kick, true);
  if (level > 1) refresh();
})();
