'use strict';
// Controller Media UI. All input arrives from the host app as actions (it reads the controllers with SDL):
// up/down/left/right, ok, back, x, y, lb, rb, lt, rt, menu, view, pageup, pagedown.

const $ = (s, el) => (el || document).querySelector(s);
const $$ = (s, el) => Array.from((el || document).querySelectorAll(s));
const send = (o) => window.chrome.webview.postMessage(o);

// ---------------------------------------------------------------- icons
const ICONS = {
  video: '<path d="M4 6h11a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2zm13 4 5-3v10l-5-3z"/>',
  music: '<path d="M9 18a3 3 0 1 1-2-2.83V5l12-2v11a3 3 0 1 1-2-2.83V7.4L9 8.73z"/>',
  photo: '<path d="M4 5h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2zm1 12h14l-4.5-6-3.5 4.5-2.5-3z"/><circle cx="8" cy="9" r="1.6"/>',
  globe: '<path d="M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm6.9 9h-3a15 15 0 0 0-1.3-5.6A8 8 0 0 1 18.9 11zM12 4c.9 1.2 1.8 3.6 2 7h-4c.2-3.4 1.1-5.8 2-7zM4.1 13h3a15 15 0 0 0 1.3 5.6A8 8 0 0 1 4.1 13zm3-2h-3a8 8 0 0 1 4.3-5.6A15 15 0 0 0 7.1 11zM12 20c-.9-1.2-1.8-3.6-2-7h4c-.2 3.4-1.1 5.8-2 7zm3.6-1.4a15 15 0 0 0 1.3-5.6h3a8 8 0 0 1-4.3 5.6z"/>',
  play: '<path d="M7 4.5v15a1 1 0 0 0 1.5.86l12.4-7.5a1 1 0 0 0 0-1.72L8.5 3.64A1 1 0 0 0 7 4.5z"/>',
  power: '<path d="M11 2h2v10h-2zm7.1 3.5 1.4 1.4A9 9 0 1 1 4.5 6.9l1.4-1.4A7 7 0 1 0 18.1 5.5z"/>',
  folder: '<path d="M3 5a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  drive: '<path d="M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zm13 11a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3z"/>',
  search: '<path d="M10 3a7 7 0 0 1 5.6 11.2l5.1 5.1-1.4 1.4-5.1-5.1A7 7 0 1 1 10 3zm0 2a5 5 0 1 0 0 10 5 5 0 0 0 0-10z"/>',
  back: '<path d="M10 5 3 12l7 7v-4.5c5 0 8.5 1.6 11 5-1-5-4-10-11-11z"/>',
  left: '<path d="m15 4 1.4 1.4L9.8 12l6.6 6.6L15 20l-8-8z"/>',
  right: '<path d="m9 4 8 8-8 8-1.4-1.4 6.6-6.6-6.6-6.6z"/>',
  reload: '<path d="M12 4a8 8 0 0 1 7.4 5H22l-3.5 4L15 9h2.2A6 6 0 1 0 18 14h2.1A8 8 0 1 1 12 4z"/>',
  home: '<path d="m12 3 9 8h-3v9h-5v-6h-2v6H6v-9H3z"/>',
  up: '<path d="m12 5 8 8-1.4 1.4L12 7.8l-6.6 6.6L4 13z"/>',
  shield: '<path d="M12 2 4 5v6c0 5 3.4 9.4 8 11 4.6-1.6 8-6 8-11V5zm-1.2 13.6L7.4 12.2l1.4-1.4 2 2 4.4-4.4 1.4 1.4z"/>',
  pause: '<path d="M6 4h4v16H6zm8 0h4v16h-4z"/>',
  prev: '<path d="M6 5h2v14H6zm3 7 10-7v14z"/>',
  next: '<path d="M16 5h2v14h-2zM5 5l10 7-10 7z"/>',
  rew: '<path d="M11 6v12l-8.5-6zm9 0v12l-8.5-6z"/>',
  fwd: '<path d="M13 6v12l8.5-6zM4 6v12l8.5-6z"/>',
  close: '<path d="m6.4 5 5.6 5.6L17.6 5 19 6.4 13.4 12l5.6 5.6-1.4 1.4-5.6-5.6L6.4 19 5 17.6l5.6-5.6L5 6.4z"/>',
  star: '<path d="m12 2.5 2.9 6 6.6.9-4.8 4.6 1.2 6.5L12 17.4l-5.9 3.1 1.2-6.5L2.5 9.4l6.6-.9z"/>',
  bookmarks: '<path d="M6 3h10a2 2 0 0 1 2 2v16l-7-4-7 4V5a2 2 0 0 1 2-2z"/>',
  dots: '<circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/>',
  volume: '<path d="M3 9h4l5-4v14l-5-4H3zm12.5-.5a5 5 0 0 1 0 7l-1.4-1.4a3 3 0 0 0 0-4.2zm2.8-2.8a9 9 0 0 1 0 12.6l-1.4-1.4a7 7 0 0 0 0-9.8z"/>',
  volup: '<path d="M2 9h4l5-4v14l-5-4H2zm15-1h2v3h3v2h-3v3h-2v-3h-3v-2h3z"/>',
  voldown: '<path d="M2 9h4l5-4v14l-5-4H2zm12 2h8v2h-8z"/>',
  subs: '<path d="M4 15h10v2H4zm12 0h4v2h-4zM4 11h4v2H4zm6 0h10v2H10zM4 7h16v2H4z"/>',
  gear: '<path d="M10.3 2h3.4l.5 2.6 1.9.8 2.2-1.5 2.4 2.4-1.5 2.2.8 1.9 2.6.5v3.4l-2.6.5-.8 1.9 1.5 2.2-2.4 2.4-2.2-1.5-1.9.8-.5 2.6h-3.4l-.5-2.6-1.9-.8-2.2 1.5-2.4-2.4 1.5-2.2-.8-1.9L1.4 13.7v-3.4l2.6-.5.8-1.9-1.5-2.2 2.4-2.4 2.2 1.5 1.9-.8zM12 8.5a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7z" fill-rule="evenodd"/>',
  pad: '<path d="M7 7h10a5 5 0 0 1 4.9 4l1 5a2.4 2.4 0 0 1-4.2 2L16.6 16H7.4l-2.1 2a2.4 2.4 0 0 1-4.2-2l1-5A5 5 0 0 1 7 7zm.2 2.6v1.6H5.6v1.6h1.6v1.6h1.6v-1.6h1.6v-1.6H8.8V9.6zm8.3.2a1.1 1.1 0 1 0 0 2.2 1.1 1.1 0 0 0 0-2.2zm2.2 2.4a1.1 1.1 0 1 0 0 2.2 1.1 1.1 0 0 0 0-2.2z" fill-rule="evenodd"/>'
};
function icon(name) { return '<svg viewBox="0 0 24 24" fill="currentColor">' + (ICONS[name] || '') + '</svg>'; }
function paintIcons(root) { $$('[data-icon]', root).forEach(el => { el.innerHTML = icon(el.dataset.icon); }); }

