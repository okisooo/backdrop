import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const repo = resolve(import.meta.dirname, '..');
const source = readFileSync(join(repo, 'src', 'CodexBackdrop', 'BackdropSentrySource.cs'), 'utf8');
const css = readFileSync(join(repo, 'src', 'CodexBackdrop', 'Runtime', 'codex-selectors.css'), 'utf8');
const match = source.match(/internal const string Script = """\r?\n([\s\S]*?)\r?\n""";/);
if (!match) throw new Error('could not extract the embedded sentry source');
const sentry = match[1];
const chrome = [process.env.CHROME_PATH, 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe']
  .find(path => path && existsSync(path));
if (!chrome) throw new Error('set CHROME_PATH to a Chromium-based browser for the headless checks');
const scratchRoot = join(tmpdir(), 'codex-backdrop-tests');
mkdirSync(scratchRoot, { recursive: true });
const scratch = mkdtempSync(join(scratchRoot, 'case-'));

try {
  runCase('popover-trigger', 'popover-trigger', 'transparent', 'healthy', false);
  runCase('popover-content', 'popover-content', 'transparent', 'guarded', true);
  runCase('dialog-content', 'dialog', 'low-contrast', 'guarded', true);
  runCase('dialog-removal-recovery', 'dialog', 'low-contrast', 'healthy', false, 'remove');
  runCase('dialog-same-node-recovery', 'dialog', 'low-contrast', 'healthy', false, 'repair');
  runCase('bounded-layout-work', 'dialog', 'readable', 'healthy', false, null, true);
  runCase('hidden-text-before-visible', 'dialog', 'readable', 'guarded', true, null, 'hidden');
  runCase('modern-color-syntax', 'dialog', 'modern', 'healthy', false);
  runCase('gradient-text', 'dialog', 'readable', 'healthy', false, null, 'gradient');
  runCase('faded-wrapper-text', 'dialog', 'readable', 'healthy', false, null, 'faded');
  console.log('sentry route scope: all checks passed');
} finally {
  rmSync(scratch, { recursive: true, force: true });
}

function runCase(name, slot, issue, expectedStatus, expectedGuarded, recovery = null, large = false) {
  const profile = join(scratch, `profile-${name}`);
  const role = slot === 'dialog' ? 'role="dialog"' : `data-slot="${slot}"`;
  const colors = issue === 'transparent'
    ? 'color:rgba(0,0,0,0);background:rgb(255,255,255)'
    : issue === 'readable' ? 'color:rgb(31,35,40);background:rgb(248,249,252)'
    : issue === 'modern' ? 'color:color(srgb 0.1216 0.1373 0.1569 / 0.9);background:oklab(0.98 0 -0.003)'
    : 'color:rgb(80,80,80);background:rgb(90,90,90)';
  const extra = {
    hidden: '<span style="display:none">hidden</span>'.repeat(200) + '<span style="color:transparent">visible text</span>',
    gradient: '<span style="background:linear-gradient(90deg,#8250df,#4735a0);-webkit-background-clip:text;background-clip:text;color:transparent">gradient text</span>',
    faded: '<div style="opacity:0"><span style="color:transparent">faded warning</span></div>',
  }[large] ?? (large ? '<span></span>'.repeat(2000) + '<span>text</span>'.repeat(500) : '');
  const html = `<!doctype html>
<html class="electron-light"><head><meta charset="utf-8"><style id="codex-backdrop-style">${css}</style></head>
<body><div id="codex-backdrop-layer"></div>
<button id="surface" ${role} style="${colors}">options${extra}</button>
<script>
let layoutReads = 0;
const originalRect = Element.prototype.getBoundingClientRect;
Element.prototype.getBoundingClientRect = function() { layoutReads++; return originalRect.call(this); };
const install = (0, eval)(${JSON.stringify(sentry)});
const style = document.getElementById('codex-backdrop-style');
const layer = document.getElementById('codex-backdrop-layer');
const controller = install({ signature: 'route-test', style, layer });
${recovery === 'remove' ? "setTimeout(() => document.getElementById('surface')?.remove(), 450);" : ''}
${recovery === 'repair' ? "setTimeout(() => document.getElementById('surface').style.cssText = 'color:rgb(31,35,40);background:rgb(248,249,252)', 450);" : ''}
setTimeout(() => {
  const surface = document.getElementById('surface');
  document.body.setAttribute('data-result', JSON.stringify({
    status: controller.state.status,
    layoutReads,
    reason: controller.state.reason,
    guardedAt: controller.state.guardedAt,
    recoveredAt: controller.state.recoveredAt,
    styleDisabled: style.disabled,
    layerHidden: layer.hidden,
    guarded: surface?.hasAttribute('data-codex-backdrop-contrast-guard') ?? false,
    textGuarded: surface?.hasAttribute('data-codex-backdrop-text-guard') || !!surface?.querySelector('[data-codex-backdrop-text-guard]'),
    color: surface ? getComputedStyle(surface).color : null,
    background: surface ? getComputedStyle(surface).backgroundColor : null,
  }));
  controller.disconnect();
}, 1200);
</script></body></html>`;
  const page = join(scratch, `${name}.html`);
  writeFileSync(page, html, 'utf8');
  const output = execFileSync(chrome, [
    '--headless=new',
    '--disable-gpu',
    '--no-first-run',
    '--no-default-browser-check',
    `--user-data-dir=${profile}`,
    '--virtual-time-budget=1500',
    '--dump-dom',
    pathToFileURL(page).href,
  ], { encoding: 'utf8', timeout: 15000 });
  const encoded = output.match(/data-result="([^"]+)"/)?.[1];
  if (!encoded) throw new Error(`${name}: browser did not return a sentry result`);
  const result = JSON.parse(decodeHtml(encoded));
  if (large === true) {
    if (result.layoutReads > 161) throw new Error('text sampling exceeded its layout budget: ' + result.layoutReads);
    console.log('large dialog: ' + result.layoutReads + ' layout reads for 2501 elements (previous scanner: 2502)');
  }
  if (result.status !== expectedStatus
      || result.styleDisabled
      || result.layerHidden
      || result.guarded !== expectedGuarded) {
    throw new Error(`${name}: expected ${expectedStatus}/enabled/${expectedGuarded}, got ${JSON.stringify(result)}`);
  }
  if (expectedGuarded && (!result.textGuarded
      || result.color !== 'rgb(31, 35, 40)'
      || result.background !== 'rgb(248, 249, 252)')) {
    throw new Error(`${name}: local guard did not restore readable native colors: ${JSON.stringify(result)}`);
  }
  if (recovery && !(result.recoveredAt > result.guardedAt)) {
    throw new Error(`${name}: recovery telemetry is stale: ${JSON.stringify(result)}`);
  }
}

function decodeHtml(value) {
  return value
    .replaceAll('&quot;', '"')
    .replaceAll('&amp;', '&')
    .replaceAll('&lt;', '<')
    .replaceAll('&gt;', '>');
}
