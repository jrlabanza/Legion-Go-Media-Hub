// Controller Media: touch swiping for YouTube's TV interface (youtube.com/tv).
// That interface is built for a TV remote and ignores touch scrolling, so swipes are turned into arrow-key moves:
// the page asks the app to press the keys (only on youtube.com/tv; the app accepts nothing else from web pages).
(() => {
  if (!/(^|\.)youtube\.com$/.test(location.hostname) || !location.pathname.startsWith('/tv')) return;
  if (window.__cmTvTouch) return;
  window.__cmTvTouch = true;

  let sx = 0, sy = 0, tracking = false, swiped = false;
  const setNoPan = () => { if (document.documentElement) document.documentElement.style.touchAction = 'none'; };
  setNoPan(); document.addEventListener('DOMContentLoaded', setNoPan);

  addEventListener('pointerdown', (e) => {
    if (e.pointerType !== 'touch') return;
    tracking = true; swiped = false; sx = e.clientX; sy = e.clientY;
  }, true);

  addEventListener('pointerup', (e) => {
    if (!tracking || e.pointerType !== 'touch') return;
    tracking = false;
    const dx = e.clientX - sx, dy = e.clientY - sy, adx = Math.abs(dx), ady = Math.abs(dy);
    if (Math.max(adx, ady) < 40) return;                  // a tap: let YouTube handle it as a click
    const horizontal = adx > ady, dist = horizontal ? adx : ady;
    // Finger moving up = content moves up = go down a row; finger moving left = go right along the row.
    const dir = horizontal ? (dx < 0 ? 'right' : 'left') : (dy < 0 ? 'down' : 'up');
    const steps = Math.max(1, Math.min(6, Math.round(dist / 140)));
    swiped = true;
    try { window.chrome.webview.postMessage({ cmTv: 'swipe', dir: dir, steps: steps }); } catch (err) { }
    e.preventDefault(); e.stopPropagation();
  }, true);

  addEventListener('pointercancel', () => { tracking = false; }, true);

  // A swipe must not also count as a click on whatever was under the finger.
  addEventListener('click', (e) => { if (swiped) { swiped = false; e.preventDefault(); e.stopPropagation(); } }, true);
})();