// ---------------------------------------------------------------- state
const SECTIONS = { videos: 'Videos', music: 'Music', pictures: 'Pictures' };
const S = {
  view: 'home',          // home | library | webhome
  section: null,         // videos | music | pictures
  roots: [], root: null, host: null, path: '',
  entries: [],           // current listing (dirs + files)
  history: [],           // views to return to
  kb: null,              // keyboard state
  browserMenuOpen: false
};
let focused = null;
const memory = {};       // remembered focus index per screen

// ---------------------------------------------------------------- helpers
function fmtTime(s) { if (!isFinite(s)) return '0:00'; s = Math.max(0, Math.floor(s)); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = s % 60; return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(x).padStart(2, '0'); }
function fmtSize(b) { if (b > 1e9) return (b / 1e9).toFixed(1) + ' GB'; if (b > 1e6) return (b / 1e6).toFixed(0) + ' MB'; return Math.max(1, Math.round(b / 1e3)) + ' KB'; }
function fileUrl(host, path, name) {
  const segs = path.split('/').filter(Boolean).map(encodeURIComponent);
  segs.push(encodeURIComponent(name));
  return 'https://' + host + '/' + segs.join('/');
}
function stripExt(n) { return n.replace(/\.[^.]+$/, ''); }
function esc(s) { return String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

// ---------------------------------------------------------------- focus engine
function topLayer() {
  for (const id of ['bindprompt', 'gamemenu', 'keyboard', 'bookmarks', 'browsermenu', 'viewer', 'player']) if ($('#' + id).classList.contains('active')) return $('#' + id);
  return $('#' + S.view);
}
function focusables(layer) { return $$('.f', layer || topLayer()).filter(el => el.offsetParent !== null && !el.classList.contains('disabled')); }
function setFocus(el) {
  if (focused) focused.classList.remove('focus');
  focused = el || null;
  if (focused) {
    focused.classList.add('focus');
    focused.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' });
    const list = focusables();
    memory[screenKey()] = list.indexOf(focused);
  }
}
function screenKey() { const l = topLayer(); return l.id + (l.id === 'library' ? ':' + S.section + ':' + (S.root || '') + ':' + S.path : ''); }
function focusFirst(preferIndex) {
  const list = focusables();
  let i = preferIndex != null ? preferIndex : memory[screenKey()];
  if (i == null || i < 0 || i >= list.length) i = 0;
  setFocus(list[i] || null);
}
function move(dir) {
  const list = focusables();
  if (!list.length) return;
  if (!focused || !list.includes(focused)) { setFocus(list[0]); return; }
  const a = focused.getBoundingClientRect(), ax = a.left + a.width / 2, ay = a.top + a.height / 2;
  let best = null, bestScore = Infinity;
  for (const el of list) {
    if (el === focused) continue;
    const b = el.getBoundingClientRect(), bx = b.left + b.width / 2, by = b.top + b.height / 2;
    const dx = bx - ax, dy = by - ay;
    let primary, secondary;
    if (dir === 'up') { if (dy >= -4) continue; primary = -dy; secondary = Math.abs(dx); }
    else if (dir === 'down') { if (dy <= 4) continue; primary = dy; secondary = Math.abs(dx); }
    else if (dir === 'left') { if (dx >= -4) continue; primary = -dx; secondary = Math.abs(dy); }
    else { if (dx <= 4) continue; primary = dx; secondary = Math.abs(dy); }
    const score = primary + secondary * 2.5;
    if (score < bestScore) { bestScore = score; best = el; }
  }
  if (best) setFocus(best);
}
function pageMove(delta) {
  const list = focusables();
  if (!list.length) return;
  let i = Math.max(0, list.indexOf(focused));
  i = Math.max(0, Math.min(list.length - 1, i + delta));
  setFocus(list[i]);
}

// ---------------------------------------------------------------- views
function showView(name, pushHistory) {
  if (pushHistory && S.view !== name) S.history.push(S.view);
  $$('.view').forEach(v => v.classList.toggle('active', v.id === name));
  S.view = name;
  $('#crumb').textContent = name === 'home' ? 'Home' : name === 'webhome' ? 'Web' : name === 'games' ? 'Games' : name === 'settings' ? 'Settings' : (SECTIONS[S.section] || '');
  $('#tbtns').classList.toggle('show', name !== 'home');
  if (name === 'webhome') send({ cmd: 'getBookmarks' });
  focusFirst();
  updateHints();
}
function goHome() { S.pick = null; S.history = []; S.section = null; S.root = null; S.path = ''; showView('home', false); }

function openLayer(id) { $('#' + id).classList.add('active'); focusFirst(); updateHints(); }
function closeLayer(id) { $('#' + id).classList.remove('active'); focusFirst(); updateHints(); }

// ---------------------------------------------------------------- controller buttons (Settings)
// BTN holds what the controller in use calls the button for each role; the app keeps it up to date, including
// after a change on the Settings screen. The legend at the bottom is built from it.
const BTN = { ok: 'A', back: 'B', x: 'X', y: 'Y', lb: 'LB', rb: 'RB', menu: 'Start', view: '−', ls: 'LS', rs: 'RS' };
const ROLES = [
  ['ok', 'Select'], ['back', 'Back'],
  ['x', 'Play or pause music · audio track · delete'], ['y', 'Search · subtitles · keyboard'],
  ['lb', 'Previous track · zoom out'], ['rb', 'Next track · zoom in'],
  ['menu', 'Browser menu'], ['view', 'Home · leave the browser'], ['ls', 'Shift on the on-screen keyboard']
];
let buttonsCustom = false;
function renderButtons() {
  const keep = focused && focused.dataset.bind ? focused.dataset.bind : null;
  $('#btnList').innerHTML = ROLES.map(([k, label]) =>
    '<button class="item f" data-bind="' + k + '"><span class="ico">' + icon('pad') + '</span><span class="name">' + esc(label) + '</span><span class="state key">' + esc(BTN[k]) + '</span></button>').join('') +
    '<button class="item f" data-bind="reset"><span class="ico">' + icon('reload') + '</span><span class="name">Put the buttons back to normal</span><span class="meta">' + (buttonsCustom ? 'changed' : 'not changed') + '</span></button>';
  if (S.view === 'settings' && topLayer().id === 'settings') {
    const el = keep ? $('#btnList [data-bind="' + keep + '"]') : null;
    if (el) setFocus(el); else focusFirst();
  }
}
function onButtons(m) {
  Object.assign(BTN, m.names || {});
  buttonsCustom = !!m.custom;
  const waiting = m.capturing || '';
  $('#bindprompt').classList.toggle('active', !!waiting);
  if (waiting) $('#bindRole').textContent = (ROLES.find(r => r[0] === waiting) || ['', waiting])[1];
  renderButtons();
  updateHints();
}
function bindRow(key) {
  if (key === 'reset') { send({ cmd: 'resetButtons' }); toast('Buttons are back to normal'); return; }
  send({ cmd: 'bindButton', role: key });
}

// ---------------------------------------------------------------- games
// The app lists installed Steam games and the shortcuts in its "games" folder; picking one starts it. The hub
// stays open behind the game and ignores the controller until it is in front again.
let GAMES = [];
function openGames() {
  if (!GAMES.length) $('#gameTiles').innerHTML = '<div class="empty">Looking for games…</div>';
  showView('games', true);
  send({ cmd: 'games' });
}
function renderGames(m) {
  const keep = focused && focused.dataset.game != null ? (GAMES[+focused.dataset.game] || {}).id : null;
  GAMES = m.items || [];
  $('#gameTiles').innerHTML = GAMES.length ? GAMES.map((g, i) => {
    const wide = g.art && /#wide$/.test(g.art), art = g.art ? g.art.replace(/#wide$/, '') : '';
    return '<button class="game f' + (art ? (wide ? ' wide' : ' cover') : '') + '" data-game="' + i + '"' +
      (art ? ' style="background-image:url(&quot;' + esc(art) + '&quot;)"' : '') + '>' +
      (art ? '' : '<span class="ico">' + icon('pad') + '</span>') + '<span class="gname">' + esc(g.name) + '</span>' +
      '<span class="gmore" data-touch="game-menu" data-idx="' + i + '">' + icon('dots') + '</span></button>';
  }).join('') : '<div class="empty">No games found.<br>Steam games appear here once installed. For other games, put a shortcut in<br>' + esc(m.folder || 'the games folder') + '</div>';
  if (S.view === 'games') {
    const again = keep != null ? GAMES.findIndex(g => g.id === keep) : -1;
    focusFirst(again >= 0 ? again : undefined);
    updateHints();
  }
}
// Picture menu for one game: look one up, choose one of your own, or go back to the automatic one.
let menuGame = null;
function openGameMenu(i) {
  const g = GAMES[i]; if (!g) return;
  menuGame = g;
  $('#gmTitle').textContent = g.name;
  $('#gmAuto').style.display = g.custom ? '' : 'none';
  $('#gmAutoText').textContent = g.kind === 'steam' ? 'Use Steam\'s own picture again' : 'Remove the picture';
  openLayer('gamemenu');
}
function gameMenuItem(what) {
  const g = menuGame; closeLayer('gamemenu');
  if (!g || what === 'cancel') return;
  if (what === 'find') send({ cmd: 'gameCover', op: 'find', kind: g.kind, id: g.id });
  else if (what === 'findall') send({ cmd: 'gameCover', op: 'findAll' });
  else if (what === 'auto') send({ cmd: 'gameCover', op: 'remove', kind: g.kind, id: g.id });
  else if (what === 'pick') { S.pick = g; openSection('pictures'); $('#libTitle').textContent = 'Choose a picture for ' + g.name; }
}
function launchGame(g) {
  if (!g) return;
  if (!audio.paused) audio.pause();
  toast('Starting ' + g.name + '…');
  send({ cmd: 'launchGame', kind: g.kind, id: g.id });
}

// ---------------------------------------------------------------- library
function openSection(section) {
  S.section = section; S.root = null; S.path = '';
  $('#libTitle').textContent = SECTIONS[section];
  $('#libPath').textContent = 'Choose a location';
  $('#libList').innerHTML = '<div class="empty">Loading…</div>';
  showView('library', true);
  send({ cmd: 'roots', section: section });
}
function renderRoots(roots) {
  S.roots = roots;
  const list = $('#libList');
  list.innerHTML = roots.map((r, i) =>
    '<button class="item f' + (r.available ? '' : ' disabled') + '" data-root="' + i + '">' +
    '<span class="ico">' + icon(r.id === 'sd' ? 'drive' : 'folder') + '</span>' +
    '<span class="name">' + esc(r.label) + '</span><span class="meta">' + (r.available ? '' : 'Not available') + '</span></button>').join('');
  focusFirst();
  updateHints();
}
function openFolder(rootId, path) {
  S.root = rootId; S.path = path;
  const r = S.roots.find(x => x.id === rootId);
  $('#libPath').textContent = (r ? r.label : rootId) + (path ? ' › ' + path.split('/').join(' › ') : '');
  $('#libList').innerHTML = '<div class="empty">Loading…</div>';
  send({ cmd: 'list', section: S.section, root: rootId, path: path });
}
function renderListing(m) {
  if (m.root !== S.root || m.path !== S.path || m.section !== S.section) return;
  const list = $('#libList');
  if (m.error) { list.innerHTML = '<div class="empty">Can\'t open this folder: ' + esc(m.error) + '</div>'; S.entries = []; return; }
  S.host = m.host;
  const fileIcon = S.section === 'music' ? 'music' : S.section === 'pictures' ? 'photo' : 'video';
  S.entries = m.dirs.map(d => ({ dir: true, name: d })).concat(m.files.map(f => ({ dir: false, name: f.name, size: f.size })));
  if (!S.entries.length) {
    const what = SECTIONS[S.section].toLowerCase();
    list.innerHTML = '<div class="empty">No ' + what + ' here yet.<br>Copy files into your ' + SECTIONS[S.section] + ' or Downloads folder, or onto the SD card.</div>';
  } else {
    list.innerHTML = S.entries.map((e, i) =>
      '<button class="item f" data-entry="' + i + '"><span class="ico">' + icon(e.dir ? 'folder' : fileIcon) + '</span>' +
      '<span class="name">' + esc(e.dir ? e.name : (S.section === 'pictures' ? e.name : stripExt(e.name))) + '</span>' +
      '<span class="meta">' + (e.dir ? '' : fmtSize(e.size)) + '</span></button>').join('');
  }
  markPlaying();
  focusFirst();
  updateHints();
}
function libraryBack() {
  if (S.root == null) { S.section = null; S.pick = null; showView(S.history.pop() || 'home', false); return; }
  if (S.path) { const parts = S.path.split('/'); parts.pop(); openFolder(S.root, parts.join('/')); return; }
  S.root = null; $('#libPath').textContent = 'Choose a location'; renderRoots(S.roots);
}
function activateEntry(i) {
  const e = S.entries[i]; if (!e) return;
  if (e.dir) { openFolder(S.root, S.path ? S.path + '/' + e.name : e.name); return; }
  if (S.pick && S.section === 'pictures') {   // choosing a picture for a game
    const g = S.pick; S.pick = null;
    send({ cmd: 'gameCover', op: 'pick', kind: g.kind, id: g.id, root: S.root, path: S.path, name: e.name });
    S.section = null; S.root = null; S.path = ''; S.history = ['home'];
    showView('games', false); toast('Picture set for ' + g.name);
    return;
  }
  const files = S.entries.filter(x => !x.dir);
  const idx = files.indexOf(e);
  const items = files.map(f => ({ name: f.name, url: fileUrl(S.host, S.path, f.name) }));
  if (S.section === 'videos') playVideo(items, idx);
  else if (S.section === 'music') playMusic(items, idx);
  else openViewer(items, idx);
}

// ---------------------------------------------------------------- video player
// Two engines: VLC's (when VLC media player is installed; the app draws the picture behind this see-through page
// and reports progress as 'vlc' messages), otherwise the browser engine's <video> element.
const V = { items: [], idx: 0, osdTimer: 0, root: null, path: '' };
const VL = { st: '', t: 0, len: 0 };   // what VLC last reported: state, position and length in seconds
let vlcOn = false;
const video = $('#video');
function playVideo(items, idx) {
  V.items = items; V.idx = idx; V.root = S.root; V.path = S.path;
  audio.pause(); updateNowPlaying();
  openLayer('player');
  loadVideo();
}
function loadVideo() {
  const it = V.items[V.idx];
  $('#vTitle').textContent = stripExt(it.name);
  $('#vMsg').textContent = '';
  if (vlcOn) {
    document.body.classList.add('vlc');
    VL.st = 'opening'; VL.t = VL.len = 0; vlcPaint();
    send({ cmd: 'vlcPlay', root: V.root, path: V.path, name: it.name });
  } else {
    video.src = it.url;
    video.play().catch(() => {});
  }
  showOsd();
  updateHints();
}
function videoPaused() { return vlcOn ? (VL.st !== 'playing' && VL.st !== 'opening') : video.paused; }
function showOsd(msg) {
  if (msg != null) $('#vMsg').textContent = msg;
  $('#osd').classList.remove('hide');
  document.body.classList.remove('osdhide');
  clearTimeout(V.osdTimer);
  // After 3 seconds of playback the controls and the button legend fade out; any button or tap brings them back.
  V.osdTimer = setTimeout(() => { if (!videoPaused()) { $('#osd').classList.add('hide'); document.body.classList.add('osdhide'); } }, 3000);
}
function closeVideo() {
  if (document.body.classList.contains('vlc')) { send({ cmd: 'vlc', op: 'stop' }); document.body.classList.remove('vlc'); VL.st = ''; }
  else { video.pause(); video.removeAttribute('src'); video.load(); }
  clearTimeout(V.osdTimer); document.body.classList.remove('osdhide');
  closeLayer('player');
}
function vlcPaint() {
  $('#vFill').style.width = (VL.len ? Math.min(100, VL.t / VL.len * 100) : 0) + '%';
  $('#vTime').textContent = fmtTime(VL.t) + ' / ' + fmtTime(VL.len);
  setPlayIcon();
}
function onVlc(m) {
  if (!document.body.classList.contains('vlc')) return;
  const before = VL.st;
  VL.st = m.st; VL.t = m.t || 0; if (m.len) VL.len = m.len;
  vlcPaint();
  if (m.st === 'opening' && m.msg) showOsd(m.msg);
  if (m.st === before) return;
  if (m.st === 'paused') showOsd('Paused');
  else if (m.st === 'playing') showOsd('');
  else if (m.st === 'ended') { if (V.idx < V.items.length - 1) { V.idx++; loadVideo(); } else showOsd('Finished'); }
  else if (m.st === 'error') showOsd(m.msg ? 'Can\'t play this video: ' + m.msg : 'VLC can\'t play this video.');
}
video.addEventListener('timeupdate', () => {
  $('#vFill').style.width = (video.duration ? video.currentTime / video.duration * 100 : 0) + '%';
  $('#vTime').textContent = fmtTime(video.currentTime) + ' / ' + fmtTime(video.duration);
});
video.addEventListener('pause', () => { showOsd('Paused'); setPlayIcon(); audioIdle(); });
video.addEventListener('play', () => { showOsd(''); setPlayIcon(); audioIdle(); });
function setPlayIcon() { $('#vPlayIco').innerHTML = icon(videoPaused() ? 'play' : 'pause'); }
video.addEventListener('ended', () => { if (V.idx < V.items.length - 1) { V.idx++; loadVideo(); } else showOsd('Finished'); });
video.addEventListener('error', () => showOsd('This video format can\'t be played here (try MP4 or WebM).'));
function videoAction(a) {
  if (document.body.classList.contains('vlc')) {
    const seek = (by, label) => { send({ cmd: 'vlc', op: 'seek', by: by }); showOsd(label); };
    switch (a) {
      case 'ok': send({ cmd: 'vlc', op: 'toggle' }); showOsd(); return true;
      case 'left': seek(-10, '−10 s'); return true;
      case 'right': seek(10, '+10 s'); return true;
      case 'lt': seek(-60, '−1 min'); return true;
      case 'rt': seek(60, '+1 min'); return true;
      case 'y': send({ cmd: 'vlc', op: 'sub' }); return true;
      case 'x': send({ cmd: 'vlc', op: 'audio' }); return true;
      case 'up': stepVolume(10); showOsd(); return true;
      case 'down': stepVolume(-10); showOsd(); return true;
      case 'lb': if (V.idx > 0) { V.idx--; loadVideo(); } return true;
      case 'rb': if (V.idx < V.items.length - 1) { V.idx++; loadVideo(); } return true;
      case 'back': case 'view': closeVideo(); return true;
    }
    showOsd(); return true;
  }
  switch (a) {
    case 'ok': case 'x': if (video.paused) video.play().catch(() => {}); else video.pause(); return true;
    case 'left': video.currentTime = Math.max(0, video.currentTime - 10); showOsd('−10 s'); return true;
    case 'right': video.currentTime = Math.min(video.duration || 0, video.currentTime + 10); showOsd('+10 s'); return true;
    case 'lt': video.currentTime = Math.max(0, video.currentTime - 60); showOsd('−1 min'); return true;
    case 'rt': video.currentTime = Math.min(video.duration || 0, video.currentTime + 60); showOsd('+1 min'); return true;
    case 'up': stepVolume(10); showOsd(); return true;
    case 'down': stepVolume(-10); showOsd(); return true;
    case 'lb': if (V.idx > 0) { V.idx--; loadVideo(); } return true;
    case 'rb': if (V.idx < V.items.length - 1) { V.idx++; loadVideo(); } return true;
    case 'back': case 'view': closeVideo(); return true;
  }
  showOsd(); return true;
}

// ---------------------------------------------------------------- music
const audio = $('#audio');
const M = { items: [], idx: -1 };
function playMusic(items, idx) { M.items = items; M.idx = idx; loadTrack(); }
function loadTrack() {
  const it = M.items[M.idx]; if (!it) return;
  audio.src = it.url; audio.play().catch(() => {});
  updateNowPlaying(); markPlaying();
}
function nextTrack(d) {
  if (!M.items.length) return;
  M.idx = (M.idx + d + M.items.length) % M.items.length;
  loadTrack();
}
function toggleMusic() { if (!audio.src) return false; if (audio.paused) audio.play().catch(() => {}); else audio.pause(); updateNowPlaying(); return true; }
function updateNowPlaying() {
  const np = $('#nowplaying'), it = M.items[M.idx];
  np.classList.toggle('show', !!(it && audio.src));
  if (!it) return;
  $('#npTitle').textContent = stripExt(it.name);
  $('#npState').textContent = audio.paused ? 'Paused' : '';
}
function markPlaying() {
  if (S.section !== 'music') return;
  const cur = M.items[M.idx];
  $$('#libList .item').forEach(el => {
    const e = S.entries[+el.dataset.entry];
    el.classList.toggle('playing', !!(cur && e && !e.dir && cur.url === fileUrl(S.host, S.path, e.name)));
  });
}
audio.addEventListener('timeupdate', () => { $('#npFill').style.width = (audio.duration ? audio.currentTime / audio.duration * 100 : 0) + '%'; });
audio.addEventListener('ended', () => nextTrack(1));
audio.addEventListener('play', () => { updateNowPlaying(); audioIdle(); });
audio.addEventListener('pause', () => { updateNowPlaying(); audioIdle(); });
audio.addEventListener('error', () => { $('#npState').textContent = 'Can\'t play'; });

// ---------------------------------------------------------------- volume (0 to 200%)
// Up to 100% is the ordinary player volume. Above 100% the sound is amplified along the same curve as VLC and
// Linux desktops: the level cubed (150% = 3.4x, 200% = 8x). What no longer fits is turned down by a fast limiter
// instead of being clipped, because clipping is what makes a boost sound broken. Both players are routed through
// that chain the first time the boost is used. The app applies the part above 100% to web pages too (ui/boost.js).
let vol = 100;
const AX = { ctx: null, src: null, gain: null };
function boostReady() {
  if (AX.src) return true;
  try {
    if (!AX.ctx) { AX.ctx = new AudioContext(); AX.ctx.addEventListener('statechange', () => { if (!AX.src && AX.ctx.state === 'running') applyVolume(); }); }
    // Routing a player into an audio context that isn't running would silence it, so wait until it runs.
    if (AX.ctx.state !== 'running') { AX.ctx.resume().catch(() => {}); return false; }
    // Fast limiter, then a soft clipper that only catches the brief peaks the limiter lets through
    // (straight up to 0.9 of full scale, then rounding off towards 1; its input range stands for -2..2).
    const limiter = AX.ctx.createDynamicsCompressor();
    limiter.threshold.value = -2; limiter.knee.value = 2; limiter.ratio.value = 20; limiter.attack.value = 0.002; limiter.release.value = 0.12;
    const N = 8193, curve = new Float32Array(N);
    for (let i = 0; i < N; i++) {
      const u = (i / (N - 1) * 2 - 1) * 2, a = Math.abs(u);
      curve[i] = Math.sign(u) * (a <= 0.9 ? a : 0.9 + 0.1 * Math.tanh((a - 0.9) / 0.1));
    }
    const trim = AX.ctx.createGain(); trim.gain.value = 0.5;
    const clipper = AX.ctx.createWaveShaper(); clipper.curve = curve; clipper.oversample = '4x';
    limiter.connect(trim); trim.connect(clipper); clipper.connect(AX.ctx.destination);
    AX.gain = AX.ctx.createGain(); AX.gain.connect(limiter);
    AX.src = [video, audio].map(el => AX.ctx.createMediaElementSource(el));
    return true;
  } catch (e) { return false; }
}
function applyVolume() {
  const gain = Math.max(1, vol / 100);
  video.volume = audio.volume = Math.min(1, vol / 100);
  if (AX.src || gain > 1) {
    if (boostReady()) {
      AX.gain.gain.value = gain * gain * gain;
      for (const s of AX.src) { s.disconnect(); s.connect(gain > 1 ? AX.gain : AX.ctx.destination); }
      audioIdle();
    }
  }
  $('#vVol').textContent = 'Volume ' + vol + '%';
  $$('[data-volstate]').forEach(el => { el.textContent = Math.max(100, vol) + '%'; el.classList.toggle('neutral', vol <= 100); });
  showBoostState();
}
// What the boost is doing on the web page that is open (reported by ui/boost.js through the app).
let boostState = 'idle';
function showBoostState(state) {
  if (state) boostState = state;
  const text = { on: 'working on this page', waiting: 'starts after you press a button on the page', blocked: 'this page\'s player can\'t be boosted', idle: 'nothing is playing' };
  $('#bmBoostState').textContent = vol > 100 && S.browsing ? (text[boostState] || '') : '';
}
// Let the audio engine sleep while nothing is playing.
function audioIdle() {
  if (!AX.src) return;
  if (video.paused && audio.paused) AX.ctx.suspend().catch(() => {}); else AX.ctx.resume().catch(() => {});
}
function setVolume(v, fromHost) {
  v = Math.max(0, Math.min(200, Math.round(v / 10) * 10));
  if (isNaN(v)) return;
  vol = v; applyVolume();
  if (!fromHost) send({ cmd: 'setVolume', v: vol });
}
// While a web page is open only the boost matters (the page has its own volume), so the range there is 100 to 200%.
function stepVolume(d) {
  if (S.browsing) setVolume(Math.max(100, Math.max(100, vol) + d)); else setVolume(vol + d);
  volToast();
}
function volToast() {
  const layer = topLayer().id;
  if (layer === 'player' || layer === 'browsermenu') return;   // those show the level themselves
  toast('Volume ' + (S.browsing ? Math.max(100, vol) : vol) + '%');
}
let toastTimer = 0;
function toast(text) {
  const t = $('#toast'); t.textContent = text; t.classList.add('show');
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove('show'), 1400);
}

