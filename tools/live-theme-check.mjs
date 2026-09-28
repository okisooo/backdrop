import assert from 'node:assert/strict';
const targets = await (await fetch('http://127.0.0.1:9223/json/list')).json();
const target = targets.find(t => t.type === 'page' && t.url === 'app://-/index.html');
assert.ok(target, 'main renderer exists');
const socket = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
const expression = `(() => {
  const root = document.documentElement;
  const originalTheme = root.getAttribute('data-theme');
  const originalClass = root.getAttribute('class');
  const results = [];
  try {
    for (const mode of ['light', 'dark']) {
      for (const marker of ['attribute', 'legacy']) {
        root.classList.remove('electron-light', 'electron-dark');
        if (marker === 'attribute') root.setAttribute('data-theme', mode);
        else { root.removeAttribute('data-theme'); root.classList.add('electron-' + mode); }
        const computed = getComputedStyle(root);
        const canvas = document.querySelector('[data-app-shell-main-surface]');
        results.push({mode, marker,
          surface: computed.getPropertyValue('--codex-backdrop-surface').trim(),
          sidebar: getComputedStyle(document.querySelector('aside')).backgroundColor,
          canvas: getComputedStyle(canvas).backgroundColor,
          guard: computed.getPropertyValue('--codex-backdrop-guard-text').trim()});
      }
    }
  } finally {
    if (originalTheme === null) root.removeAttribute('data-theme'); else root.setAttribute('data-theme', originalTheme);
    if (originalClass === null) root.removeAttribute('class'); else root.setAttribute('class', originalClass);
  }
  return results;
})()`;
try {
  const response = await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(Error('theme check timed out')), 8000);
    socket.onmessage = e => { const m = JSON.parse(e.data); if (m.id === 1) { clearTimeout(timeout); resolve(m); } };
    socket.send(JSON.stringify({id:1, method:'Runtime.evaluate', params:{expression, returnByValue:true}}));
  });
  assert.ok(!response.error && !response.result.exceptionDetails, JSON.stringify(response));
  const results = response.result.result.value;
  for (const result of results) {
    assert.ok(result.surface.startsWith(result.mode === 'light' ? 'rgb(248 249 252 /' : 'rgb(8 12 22 /'), JSON.stringify(result));
    assert.notEqual(result.canvas, 'rgba(0, 0, 0, 0)', 'canvas tint must be applied');
  }
  for (const mode of ['light', 'dark']) {
    const [current, legacy] = results.filter(r => r.mode === mode);
    assert.equal(current.sidebar, legacy.sidebar);
    assert.equal(current.canvas, legacy.canvas);
    assert.equal(current.guard, legacy.guard);
  }
  console.log('passed: current and legacy light/dark palettes, sidebar, canvas, and guard colors; original theme restored');
} finally { socket.close(); }
