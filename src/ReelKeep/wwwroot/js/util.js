// Small formatting / parsing helpers and the Lucide-style icon set (stroke 1.5, per the design system).
import { html } from './vendor/preact-htm.js';

export const fmt = s => {
  s = Math.max(0, Math.floor(s || 0));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}`;
};

export const fmt1 = s => {
  s = Math.max(0, s || 0);
  const m = Math.floor(s / 60), r = (s - m * 60).toFixed(1);
  return m + ':' + (r < 10 ? '0' : '') + r;
};

export const mmss = s => String(Math.floor(s / 60)).padStart(2, '0') + 'm' + String(Math.floor(s % 60)).padStart(2, '0') + 's';

/** "83", "1:23", "1:23.5", "1:02:03" -> seconds (null if it isn't a time). */
export const parseT = str => {
  const m = String(str).trim().match(/^(?:(\d+):)?(?:(\d+):)?(\d+(?:\.\d+)?)$/);
  if (!m) return null;
  const parts = [m[1], m[2], m[3]].filter(x => x !== undefined).map(Number);
  return parts.reduce((acc, v) => acc * 60 + v, 0);
};

export const pct = f => (Math.max(0, Math.min(1, f || 0)) * 100).toFixed(2) + '%';

export const fmtSize = b => {
  if (!b) return '—';
  if (b >= 1 << 30) return (b / (1 << 30)).toFixed(2) + ' GB';
  if (b >= 1 << 20) return (b / (1 << 20)).toFixed(1) + ' MB';
  return Math.max(1, Math.round(b / 1024)) + ' KB';
};

/** What's been pasted: one video, a playlist/channel, or several links. */
export function linkKind(v) {
  const urls = String(v || '').split(/[\s,;]+/).map(x => x.trim()).filter(x => /^https?:\/\//i.test(x));
  const unique = [...new Set(urls)];
  if (unique.length > 1) return { kind: 'multi', urls: unique };
  if (unique.length === 1) {
    const u = unique[0];
    const isVideo = /[?&]v=|youtu\.be\/|\/shorts\/|\/live\//i.test(u);
    if (!isVideo && (/\/playlist\b|[?&]list=|\/@|\/channel\/|\/c\/|\/user\//i.test(u))) return { kind: 'playlist', urls: unique };
    return { kind: 'single', urls: unique, inList: /[?&]list=/.test(u) };
  }
  return { kind: '', urls: [] };
}

export const listIdOf = url => (String(url).match(/[?&]list=([\w-]+)/) || [])[1];

export const Corners = () => html`<i class="corner tl"></i><i class="corner tr"></i><i class="corner bl"></i><i class="corner br"></i>`;

const svg = (size, body) => html`<svg width=${size} height=${size} viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;

export const Icon = {
  grid: (s = 16) => svg(s, html`<rect x="3" y="3" width="7" height="7"></rect><rect x="14" y="3" width="7" height="7"></rect><rect x="14" y="14" width="7" height="7"></rect><rect x="3" y="14" width="7" height="7"></rect>`),
  download: (s = 16) => svg(s, html`<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path><polyline points="7 10 12 15 17 10"></polyline><line x1="12" x2="12" y1="15" y2="3"></line>`),
  scissors: (s = 16) => svg(s, html`<circle cx="6" cy="6" r="3"></circle><path d="M8.12 8.12 12 12"></path><path d="M20 4 8.12 15.88"></path><circle cx="6" cy="18" r="3"></circle><path d="M14.8 14.8 20 20"></path>`),
  music: (s = 16) => svg(s, html`<path d="M9 18V5l12-2v13"></path><circle cx="6" cy="18" r="3"></circle><circle cx="18" cy="16" r="3"></circle>`),
  link: (s = 17) => svg(s, html`<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"></path><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"></path>`),
  play: (s = 14) => svg(s, html`<polygon points="6 3 20 12 6 21 6 3"></polygon>`),
  pause: (s = 14) => svg(s, html`<rect x="6" y="4" width="4" height="16"></rect><rect x="14" y="4" width="4" height="16"></rect>`),
  stop: (s = 13) => svg(s, html`<rect x="5" y="5" width="14" height="14"></rect>`),
  back: (s = 15) => svg(s, html`<path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"></path><path d="M3 3v5h5"></path>`),
  fwd: (s = 15) => svg(s, html`<path d="M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8"></path><path d="M21 3v5h-5"></path>`),
  volume: (s = 15, on = true) => svg(s, html`<polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"></polygon>${on ? html`<path d="M15.54 8.46a5 5 0 0 1 0 7.07"></path>` : html`<path d="m22 9-6 6"></path><path d="m16 9 6 6"></path>`}`),
  expand: (s = 15) => svg(s, html`<path d="M8 3H5a2 2 0 0 0-2 2v3"></path><path d="M21 8V5a2 2 0 0 0-2-2h-3"></path><path d="M3 16v3a2 2 0 0 0 2 2h3"></path><path d="M16 21h3a2 2 0 0 0 2-2v-3"></path>`),
  chevronLeft: (s = 14) => svg(s, html`<path d="m15 18-6-6 6-6"></path>`),
  x: (s = 13) => svg(s, html`<path d="M18 6 6 18"></path><path d="m6 6 12 12"></path>`),
  sun: (s = 13) => svg(s, html`<circle cx="12" cy="12" r="4"></circle><path d="M12 2v2"></path><path d="M12 20v2"></path><path d="m4.93 4.93 1.41 1.41"></path><path d="m17.66 17.66 1.41 1.41"></path><path d="M2 12h2"></path><path d="M20 12h2"></path><path d="m6.34 17.66-1.41 1.41"></path><path d="m19.07 4.93-1.41 1.41"></path>`),
  moon: (s = 13) => svg(s, html`<path d="M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z"></path>`),
  monitor: (s = 13) => svg(s, html`<rect width="20" height="14" x="2" y="3"></rect><line x1="8" x2="16" y1="21" y2="21"></line><line x1="12" x2="12" y1="17" y2="21"></line>`),
  folder: (s = 13) => svg(s, html`<path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"></path>`),
};

// ---- Theme ------------------------------------------------------------------------------------------
const darkQuery = window.matchMedia('(prefers-color-scheme: dark)');

/** 'system' | 'light' | 'dark' -> sets <html data-theme> to the resolved 'light' / 'dark'. */
export function applyTheme(pref) {
  const dark = pref === 'dark' || (pref !== 'light' && darkQuery.matches);
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
}

/** Calls fn when Windows switches between light and dark apps. */
export const onSystemThemeChange = fn => darkQuery.addEventListener('change', fn);