// ---------------------------------------------------------------- picture viewer
const P = { items: [], idx: 0 };
function openViewer(items, idx) { P.items = items; P.idx = idx; openLayer('viewer'); loadPhoto(); }
function loadPhoto() { const it = P.items[P.idx]; $('#photo').src = it.url; $('#pCaption').textContent = it.name + '   ' + (P.idx + 1) + ' / ' + P.items.length; }
function viewerAction(a) {
  if (a === 'left' || a === 'lb') { P.idx = (P.idx - 1 + P.items.length) % P.items.length; loadPhoto(); }
  else if (a === 'right' || a === 'rb' || a === 'ok') { P.idx = (P.idx + 1) % P.items.length; loadPhoto(); }
  else if (a === 'back' || a === 'view') { $('#photo').removeAttribute('src'); closeLayer('viewer'); }
  return true;
}

// ---------------------------------------------------------------- on-screen keyboard
const KB_ROWS = {
  lower: [['1','2','3','4','5','6','7','8','9','0'], ['q','w','e','r','t','y','u','i','o','p'], ['a','s','d','f','g','h','j','k','l','\''], ['⇧','z','x','c','v','b','n','m','-','_'], ['.com','@','␣','.','/','⌫','Done','Go']],
  upper: [['!','?','#','$','%','&','*','(',')','='], ['Q','W','E','R','T','Y','U','I','O','P'], ['A','S','D','F','G','H','J','K','L','"'], ['⇧','Z','X','C','V','B','N','M',':',';'], ['.com','@','␣',',','?','⌫','Done','Go']]
};
function openKeyboard(target, label, initial) {
  S.kb = { target: target, value: initial || '', shift: false, from: topLayer().id };
  $('#kbLabel').textContent = label;
  renderKeys();
  openLayer('keyboard');
}
function renderKeys() {
  const rows = KB_ROWS[S.kb.shift ? 'upper' : 'lower'];
  const prevIndex = focused && focused.classList.contains('key') ? focusables($('#keyboard')).indexOf(focused) : 15;
  $('#kbKeys').innerHTML = rows.map(r => '<div class="krow">' + r.map(k => {
    const cls = k === '␣' ? ' wide' : (k.length > 1 ? ' mid' : '') + (k === 'Go' ? ' go' : '');
    const label = k === '␣' ? 'Space' : k;
    return '<button class="key f' + cls + '" data-key="' + esc(k) + '">' + esc(label) + '</button>';
  }).join('') + '</div>').join('');
  $('#kbValue').textContent = S.kb.value;
  if ($('#keyboard').classList.contains('active')) focusFirst(prevIndex);
}
function kbPress(k) {
  const kb = S.kb;
  if (k === '⇧') { kb.shift = !kb.shift; renderKeys(); return; }
  if (k === '⌫') kb.value = kb.value.slice(0, -1);
  else if (k === '␣') kb.value += ' ';
  else if (k === 'Done') { kbSubmit(false); return; }
  else if (k === 'Go') { kbSubmit(true); return; }
  else { kb.value += k; if (kb.shift && k.length === 1 && /[A-Z]/.test(k)) { kb.shift = false; renderKeys(); } }
  $('#kbValue').textContent = kb.value;
}
function kbSubmit(enter) {
  const kb = S.kb; closeLayer('keyboard');
  if (kb.target === 'url') {
    if (kb.value.trim()) { S.browserMenuOpen = false; $('#browsermenu').classList.remove('active'); send({ cmd: 'browse', url: kb.value.trim(), tv: false }); }
    else returnToPageIfBrowsing();
  }
  else if (kb.target === 'page') send({ cmd: 'typeText', text: kb.value, enter: enter });
  S.kb = null; updateHints();
}
function kbCancel() {
  const kb = S.kb; closeLayer('keyboard'); S.kb = null;
  if (kb && kb.target === 'page') send({ cmd: 'overlayClosed' });
  else returnToPageIfBrowsing();
  updateHints();
}
// After closing an overlay opened from the touch browser bar: if no other overlay is left, show the page again.
function returnToPageIfBrowsing() {
  if (!S.browsing) return;
  if ($('#browsermenu').classList.contains('active') || $('#bookmarks').classList.contains('active') || $('#keyboard').classList.contains('active')) return;
  send({ cmd: 'overlayClosed' });
}
function keyboardAction(a) {
  switch (a) {
    case 'ok': if (focused && focused.dataset.key) kbPress(focused.dataset.key); return true;
    case 'x': kbPress('⌫'); return true;
    case 'y': kbPress('␣'); return true;
    case 'ls': case 'lt': kbPress('⇧'); return true;
    case 'menu': kbSubmit(true); return true;
    case 'back': kbCancel(); return true;
  }
  return false;
}

