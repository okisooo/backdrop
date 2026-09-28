import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import vm from 'node:vm';

const source = readFileSync(resolve(import.meta.dirname, '../src/CodexBackdrop/BackdropSentrySource.cs'), 'utf8');
const script = source.match(/internal const string Script = """\r?\n([\s\S]*?)\r?\n""";/)?.[1];
assert.ok(script, 'embedded sentry must be available');
let now = 1;
let nextId = 1;
const timers = new Map();
let mutate;
let observerOptions;
class Element {
  constructor(protectedSurface = false, parentElement = null) {
    this.protectedSurface = protectedSurface;
    this.parentElement = parentElement;
  }
  closest() { return this.protectedSurface ? this : this.parentElement?.closest(); }
  matches() { return this.protectedSurface; }
  querySelector() { return null; }
  contains(node) { return node === this || node.parentElement === this; }
}
const html = new Element();
const chat = new Element(false, html);
const dialog = new Element(true, html);
const chatMutation = { type: 'characterData', target: { parentElement: chat } };
const dialogMutation = { type: 'characterData', target: { parentElement: dialog } };
const context = vm.createContext({
  Element,
  Date: { now: () => now },
  setTimeout(callback, delay) { const id = nextId++; timers.set(id, { callback, due: now + delay }); return id; },
  clearTimeout(id) { timers.delete(id); },
  document: {
    documentElement: html,
    querySelectorAll(selector) {
      if (selector === '#codex-backdrop-style' || selector === '#codex-backdrop-layer') return [{}];
      return [];
    },
  },
  MutationObserver: class {
    constructor(callback) { mutate = callback; }
    observe(_, options) { observerOptions = options; }
    disconnect() { mutate = () => {}; }
  },
});
const sentry = vm.runInContext(script, context)({ signature: 'test' });
function advance(milliseconds) {
  const end = now + milliseconds;
  while (true) {
    const pending = [...timers].sort((a, b) => a[1].due - b[1].due)[0];
    if (!pending || pending[1].due > end) break;
    const [id, timer] = pending;
    now = timer.due;
    timers.delete(id);
    timer.callback();
  }
  now = end;
}
// Relevant streaming edits must never starve the bounded audit timer.
for (let index = 0; index < 20; index++) { advance(50); mutate([dialogMutation]); }
assert.equal(sentry.state.status, 'healthy', 'continuous mutations must not starve the first audit');
assert.ok(now - sentry.state.lastAuditAt <= 200, 'audits must keep running while content streams');
assert.equal(observerOptions.characterData, true, 'text-node edits must trigger contrast checks');
advance(200);
const settledAudit = sentry.state.lastAuditAt;
for (let index = 0; index < 200; index++) { advance(50); mutate([chatMutation]); }
assert.equal(sentry.state.lastAuditAt, settledAudit, 'ordinary streaming must cause zero additional audits');
assert.equal(timers.size, 0, 'healthy sentry has no idle timer');
mutate([{ type: 'childList', target: chat, addedNodes: [new Element(true)], removedNodes: [] }]);
advance(150);
assert.ok(sentry.state.lastAuditAt > settledAudit, 'new protected surfaces must be detected');
const beforeTheme = sentry.state.lastAuditAt;
mutate([{ type: 'attributes', target: html }]);
advance(150);
assert.ok(sentry.state.lastAuditAt > beforeTheme, 'theme changes must trigger checks');
const beforeRemoval = sentry.state.lastAuditAt;
mutate([{ type: 'childList', target: chat, addedNodes: [], removedNodes: [dialog] }]);
advance(150);
assert.ok(sentry.state.lastAuditAt > beforeRemoval, 'removed protected surfaces must trigger recovery');
sentry.disconnect();
const lastAudit = sentry.state.lastAuditAt;
advance(1000);
assert.equal(sentry.state.lastAuditAt, lastAudit, 'disconnected sentry must release its timers');
assert.equal(timers.size, 0);
console.log('sentry scheduling: all checks passed; 200 unrelated streaming edits caused 0 audits');
