import { readFile } from 'node:fs/promises';

const css = await readFile(new URL('../src/CodexBackdrop/Runtime/codex-selectors.css', import.meta.url), 'utf8');
const sentry = await readFile(new URL('../src/CodexBackdrop/BackdropSentrySource.cs', import.meta.url), 'utf8');
const runtimeEntries = await import('node:fs/promises').then(fs => fs.readdir(new URL('../src/CodexBackdrop/Runtime/', import.meta.url)));
if (runtimeEntries.some(name => /sentry|\.js$/i.test(name))) {
  throw new Error(`sentry must be assembly-owned, not a replaceable runtime script: ${runtimeEntries.join(', ')}`);
}
const forbidden = [
  '[role="dialog"]',
  '[role="menu"]',
  '[role="tooltip"]',
  '[role="listbox"]',
  '[aria-modal="true"]',
  '[data-radix-popper-content-wrapper]',
  '[data-slot="popover-content"]',
  '[data-slot="dropdown-menu-content"]',
  '[data-slot*="popover" i]',
  '[data-slot*="dropdown" i]',
  '[data-slot*="tooltip" i]',
  '--vscode-menu-background',
  '--vscode-dropdown-background',
  '--color-background-elevated-primary',
  '--color-background-elevated-secondary',
  '--color-surface-elevated',
];
const leaked = forbidden.filter(token => css.includes(token));
if (leaked.length) throw new Error(`Backdrop still owns protected transient styling: ${leaked.join(', ')}`);

for (const required of ['MutationObserver', 'low-contrast', 'transparent-text', 'ownership:', "style.disabled = !enabled", 'layer.hidden = !enabled', 'data-codex-backdrop-contrast-guard', 'data-codex-backdrop-text-guard']) {
  if (!sentry.includes(required)) throw new Error(`sentry is missing required fail-safe behavior: ${required}`);
}

console.log(JSON.stringify({
  protectedSurfaceOwnership: 'native',
  themesCovered: ['electron-light', 'electron-dark'],
  sentry: 'ownership fail-safe and local contrast guard present',
}, null, 2));