// ---------------------------------------------------------------- browser menu
function openBrowserMenu(m) {
  S.browserMenuOpen = true;
  $('#bmTitle').textContent = m.title || 'Browser';
  $('#bmUrl').textContent = m.url || '';
  $('#bmBack').classList.toggle('disabled', !m.canBack);
  $('#bmForward').classList.toggle('disabled', !m.canForward);
  setNav(m);
  showAdblock(m.adblock);
  $('#bmBlocked').textContent = m.adblock ? (m.blocked + (m.blocked === 1 ? ' ad or tracker' : ' ads and trackers') + ' blocked on this page') : 'Ad blocker is off';
  openLayer('browsermenu');
}

// ---------------------------------------------------------------- ad blocker switch
let adblockOn = true;
function showAdblock(on) {
  adblockOn = !!on;
  $$('[data-adstate]').forEach(el => { el.textContent = adblockOn ? 'On' : 'Off'; el.classList.toggle('off', !adblockOn); });
}
function toggleAdblock() {
  showAdblock(!adblockOn);
  send({ cmd: 'setAdblock', on: adblockOn });
  if (S.browserMenuOpen) closeBrowserMenu(true);   // back to the page, which reloads with the new setting
}
function closeBrowserMenu(returnToPage) {
  S.browserMenuOpen = false; $('#browsermenu').classList.remove('active');
  if (returnToPage) send({ cmd: 'overlayClosed' });
  focusFirst(); updateHints();
}
function browserMenuItem(el) {
  const act = el.dataset.bm;
  if (act === 'return') closeBrowserMenu(true);
  else if (act === 'back') { closeBrowserMenu(false); send({ cmd: 'browserBack' }); }
  else if (act === 'forward') { closeBrowserMenu(false); send({ cmd: 'browserForward' }); }
  else if (act === 'reload') { closeBrowserMenu(false); send({ cmd: 'browserReload' }); }
  else if (act === 'close') { closeBrowserMenu(false); send({ cmd: 'closeBrowser' }); }
  else if (act === 'star') send({ cmd: 'toggleBookmark' });
  else if (act === 'bookmarks') openBookmarks();
}

