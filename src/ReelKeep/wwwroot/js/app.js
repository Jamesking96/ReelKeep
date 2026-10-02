// ReelKeep UI - built from the "Downloader Board" Claude Design (Industry design system).
// Plain ES modules + Preact/htm, no build step. The native host (C#) does the real work; see bridge.js.
import { html, render, Component } from './vendor/preact-htm.js';
import { call, on, inHost } from './bridge.js';
import { fmt, fmt1, mmss, parseT, pct, fmtSize, linkKind, listIdOf, Corners, Icon, applyTheme, onSystemThemeChange } from './util.js';

const SA_LABEL = { video: 'Video', audio: 'Audio only', va: 'Video + audio' };
const QUALITIES = ['Best', '2160p', '1440p', '1080p', '720p', '480p', '360p'];
const AUDIO_FMTS = [['M4A', 'M4A (recommended)'], ['MP3', 'MP3 · 320k'], ['FLAC', 'FLAC · lossless'], ['WAV', 'WAV · PCM']];
const AUDIO_HINTS = {
  M4A: "YouTube's AAC copied bit-for-bit — small, cover art, plays everywhere.",
  MP3: '320 kbps — for old players and car stereos.',
  FLAC: 'Lossless, ~5× bigger. No gain over M4A; for editing software.',
  WAV: 'Raw PCM for DAWs. Largest, no cover art.',
};
const PATTERNS = ['{title}', '{channel} - {title}', '{title} ({year})', '{date} {title}', '{title} [{id}]'];
const SORTS = {
  newest: (a, b) => b.savedTs - a.savedTs,
  title: (a, b) => a.name.localeCompare(b.name),
  length: (a, b) => a.len - b.len,
  channel: (a, b) => a.channel.localeCompare(b.channel) || a.name.localeCompare(b.name),
};
/** Keys of yt-dlp's info JSON that are too big to be useful to read. */
const BULKY_JSON = ['formats', 'requested_formats', 'thumbnails', 'automatic_captions', 'subtitles', 'heatmap', 'requested_downloads', 'http_headers'];

class App extends Component {
  state = {
    data: { library: [], queue: [], queuePaused: false, tools: {}, settings: {} }, loaded: false,
    view: 'library', channel: null, q: '', mode: 'grid', sort: 'newest', sel: [], anchor: null,
    playingId: null, playing: false, t: 0, dur: 0, vol: 80, muted: false, rate: 1, autoSkip: true,
    playerOpen: false, theater: false, tab: 'details', rawJson: null,
    clipS: null, clipE: null, clipKind: 'mp4', clipName: null, previewEnd: null,
    paste: '', fetched: null, fetching: false, fetchErr: null, optsOpen: false,
    saveAs: 'va', quality: 'Best', audioFmt: 'M4A', pattern: '{title}', fileName: '',
    menu: null, dlg: null, dlgBusy: false, renameVal: '', renameTarget: null, redlIds: [], keep: true,
    redlSaveAs: 'va', redlQuality: 'Best', redlFmt: 'M4A',
    multiText: '', pl: null, plNum: true, pendingPlaylists: [], alert: null,
    toast: null, toastVideo: false, task: null, skippedSetup: false,
    theme: window.__rkTheme || 'system', parallel: 2,
  };

  // =========================================================================================
  // Lifecycle
  // =========================================================================================
  componentDidMount() {
    on('state', d => this.applyState(d));
    on('job', j => this.setState(s => ({ data: { ...s.data, queue: s.data.queue.map(x => x.id === j.id ? { ...x, ...j } : x) } })));
    on('toast', m => this.toast(m));
    on('play', id => this.play(id, true));
    on('task', t => this.setState({ task: t }));
    on('release', r => this.release(r));
    if (inHost) call('init').then(d => this.applyState(d, true)).catch(e => this.fail(e));

    this.timer = setInterval(() => this.tick(), 250);
    this.onKey = e => this.key(e);
    window.addEventListener('keydown', this.onKey);
    this.onResize = () => this.positionVideo();
    window.addEventListener('resize', this.onResize);
    applyTheme(this.state.theme);
    onSystemThemeChange(() => applyTheme(this.state.theme));
  }

  componentWillUnmount() {
    clearInterval(this.timer);
    window.removeEventListener('keydown', this.onKey);
    window.removeEventListener('resize', this.onResize);
  }

  componentDidUpdate() {
    this.syncMedia();
    this.positionVideo();
  }

  applyState(d, first) {
    const s = d.settings || {};
    const up = { data: d, loaded: true };
    if (first) Object.assign(up, {
      mode: s.viewMode || 'grid', sort: s.sort || 'newest', autoSkip: s.autoSkip !== false, vol: s.volume ?? 80,
      saveAs: s.saveAs || 'va', quality: s.quality || 'Best', audioFmt: s.audioFmt || 'M4A', pattern: s.pattern || '{title}',
      theme: ['light', 'dark'].includes(s.theme) ? s.theme : 'system', parallel: s.parallel || 2,
      plNum: s.numberPlaylist !== false, keep: s.keepExtras !== false, clipKind: ['mp4', 'fast', 'audio'].includes(s.clipKind) ? s.clipKind : 'mp4',
    });
    // Drop selections / playback of items that are gone
    const ids = new Set(d.library.map(x => x.id));
    up.sel = this.state.sel.filter(id => ids.has(id));
    if (this.state.playingId && !ids.has(this.state.playingId)) Object.assign(up, { playingId: null, playing: false, playerOpen: false });
    this.setState(up);
    if (first) applyTheme(up.theme);
    if (this.released) setTimeout(() => { this.released = false; this.forceUpdate(); }, 60);
  }

  setTheme(theme) {
    this.setState({ theme });
    applyTheme(theme);
    this.saveSetting({ theme });
  }

  setParallel(n) {
    this.setState({ parallel: n });
    this.saveSetting({ parallel: n });
  }

  saveSetting(patch) { if (inHost) call('settings', patch).catch(() => {}); }

  // =========================================================================================
  // Helpers
  // =========================================================================================
  get lib() { return this.state.data.library; }
  item(id) { return this.lib.find(x => x.id === id); }

  toast(msg, inVideo) {
    clearTimeout(this.toastT);
    this.setState({ toast: msg, toastVideo: !!inVideo });
    this.toastT = setTimeout(() => this.setState({ toast: null }), 2600);
  }

  fail(e) {
    const msg = (e && e.message) || String(e);
    if (msg.length > 90 || msg.includes('\n')) this.setState({ alert: msg, dlgBusy: false });
    else { this.toast(msg); this.setState({ dlgBusy: false }); }
  }

  async run(cmd, args, okMsg) {
    try {
      const r = await call(cmd, args);
      if (okMsg) this.toast(typeof okMsg === 'function' ? okMsg(r) : okMsg);
      return r;
    } catch (e) { this.fail(e); throw e; }
  }

