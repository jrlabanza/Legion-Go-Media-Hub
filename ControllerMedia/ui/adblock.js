// Controller Media ad blocker: injected into every web page while the ad blocker is on.
// Network blocking of ad/tracker domains happens in the app itself; this script handles what that can't:
// hiding leftover ad boxes, and YouTube ads (which come from YouTube's own servers).
(() => {
  if (window.__cmAdblock) return;
  window.__cmAdblock = true;
  const isYT = /(^|\.)youtube\.com$/.test(location.hostname);

  // ---------- Hide leftover ad containers (conservative selectors, so pages don't break) ----------
  const css = isYT
    ? 'ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer, ytd-banner-promo-renderer, ytd-statement-banner-renderer,' +
      'ytd-promoted-sparkles-web-renderer, ytd-promoted-video-renderer, ytd-display-ad-renderer, ytd-compact-promoted-video-renderer,' +
      'ytd-action-companion-ad-renderer, ytd-player-legacy-desktop-watch-ads-renderer, ytd-merch-shelf-renderer, #masthead-ad, #player-ads,' +
      '.ytp-ad-overlay-container, .ytp-ad-image-overlay, ytd-rich-item-renderer:has(ytd-ad-slot-renderer), ytm-promoted-sparkles-web-renderer' +
      '{ display: none !important; }'
    : 'ins.adsbygoogle, .adsbygoogle, iframe[src*="doubleclick.net"], iframe[src*="googlesyndication.com"], iframe[id^="google_ads_iframe"],' +
      'div[id^="google_ads_iframe"], div[id^="div-gpt-ad"], [data-google-query-id], amp-ad, amp-embed[type="taboola"],' +
      '#taboola-below-article-thumbnails, .trc_rbox_container, .OUTBRAIN, .ob-widget' +
      '{ display: none !important; }';
  const addCss = () => {
    const s = document.createElement('style');
    s.id = '__cm_adblock_css';
    s.textContent = css;
    (document.head || document.documentElement).appendChild(s);
  };
  if (document.documentElement) addCss(); else document.addEventListener('DOMContentLoaded', addCss);

  if (!isYT) return;

  // ---------- YouTube: remove ad data from the player's video information ----------
  const AD_KEYS = ['adPlacements', 'playerAds', 'adSlots', 'adBreakHeartbeatParams', 'adBreakParams'];
  const strip = (o) => {
    try {
      if (o && typeof o === 'object') {
        for (const k of AD_KEYS) if (k in o) delete o[k];
        if (o.playerResponse) strip(o.playerResponse);
        if (Array.isArray(o)) for (const x of o) if (x && typeof x === 'object' && x.playerResponse) strip(x.playerResponse);
      }
    } catch (e) { }
    return o;
  };
  const origParse = JSON.parse;
  JSON.parse = function () { return strip(origParse.apply(this, arguments)); };
  const origJson = Response.prototype.json;
  Response.prototype.json = function () { return origJson.apply(this, arguments).then(strip); };
  for (const name of ['ytInitialPlayerResponse', 'ytInitialData']) {
    let value;
    try { Object.defineProperty(window, name, { configurable: true, get: () => value, set: (x) => { value = strip(x); } }); } catch (e) { }
  }

  // ---------- YouTube fallback: skip any ad that still plays ----------
  let mutedByUs = false;
  setInterval(() => {
    const player = document.querySelector('.html5-video-player.ad-showing, .ad-showing, .ad-interrupting');
    const v = (player && player.querySelector('video')) || document.querySelector('video');
    if (player && v) {
      if (!v.muted) { v.muted = true; mutedByUs = true; }
      if (isFinite(v.duration) && v.duration > 0 && v.currentTime < v.duration - 0.1) v.currentTime = v.duration;
      const skip = document.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-ad-skip-button-container button');
      if (skip) skip.click();
    } else if (mutedByUs && v) {
      v.muted = false; mutedByUs = false;   // give the sound back once the ad is gone
    }
  }, 300);
})();