// ---------------------------------------------------------------- browser bar state + bookmarks
const N = { url: '', title: '', canBack: false, canForward: false, bookmarked: false };
let BK = [];
function hostOf(u) { try { return new URL(u).hostname.replace(/^www\./, ''); } catch (e) { return u || ''; } }
function setNav(m) {
  if (m.url !== undefined) { N.url = m.url || ''; N.title = m.title || ''; N.canBack = !!m.canBack; N.canForward = !!m.canForward; N.bookmarked = !!m.bookmarked; }
  if (m.browsing !== undefined) S.browsing = !!m.browsing;
  const host = hostOf(N.url);
  $('#tbUrl').textContent = N.title && N.title !== N.url ? host + '  ·  ' + N.title : (host || 'Search or type a web address');
  $('#tbBack').classList.toggle('disabled', !N.canBack);
  $('#tbFwd').classList.toggle('disabled', !N.canForward);
  $('#tbStar').classList.toggle('on', N.bookmarked);
  $('#bmStarText').textContent = N.bookmarked ? 'Remove bookmark' : 'Bookmark this page';
}
function bookmarkRow(b, cls, inner) {
  return '<button class="' + cls + ' f bk" data-url="' + esc(b.url) + '">' + inner +
    '<span class="del" data-touch="bk-del" data-del="' + esc(b.url) + '">' + icon('close') + '</span></button>';
}
function renderBookmarks(items) {
  BK = items || [];
  $('#bkList').innerHTML = BK.length
    ? BK.map(b => bookmarkRow(b, 'item', '<span class="ico">' + icon('globe') + '</span><span class="name">' + esc(b.title || hostOf(b.url)) + '</span><span class="meta">' + esc(hostOf(b.url)) + '</span>')).join('')
    : '<div class="empty">No bookmarks yet.<br>Open a page, then use the star to save it.</div>';
  $('#bkTiles').innerHTML = BK.map(b => bookmarkRow(b, 'tile', '<span class="ico">' + icon('bookmarks') + '</span><span class="tl">' + esc(b.title || hostOf(b.url)) + '</span>')).join('');
  if (topLayer().id === 'bookmarks' || S.view === 'webhome') { if (!focused || !document.contains(focused)) focusFirst(); }
  updateHints();
}
function openBookmarks() { send({ cmd: 'getBookmarks' }); openLayer('bookmarks'); }
function closeBookmarks() { closeLayer('bookmarks'); returnToPageIfBrowsing(); }
function removeBookmark(url) { if (url) send({ cmd: 'removeBookmark', url: url }); }

