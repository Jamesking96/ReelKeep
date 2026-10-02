// Messaging with the native host (WebView2).
//   call(cmd, args) -> Promise of the host's result
//   on(evt, fn)     -> host events: state, job, toast, play, task, release
const wv = window.chrome && window.chrome.webview;
const pending = new Map();
const listeners = {};
let nextId = 1;

export const inHost = !!wv;

export function call(cmd, args = {}) {
  if (!wv) return Promise.reject(new Error('Not running inside ReelKeep'));
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    wv.postMessage({ id, cmd, args });
  });
}

export function on(evt, fn) {
  (listeners[evt] = listeners[evt] || []).push(fn);
}

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m && m.reply) {
      const p = pending.get(m.reply);
      if (!p) return;
      pending.delete(m.reply);
      if (m.ok) p.resolve(m.result); else p.reject(new Error(m.error || 'Something went wrong'));
    } else if (m && m.evt) {
      (listeners[m.evt] || []).forEach(fn => fn(m.data));
    }
  });
}
