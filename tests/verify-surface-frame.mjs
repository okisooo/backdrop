import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';

const repo = resolve(import.meta.dirname, '..');
const source = readFileSync(join(repo, 'src', 'CodexBackdrop', 'BackdropSentrySource.cs'), 'utf8');
const theme = readFileSync(join(repo, 'src', 'CodexBackdrop', 'Runtime', 'theme.css'), 'utf8');
const match = source.match(/internal const string Script = """\r?\n([\s\S]*?)\r?\n""";/);
if (!match) throw new Error('could not extract the embedded sentry source');
if (!theme.includes('.codex-backdrop-overlay::after') || !theme.includes('box-shadow: 0 0 0 100vmax var(--codex-backdrop-frame)'))
  throw new Error('theme must paint the window frame around the published surface');
const chrome = [process.env.CHROME_PATH, 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe']
  .find(path => path && existsSync(path));
if (!chrome) throw new Error('set CHROME_PATH to a Chromium-based browser for the headless checks');
const scratchRoot = join(tmpdir(), 'codex-backdrop-tests');
mkdirSync(scratchRoot, { recursive: true });
const scratch = mkdtempSync(join(scratchRoot, 'case-'));

try {
  const html = `<!doctype html>
<html><head><style id="codex-backdrop-style"></style></head>
<body><div id="codex-backdrop-layer"><div class="codex-backdrop-overlay"></div></div>
<main data-app-shell-main-surface="default" style="position:fixed;top:44px;left:335px;right:4px;bottom:4px;border-radius:0 12px 4px 0"></main>
<script>
const install = (0, eval)(${JSON.stringify(match[1])});
const style = document.getElementById('codex-backdrop-style');
const layer = document.getElementById('codex-backdrop-layer');
const surface = () => document.querySelector('main');
const read = () => ['top', 'right', 'bottom', 'left', 'radius']
  .map(side => getComputedStyle(layer).getPropertyValue('--codex-backdrop-surface-' + side).trim());
const controller = install({ signature: 'frame-test', style, layer });
const published = read();
surface().style.left = '200px';
setTimeout(() => {
  const resized = read();
  const replacement = surface().cloneNode();
  replacement.style.left = '100px';
  surface().replaceWith(replacement);
  setTimeout(() => {
    const replaced = read();
    controller.disconnect();
    document.body.setAttribute('data-result', JSON.stringify({
      published,
      resized,
      replaced,
      removed: read(),
      sheets: document.adoptedStyleSheets.length,
    }));
  }, 300);
}, 300);
</script></body></html>`;
  const page = join(scratch, 'frame.html');
  writeFileSync(page, html, 'utf8');
  const output = execFileSync(chrome, [
    '--headless=new',
    '--disable-gpu',
    '--no-first-run',
    '--no-default-browser-check',
    `--user-data-dir=${join(scratch, 'profile')}`,
    '--virtual-time-budget=1500',
    '--dump-dom',
    pathToFileURL(page).href,
  ], { encoding: 'utf8', timeout: 15000 });
  const encoded = output.match(/data-result="([^"]+)"/)?.[1];
  if (!encoded) throw new Error('browser did not return a surface frame result');
  const result = JSON.parse(encoded.replaceAll('&quot;', '"').replaceAll('&amp;', '&'));
  const expect = (label, actual, expected) => {
    if (JSON.stringify(actual) !== JSON.stringify(expected))
      throw new Error(`${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  };
  expect('published geometry', result.published, ['44px', '4px', '4px', '335px', '0px 12px 4px 0px']);
  expect('resized surface', result.resized[3], '200px');
  expect('replaced surface', result.replaced[3], '100px');
  expect('disconnect', result.removed, ['', '', '', '', '']);
  expect('adopted sheets after disconnect', result.sheets, 0);
  console.log('surface frame: geometry published, followed through resize and replacement, removed on disconnect');
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