// ---------------------------------------------------------------- activation
function activate() {
  const el = focused; if (!el) return;
  if (el.dataset.key) { kbPress(el.dataset.key); return; }
  if (el.dataset.adblock) { toggleAdblock(); return; }
  if (el.dataset.vol) { if (Math.max(100, vol) >= 200) { setVolume(100); volToast(); } else stepVolume(10); return; }
  if (el.dataset.bm) { browserMenuItem(el); return; }
  if (el.dataset.kb === 'url') { openKeyboard('url', 'Search Google or type a web address', ''); return; }
  if (el.dataset.url) {
    $('#bookmarks').classList.remove('active');
    if (S.browserMenuOpen) closeBrowserMenu(false);
    send({ cmd: 'browse', url: el.dataset.url, tv: el.dataset.tv === '1' || /youtube\.com\/tv/.test(el.dataset.url) }); return;
  }
  if (el.dataset.go) {
    const g = el.dataset.go;
    if (g === 'exit') send({ cmd: 'exit' });
    else if (g === 'games') openGames();
    else if (g === 'settings') { showView('settings', true); renderButtons(); send({ cmd: 'getButtons' }); }
    else if (g === 'web') showView('webhome', true);
    else openSection(g);
    return;
  }
  if (el.dataset.game != null) { launchGame(GAMES[+el.dataset.game]); return; }
  if (el.dataset.bind) { bindRow(el.dataset.bind); return; }
  if (el.dataset.gm) { gameMenuItem(el.dataset.gm); return; }
  if (el.dataset.root != null) { const r = S.roots[+el.dataset.root]; if (r && r.available) openFolder(r.id, ''); return; }
  if (el.dataset.entry != null) { activateEntry(+el.dataset.entry); }
}