  groups() {
    const counts = {};
    this.lib.forEach(x => { counts[x.channel] = (counts[x.channel] || 0) + 1; });
    const named = Object.entries(counts).filter(([c, n]) => c && n >= 2).sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).slice(0, 7).map(([c]) => c);
    const groupOf = it => named.includes(it.channel) ? it.channel : 'Other';
    const list = [...named];
    if (this.lib.some(x => !named.includes(x.channel))) list.push('Other');
    return { list, groupOf };
  }

  visible() {
    const s = this.state, q = s.q.trim().toLowerCase(), { groupOf } = this.groups();
    return this.lib
      .filter(it => (s.view !== 'audio' || it.audio) && (!s.channel || groupOf(it) === s.channel) &&
        (!q || (it.name + ' ' + it.title + ' ' + it.channel).toLowerCase().includes(q)))
      .slice().sort(SORTS[s.sort] || SORTS.newest);
  }

  // =========================================================================================
  // Playback (one <video> element, positioned over the player frame or the mini-player thumbnail)
  // =========================================================================================
  syncMedia() {
    const v = this.video; if (!v) return;
    const it = this.item(this.state.playingId);
    const src = it && !this.released ? it.src : null;
    if (src === this.loadedSrc) return;
    this.loadedSrc = src;
    const a = this.audioEl;
    if (!src) {
      v.pause(); v.removeAttribute('src'); v.load();
      if (a) { a.pause(); a.removeAttribute('src'); a.load(); }
      return;
    }
    const startAt = this.pendingSeek || 0; this.pendingSeek = null;
    v.src = src;
    v.poster = it.thumb || '';
    v.playbackRate = this.state.rate;
    this.applyVolume();
    if (a) { if (it.audioSrc) a.src = it.audioSrc; else { a.removeAttribute('src'); a.load(); } }
    v.addEventListener('loadedmetadata', () => {
      if (startAt) v.currentTime = startAt;
      if (this.state.playing) v.play().catch(() => this.setState({ playing: false }));
    }, { once: true });
  }

  applyVolume() {
    const v = this.video, a = this.audioEl, vol = this.state.muted ? 0 : this.state.vol / 100;
    if (v) { v.volume = vol; v.muted = this.state.muted; }
    if (a) { a.volume = vol; a.muted = this.state.muted; }
  }

  /** Keep the separate audio track (items downloaded without ffmpeg) in step with the video. */
  syncAudio(evt) {
    const v = this.video, a = this.audioEl;
    if (!v || !a || !a.getAttribute('src')) return;
    if (evt === 'play') { a.currentTime = v.currentTime; a.play().catch(() => {}); }
    else if (evt === 'pause') a.pause();
    else if (evt === 'seek') a.currentTime = v.currentTime;
    else if (evt === 'rate') a.playbackRate = v.playbackRate;
    else if (!v.paused && Math.abs(a.currentTime - v.currentTime) > 0.3) a.currentTime = v.currentTime;
  }

  positionVideo() {
    const layer = this.vlayer, root = this.root; if (!layer || !root) return;
    const it = this.item(this.state.playingId);
    const target = this.state.playerOpen ? this.frameEl : this.miniThumbEl;
    // Audio-only items show their cover art instead (the element keeps playing while hidden)
    if (!it || !target || !it.src || it.type === 'Audio') { layer.style.display = 'none'; return; }
    const r = root.getBoundingClientRect(), t = target.getBoundingClientRect();
    Object.assign(layer.style, {
      display: 'block', left: (t.left - r.left + 1) + 'px', top: (t.top - r.top + 1) + 'px',
      width: Math.max(0, t.width - 2) + 'px', height: Math.max(0, t.height - 2) + 'px',
    });
  }

  tick() {
    const v = this.video, s = this.state;
    if (!v || !s.playingId) return;
    let t = v.currentTime || 0;
    const up = {};
    const it = this.item(s.playingId);
    if (it && s.autoSkip && !v.paused) {
      for (const seg of it.sponsors) {
        if (t >= seg.s && t < seg.e - 0.4 && this.lastSkip !== seg.s) {
          this.lastSkip = seg.s; v.currentTime = seg.e; t = seg.e;
          this.toast('Skipped ' + (seg.cat || 'sponsor') + ' · ' + fmt(seg.s) + '–' + fmt(seg.e), true);
        }
      }
    }
    if (s.previewEnd != null && t >= s.previewEnd) { v.pause(); up.previewEnd = null; }
    this.syncAudio();
    if (Math.abs(t - s.t) > 0.05) up.t = t;
    const d = isFinite(v.duration) ? v.duration : (it ? it.len : 0);
    if (d && Math.abs(d - s.dur) > 0.5) up.dur = d;
    if (Object.keys(up).length) this.setState(up);
    this.positionVideo();
  }

  len() { const it = this.item(this.state.playingId); return this.state.dur || (it ? it.len : 0) || 1; }

  play(id, open) {
    const s = this.state;
    if (!this.item(id)) return;
    if (id === s.playingId) {
      this.setState({ playerOpen: open || s.playerOpen, menu: null });
      this.video && this.video.play().catch(() => {});
      return;
    }
    this.lastSkip = null;
    this.setState({ playingId: id, t: 0, dur: 0, playing: true, playerOpen: open || s.playerOpen, clipS: null, clipE: null, clipName: null, previewEnd: null, menu: null, rawJson: null });
  }

  togglePlay() {
    const v = this.video; if (!v || !this.state.playingId) return;
    this.setState({ previewEnd: null });
    if (v.paused) v.play().catch(() => {}); else v.pause();
  }

  stopPlayback() { const v = this.video; if (!v) return; v.pause(); v.currentTime = 0; this.setState({ t: 0 }); }

  seek(t) {
    const v = this.video; if (!v || !this.state.playingId) return;
    const len = this.len();
    v.currentTime = Math.max(0, Math.min(len, t));
    this.lastSkip = null;
    this.setState({ t: v.currentTime });
  }

  seekEv(e) { const r = e.currentTarget.getBoundingClientRect(); this.seek((e.clientX - r.left) / r.width * this.len()); }

  setRate(rate) { this.setState({ rate }); if (this.video) this.video.playbackRate = rate; }
  setVol(vol) { this.setState({ vol, muted: false }, () => this.applyVolume()); clearTimeout(this.volT); this.volT = setTimeout(() => this.saveSetting({ volume: vol }), 500); }
  toggleMute() { this.setState({ muted: !this.state.muted }, () => this.applyVolume()); }

  /** The host is about to rename / replace / delete this item's files: let go of them. */
  release({ id, token }) {
    if (id === this.state.playingId && this.video) {
      this.pendingSeek = this.video.currentTime;
      this.released = true;
      this.setState({ playing: false });
      this.loadedSrc = undefined;
      this.video.pause(); this.video.removeAttribute('src'); this.video.load();
      if (this.audioEl) { this.audioEl.pause(); this.audioEl.removeAttribute('src'); this.audioEl.load(); }
    }
    call('released', { token }).catch(() => {});
  }

  // =========================================================================================
  // Keyboard
  // =========================================================================================
  key(e) {
    const tag = e.target.tagName, s = this.state;
    if (e.key === 'Escape') {
      if (s.alert) this.setState({ alert: null });
      else if (s.dlg || s.menu) this.setState({ dlg: null, menu: null });
      else if (s.theater) this.setState({ theater: false });
      else if (s.playerOpen) this.setState({ playerOpen: false });
      else if (s.sel.length) this.setState({ sel: [] });
      return;
    }
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || s.dlg || s.alert) return;
    const k = e.key;
    if (k === ' ' && s.playingId) { e.preventDefault(); this.togglePlay(); }
    else if (k === 'ArrowLeft' && s.playingId) { e.preventDefault(); this.seek(s.t - 10); }
    else if (k === 'ArrowRight' && s.playingId) { e.preventDefault(); this.seek(s.t + 10); }
    else if (k === 'ArrowUp') { e.preventDefault(); this.setVol(Math.min(100, s.vol + 5)); }
    else if (k === 'ArrowDown') { e.preventDefault(); this.setVol(Math.max(0, s.vol - 5)); }
    else if (k === 'm' || k === 'M') this.toggleMute();
    else if ((k === 'f' || k === 'F') && s.playerOpen) this.setState({ theater: !s.theater });
    else if (k === '[' && s.playingId) this.setState({ clipS: s.t, clipName: null, playerOpen: true });
    else if (k === ']' && s.playingId) this.setState({ clipE: s.t, clipName: null, playerOpen: true });
    else if (k === 'F2') { e.preventDefault(); const id = s.playerOpen ? s.playingId : s.sel[0]; if (id) this.openRename(id); }
    else if (k === 'Delete' && s.sel.length && !s.playerOpen) this.setState({ dlg: 'del' });
    else if (k === 'Enter' && e.altKey) { e.preventDefault(); const id = s.playerOpen ? s.playingId : s.sel.length === 1 ? s.sel[0] : null; if (id) this.openInfo(id); }
    else if (k === 'Enter' && s.sel.length === 1 && !s.playerOpen) this.play(s.sel[0], true);
    else if ((k === 'a' || k === 'A') && e.ctrlKey && !s.playerOpen) { e.preventDefault(); this.setState({ sel: this.visible().map(x => x.id) }); }
  }

  // =========================================================================================
  // Library selection
  // =========================================================================================
  select(id, e) {
    const s = this.state;
    if (e.shiftKey && s.anchor) {
      const ids = this.visible().map(x => x.id), a = ids.indexOf(s.anchor), b = ids.indexOf(id);
      if (a >= 0 && b >= 0) return this.setState({ sel: ids.slice(Math.min(a, b), Math.max(a, b) + 1) });
    }
    if (e.ctrlKey || e.metaKey) this.setState({ sel: s.sel.includes(id) ? s.sel.filter(x => x !== id) : [...s.sel, id], anchor: id });
    else this.setState({ sel: [id], anchor: id });
  }

  ctxIds(id) { const s = this.state; return s.sel.includes(id) ? s.sel : [id]; }

  openMenu(e, id) {
    e.preventDefault(); e.stopPropagation();
    const r = this.root.getBoundingClientRect();
    const up = { menu: { id, x: Math.min(e.clientX - r.left, r.width - 250) + 'px', y: Math.min(e.clientY - r.top, r.height - 330) + 'px' } };
    if (!this.state.sel.includes(id)) Object.assign(up, { sel: [id], anchor: id });
    this.setState(up);
  }

  // =========================================================================================
  // Paste bar
  // =========================================================================================
  setPaste(v) {
    clearTimeout(this.fetchT);
    const k = linkKind(v);
    this.fetchSeq = (this.fetchSeq || 0) + 1;
    const seq = this.fetchSeq;
    this.setState({ paste: v, fetched: null, fetchErr: null, fetching: k.kind === 'single' });
    if (k.kind !== 'single') return;
    this.fetchT = setTimeout(async () => {
      try {
        const f = await call('fetchInfo', { url: k.urls[0], pattern: this.state.pattern });
        if (seq !== this.fetchSeq) return;
        this.setState({ fetching: false, fetched: { ...f, url: k.urls[0] }, optsOpen: true, fileName: f.name });
      } catch (e) {
        if (seq !== this.fetchSeq) return;
        this.setState({ fetching: false, fetchErr: e.message });
      }
    }, 450);
  }

  opts() { const s = this.state; return { saveAs: s.saveAs, quality: s.quality, audioFmt: s.audioFmt }; }

  async enqueueUrls(urls, extra = {}) {
    const n = await this.run('enqueue', { ...this.opts(), items: urls.map(url => ({ url })), ...extra });
    this.toast(n ? n + (n === 1 ? ' link' : ' links') + ' added to the queue' : 'Already in the queue');
    return n;
  }

  async doPrimary() {
    const s = this.state, k = linkKind(s.paste);
    if (k.kind === 'single') { if (s.fetched) this.download(false); return; }
    if (k.kind === 'multi') {
      const lists = k.urls.filter(u => linkKind(u).kind === 'playlist'), singles = k.urls.filter(u => linkKind(u).kind !== 'playlist');
      if (singles.length) await this.enqueueUrls(singles).catch(() => {});
      this.setState({ paste: '' });
      if (lists.length) this.openPlaylist(lists[0], lists.slice(1));
      return;
    }
    if (k.kind === 'playlist') this.openPlaylist(k.urls[0], []);
  }

  async openPlaylist(url, rest) {
    this.setState({ dlg: 'playlist', pl: { loading: true, url, items: [] }, pendingPlaylists: rest });
    try {
      const p = await call('playlist', { url });
      this.setState({ pl: { ...p, url, items: p.items.map(x => ({ ...x, checked: !x.note })) } });
    } catch (e) { this.setState({ dlg: null, pl: null }); this.fail(e); }
  }

  async download(andPlay) {
    const s = this.state, f = s.fetched; if (!f) return;
    const name = s.fileName.trim();
    const n = await this.run('enqueue', {
      ...this.opts(), playAfter: andPlay,
      items: [{ url: f.url, id: f.id, title: name || f.title, name: name && name !== f.name ? name : null }],
    }).catch(() => null);
    if (n == null) return;
    this.setState({ paste: '', fetched: null, optsOpen: false });
    this.toast(f.inLibrary ? 'Already in your library' + (andPlay ? ' — playing it' : '') : andPlay ? 'Downloading — plays when ready' : 'Added to queue');
  }

  setDefaults(patch) {
    this.setState(patch);
    const m = {};
    if (patch.saveAs) m.saveAs = patch.saveAs;
    if (patch.quality) m.quality = patch.quality;
    if (patch.audioFmt) m.audioFmt = patch.audioFmt;
    if (patch.pattern) m.pattern = patch.pattern;
    this.saveSetting(m);
  }

  async onPattern(p) {
    this.setDefaults({ pattern: p });
    const f = this.state.fetched;
    if (f) { try { this.setState({ fileName: await call('applyPattern', { url: f.url, pattern: p }) }); } catch { /* keep */ } }
  }

  // =========================================================================================
  // Library actions
  // =========================================================================================
  openRename(id, file) {
    const it = this.item(id); if (!it) return;
    const val = file ? file.name.replace(/\.[^.]+$/, '') : it.name;
    this.focusedRename = false;
    this.setState({ dlg: 'rename', renameTarget: { id, file: file ? file.name : null }, renameVal: val, menu: null });
  }

  async applyRename() {
    const s = this.state, v = s.renameVal.trim(); if (!v) return;
    this.setState({ dlgBusy: true });
    try {
      if (s.renameTarget.file) await call('renameFile', { id: s.renameTarget.id, file: s.renameTarget.file, name: v });
      else await call('rename', { id: s.renameTarget.id, name: v });
      this.setState({ dlg: null, dlgBusy: false });
      this.toast('Renamed');
    } catch (e) { this.fail(e); }
  }

  async saveAudio(ids) {
    this.setState({ menu: null });
    const fmtName = this.state.audioFmt;
    try {
      const n = await call('saveAudio', { ids, fmt: fmtName });
      this.toast(n ? 'Saved audio · ' + fmtName + (n > 1 ? ' · ' + n + ' files' : '') : 'Already saved as ' + fmtName);
    } catch (e) { this.fail(e); }
  }

  async redl(ids) {
    this.setState({ menu: null });
    try {
      const n = await call('redownload', { ids, same: true, keep: true });
      this.toast(n ? n + (n > 1 ? ' items' : ' item') + ' queued for re-download' : 'Already queued');
    } catch (e) { this.fail(e); }
  }

  openRedl(ids) {
    const s = this.state, first = this.item(ids[0]);
    const saveAs = first ? (first.type === 'Audio' ? 'audio' : first.type === 'V + A' ? 'va' : 'video') : s.saveAs;
    this.setState({ dlg: 'redl', redlIds: ids, menu: null, redlSaveAs: saveAs, redlQuality: s.quality, redlFmt: s.audioFmt });
  }

  async applyRedl() {
    const s = this.state;
    try {
      const n = await call('redownload', { ids: s.redlIds, same: false, saveAs: s.redlSaveAs, quality: s.redlQuality, audioFmt: s.redlFmt, keep: s.keep });
      this.saveSetting({ keepExtras: s.keep });
      this.setState({ dlg: null });
      this.toast(n + (n !== 1 ? ' items' : ' item') + ' queued for re-download');
    } catch (e) { this.fail(e); }
  }

  async applyDelete() {
    const s = this.state, gone = s.sel.slice();
    this.setState({ dlgBusy: true });
    try {
      const n = await call('delete', { ids: gone });
      this.setState({ sel: [], dlg: null, dlgBusy: false });
      this.toast('Moved ' + n + (n !== 1 ? ' items' : ' item') + ' to the Recycle Bin');
    } catch (e) { this.fail(e); }
  }

  openInfo(id) { if (this.item(id)) this.setState({ dlg: 'info', infoId: id, menu: null }); }

  /** Everything worth knowing about an item, as plain text (for "Copy details"). */
  detailsText(it) {
    const line = (k, v) => v ? k + ': ' + v + '\n' : '';
    return line('Name', it.name) + (it.title !== it.name ? line('Title', it.title) : '') + line('Channel', it.channel) +
      line('Link', it.url) + line('Video ID', it.id) + line('Uploaded', it.uploaded) + line('Views', it.views ? it.views.toLocaleString() : '') +
      line('Length', fmt(it.len)) + line('Type', it.type) + line('Quality', it.quality) + line('Resolution', it.width ? it.width + ' × ' + it.height : '') +
      line('Audio', it.audioLabel) + line('Size', fmtSize(it.size)) + line('Saved', it.saved) +
      line('Chapters', (it.chapters || []).length || '') + line('Sponsor segments', (it.sponsors || []).length || '') +
      '\nFiles:\n' + it.files.map(f => '  ' + f.name + ' (' + f.what + ', ' + fmtSize(f.size) + ')').join('\n') + '\n' +
      (it.desc ? '\nDescription:\n' + it.desc + '\n' : '');
  }

  async copy(text, what) {
    try { await navigator.clipboard.writeText(text); }
    catch {
      // Fallback for when the async clipboard API isn't allowed
      const ta = document.createElement('textarea');
      ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
      document.body.appendChild(ta); ta.select(); document.execCommand('copy'); ta.remove();
    }
    // Toasts sit behind dialogs, so confirm on the button itself while one is open
    if (this.state.dlg) { clearTimeout(this.copiedTimer); this.setState({ copied: what }); this.copiedTimer = setTimeout(() => this.setState({ copied: null }), 1600); }
    else this.toast(what + ' copied');
  }

  showInFolder(id, file) { this.setState({ menu: null }); call('showInFolder', { id, file }).catch(e => this.fail(e)); }

  async saveClip() {
    const s = this.state, it = this.item(s.playingId);
    if (!it || s.clipS == null || s.clipE == null || s.clipE <= s.clipS) return;
    try {
      const name = await call('saveClip', { id: it.id, start: s.clipS, end: s.clipE, kind: s.clipKind, name: s.clipName });
      this.setState({ tab: 'files', clipS: null, clipE: null, clipName: null });
      this.toast('Clip saved · ' + name);
    } catch (e) { this.fail(e); }
  }

  async openTab(key) {
    this.setState({ tab: key });
    if (key === 'json' && this.state.playingId && !this.state.rawJson) {
      try {
        const raw = await call('rawJson', { id: this.state.playingId });
        let text = 'No metadata file for this item.';
        if (raw) {
          const o = JSON.parse(raw);
          for (const k of BULKY_JSON) if (Array.isArray(o[k])) o[k] = `[${o[k].length} items – omitted]`; else if (o[k] && typeof o[k] === 'object') o[k] = '{… omitted}';
          text = JSON.stringify(o, null, 2);
        }
        this.setState({ rawJson: text });
      } catch (e) { this.setState({ rawJson: 'Could not read the metadata: ' + e.message }); }
    }
  }

  // =========================================================================================
  // Render
  // =========================================================================================
  render() {
    const s = this.state, d = s.data;
    const it = this.item(s.playingId);
    const isBoard = !s.playerOpen && (s.view === 'library' || s.view === 'audio');
    const tools = d.tools || {};
    const needFfmpeg = s.loaded && tools.ready && !tools.ffmpeg && !s.skippedSetup;

    return html`
      <div ref=${el => { this.root = el; }} onClick=${() => s.menu && this.setState({ menu: null })}
        style="position:relative;height:100vh;min-height:620px;min-width:900px;display:grid;grid-template-columns:clamp(170px,18vw,212px) minmax(0,1fr);grid-template-rows:minmax(0,1fr) auto;background:var(--color-bg);overflow:hidden">
        ${this.renderSidebar()}
        <div style="min-height:0;display:flex;flex-direction:column;position:relative">
          ${!s.playerOpen && s.view !== 'clips' && this.renderPasteBar()}
          ${isBoard && this.renderBoard()}
          ${!s.playerOpen && s.view === 'queue' && this.renderQueue()}
          ${!s.playerOpen && s.view === 'clips' && this.renderClips()}
          ${s.playerOpen && it && this.renderPlayer(it)}
        </div>
        <div style="grid-column:2">${it && !s.playerOpen && this.renderMini(it)}</div>

        <div class="video-layer" ref=${el => { this.vlayer = el; }}>
          <video ref=${el => { this.video = el; }} preload="metadata" playsinline
            onPlay=${() => { this.setState({ playing: true }); this.syncAudio('play'); }}
            onPause=${() => { this.setState({ playing: false }); this.syncAudio('pause'); }}
            onEnded=${() => this.setState({ playing: false })}
            onSeeked=${() => this.syncAudio('seek')}
            onRateChange=${() => this.syncAudio('rate')}
            onError=${() => it && this.loadedSrc && this.toast("Can't play this file")}></video>
        </div>
        <audio ref=${el => { this.audioEl = el; }} preload="auto" style="display:none"></audio>

        ${s.menu && this.renderMenu()}
        ${s.toast && !(s.toastVideo && s.playerOpen) && html`
          <div class="blueprint elev-md" style="position:absolute;left:50%;bottom:84px;transform:translateX(-50%);background:var(--screen-bg);color:var(--screen-fg);padding:8px 16px;font-size:13px;z-index:30;pointer-events:none;max-width:70%;text-align:center"><${Corners} />${s.toast}</div>`}
        ${s.task && html`
          <div class="blueprint elev-lg" style="position:absolute;left:24px;bottom:16px;width:300px;background:var(--color-bg);padding:10px 14px;z-index:25;display:flex;flex-direction:column;gap:6px;font-size:13px">
            <${Corners} /><div style="display:flex"><span style="font-weight:500">${s.task.label}</span><span class="text-muted pill-time" style="margin-left:auto">${Math.floor(s.task.pct || 0)}%</span></div>
            <div class="bar"><div style=${'width:' + pct((s.task.pct || 0) / 100)}></div></div>
          </div>`}
        ${this.renderDialogs()}
        ${needFfmpeg && !s.dlg && this.renderSetup()}
      </div>`;
  }

  renderSidebar() {
    const s = this.state, d = s.data, tools = d.tools || {}, st = d.settings || {};
    const { list, groupOf } = this.groups();
    const qLeft = d.queue.filter(x => x.state === 'wait' || x.state === 'dl').length;
    const clips = this.lib.reduce((n, x) => n + x.files.filter(f => f.role === 'clip').length, 0);
    const nav = (v, label, icon, count) => html`
      <div class=${'nav-item' + (s.view === v && !s.playerOpen ? ' on' : '')} onClick=${() => this.setState({ view: v, playerOpen: false, sel: [], menu: null })}>
        ${icon}${label}<span style="margin-left:auto;font-size:12px;font-weight:400">${count}</span></div>`;
    return html`
      <div style="grid-row:1 / 3;border-right:1px solid var(--color-divider);display:flex;flex-direction:column;padding:16px 12px 12px;gap:2px;min-height:0">
        <div style="display:flex;align-items:center;gap:10px;padding:0 8px 18px">
          <img src="icon.png" width="26" height="26" alt="" />
          <div style="font-family:var(--font-heading);font-weight:600;font-size:21px;line-height:1">ReelKeep</div>
        </div>
        ${nav('library', 'Library', Icon.grid(), this.lib.length)}
        ${nav('queue', 'Queue', Icon.download(), qLeft || '')}
        ${nav('clips', 'Clips', Icon.scissors(), clips || '')}
        ${nav('audio', 'Audio files', Icon.music(), this.lib.filter(x => x.audio).length || '')}
        ${list.length > 0 && html`<h6 style="margin:22px 8px 6px" class="text-muted">Channels</h6>`}
        <div style="overflow:auto;min-height:0;flex:0 1 auto">
          ${list.map(g => html`
            <div class=${'ch-item' + (s.channel === g ? ' on' : '')} title=${g}
              onClick=${() => this.setState({ channel: s.channel === g ? null : g, view: s.view === 'audio' ? 'audio' : 'library', playerOpen: false })}>
              <span>${g}</span><span class="text-muted" style="margin-left:auto">${this.lib.filter(x => groupOf(x) === g).length}</span></div>`)}
        </div>
        <div style="margin-top:auto;padding-top:12px;font-size:11.5px;display:flex;flex-direction:column;gap:4px;padding-left:8px;padding-right:8px">
          <div class="seg theme-seg" role="radiogroup" aria-label="Theme">
            ${[['system', 'Auto', Icon.monitor(), 'Follow Windows'], ['light', 'Light', Icon.sun(), 'Light theme'], ['dark', 'Dark', Icon.moon(), 'Dark theme']].map(([k, label, icon, tip]) => html`
              <label class="seg-opt" title=${tip}><input type="radio" name="theme" checked=${s.theme === k} onChange=${() => this.setTheme(k)} />${icon}${label}</label>`)}
          </div>
          <a class="text-muted" style="text-decoration:none;word-break:break-all" title=${'Open ' + (st.libraryFolder || '')} onClick=${() => call('openLibraryFolder')}>${st.libraryShort || ''}</a>
          <a onClick=${async () => { const p = await call('changeFolder').catch(e => this.fail(e)); if (p) this.toast('Library folder: ' + p); }}>Change folder…</a>
          <span class="text-muted" style="margin-top:6px">${tools.ready ? 'yt-dlp ' + (tools.ytdlp || '') + ' · ffmpeg ' + (tools.ffmpeg ? '✓' : '✗') + (tools.js ? ' · ' + tools.js : '') : tools.error ? 'Tools unavailable' : 'Preparing tools…'}</span>
          ${tools.error && html`<span class="err" style="font-size:11px">${tools.error}</span>`}
          ${tools.ready && html`<a onClick=${async () => { this.toast('Checking for yt-dlp updates…'); const r = await call('updateYtdlp').catch(e => this.fail(e)); if (r) this.toast(r); }}>Update yt-dlp</a>`}
          ${tools.ready && !tools.ffmpeg && html`<a onClick=${() => this.setState({ skippedSetup: false })}>Install ffmpeg…</a>`}
        </div>
      </div>`;
  }

  renderPasteBar() {
    const s = this.state, k = linkKind(s.paste), f = s.fetched;
    const pasteChip = { multi: k.urls.length + ' links', playlist: 'Playlist', single: f ? 'Video' : '' }[k.kind] || '';
    const primaryLabel = { multi: 'Queue ' + k.urls.length + ' links', playlist: 'Choose videos…', single: s.fetching ? 'Fetching…' : 'Download' }[k.kind] || 'Download';
    const primaryDisabled = !k.kind || (k.kind === 'single' && !f);
    const seg = (key, label) => html`<label class="seg-opt"><input type="radio" checked=${s.saveAs === key} onChange=${() => this.setDefaults({ saveAs: key })} />${label}</label>`;
    return html`
      <div style="flex:none;padding:22px 28px 10px;display:flex;flex-direction:column;gap:8px">
        <div class="blueprint" style="display:flex;align-items:center;gap:10px;padding:6px 6px 6px 14px">
          <${Corners} />
          <span style="color:var(--color-accent);flex:none;display:flex">${Icon.link()}</span>
          <input value=${s.paste} onInput=${e => this.setPaste(e.target.value)} onKeyDown=${e => { if (e.key === 'Enter') this.doPrimary(); }}
            placeholder="Paste a link, several links, or a playlist / channel…" spellcheck="false"
            style="flex:1;min-width:0;border:0;background:transparent;font:inherit;font-size:15px;color:var(--color-text);outline:none;padding:6px 0" />
          ${pasteChip && html`<span class="tag tag-accent" style="white-space:nowrap;flex:none">${pasteChip}</span>`}
          ${k.kind === 'single' && k.inList && html`<button class="btn btn-ghost" style="font-size:13px" title="This video is part of a playlist" onClick=${() => this.openPlaylist('https://www.youtube.com/playlist?list=' + listIdOf(k.urls[0]), [])}>Whole playlist</button>`}
          <button class="btn btn-ghost" style="font-size:13px" onClick=${() => this.setState({ dlg: 'multi', multiText: k.kind === 'multi' ? k.urls.join('\n') : '' })}>Multiple links</button>
          <button class="btn btn-ghost" style="font-size:13px" onClick=${() => this.setState({ optsOpen: !s.optsOpen })}>${s.optsOpen ? 'Options ▴' : 'Options ▾'}</button>
          <button class="btn btn-primary" style="height:36px;padding-inline:18px" onClick=${() => this.doPrimary()} disabled=${primaryDisabled}>${primaryLabel}</button>
        </div>
        ${!s.paste && !s.optsOpen && this.lib.length === 0 && s.loaded && html`
          <div class="text-muted" style="font-size:12.5px">Paste a video, a playlist or several links above. Everything you download lands in your library.</div>`}
        ${s.fetching && html`<div style="display:flex;flex-direction:column;gap:5px;font-size:12px;color:var(--color-accent-700)">Fetching video info…<div class="indeterminate"><div></div></div></div>`}
        ${s.fetchErr && html`<div class="err" style="font-size:12.5px">Couldn't read that link: ${s.fetchErr.split('\n').pop()}</div>`}
        ${s.optsOpen && html`
          <div class="blueprint" style=${'display:grid;grid-template-columns:' + (f ? '240px minmax(0,1fr)' : 'minmax(0,1fr)') + ';gap:16px;padding:14px'}>
            <${Corners} />
            ${f && html`
              <div style="display:flex;flex-direction:column;gap:6px">
                <div class="blueprint thumb" style="position:relative"><${Corners} />
                  ${f.thumb ? html`<img src=${f.thumb} alt="" />` : html`<span class="placeholder text-muted">THUMBNAIL</span>`}
                  <span style="position:absolute;right:6px;bottom:6px;font-size:11px;padding:1px 5px;background:var(--screen-bg);color:var(--screen-fg)">${fmt(f.len)}</span>
                </div>
                <div style="font-size:13.5px;font-weight:500;line-height:1.3">${f.title}</div>
                <div style="font-size:12px" class="text-muted">${f.channel}${f.date ? ' · ' + f.date : ''}</div>
                ${f.inLibrary && html`<span class="tag tag-accent" style="align-self:flex-start">Already in your library</span>`}
              </div>`}
            <div style="display:flex;flex-direction:column;gap:12px;min-width:0">
              <div class="field"><label>${f ? 'Save as file name' : 'Naming pattern for new downloads'}</label>
                <div style="display:flex">
                  ${f ? html`<input class="input" style="flex:1;min-width:0" value=${s.fileName} onInput=${e => this.setState({ fileName: e.target.value })} placeholder="File name — filled in from the video title" />`
                      : html`<div class="input text-muted" style="flex:1;min-width:0;display:flex;align-items:center">New downloads are named <b style="margin-left:6px;font-weight:500">${s.pattern}</b></div>`}
                  <select class="input" style="width:170px;margin-left:-1px" value=${s.pattern} onChange=${e => this.onPattern(e.target.value)}>
                    ${PATTERNS.map(p => html`<option value=${p}>${p}</option>`)}
                  </select>
                </div>
              </div>
              <div style="display:flex;gap:14px;flex-wrap:wrap;align-items:flex-end">
                <div class="field"><label>Save as</label><div class="seg">${seg('video', 'Video')}${seg('audio', 'Audio only')}${seg('va', 'Video + audio file')}</div></div>
                <div class="field"><label>Quality</label>
                  <select class="input" style="width:130px;min-height:33px" value=${s.quality} onChange=${e => this.setDefaults({ quality: e.target.value })} disabled=${s.saveAs === 'audio'}>
                    ${QUALITIES.map(q => html`<option value=${q}>${q}</option>`)}</select></div>
                <div class="field"><label>Audio format</label>
                  <select class="input" style="width:160px;min-height:33px" value=${s.audioFmt} onChange=${e => this.setDefaults({ audioFmt: e.target.value })} disabled=${s.saveAs === 'video'}>
                    ${AUDIO_FMTS.map(([v, l]) => html`<option value=${v}>${l}</option>`)}</select></div>
                <div style="font-size:12px;max-width:240px;padding-bottom:2px" class="text-muted">${s.saveAs === 'video' ? 'Video only — no separate audio file.' : AUDIO_HINTS[s.audioFmt]}</div>
              </div>
              ${f ? html`
                <div style="display:flex;gap:8px;justify-content:flex-end">
                  <button class="btn btn-ghost" onClick=${() => this.setState({ paste: '', fetched: null, optsOpen: false })}>Cancel</button>
                  <button class="btn btn-secondary" onClick=${() => this.download(false)}>Add to queue</button>
                  <button class="btn btn-primary blueprint" onClick=${() => this.download(true)}><${Corners} />Download & play</button>
                </div>` : html`<div style="font-size:12px" class="text-muted">These become the defaults for every new link. Each queued item keeps the settings it was queued with.</div>`}
            </div>
          </div>`}
      </div>`;
  }

  renderBoard() {
    const s = this.state, d = s.data, vis = this.visible(), multi = s.sel.length > 1;
    const q = d.queue, qActive = q.filter(x => x.state === 'dl'), qWait = q.filter(x => x.state === 'wait').length, qFail = q.filter(x => x.state === 'fail').length;
    const showQBox = (qActive.length || qWait || qFail) && !s.menu;
    const card = x => {
      const sel = s.sel.includes(x.id), playing = x.id === s.playingId;
      const common = {
        onClick: e => { e.stopPropagation(); this.setState({ menu: null }); this.select(x.id, e); },
        onDblClick: () => this.play(x.id, true),
        onContextMenu: e => this.openMenu(e, x.id),
      };
      return s.mode === 'grid' ? html`
        <div class=${'tile' + (sel ? ' sel' : '')} ...${common} title=${x.title}>
          <div class="blueprint thumb" style="position:relative"><${Corners} />
            ${x.thumb ? html`<img src=${x.thumb} alt="" loading="lazy" />` : html`<span class="placeholder text-muted">THUMBNAIL</span>`}
            <span style="position:absolute;right:6px;bottom:6px;font-size:11px;padding:1px 5px;background:var(--screen-bg);color:var(--screen-fg);font-variant-numeric:tabular-nums">${fmt(x.len)}</span>
            <button class="btn btn-primary btn-icon play-btn" title="Play" style="position:absolute;left:6px;bottom:6px;width:26px;height:26px" onClick=${e => { e.stopPropagation(); this.play(x.id, false); }}>${Icon.play(11)}</button>
            ${playing && html`<span style="position:absolute;left:6px;top:6px;font-size:10px;letter-spacing:.08em;padding:2px 6px;background:var(--color-accent);color:var(--color-bg)">NOW PLAYING</span>`}
            ${x.isNew && html`<span style="position:absolute;right:6px;top:6px;font-size:10px;letter-spacing:.08em;padding:2px 6px;background:var(--color-bg);color:var(--color-accent-800);border:1px solid var(--color-accent)">NEW</span>`}
          </div>
          <div style="font-size:13.5px;font-weight:500;line-height:1.3;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden">${x.name}</div>
          <div style="display:flex;align-items:center;gap:6px;font-size:12px" class="text-muted"><span style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${x.channel}</span><span class="tag tag-neutral" style="margin-left:auto;padding:1px 6px;font-size:10px;flex:none">${x.type}</span></div>
        </div>` : html`
        <div class=${'lrow' + (sel ? ' sel' : '')} ...${common} style="display:grid;grid-template-columns:72px minmax(0,1fr) 180px 80px 60px 96px;align-items:center;padding:6px 8px;font-size:13px;border-bottom:1px solid color-mix(in srgb,var(--color-text) 7%,transparent)">
          <div class="thumb-sm">${x.thumb && html`<img src=${x.thumb} alt="" loading="lazy" />`}</div>
          <span style="font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;padding-right:12px">${x.name}${playing && html`<span style="color:var(--color-accent-700);font-weight:400"> · playing</span>`}${x.isNew && html`<span class="tag tag-outline" style="margin-left:8px;padding:0 5px;font-size:10px">NEW</span>`}</span>
          <span class="text-muted" style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis;padding-right:8px">${x.channel}</span>
          <span class="text-muted" style="font-size:11px">${x.type}</span>
          <span style="font-variant-numeric:tabular-nums">${fmt(x.len)}</span>
          <span class="text-muted" style="font-variant-numeric:tabular-nums">${x.saved}</span>
        </div>`;
    };
    const ab = (label, fn, extra = {}) => html`<button class="btn btn-secondary" style="font-size:13px;background:var(--color-bg)" onClick=${fn} ...${extra}>${label}</button>`;
    return html`
      <div style="flex:none;padding:8px 28px 12px;display:flex;align-items:center;gap:10px;flex-wrap:wrap">
        <h3 style="margin:0">${s.view === 'audio' ? 'Audio files' : 'Library'}</h3><span class="text-muted" style="font-size:13px">${vis.length} ${vis.length === 1 ? 'item' : 'items'}</span>
        ${s.channel && html`<span class="tag tag-outline" style="cursor:pointer" onClick=${() => this.setState({ channel: null })}>${s.channel} ×</span>`}
        <div style="margin-left:auto;display:flex;gap:8px;align-items:center">
          <input class="input" style="width:clamp(140px,20vw,220px);min-height:32px;font-size:13px" placeholder="Search title or channel" value=${s.q} onInput=${e => this.setState({ q: e.target.value })} />
          <div class="seg">
            <label class="seg-opt"><input type="radio" checked=${s.mode === 'grid'} onChange=${() => { this.setState({ mode: 'grid' }); this.saveSetting({ viewMode: 'grid' }); }} />Grid</label>
            <label class="seg-opt"><input type="radio" checked=${s.mode === 'list'} onChange=${() => { this.setState({ mode: 'list' }); this.saveSetting({ viewMode: 'list' }); }} />List</label>
          </div>
          <select class="input" style="width:120px;flex:none;min-height:33px;font-size:13px" value=${s.sort} onChange=${e => { this.setState({ sort: e.target.value }); this.saveSetting({ sort: e.target.value }); }}>
            <option value="newest">Newest</option><option value="title">Title A–Z</option><option value="length">Length</option><option value="channel">Channel</option>
          </select>
        </div>
      </div>
      ${s.sel.length > 0 && html`
        <div style="flex:none;margin:0 28px 12px;padding:6px 8px 6px 12px;display:flex;align-items:center;gap:6px;background:var(--color-accent-100);color:var(--color-accent-800);font-size:13px;flex-wrap:wrap">
          <span style="font-weight:500;margin-right:6px">${s.sel.length} selected</span>
          ${ab('Play', () => this.play(s.sel[0], true), { disabled: multi })}
          ${ab(html`Rename <span style="font-weight:400" class="text-muted">F2</span>`, () => this.openRename(s.sel[0]), { disabled: multi })}
          ${ab('Save audio', () => this.saveAudio(s.sel))}
          ${ab(multi ? '⟳ Re-download ' + s.sel.length : '⟳ Re-download', () => this.redl(s.sel))}
          ${ab('…with settings', () => this.openRedl(s.sel))}
          ${ab('Show in folder', () => this.showInFolder(s.sel[0]), { disabled: multi })}
          ${ab('Delete', () => this.setState({ dlg: 'del' }))}
          <span style="margin-left:auto;font-size:12px">Shift / Ctrl-click to select more · right-click for menu</span>
          <button class="btn btn-ghost" style="font-size:13px" onClick=${() => this.setState({ sel: [] })}>Clear</button>
        </div>`}
      <div style="flex:1;min-height:0;overflow:auto;padding:6px 28px 120px" onClick=${e => { if (e.target === e.currentTarget) this.setState({ sel: [] }); }}>
        ${s.mode === 'grid' ? html`
          <div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:22px 18px;align-content:start">${vis.map(card)}</div>` : html`
          <div style="display:grid;grid-template-columns:72px minmax(0,1fr) 180px 80px 60px 96px;font-size:11px;letter-spacing:.08em;padding:0 8px 6px;border-bottom:1px solid var(--color-divider)" class="text-muted"><span></span><span>NAME</span><span>CHANNEL</span><span>TYPE</span><span>LENGTH</span><span>SAVED</span></div>
          ${vis.map(card)}`}
        ${s.loaded && !vis.length && html`<div style="padding:60px 0;text-align:center" class="text-muted">${this.lib.length ? 'Nothing matches. Clear the search or channel filter.' : s.view === 'audio' ? 'No audio files yet — choose "Audio only" or "Video + audio file" when downloading, or use Save audio.' : 'Your library is empty. Paste a link above to download your first video.'}</div>`}
      </div>
      ${showQBox && html`
        <div class="blueprint elev-lg" style="position:absolute;right:24px;bottom:16px;width:340px;background:var(--color-bg);padding:12px 14px;display:flex;flex-direction:column;gap:10px;z-index:5">
          <${Corners} />
          <div style="display:flex;align-items:baseline;gap:8px"><h6 style="margin:0">Queue</h6><span class="text-muted" style="font-size:12px">${this.qSummary()}</span>
            <a style="margin-left:auto;font-size:12px" onClick=${() => this.qToggle()}>${d.queuePaused ? 'Resume' : 'Pause'}</a><a style="font-size:12px" onClick=${() => this.setState({ view: 'queue', playerOpen: false })}>Open</a></div>
          ${qActive.map(qActive => html`
            <div style="display:flex;flex-direction:column;gap:4px"><div style="display:flex;gap:8px;font-size:13px"><span style="font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis" title=${qActive.detail}>${qActive.title}</span><span style="margin-left:auto;font-variant-numeric:tabular-nums;white-space:nowrap" class="text-muted">${Math.floor(qActive.pct)}%</span></div>
              <div class="bar"><div style=${'width:' + pct(qActive.pct / 100)}></div></div></div>`)}
          <div style="display:flex;font-size:12.5px;gap:8px" class="text-muted"><span style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${qWait ? 'Next: ' + q.find(x => x.state === 'wait').title : 'Nothing waiting'}</span>
            ${qFail > 0 && html`<a style="margin-left:auto;white-space:nowrap" onClick=${() => call('queueRetry', {})}>${qFail} failed — retry</a>`}</div>
        </div>`}`;
  }

  qSummary() {
    const d = this.state.data, q = d.queue;
    const active = q.filter(x => x.state === 'dl').length, wait = q.filter(x => x.state === 'wait').length, done = q.filter(x => x.state === 'done').length;
    if (active) return (d.queuePaused ? 'Pausing' : active + ' downloading') + ' · ' + wait + ' waiting';
    if (wait) return wait + ' waiting' + (d.queuePaused ? ' · paused' : '');
    return done + ' done';
  }

  qToggle() { call(this.state.data.queuePaused ? 'queueResume' : 'queuePause').catch(e => this.fail(e)); }

  renderQueue() {
    const s = this.state, d = s.data, noFailed = !d.queue.some(x => x.state === 'fail');
    const MAP = {
      done: ['Done', 'tag-neutral'], skip: ['Skipped', 'tag-neutral'], dl: ['Downloading', 'tag-accent'],
      wait: [d.queuePaused ? 'Paused' : 'Waiting', 'tag-outline'], fail: ['Failed', 'tag-accent'],
    };
    const cols = 'grid-template-columns:36px minmax(0,1fr) 210px 260px 110px';
    return html`
      <div style="flex:none;padding:8px 28px 12px;display:flex;align-items:center;gap:10px">
        <h3 style="margin:0">Queue</h3><span class="text-muted" style="font-size:13px">${this.qSummary()}</span>
        <div style="margin-left:auto;display:flex;gap:6px;align-items:center">
          <span class="text-muted" style="font-size:12.5px;margin-right:2px" title="How many downloads run at the same time. More can be faster for playlists of short videos, but the site may start rate-limiting – 2 or 3 is a good balance.">Simultaneous downloads</span>
          <div class="seg" role="radiogroup" aria-label="Simultaneous downloads" style="margin-right:8px">
            ${Array.from({ length: (d.settings || {}).parallelMax || 6 }, (_, i) => i + 1).map(n => html`
              <label class="seg-opt" style="padding:7px 10px;font-variant-numeric:tabular-nums"><input type="radio" name="parallel" checked=${s.parallel === n} onChange=${() => this.setParallel(n)} />${n}</label>`)}
          </div>
          <button class="btn btn-primary" onClick=${() => this.qToggle()}>${d.queuePaused ? 'Start / Resume' : 'Pause'}</button>
          <button class="btn btn-secondary" onClick=${() => call('queueRetry', {})} disabled=${noFailed}>Retry failed</button>
          <button class="btn btn-secondary" onClick=${() => call('queueClear')}>Clear finished</button>
        </div>
      </div>
      <div style="padding:0 28px;font-size:12.5px" class="text-muted">Downloads ${s.parallel === 1 ? 'one at a time' : 'up to ' + s.parallel + ' at a time'}, each with the settings chosen when queued. Items already in the library are skipped. Keep pasting links while it runs — double-click a finished item to play it.</div>
      <div style="flex:1;min-height:0;overflow:auto;padding:14px 28px 28px">
        <div style=${'display:grid;' + cols + ';font-size:11px;letter-spacing:.08em;padding:0 8px 6px;border-bottom:1px solid var(--color-divider)'} class="text-muted"><span>#</span><span>TITLE</span><span>SETTINGS</span><span>STATUS</span><span></span></div>
        ${d.queue.map((x, i) => {
          const [label, cls] = MAP[x.state] || MAP.wait;
          const playable = x.resultId && this.item(x.resultId);
          return html`
            <div class="qrow" onDblClick=${() => playable && this.play(x.resultId, true)} style=${'display:grid;' + cols + ';align-items:center;padding:10px 8px;font-size:13px;border-bottom:1px solid color-mix(in srgb,var(--color-text) 7%,transparent)'}>
              <span class="text-muted" style="font-variant-numeric:tabular-nums">${i + 1}</span>
              <span style="font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;padding-right:12px" title=${x.title}>${x.title}</span>
              <span class="text-muted" style="font-size:12px">${x.opts}</span>
              <div style="display:flex;flex-direction:column;gap:5px;padding-right:16px;min-width:0">
                <span style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis" title=${x.detail}><span class=${'tag ' + cls}>${label}</span><span class="text-muted" style="font-size:12px;margin-left:8px">${x.state === 'wait' && x.detail === 'Paused' ? '' : x.detail}</span></span>
                ${x.state === 'dl' && html`<div class="bar"><div style=${'width:' + pct(x.pct / 100)}></div></div>`}
              </div>
              <div style="display:flex;gap:4px;justify-content:flex-end">
                ${x.state === 'fail' && html`<button class="btn btn-ghost" style="font-size:12px" onClick=${() => call('queueRetry', { id: x.id })}>Retry</button>`}
                ${playable && html`<button class="btn btn-ghost" style="font-size:12px" onClick=${() => this.play(x.resultId, true)}>Play</button>`}
                <button class="btn btn-ghost btn-icon" title=${x.state === 'dl' ? 'Cancel and remove' : 'Remove'} style="width:28px;height:28px;color:var(--color-text)" onClick=${() => call('queueRemove', { id: x.id })}>${Icon.x()}</button>
              </div>
            </div>`;
        })}
        ${!d.queue.length && html`<div style="padding:60px 0;text-align:center" class="text-muted">The queue is empty. Paste links above.</div>`}
      </div>`;
  }

  renderClips() {
    const clips = [];
    this.lib.forEach(x => x.files.filter(f => f.role === 'clip').forEach(f => clips.push({ entry: x, f })));
    clips.sort((a, b) => b.f.created - a.f.created);
    const cols = 'grid-template-columns:minmax(0,1fr) 220px 130px 130px 70px 170px';
    return html`
      <div style="flex:none;padding:26px 28px 12px;display:flex;align-items:center;gap:10px"><h3 style="margin:0">Clips</h3><span class="text-muted" style="font-size:13px">${clips.length} saved</span></div>
      <div style="flex:1;min-height:0;overflow:auto;padding:0 28px 28px">
        <div style=${'display:grid;' + cols + ';font-size:11px;letter-spacing:.08em;padding:0 8px 6px;border-bottom:1px solid var(--color-divider)'} class="text-muted"><span>FILE</span><span>FROM</span><span>RANGE</span><span>OUTPUT</span><span>SIZE</span><span></span></div>
        ${clips.map(({ entry, f }) => html`
          <div class="crow" style=${'display:grid;' + cols + ';align-items:center;padding:9px 8px;font-size:13px;border-bottom:1px solid color-mix(in srgb,var(--color-text) 7%,transparent)'}>
            <span style="font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;padding-right:12px" title=${f.name}>${f.name}</span>
            <span class="text-muted" style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis;padding-right:12px">${entry.name}</span>
            <span style="font-variant-numeric:tabular-nums">${fmt1(f.clipS)} → ${fmt1(f.clipE)}</span>
            <span class="text-muted">${f.fmt}</span>
            <span class="text-muted" style="font-variant-numeric:tabular-nums">${fmtSize(f.size)}</span>
            <div style="display:flex;gap:4px;justify-content:flex-end">
              <button class="btn btn-ghost" style="font-size:12px" onClick=${() => { this.play(entry.id, true); this.pendingSeek = f.clipS; this.setState({ clipS: f.clipS, clipE: f.clipE, previewEnd: f.clipE }); if (this.video && this.loadedSrc === entry.src) { this.video.currentTime = f.clipS; this.video.play().catch(() => {}); } }}>Play</button>
              <button class="btn btn-ghost" style="font-size:12px" onClick=${() => this.openRename(entry.id, f)}>Rename</button>
              <button class="btn btn-ghost" style="font-size:12px" onClick=${() => this.showInFolder(entry.id, f.name)}>Show</button>
            </div>
          </div>`)}
        ${!clips.length && html`<div style="padding:60px 0;text-align:center" class="text-muted">No clips yet. Open something in the player and press [ and ] to mark a range.</div>`}
      </div>`;
  }

  renderPlayer(it) {
    const s = this.state, len = this.len();
    const clipOk = s.clipS != null && s.clipE != null && s.clipE > s.clipS;
    const defClipName = clipOk ? it.name + ' [clip ' + mmss(s.clipS) + '-' + mmss(s.clipE) + ']' : '';
    const chapters = (it.chapters || []).map(c => ({ ...c, act: s.t >= c.s && s.t < c.e }));
    const sponsors = it.sponsors || [];
    const res = it.type === 'Audio' ? 'AUDIO · ' + (it.audioLabel || '') : it.width ? `VIDEO · ${it.width} × ${it.height}` : 'VIDEO';
    const cseg = (key, label) => html`<label class="seg-opt"><input type="radio" checked=${s.clipKind === key} onChange=${() => { this.setState({ clipKind: key }); this.saveSetting({ clipKind: key }); }} />${label}</label>`;
    const tabs = [['details', 'Details'], ['files', 'Files & clips'], ['chapters', 'Chapters'], ['sponsor', 'Sponsor'], ['desc', 'Description'], ['json', 'JSON']];
    const lane = 'position:relative;height:24px;border-bottom:1px solid var(--color-divider)';
    const fileRow = f => html`
      <div style="display:grid;grid-template-columns:minmax(0,1fr) auto auto;gap:0 8px;align-items:center;padding:7px 0;border-bottom:1px solid color-mix(in srgb,var(--color-text) 7%,transparent)">
        <div style="min-width:0"><div style="font-size:13px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis" title=${f.name}>${f.name}</div><div style="font-size:11.5px" class="text-muted">${f.what}</div></div>
        <span style="font-size:12px;font-variant-numeric:tabular-nums" class="text-muted">${fmtSize(f.size)}</span>
        <span style="display:flex">
          ${f.renamable && html`<button class="btn btn-ghost" style="font-size:12px" onClick=${() => this.openRename(it.id, f)}>Rename</button>`}
          <button class="btn btn-ghost btn-icon" title="Show in folder" style="width:26px;height:26px" onClick=${() => this.showInFolder(it.id, f.name)}>${Icon.folder()}</button>
        </span>
      </div>`;
    return html`
      <div style=${'flex:1;min-height:0;display:grid;grid-template-columns:minmax(0,1fr) ' + (s.theater ? '0px' : 'clamp(260px,28vw,360px)')}>
        <div style="display:flex;flex-direction:column;min-height:0;padding:14px 18px 14px;gap:11px">
          <div style="flex:none;display:flex;align-items:center;gap:12px">
            <button class="btn btn-secondary" onClick=${() => this.setState({ playerOpen: false, theater: false })}>${Icon.chevronLeft()}${s.view === 'audio' ? 'Audio files' : s.view === 'clips' ? 'Clips' : s.view === 'queue' ? 'Queue' : 'Library'}</button>
            <div style="min-width:0"><div style="font-family:var(--font-heading);font-weight:600;font-size:19px;line-height:1.1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis" title=${it.title}>${it.name}</div><div class="text-muted" style="font-size:12px">${it.channel}</div></div>
            <span class="tag tag-neutral" style="margin-left:auto;flex:none">${it.type}</span>
          </div>
          <div class="blueprint" ref=${el => { this.frameEl = el; }} onClick=${() => this.togglePlay()} onDblClick=${() => this.setState({ theater: !s.theater })}
            style="flex:1;min-height:160px;background:var(--screen-bg);background-image:repeating-linear-gradient(135deg,transparent 0 14px,rgba(255,255,255,.035) 14px 15px);display:grid;place-items:center;cursor:pointer;overflow:visible">
            <${Corners} />
            ${it.type === 'Audio' && it.thumb && html`<img src=${it.thumb} alt="" style="max-width:60%;max-height:80%;object-fit:contain;position:relative;z-index:2;border:1px solid var(--screen-line)" />`}
            ${!s.playing && html`<div class="over-video" style="width:64px;height:64px;border:1px solid var(--screen-dim);display:grid;place-items:center;color:var(--screen-hi);background:color-mix(in srgb,var(--screen-bg) 55%,transparent);position:absolute">${Icon.play(24)}</div>`}
            <span class="over-video" style="position:absolute;right:12px;top:10px;font:11px ui-monospace,Menlo,monospace;color:var(--screen-dim);letter-spacing:.1em;text-shadow:0 0 4px #000">${res}</span>
            ${s.toast && s.toastVideo && html`<span class="over-video" style="position:absolute;left:14px;bottom:12px;font-size:12px;padding:3px 8px;background:var(--color-accent-100);color:var(--color-accent-800)">${s.toast}</span>`}
          </div>
          <div style="flex:none;display:flex;align-items:center;gap:6px;flex-wrap:wrap;row-gap:4px">
            <button class="btn btn-primary btn-icon" style="width:34px;height:34px" onClick=${() => this.togglePlay()} title="Play / pause (Space)">${s.playing ? Icon.pause() : Icon.play()}</button>
            <button class="btn btn-ghost btn-icon" style="width:32px;height:32px;color:var(--color-text)" onClick=${() => this.stopPlayback()} title="Stop">${Icon.stop()}</button>
            <button class="btn btn-ghost btn-icon" style="width:32px;height:32px;color:var(--color-text)" onClick=${() => this.seek(s.t - 10)} title="−10 s (←)">${Icon.back()}</button>
            <button class="btn btn-ghost btn-icon" style="width:32px;height:32px;color:var(--color-text)" onClick=${() => this.seek(s.t + 10)} title="+10 s (→)">${Icon.fwd()}</button>
            <span style="font-family:var(--font-heading);font-size:22px;font-weight:600;font-variant-numeric:tabular-nums;margin-left:8px">${fmt1(s.t)}</span><span class="text-muted" style="font-size:13px;white-space:nowrap">/ ${fmt(len)}</span>
            <div style="margin-left:auto;display:flex;align-items:center;gap:8px;flex-wrap:wrap">
              <button class="btn btn-ghost btn-icon" style="width:32px;height:32px;color:var(--color-text)" onClick=${() => this.toggleMute()} title="Mute (M)">${Icon.volume(15, !s.muted && s.vol > 0)}</button>
              <input type="range" min="0" max="100" value=${s.muted ? 0 : s.vol} onInput=${e => this.setVol(+e.target.value)} style="width:80px" />
              <select class="input" style="width:68px;min-height:30px;font-size:12px;padding:2px 6px" value=${String(s.rate)} onChange=${e => this.setRate(+e.target.value)}>
                ${['0.5', '0.75', '1', '1.25', '1.5', '2'].map(r => html`<option value=${r}>${r}×</option>`)}</select>
              <button class="btn btn-ghost btn-icon" style="width:32px;height:32px;color:var(--color-text)" onClick=${() => this.setState({ theater: !s.theater })} title="Theater (F)">${Icon.expand()}</button>
            </div>
          </div>
          <div class="blueprint" style="flex:none;display:grid;grid-template-columns:76px minmax(0,1fr);font-size:11px">
            <${Corners} />
            <div style="display:flex;flex-direction:column;border-right:1px solid var(--color-divider);letter-spacing:.08em" class="text-muted">
              <div style="height:24px;padding:5px 10px;border-bottom:1px solid var(--color-divider)">TIME</div>
              <div style="height:24px;padding:5px 10px;border-bottom:1px solid var(--color-divider)">CHAPTERS</div>
              <div style="height:24px;padding:5px 10px;border-bottom:1px solid var(--color-divider)">SPONSOR</div>
              <div style="height:26px;padding:6px 10px;color:var(--color-accent-700)">CLIP</div>
            </div>
            <div onClick=${e => this.seekEv(e)} style="position:relative;cursor:pointer;overflow:hidden">
              <div style=${lane + ';background-image:repeating-linear-gradient(90deg,var(--color-divider) 0 1px,transparent 1px 5%);font-variant-numeric:tabular-nums'} class="text-muted">
                ${[0, 0.25, 0.5, 0.75].map(f => html`<span style=${'position:absolute;top:5px;left:' + pct(f) + ';padding-left:4px'}>${fmt(f * len)}</span>`)}
                <div style=${'position:absolute;left:0;top:0;height:2px;width:' + pct(s.t / len) + ';background:var(--color-accent)'}></div>
              </div>
              <div style=${lane + ';font-size:11.5px'}>
                ${chapters.map(c => html`<div title=${c.name} style=${'position:absolute;top:0;bottom:0;left:' + pct(c.s / len) + ';width:' + pct((c.e - c.s) / len) + ';border-right:1px solid var(--color-divider);padding:5px 6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;background:' + (c.act ? 'var(--color-accent-100)' : 'transparent') + ';color:' + (c.act ? 'var(--color-accent-800)' : 'var(--color-text)')}>${c.name}</div>`)}
                ${!chapters.length && html`<span style="position:absolute;top:5px;left:8px" class="text-muted">No chapters</span>`}
              </div>
              <div style=${lane}>
                ${sponsors.map(sp => html`<div title=${sp.cat} style=${'position:absolute;top:5px;bottom:5px;left:' + pct(sp.s / len) + ';width:' + pct((sp.e - sp.s) / len) + ';background:repeating-linear-gradient(135deg,var(--color-neutral-700) 0 2px,transparent 2px 5px);border:1px solid var(--color-neutral-700)'}></div>`)}
                ${sponsors.length > 0 && html`<span style=${'position:absolute;top:5px;left:' + pct(sponsors[0].e / len) + ';padding-left:6px;white-space:nowrap'} class="text-muted">${sponsors[0].cat || 'sponsor'} · ${s.autoSkip ? 'auto-skip' : 'plays'}</span>`}
                ${!sponsors.length && html`<span style="position:absolute;top:5px;left:8px" class="text-muted">No SponsorBlock segments</span>`}
              </div>
              <div style="position:relative;height:26px">
                ${clipOk && html`<div style=${'position:absolute;left:' + pct(s.clipS / len) + ';width:' + pct((s.clipE - s.clipS) / len) + ';top:4px;bottom:4px;background:color-mix(in srgb,var(--color-accent) 25%,transparent);border-left:3px solid var(--color-accent-700);border-right:3px solid var(--color-accent-700)'}></div>
                  <span style=${'position:absolute;left:' + pct(Math.min(s.clipE / len, 0.7)) + ';top:6px;padding-left:8px;color:var(--color-accent-700);font-variant-numeric:tabular-nums;white-space:nowrap'}>${fmt1(s.clipS)} → ${fmt1(s.clipE)} · ${(s.clipE - s.clipS).toFixed(1)} s</span>`}
                ${s.clipS == null && s.clipE == null && html`<span style="position:absolute;left:8px;top:6px" class="text-muted">Press [ and ] (or the buttons below) to mark a range at the playhead</span>`}
                ${(s.clipS != null) !== (s.clipE != null) && html`<div style=${'position:absolute;top:4px;bottom:4px;width:3px;background:var(--color-accent-700);left:' + pct((s.clipS ?? s.clipE) / len)}></div>`}
              </div>
              <div style=${'position:absolute;top:0;bottom:0;left:' + pct(s.t / len) + ';width:1px;background:var(--color-accent-800);pointer-events:none'}></div>
            </div>
          </div>
          <div style="flex:none;display:flex;gap:6px;align-items:center;flex-wrap:wrap">
            <button class="btn btn-secondary" style="font-size:13px" onClick=${() => this.setState({ clipS: s.t, clipName: null })}>⟦ Set start</button>
            <input class="input" style="width:78px;min-height:32px;font-variant-numeric:tabular-nums;text-align:center;font-size:13px" value=${s.clipS == null ? '' : fmt1(s.clipS)} onChange=${e => { const v = parseT(e.target.value); if (v != null) this.setState({ clipS: Math.min(v, len), clipName: null }); }} placeholder="0:00.0" />
            <span class="text-muted">→</span>
            <input class="input" style="width:78px;min-height:32px;font-variant-numeric:tabular-nums;text-align:center;font-size:13px" value=${s.clipE == null ? '' : fmt1(s.clipE)} onChange=${e => { const v = parseT(e.target.value); if (v != null) this.setState({ clipE: Math.min(v, len), clipName: null }); }} placeholder="0:00.0" />
            <button class="btn btn-secondary" style="font-size:13px" onClick=${() => this.setState({ clipE: s.t, clipName: null })}>Set end ⟧</button>
            <button class="btn btn-ghost" style="font-size:13px" onClick=${() => { if (clipOk && this.video) { this.video.currentTime = s.clipS; this.setState({ previewEnd: s.clipE }); this.video.play().catch(() => {}); } }} disabled=${!clipOk}>Preview</button>
            <input class="input" style="flex:1;min-width:120px;min-height:32px;font-size:13px" value=${s.clipName ?? defClipName} onInput=${e => this.setState({ clipName: e.target.value })} placeholder="Clip file name" />
            <div class="seg">${it.type !== 'Audio' && cseg('mp4', 'MP4 precise')}${it.type !== 'Audio' && cseg('fast', 'Fast copy')}${cseg('audio', 'Audio')}</div>
            <button class="btn btn-primary" style="height:33px" onClick=${() => this.saveClip()} disabled=${!clipOk || !!s.task}>Save clip</button>
          </div>
        </div>
        ${!s.theater && html`
          <div style="border-left:1px solid var(--color-divider);display:flex;flex-direction:column;min-height:0;padding:14px 16px 12px;gap:12px">
            <div style="display:flex;border-bottom:1px solid var(--color-divider);font-size:12.5px;white-space:nowrap;overflow-x:auto;flex:none">
              ${tabs.map(([key, label]) => html`<span class=${'tab' + (s.tab === key ? ' on' : '')} onClick=${() => this.openTab(key)}>${label}</span>`)}
            </div>
            <div style="flex:1;min-height:0;overflow:auto;display:flex;flex-direction:column;gap:12px">
              ${s.tab === 'details' && html`
                <div class="blueprint" style="display:grid;grid-template-columns:1fr 1fr;margin:6px"><${Corners} />
                  ${[['LENGTH', fmt(it.len)], ['SAVED', it.saved], ['QUALITY', it.quality], ['AUDIO', it.audioLabel], ['SIZE', fmtSize(it.size)], ['SPONSOR', sponsors.length ? sponsors.length + (sponsors.length > 1 ? ' segments' : ' segment') : 'None']].map(([k, v]) => html`
                    <div style="padding:8px 10px;border-right:1px solid var(--color-divider);border-bottom:1px solid var(--color-divider);margin:0 -1px -1px 0"><div style="font-size:10px;letter-spacing:.1em" class="text-muted">${k}</div><div style="font-size:14px;font-weight:500;font-variant-numeric:tabular-nums">${v}</div></div>`)}
                </div>
                <div style="font-size:13px;line-height:1.4"><div style="font-weight:500">${it.title}</div><div class="text-muted" style="font-size:12px">${it.channel}${it.uploaded ? ' · uploaded ' + it.uploaded : ''}${it.views ? ' · ' + it.views.toLocaleString() + ' views' : ''}</div></div>
                <a style="font-size:12px;word-break:break-all" onClick=${() => call('openUrl', { url: it.url })}>${it.url}</a>
                <div style="display:flex;gap:6px;flex-wrap:wrap">
                  <button class="btn btn-secondary" style="font-size:13px" onClick=${() => this.openRename(it.id)}>Rename <span style="font-weight:400" class="text-muted">F2</span></button>
                  <button class="btn btn-secondary" style="font-size:13px" onClick=${() => this.saveAudio([it.id])}>Save audio</button>
                  <button class="btn btn-secondary" style="font-size:13px" onClick=${() => this.openRedl([it.id])}>Re-download</button>
                  <button class="btn btn-ghost" style="font-size:13px" onClick=${() => this.showInFolder(it.id)}>Show in folder</button>
                </div>`}
              ${s.tab === 'files' && html`<div>${it.files.map(fileRow)}</div>`}
              ${s.tab === 'chapters' && html`
                <div>${chapters.map(c => html`
                  <div style="display:grid;grid-template-columns:52px minmax(0,1fr) auto;align-items:center;padding:6px 0;border-bottom:1px solid color-mix(in srgb,var(--color-text) 7%,transparent);font-size:13px">
                    <a onClick=${() => this.seek(c.s)} style="font-variant-numeric:tabular-nums">${fmt(c.s)}</a>
                    <span style=${'color:' + (c.act ? 'var(--color-accent-800)' : 'var(--color-text)')}>${c.name}</span>
                    <button class="btn btn-ghost" style="font-size:12px" onClick=${() => this.setState({ clipS: c.s, clipE: c.e, clipName: it.name + ' - ' + c.name })}>Set clip</button>
                  </div>`)}
                  ${!chapters.length && html`<div class="text-muted" style="font-size:13px">This video has no chapters.</div>`}</div>`}
              ${s.tab === 'sponsor' && html`
                <label class="radio" style="font-size:13px" onClick=${e => { e.preventDefault(); this.setState({ autoSkip: !s.autoSkip }); this.saveSetting({ autoSkip: !s.autoSkip }); }}><span class="dot" style=${'border-radius:0;border-color:var(--color-accent);background:' + (s.autoSkip ? 'var(--color-accent)' : 'transparent')}></span>Skip sponsor segments automatically</label>
                <div>${sponsors.map(sp => html`<div style="display:grid;grid-template-columns:60px 60px minmax(0,1fr);padding:6px 0;border-bottom:1px solid color-mix(in srgb,var(--color-text) 7%,transparent);font-size:13px;font-variant-numeric:tabular-nums"><a onClick=${() => this.seek(Math.max(0, sp.s - 2))}>${fmt(sp.s)}</a><span>${fmt(sp.e)}</span><span>${sp.cat || 'sponsor'}</span></div>`)}
                  ${!sponsors.length && html`<div class="text-muted" style="font-size:13px">No segments for this video.</div>`}</div>
                <div style="font-size:12px" class="text-muted">YouTube's inserted ads are never in the downloaded file. This list is from SponsorBlock (community data) and covers sponsor reads baked into the video itself.</div>`}
              ${s.tab === 'desc' && html`<div class="selectable" style="font-size:13px;line-height:1.55;white-space:pre-wrap;word-break:break-word">${it.desc || 'No description.'}</div>`}
              ${s.tab === 'json' && html`<pre style="margin:0;font:11.5px/1.5 ui-monospace,Menlo,monospace;white-space:pre-wrap;word-break:break-all">${s.rawJson || 'Loading…'}</pre>`}
            </div>
          </div>`}
      </div>`;
  }

  renderMini(it) {
    const s = this.state, len = this.len();
    return html`
      <div style="height:64px;border-top:1px solid var(--color-divider);display:flex;align-items:center;gap:14px;padding:0 20px">
        <div ref=${el => { this.miniThumbEl = el; }} onClick=${() => this.setState({ playerOpen: true })} style="position:relative;width:72px;height:40px;background:var(--screen-bg);flex:none;cursor:pointer;overflow:hidden">
          ${it.type === 'Audio' && it.thumb && html`<img src=${it.thumb} alt="" style="width:100%;height:100%;object-fit:cover" />`}
        </div>
        <div onClick=${() => this.setState({ playerOpen: true })} style="width:220px;min-width:0;cursor:pointer"><div style="font-size:13.5px;font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${it.name}</div><div class="text-muted" style="font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${it.channel}</div></div>
        <button class="btn btn-ghost btn-icon" style="width:32px;height:32px;color:var(--color-text)" onClick=${() => this.seek(s.t - 10)}>${Icon.back()}</button>
        <button class="btn btn-primary btn-icon" style="width:34px;height:34px" onClick=${() => this.togglePlay()}>${s.playing ? Icon.pause() : Icon.play()}</button>
        <button class="btn btn-ghost btn-icon" style="width:32px;height:32px;color:var(--color-text)" onClick=${() => this.seek(s.t + 10)}>${Icon.fwd()}</button>
        <span style="font-size:12px;font-variant-numeric:tabular-nums;width:44px;text-align:right" class="text-muted">${fmt(s.t)}</span>
        <div onClick=${e => this.seekEv(e)} style="flex:1;height:14px;display:flex;align-items:center;cursor:pointer;position:relative">
          <div style="width:100%;height:2px;background:var(--color-neutral-300)"><div style=${'width:' + pct(s.t / len) + ';height:2px;background:var(--color-accent)'}></div></div>
          ${(it.sponsors || []).map(sp => html`<div style=${'position:absolute;top:5px;height:4px;left:' + pct(sp.s / len) + ';width:' + pct((sp.e - sp.s) / len) + ';background:var(--color-neutral-700)'}></div>`)}
        </div>
        <span style="font-size:12px;font-variant-numeric:tabular-nums" class="text-muted">${fmt(len)}</span>
        <button class="btn btn-secondary" style="font-size:13px" onClick=${() => this.setState({ playerOpen: true, clipS: s.clipS ?? s.t })}>${Icon.scissors(13)}Clip</button>
        <button class="btn btn-secondary" style="font-size:13px" onClick=${() => this.setState({ playerOpen: true })}>${Icon.expand(13)}Open player</button>
      </div>`;
  }

  renderMenu() {
    const s = this.state, ids = this.ctxIds(s.menu.id), n = ids.length, m = s.menu.id;
    const items = [
      { label: 'Play', key: 'Enter', on: () => this.play(m, true), dis: n > 1 },
      { label: 'Play in mini player', on: () => this.play(m, false), dis: n > 1 },
      { label: 'Rename…', key: 'F2', on: () => this.openRename(m), sep: true, dis: n > 1 },
      { label: n > 1 ? 'Save audio for ' + n + ' items' : 'Save audio', on: () => this.saveAudio(ids) },
      { label: n > 1 ? '⟳ Re-download ' + n + ' items' : '⟳ Re-download', on: () => this.redl(ids), sep: true },
      { label: '⟳ Re-download with settings…', on: () => this.openRedl(ids) },
      { label: 'Details…', key: 'Alt+Enter', on: () => this.openInfo(m), sep: true, dis: n > 1 },
      { label: 'Show in folder', on: () => this.showInFolder(m), dis: n > 1 },
      { label: 'Open on YouTube', on: () => { this.setState({ menu: null }); const x = this.item(m); x && call('openUrl', { url: x.url }); }, dis: n > 1 },
      { label: n > 1 ? 'Delete ' + n + ' items' : 'Delete', key: 'Del', on: () => this.setState({ dlg: 'del', menu: null }), sep: true },
    ];
    return html`
      <div class="blueprint elev-lg" onClick=${e => e.stopPropagation()} style=${'position:absolute;left:' + s.menu.x + ';top:' + s.menu.y + ';width:240px;background:var(--color-bg);padding:4px 0;z-index:20;display:flex;flex-direction:column;font-size:13px'}>
        <${Corners} />
        ${items.map(o => html`<div class=${'menu-item' + (o.dis ? ' disabled' : '')} onClick=${() => { this.setState({ menu: null }); o.on(); }} style=${'border-top:' + (o.sep ? '1px solid var(--color-divider)' : 'none')}>${o.label}<span class="text-muted" style="margin-left:auto">${o.key || ''}</span></div>`)}
      </div>`;
  }

  renderDialogs() {
    const s = this.state;
    const close = () => this.setState({ dlg: null, dlgBusy: false });
    const shell = (body, width = 440) => html`
      <div class="dialog-backdrop" style="position:absolute" onClick=${close}>
        <div class="dialog blueprint" style=${'background:var(--color-bg);width:min(' + width + 'px,100%)'} onClick=${e => e.stopPropagation()}><${Corners} />${body}</div>
      </div>`;
    const seg = (cur, set, key, label) => html`<label class="seg-opt"><input type="radio" checked=${cur === key} onChange=${() => set(key)} />${label}</label>`;

    if (s.alert) return html`
      <div class="dialog-backdrop" style="position:absolute" onClick=${() => this.setState({ alert: null })}>
        <div class="dialog blueprint" style="background:var(--color-bg);width:min(520px,100%)" onClick=${e => e.stopPropagation()}><${Corners} />
          <div class="dialog-title">Something went wrong</div>
          <pre class="selectable" style="margin:0;white-space:pre-wrap;font:12.5px/1.5 ui-monospace,Menlo,monospace;max-height:50vh;overflow:auto">${s.alert}</pre>
          <div class="dialog-body" style="font-size:12.5px">If downloads keep failing after a YouTube change, try "Update yt-dlp" in the sidebar.</div>
          <div class="dialog-actions"><button class="btn btn-primary" onClick=${() => this.setState({ alert: null })}>OK</button></div>
        </div></div>`;

    switch (s.dlg) {
      case 'info': {
        const it = this.item(s.infoId);
        if (!it) return null;
        const cell = (k, v) => html`<div style="padding:8px 10px;border-right:1px solid var(--color-divider);border-bottom:1px solid var(--color-divider);margin:0 -1px -1px 0;min-width:0"><div style="font-size:10px;letter-spacing:.1em" class="text-muted">${k}</div><div style="font-size:14px;font-weight:500;font-variant-numeric:tabular-nums;white-space:nowrap;overflow:hidden;text-overflow:ellipsis" title=${v}>${v || '—'}</div></div>`;
        const sp = (it.sponsors || []).length, ch = (it.chapters || []).length;
        return shell(html`
          <div style="display:flex;gap:14px;align-items:flex-start">
            ${it.thumb && html`<img src=${it.thumb} alt="" style="width:128px;aspect-ratio:16/9;object-fit:cover;flex:none;border:1px solid var(--color-divider)" />`}
            <div style="min-width:0">
              <div class="card-kicker">${it.type === 'Audio' ? 'AUDIO' : 'VIDEO'} · DETAILS</div>
              <div class="dialog-title selectable" style="line-height:1.15;word-break:break-word">${it.name}</div>
              <div class="text-muted" style="font-size:12.5px;margin-top:3px">${it.channel}${it.uploaded ? ' · uploaded ' + it.uploaded : ''}${it.views ? ' · ' + it.views.toLocaleString() + ' views' : ''}</div>
            </div>
          </div>
          <div class="dialog-scroll" style="display:flex;flex-direction:column;gap:14px;padding:6px">
            <div class="blueprint" style="display:grid;grid-template-columns:repeat(4,minmax(0,1fr))"><${Corners} />
              ${cell('LENGTH', fmt(it.len))}${cell('TYPE', it.type)}${cell('QUALITY', it.quality)}${cell('RESOLUTION', it.width ? it.width + ' × ' + it.height : '')}
              ${cell('AUDIO', it.audioLabel)}${cell('SIZE', fmtSize(it.size))}${cell('SAVED', it.saved)}${cell('CHAPTERS · SPONSOR', (ch || 'None') + ' · ' + (sp || 'None'))}
            </div>
            <div style="display:grid;grid-template-columns:auto minmax(0,1fr);gap:4px 14px;font-size:13px">
              ${it.title !== it.name && html`<span class="text-muted">Original title</span><span class="selectable">${it.title}</span>`}
              <span class="text-muted">Link</span><a style="word-break:break-all" onClick=${() => call('openUrl', { url: it.url })}>${it.url}</a>
              <span class="text-muted">Video ID</span><span class="selectable" style="font-family:ui-monospace,Menlo,monospace;font-size:12.5px">${it.id}</span>
            </div>
            <div>
              <h6 style="margin:0 0 4px" class="text-muted">Files</h6>
              ${it.files.map(f => html`
                <div style="display:grid;grid-template-columns:minmax(0,1fr) auto auto;gap:0 10px;align-items:center;padding:6px 0;border-bottom:1px solid color-mix(in srgb,var(--color-text) 7%,transparent)">
                  <div style="min-width:0"><div class="selectable" style="font-size:13px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis" title=${f.name}>${f.name}</div><div style="font-size:11.5px" class="text-muted">${f.what}</div></div>
                  <span style="font-size:12px;font-variant-numeric:tabular-nums" class="text-muted">${fmtSize(f.size)}</span>
                  <button class="btn btn-ghost btn-icon" title="Show in folder" style="width:26px;height:26px" onClick=${() => this.showInFolder(it.id, f.name)}>${Icon.folder()}</button>
                </div>`)}
            </div>
            ${it.desc && html`<div><h6 style="margin:0 0 4px" class="text-muted">Description</h6><div class="selectable" style="font-size:12.5px;line-height:1.5;white-space:pre-wrap;word-break:break-word">${it.desc}</div></div>`}
          </div>
          <div class="dialog-actions" style="flex-wrap:wrap">
            <button class="btn btn-secondary" style="margin-right:auto" onClick=${() => this.copy(this.detailsText(it), 'Details')}>${s.copied === 'Details' ? 'Copied ✓' : 'Copy details'}</button>
            <button class="btn btn-ghost" onClick=${() => this.copy(it.url, 'Link')}>${s.copied === 'Link' ? 'Copied ✓' : 'Copy link'}</button>
            <button class="btn btn-ghost" onClick=${() => this.showInFolder(it.id)}>Show in folder</button>
            <button class="btn btn-primary" onClick=${close}>Close</button>
          </div>`, 620);
      }
      case 'rename': {
        const isFile = s.renameTarget && s.renameTarget.file;
        return shell(html`
          <div class="dialog-title">${isFile ? 'Rename file' : 'Rename'}</div>
          <input class="input" value=${s.renameVal} ref=${el => el && !this.focusedRename && (this.focusedRename = true, setTimeout(() => { el.focus(); el.select(); }, 0))}
            onInput=${e => this.setState({ renameVal: e.target.value })} onKeyDown=${e => e.key === 'Enter' && this.applyRename()} />
          <div class="dialog-body" style="font-size:13px">${isFile ? 'Gives this file its own name.' : 'Renames every file that follows this name — video, audio track, exports, clips and thumbnail.'}</div>
          <div class="dialog-actions"><button class="btn btn-ghost" onClick=${() => { this.focusedRename = false; close(); }}>Cancel</button><button class="btn btn-primary" disabled=${s.dlgBusy} onClick=${() => { this.focusedRename = false; this.applyRename(); }}>Rename</button></div>`);
      }
      case 'del': {
        const n = s.sel.length, first = this.item(s.sel[0]);
        return shell(html`
          <div class="dialog-title">${n > 1 ? 'Delete ' + n + ' items?' : 'Delete “' + (first ? first.name : '') + '”?'}</div>
          <div class="dialog-body">The video, its audio files, clips and thumbnail are moved to the Recycle Bin.</div>
          <div class="dialog-actions"><button class="btn btn-ghost" onClick=${close}>Cancel</button><button class="btn btn-primary" disabled=${s.dlgBusy} onClick=${() => this.applyDelete()}>Delete</button></div>`);
      }
      case 'redl': {
        const n = s.redlIds.length;
        return shell(html`
          <div class="dialog-title">${n > 1 ? 'Re-download ' + n + ' items' : 'Re-download with settings'}</div>
          ${n === 1 && html`<div class="text-muted" style="font-size:12.5px;margin-top:-6px">${(this.item(s.redlIds[0]) || {}).name}</div>`}
          <div class="field"><label>Save as</label><div class="seg">
            ${seg(s.redlSaveAs, v => this.setState({ redlSaveAs: v }), 'video', 'Video')}${seg(s.redlSaveAs, v => this.setState({ redlSaveAs: v }), 'audio', 'Audio only')}${seg(s.redlSaveAs, v => this.setState({ redlSaveAs: v }), 'va', 'Video + audio file')}</div></div>
          <div style="display:flex;gap:12px">
            <div class="field" style="flex:1"><label>Quality</label><select class="input" value=${s.redlQuality} disabled=${s.redlSaveAs === 'audio'} onChange=${e => this.setState({ redlQuality: e.target.value })}>${QUALITIES.map(q => html`<option value=${q}>${q}</option>`)}</select></div>
            <div class="field" style="flex:1"><label>Audio format</label><select class="input" value=${s.redlFmt} disabled=${s.redlSaveAs === 'video'} onChange=${e => this.setState({ redlFmt: e.target.value })}>${AUDIO_FMTS.map(([v, l]) => html`<option value=${v}>${l}</option>`)}</select></div>
          </div>
          <label class="radio" style="font-size:13px" onClick=${e => { e.preventDefault(); this.setState({ keep: !s.keep }); }}><span class="dot" style=${'border-radius:0;border-color:var(--color-accent);background:' + (s.keep ? 'var(--color-accent)' : 'transparent')}></span>Keep saved audio files and clips</label>
          <div class="dialog-body" style="font-size:12.5px">Downloads under a temporary name first — the old files are only replaced once it finishes.${n > 1 ? ' Items go through the download queue.' : ''}</div>
          <div class="dialog-actions"><button class="btn btn-ghost" onClick=${close}>Cancel</button><button class="btn btn-primary" onClick=${() => this.applyRedl()}>Re-download</button></div>`, 480);
      }
      case 'multi': {
        const k = linkKind(s.multiText), urls = k.urls;
        const lists = urls.filter(u => linkKind(u).kind === 'playlist').length;
        return shell(html`
          <div class="dialog-title">Multiple links</div>
          <textarea class="input" style="min-height:170px;font-size:13px" spellcheck="false" value=${s.multiText} onInput=${e => this.setState({ multiText: e.target.value })} placeholder="One link per line, or separated by spaces / commas"></textarea>
          <div class="dialog-body" style="font-size:12.5px">${urls.length} ${urls.length === 1 ? 'link' : 'links'}${lists ? ' (' + lists + ' playlist' + (lists > 1 ? 's' : '') + ' — you pick videos next)' : ''} — queued with the current Save-as, quality and audio format.</div>
          <div class="dialog-actions"><button class="btn btn-ghost" onClick=${close}>Cancel</button>
            <button class="btn btn-primary" disabled=${!urls.length} onClick=${async () => {
              const singles = urls.filter(u => linkKind(u).kind !== 'playlist'), pls = urls.filter(u => linkKind(u).kind === 'playlist');
              this.setState({ dlg: null, paste: '' });
              if (singles.length) await this.enqueueUrls(singles).catch(() => {});
              if (pls.length) this.openPlaylist(pls[0], pls.slice(1));
            }}>Add to queue</button></div>`, 520);
      }
      case 'playlist': {
        const pl = s.pl || { items: [] }, checked = pl.items.filter(p => p.checked);
        const setItems = items => this.setState({ pl: { ...pl, items } });
        const next = () => { const rest = s.pendingPlaylists; if (rest.length) this.openPlaylist(rest[0], rest.slice(1)); };
        return html`
          <div class="dialog-backdrop" style="position:absolute" onClick=${close}>
            <div class="dialog blueprint" style="background:var(--color-bg);width:min(640px,100%)" onClick=${e => e.stopPropagation()}><${Corners} />
              <div><div class="card-kicker">PLAYLIST${pl.channel ? ' · ' + pl.channel.toUpperCase() : ''}</div><div class="dialog-title">${pl.loading ? 'Reading playlist…' : pl.title || 'Pick videos to download'}</div></div>
              ${pl.loading ? html`<div class="indeterminate"><div></div></div>` : html`
                <div style="display:flex;gap:10px;font-size:12.5px"><a onClick=${() => setItems(pl.items.map(p => ({ ...p, checked: p.note !== 'Private' })))}>Select all</a><a onClick=${() => setItems(pl.items.map(p => ({ ...p, checked: false })))}>None</a><a onClick=${() => setItems(pl.items.map(p => ({ ...p, checked: !p.note })))}>Only new</a>
                  <span class="text-muted" style="margin-left:auto">${checked.length} of ${pl.items.length} · ${fmt(checked.reduce((a, p) => a + (p.len || 0), 0))} total</span></div>
                <div class="dialog-scroll" style="border-top:1px solid var(--color-divider)">
                  ${pl.items.map((p, i) => html`
                    <label style=${'display:grid;grid-template-columns:24px 34px minmax(0,1fr) auto 50px;align-items:center;padding:7px 0;border-bottom:1px solid color-mix(in srgb,var(--color-text) 7%,transparent);font-size:13px;cursor:pointer;opacity:' + (p.note === 'Private' ? 0.45 : 1)}>
                      <input type="checkbox" checked=${p.checked} disabled=${p.note === 'Private'} onChange=${() => setItems(pl.items.map((y, j) => j === i ? { ...y, checked: !y.checked } : y))} />
                      <span class="text-muted" style="font-variant-numeric:tabular-nums">${p.n}</span>
                      <span style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis" title=${p.title}>${p.title}</span>
                      <span style="padding:0 8px">${p.note && html`<span class="tag tag-neutral" style="font-size:10px;padding:1px 6px">${p.note}</span>`}</span>
                      <span style="font-variant-numeric:tabular-nums;text-align:right">${p.len ? fmt(p.len) : '—'}</span>
                    </label>`)}
                </div>
                <label class="radio" style="font-size:13px" onClick=${e => { e.preventDefault(); this.setState({ plNum: !s.plNum }); this.saveSetting({ numberPlaylist: !s.plNum }); }}><span class="dot" style=${'border-radius:0;border-color:var(--color-accent);background:' + (s.plNum ? 'var(--color-accent)' : 'transparent')}></span>Number files by playlist position (${pl.items[0] ? pl.items[0].n : '01'} - Title)</label>`}
              <div class="dialog-actions"><button class="btn btn-ghost" onClick=${() => { close(); next(); }}>Cancel</button>
                <button class="btn btn-primary" disabled=${pl.loading || !checked.length} onClick=${async () => {
                  this.setState({ dlg: null, paste: '' });
                  try {
                    const n = await call('enqueue', { ...this.opts(), items: checked.map(p => ({ url: p.url, id: p.id, title: p.title, prefix: s.plNum ? p.n + ' - ' : null })) });
                    this.toast(n + (n === 1 ? ' video' : ' videos') + ' added to the queue');
                  } catch (e) { this.fail(e); }
                  next();
                }}>${'Queue ' + checked.length + (checked.length === 1 ? ' video' : ' videos')}</button></div>
            </div>
          </div>`;
      }
      default: return null;
    }
  }

  renderSetup() {
    const s = this.state;
    return html`
      <div class="dialog-backdrop" style="position:absolute">
        <div class="dialog blueprint" style="background:var(--color-bg);width:min(480px,100%)"><${Corners} />
          <div><div class="card-kicker">ONE-TIME SETUP</div><div class="dialog-title">ReelKeep needs ffmpeg</div></div>
          <div class="dialog-body" style="font-size:13.5px">ffmpeg is a free media tool. ReelKeep uses it to join video and audio into one file, save audio (M4A / MP3 / FLAC / WAV) and cut clips. It's about 140 MB and is downloaded once into ReelKeep's own folder.</div>
          ${s.task && html`<div style="display:flex;flex-direction:column;gap:5px;font-size:12.5px">${s.task.label}<div class="bar"><div style=${'width:' + pct((s.task.pct || 0) / 100)}></div></div></div>`}
          <div class="dialog-actions">
            <button class="btn btn-ghost" disabled=${!!s.task} onClick=${() => this.setState({ skippedSetup: true })}>Later</button>
            <button class="btn btn-primary" disabled=${!!s.task} onClick=${() => call('installFfmpeg').catch(e => this.fail(e))}>${s.task ? 'Installing…' : 'Install ffmpeg'}</button>
          </div>
        </div>
      </div>`;
  }
}

render(html`<${App} />`, document.getElementById('app'));
