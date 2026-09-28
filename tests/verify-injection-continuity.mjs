import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';

const repo = resolve(import.meta.dirname, '..');
const source = readFileSync(join(repo, 'src', 'CodexBackdrop', 'BackdropInjector.cs'), 'utf8');
if (!source.includes('mediaStamp = state.MediaFingerprint')) {
  throw new Error('injector payload does not carry the stable media fingerprint');
}
const match = source.match(/private static string InstallExpression\(string payload\) => """\r?\n([\s\S]*?)\r?\n\s*"""\.Replace/);
if (!match) throw new Error('could not extract the embedded install expression');
const template = match[1].replace("location.href!=='app://-/index.html'||", '');
const chrome = [process.env.CHROME_PATH, 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe']
  .find(path => path && existsSync(path));
if (!chrome) throw new Error('set CHROME_PATH to a Chromium-based browser for the headless checks');
const scratchRoot = join(tmpdir(), 'codex-backdrop-tests');
mkdirSync(scratchRoot, { recursive: true });
const scratch = mkdtempSync(join(scratchRoot, 'case-'));

try {
  const sentry = `(context => ({ state: { status: 'healthy' }, disconnect() {} }))`;
  const payload = (signature, css, mediaStamp) => ({
    signature,
    css,
    sentry,
    kind: 'image',
    url: 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==',
    mediaStamp,
    playbackRate: 1,
    documentToken: 'continuity-test',
  });
  const expression = value => template.replace('__PAYLOAD__', JSON.stringify(value));
  const first = JSON.stringify(expression(payload('one', 'html{--revision:1}', 'media-one')));
  const second = JSON.stringify(expression(payload('two', 'html{--revision:2}', 'media-one')));
  const third = JSON.stringify(expression(payload('three', 'html{--revision:3}', 'media-one')));
  const fourth = JSON.stringify(expression(payload('four', 'html{--revision:4}', 'media-two')));
  const html = `<!doctype html><html><head></head><body><script>
window.__codexBackdropDocumentToken = 'continuity-test';
(0, eval)(${first});
const firstStyle = document.getElementById('codex-backdrop-style');
const firstLayer = document.getElementById('codex-backdrop-layer');
const firstMedia = firstLayer.querySelector('.codex-backdrop-media');
(0, eval)(${second});
const secondStyle = document.getElementById('codex-backdrop-style');
const secondLayer = document.getElementById('codex-backdrop-layer');
const secondMedia = secondLayer.querySelector('.codex-backdrop-media');
secondLayer.dataset.mediaError = 'load';
(0, eval)(${third});
const thirdStyle = document.getElementById('codex-backdrop-style');
const thirdLayer = document.getElementById('codex-backdrop-layer');
const thirdMedia = thirdLayer.querySelector('.codex-backdrop-media');
(0, eval)(${fourth});
const fourthStyle = document.getElementById('codex-backdrop-style');
const fourthLayer = document.getElementById('codex-backdrop-layer');
const fourthMedia = fourthLayer.querySelector('.codex-backdrop-media');
document.body.setAttribute('data-result', JSON.stringify({
  styleStable: firstStyle === secondStyle && secondStyle === thirdStyle && thirdStyle === fourthStyle,
  layerStable: firstLayer === secondLayer && secondLayer === thirdLayer && thirdLayer === fourthLayer,
  unchangedMediaStable: firstMedia === secondMedia,
  failedMediaRepaired: secondMedia !== thirdMedia,
  changedMediaReplaced: thirdMedia !== fourthMedia,
  cssUpdated: fourthStyle.textContent === 'html{--revision:4}',
  mediaErrorCleared: !fourthLayer.dataset.mediaError,
  styleCount: document.querySelectorAll('#codex-backdrop-style').length,
  layerCount: document.querySelectorAll('#codex-backdrop-layer').length,
}));
</script></body></html>`;
  const page = join(scratch, 'continuity.html');
  writeFileSync(page, html, 'utf8');
  const output = execFileSync(chrome, [
    '--headless=new',
    '--disable-gpu',
    '--no-first-run',
    '--no-default-browser-check',
    `--user-data-dir=${join(scratch, 'profile')}`,
    '--virtual-time-budget=1000',
    '--dump-dom',
    pathToFileURL(page).href,
  ], { encoding: 'utf8', timeout: 15000 });
  const encoded = output.match(/data-result="([^"]+)"/)?.[1];
  if (!encoded) throw new Error('browser did not return an injection continuity result');
  const result = JSON.parse(decodeHtml(encoded));
  if (Object.values(result).some(value => value === false)
      || result.styleCount !== 1
      || result.layerCount !== 1) {
    throw new Error(`injection continuity failed: ${JSON.stringify(result)}`);
  }
  console.log('injection continuity: all checks passed');
} finally {
  rmSync(scratch, { recursive: true, force: true });
}

function decodeHtml(value) {
  return value
    .replaceAll('&quot;', '"')
    .replaceAll('&amp;', '&')
    .replaceAll('&lt;', '<')
    .replaceAll('&gt;', '>');
}