function goBack() {
  if (S.view === 'library') { libraryBack(); return; }
  if (S.view === 'webhome' || S.view === 'games' || S.view === 'settings') { showView(S.history.pop() || 'home', false); return; }
}

// ---------------------------------------------------------------- action dispatch
function onAction(a) {
  const layer = topLayer().id;
  if (layer === 'bindprompt') return;   // waiting for a button press; the app assigns it
  if (layer === 'keyboard') { if (keyboardAction(a)) return; }
  else if (layer === 'browsermenu') {
    if (a === 'back' || a === 'menu') { closeBrowserMenu(true); return; }
    if ((a === 'left' || a === 'right') && focused && focused.dataset.vol) { stepVolume(a === 'right' ? 10 : -10); return; }
  }
  else if (layer === 'gamemenu') { if (a === 'back' || a === 'x') { closeLayer('gamemenu'); return; } }
  else if (layer === 'bookmarks') {
    if (a === 'back' || a === 'menu') { closeBookmarks(); return; }
    if (a === 'x') { if (focused && focused.classList.contains('bk')) removeBookmark(focused.dataset.url); return; }
  }
  else if (layer === 'player') { videoAction(a); return; }
  else if (layer === 'viewer') { viewerAction(a); return; }

  switch (a) {
    case 'up': case 'down': case 'left': case 'right': move(a); break;
    case 'pageup': pageMove(-6); break;
    case 'pagedown': pageMove(6); break;
    case 'ok': activate(); break;
    case 'back': goBack(); break;
    case 'view': goHome(); break;
    case 'x':
      if (S.view === 'games' && focused && focused.dataset.game != null) openGameMenu(+focused.dataset.game);
      else if (focused && focused.classList.contains('bk')) removeBookmark(focused.dataset.url); else toggleMusic();
      break;
    case 'lb': if (M.items.length) nextTrack(-1); break;
    case 'rb': if (M.items.length) nextTrack(1); break;
    case 'lt': stepVolume(-10); break;
    case 'rt': stepVolume(10); break;
    case 'y':
      if (S.view === 'games') { send({ cmd: 'gameCover', op: 'findAll' }); }
      else if (S.view === 'webhome' || S.view === 'home') openKeyboard('url', 'Search Google or type a web address', '');
      break;
  }
}

// ---------------------------------------------------------------- hints bar
function hint(btn, text) { return '<span class="hint"><span class="btn">' + btn + '</span>' + text + '</span>'; }
function updateHints() {
  const layer = topLayer().id; let h = '';
  if (layer === 'keyboard') h = hint(BTN.ok, 'Type') + hint(BTN.x, 'Delete') + hint(BTN.y, 'Space') + hint(BTN.ls, 'Shift') + hint(BTN.menu, 'Go') + hint(BTN.back, 'Cancel');
  else if (layer === 'browsermenu') h = hint(BTN.ok, 'Select') + hint('LT RT', 'Volume boost') + hint(BTN.back, 'Back to page');
  else if (layer === 'bookmarks') h = hint(BTN.ok, 'Open') + hint(BTN.x, 'Remove') + hint(BTN.back, 'Back');
  else if (layer === 'player') h = hint(BTN.ok, 'Play/Pause') + hint('◀ ▶', '10 s') + hint('LT RT', '1 min') + hint('▲ ▼', 'Volume') + hint(BTN.lb + ' ' + BTN.rb, 'Prev/Next')
    + (vlcOn ? hint(BTN.y, 'Subtitles') + hint(BTN.x, 'Audio') : '') + hint(BTN.back, 'Close');
  else if (layer === 'viewer') h = hint('◀ ▶', 'Previous/Next') + hint(BTN.back, 'Close');
  else {
    h = hint(BTN.ok, 'Select');
    if (S.view !== 'home') h += hint(BTN.back, 'Back') + hint(BTN.view, 'Home');
    if (M.items.length) h += hint(BTN.x, 'Play/Pause music') + hint(BTN.lb + ' ' + BTN.rb, 'Track') + hint('LT RT', 'Volume');
    if (S.view === 'home' || S.view === 'webhome') h += hint(BTN.y, 'Search the web');
    if (S.view === 'webhome' && BK.length) h += hint(BTN.x, 'Remove bookmark');
    if (S.view === 'games' && GAMES.length) h += hint(BTN.x, 'Picture') + hint(BTN.y, 'Find missing pictures');
  }
  $('#hints').innerHTML = h;
}

// ---------------------------------------------------------------- host messages
window.chrome.webview.addEventListener('message', (e) => {
  const m = e.data;
  if (m.a) { lastPad = Date.now(); onAction(m.a); return; }
  switch (m.evt) {
    case 'roots': if (m.section === S.section) renderRoots(m.roots); break;
    case 'list': renderListing(m); break;
    case 'browserMenu': openBrowserMenu(m); showBoostState(m.boost); break;
    case 'boost': showBoostState(m.state); break;
    case 'settings': showAdblock(m.adblock); setTouch(m.touch); vlcOn = !!m.vlc; if (m.volume != null) setVolume(+m.volume, true); break;
    case 'games': renderGames(m); break;
    case 'buttons': onButtons(m); break;
    case 'coverSearch': if (m.note) toast(m.note); break;
    case 'gameStarted': if (!m.ok) toast('Could not start it: ' + (m.msg || 'unknown error')); break;
    case 'vlc': onVlc(m); break;
    case 'vlcTrack': showOsd(m.kind + ': ' + m.name); break;
    case 'mode': setTouch(m.touch); break;
    case 'bar': document.body.classList.toggle('bar', !!m.show); setNav(m); break;
    case 'nav': setNav(m); break;
    case 'bookmarks': renderBookmarks(m.items); break;
    case 'pageKeyboard': openKeyboard('page', 'Type into the page', ''); break;
    case 'browserClosed':
      S.browserMenuOpen = false; S.browsing = false;
      for (const id of ['browsermenu', 'keyboard', 'bookmarks']) $('#' + id).classList.remove('active');
      if (S.view === 'webhome') send({ cmd: 'getBookmarks' });
      focusFirst(); updateHints(); break;
  }
});

// ---------------------------------------------------------------- input mode: touch interface vs controller interface
// The app switches this when it sees a controller press; a finger on the screen switches it back.
function setTouch(on) { document.body.classList.toggle('touch', !!on); }
let lastTouchNote = 0;
window.addEventListener('pointerdown', (e) => {
  if (e.pointerType !== 'touch') return;
  const now = Date.now();
  if (document.body.classList.contains('touch') && now - lastTouchNote < 800) return;
  lastTouchNote = now; setTouch(true); send({ cmd: 'inputTouch' });
}, true);

// Keyboard fallback for use without a controller. Suspended for 30 s after any controller input, because Steam's
// desktop controller mode also sends keys (Enter, Space, arrows) for the same button presses.
var lastPad = 0;
window.addEventListener('keydown', (e) => {
  e.preventDefault();
  if (Date.now() - lastPad < 30000) return;
  const map = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right', Enter: 'ok', Escape: 'back', Backspace: 'back', PageUp: 'pageup', PageDown: 'pagedown' };
  const a = map[e.key]; if (a) onAction(a);
});
window.addEventListener('mousedown', (e) => e.preventDefault(), true);
window.addEventListener('contextmenu', (e) => e.preventDefault());

// ---------------------------------------------------------------- touch (and mouse) controls
function touchAction(t, el) {
  switch (t) {
    // browser bar
    case 'b-back': if (N.canBack) send({ cmd: 'browserBack' }); break;
    case 'b-forward': if (N.canForward) send({ cmd: 'browserForward' }); break;
    case 'b-reload': send({ cmd: 'browserReload' }); break;
    case 'b-address': send({ cmd: 'overlayOpen' }); openKeyboard('url', 'Search Google or type a web address', ''); break;
    case 'b-star': send({ cmd: 'toggleBookmark' }); break;
    case 'b-bookmarks': send({ cmd: 'overlayOpen' }); openBookmarks(); break;
    case 'b-menu': send({ cmd: 'openMenu' }); break;
    case 'b-exit': send({ cmd: 'closeBrowser' }); break;
    case 'bk-close': closeBookmarks(); break;
    case 'bk-del': removeBookmark(el.dataset.del); break;
    case 'back': onAction('back'); break;
    case 'home': onAction('view'); break;
    case 'v-prev': videoAction('lb'); break;
    case 'v-rew': videoAction('left'); break;
    case 'v-play': videoAction('ok'); break;
    case 'v-fwd': videoAction('right'); break;
    case 'v-next': videoAction('rb'); break;
    case 'v-close': closeVideo(); break;
    case 'v-sub': videoAction('y'); break;
    case 'bind-cancel': send({ cmd: 'cancelBind' }); break;
    case 'game-menu': openGameMenu(+el.dataset.idx); break;
    case 'gm-close': closeLayer('gamemenu'); break;
    case 'v-audio': videoAction('x'); break;
    case 'vol-up': stepVolume(10); if (topLayer().id === 'player') showOsd(); break;
    case 'vol-down': stepVolume(-10); if (topLayer().id === 'player') showOsd(); break;
    case 'p-close': viewerAction('back'); break;
    case 'kb-close': kbCancel(); break;
    case 'np-toggle': toggleMusic(); break;
  }
}
document.addEventListener('click', (e) => {
  const t = e.target.closest('[data-touch]');
  if (t) { e.stopPropagation(); touchAction(t.dataset.touch, t); return; }
  const layer = topLayer();
  if (layer.id === 'bookmarks' && e.target === layer) { closeBookmarks(); return; }   // tap outside the panel

  // Video: tapping the seek bar jumps there; tapping the picture shows the controls, or pauses if they're showing
  if (layer.id === 'player') {
    const seek = e.target.closest('#vSeek');
    if (seek) {
      const r = seek.getBoundingClientRect(), pos = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width));
      if (document.body.classList.contains('vlc')) { if (VL.len) send({ cmd: 'vlc', op: 'seekTo', pos: pos }); showOsd(); return; }
      if (video.duration) { video.currentTime = pos * video.duration; showOsd(); return; }
    }
    if ($('#osd').classList.contains('hide')) showOsd(); else if (!e.target.closest('#osd')) videoAction('ok');
    return;
  }
  // Pictures: tap the left third for previous, the right third for next
  if (layer.id === 'viewer') {
    const x = e.clientX / window.innerWidth;
    if (x < 0.33) viewerAction('left'); else if (x > 0.67) viewerAction('right');
    return;
  }
  // Menus: tap any tile, item or key to select it
  const el = e.target.closest('.f');
  if (!el || !focusables(layer).includes(el)) {
    if (layer.id === 'browsermenu' && e.target === layer) closeBrowserMenu(true); // tap outside the menu: back to page
    return;
  }
  setFocus(el);
  activate();
});

// ---------------------------------------------------------------- clock + start
function tick() { $('#clock').textContent = new Date().toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }); }
tick(); setInterval(tick, 15000);
paintIcons(document);
showView('home', false);
send({ cmd: 'getSettings' });
