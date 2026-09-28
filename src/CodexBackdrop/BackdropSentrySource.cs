namespace CodexBackdrop;

internal static class BackdropSentrySource
{
    internal const string Script = """
(context => {
  const protectedSelector = [
    '[role="dialog"]',
    '[role="menu"]',
    '[role="tooltip"]',
    '[role="listbox"]',
    '[aria-modal="true"]',
    '[data-radix-popper-content-wrapper]',
    '[data-slot*="popover" i]:not([data-slot*="trigger" i])',
    '[data-slot*="dropdown" i]:not([data-slot*="trigger" i])',
    '[data-slot*="tooltip" i]:not([data-slot*="trigger" i])',
  ].join(',');
  const state = {
    signature: context.signature,
    status: 'starting',
    reason: null,
    lastAuditAt: 0,
    disabledAt: null,
    guardedAt: null,
    recoveredAt: null,
  };
  let observer;
  let auditTimer;
  let recoveryTimer;
  let recoveryPasses = 0;
  let recoveryDelay = 350;
  let protectedSurfaces = [];
  const guardedSurfaces = new Set();
  const guardedText = new Set();

  const visible = element => {
    const rect = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    return rect.width > 0 && rect.height > 0
      && style.display !== 'none' && style.visibility !== 'hidden'
      && Number(style.opacity || 1) > 0.05;
  };

  // Computed colors can use any CSS Color 4 syntax (color(srgb ...), oklab(...)
  // from color-mix, ...). A 1x1 canvas normalizes every one of them to sRGB.
  const colorCache = new Map();
  let colorContext;
  const parseColor = value => {
    const key = String(value);
    if (colorCache.has(key)) return colorCache.get(key);
    colorContext ??= Object.assign(document.createElement('canvas'), { width: 1, height: 1 })
      .getContext('2d', { willReadFrequently: true });
    let color = null;
    if (colorContext && CSS.supports('color', key)) {
      colorContext.clearRect(0, 0, 1, 1);
      colorContext.fillStyle = key;
      colorContext.fillRect(0, 0, 1, 1);
      const [r, g, b, a] = colorContext.getImageData(0, 0, 1, 1).data;
      color = { r, g, b, a: a / 255 };
    }
    if (colorCache.size >= 256) colorCache.clear();
    colorCache.set(key, color);
    return color;
  };

  // Text inside a faded-out wrapper is not painted, whatever its own opacity.
  const shownWithin = (element, surface) => {
    for (let current = element.parentElement; current && current !== surface; current = current.parentElement)
      if (Number(getComputedStyle(current).opacity || 1) <= 0.05) return false;
    return true;
  };

  // Gradient text paints a background through transparent glyphs, so it has
  // no single foreground color to judge.
  const clipsBackgroundToText = (element, surface) => {
    for (let current = element; current; current = current.parentElement) {
      const style = getComputedStyle(current);
      if (style.backgroundClip === 'text' || style.webkitBackgroundClip === 'text') return true;
      if (current === surface) break;
    }
    return false;
  };

  const over = (front, back) => {
    const alpha = front.a + back.a * (1 - front.a);
    if (alpha <= 0) return { r: 0, g: 0, b: 0, a: 0 };
    return {
      r: (front.r * front.a + back.r * back.a * (1 - front.a)) / alpha,
      g: (front.g * front.a + back.g * back.a * (1 - front.a)) / alpha,
      b: (front.b * front.a + back.b * back.a * (1 - front.a)) / alpha,
      a: alpha,
    };
  };

  const resolvedBackground = element => {
    let result = { r: 0, g: 0, b: 0, a: 0 };
    for (let current = element; current instanceof Element; current = current.parentElement) {
      const color = parseColor(getComputedStyle(current).backgroundColor);
      if (color && color.a > 0) result = over(result, color);
      if (result.a >= 0.995) break;
    }
    const root = document.documentElement;
    const dark = root.dataset.theme === 'dark' || root.classList.contains('electron-dark');
    return over(result, dark
      ? { r: 8, g: 12, b: 22, a: 1 }
      : { r: 248, g: 249, b: 252, a: 1 });
  };

  const luminance = color => {
    const channel = value => {
      const normalized = value / 255;
      return normalized <= 0.04045
        ? normalized / 12.92
        : ((normalized + 0.055) / 1.055) ** 2.4;
    };
    return channel(color.r) * 0.2126 + channel(color.g) * 0.7152 + channel(color.b) * 0.0722;
  };

  const contrast = (foreground, background) => {
    const composed = over(foreground, background);
    const first = luminance(composed);
    const second = luminance(background);
    return (Math.max(first, second) + 0.05) / (Math.min(first, second) + 0.05);
  };

  const invalidSurface = surface => {
    const issues = [];
    // ponytail: skip non-text elements and stop after 160 visible text samples.
    const walker = document.createTreeWalker(surface, NodeFilter.SHOW_TEXT);
    const checked = new Set();
    let samples = 0;
    for (let node; samples < 160 && (node = walker.nextNode());) {
      if (!node.textContent?.trim()) continue;
      const element = node.parentElement;
      if (!element || checked.has(element)) continue;
      checked.add(element);
      if (!visible(element) || !shownWithin(element, surface)) continue;
      samples++;
      // The fill color is what glyphs are painted with; it follows color
      // unless the app overrides it.
      const textStyle = getComputedStyle(element);
      const foreground = parseColor(textStyle.webkitTextFillColor || textStyle.color);
      if (!foreground || foreground.a < 0.35) {
        if (clipsBackgroundToText(element, surface)) continue;
        issues.push({ kind: 'transparent-text', element });
        continue;
      }
      const ratio = contrast(foreground, resolvedBackground(element));
      if (ratio < 2.25) issues.push({ kind: 'low-contrast', ratio, element });
    }
    return issues;
  };

  const owners = () => ({
    styles: [...document.querySelectorAll('#codex-backdrop-style')],
    layers: [...document.querySelectorAll('#codex-backdrop-layer')],
  });

  const setBackdropEnabled = enabled => {
    const current = owners();
    for (const style of current.styles) style.disabled = !enabled;
    for (const layer of current.layers) layer.hidden = !enabled;
  };

  const clearGuards = () => {
    for (const surface of guardedSurfaces) surface.removeAttribute('data-codex-backdrop-contrast-guard');
    for (const element of guardedText) element.removeAttribute('data-codex-backdrop-text-guard');
    guardedSurfaces.clear();
    guardedText.clear();
  };

  const guard = failures => {
    for (const failure of failures) {
      failure.surface.setAttribute('data-codex-backdrop-contrast-guard', '');
      guardedSurfaces.add(failure.surface);
      for (const issue of failure.issues) {
        issue.element.setAttribute('data-codex-backdrop-text-guard', '');
        guardedText.add(issue.element);
      }
    }
    const first = failures[0];
    const issue = first.issues[0];
    const label = first.surface.getAttribute('role') || first.surface.getAttribute('data-slot') || first.surface.tagName.toLowerCase();
    const ratio = issue.ratio ? `:${issue.ratio.toFixed(2)}` : '';
    if (state.status !== 'guarded') state.guardedAt = Date.now();
    state.status = 'guarded';
    state.reason = `${issue.kind}:${label}${ratio}`;
    clearTimeout(recoveryTimer);
    recoveryTimer = setTimeout(runAudit, recoveryDelay);
    recoveryDelay = Math.min(2000, recoveryDelay * 2);
  };

  const failSafe = reason => {
    clearGuards();
    setBackdropEnabled(false);
    recoveryPasses = 0;
    if (state.status !== 'disabled' || state.reason !== reason) state.disabledAt = Date.now();
    state.status = 'disabled';
    state.reason = reason;
    clearTimeout(recoveryTimer);
    recoveryTimer = setTimeout(runAudit, recoveryDelay);
    recoveryDelay = Math.min(2000, recoveryDelay * 2);
  };

  const schedule = () => {
    recoveryDelay = 350;
    // Bound the wait even when streaming content keeps mutating the document.
    if (auditTimer !== undefined) return;
    auditTimer = setTimeout(runAudit, 140);
  };

  const runAudit = () => {
    clearTimeout(auditTimer);
    auditTimer = undefined;
    clearTimeout(recoveryTimer);
    state.lastAuditAt = Date.now();
    const current = owners();
    if (current.styles.length !== 1 || current.layers.length !== 1) {
      for (const extra of current.styles.slice(1)) extra.remove();
      for (const extra of current.layers.slice(1)) extra.remove();
      failSafe(`ownership:${current.styles.length}-styles:${current.layers.length}-layers`);
      return state;
    }

    if (state.status === 'disabled') {
      recoveryPasses += 1;
      if (recoveryPasses < 2) {
        recoveryTimer = setTimeout(runAudit, 350);
        return state;
      }
      setBackdropEnabled(true);
      state.status = 'probing';
      state.reason = null;
      recoveryTimer = setTimeout(runAudit, 180);
      return state;
    }

    // Probe the native styles in one synchronous task, then restore only the
    // guards that are still needed. The browser cannot paint between these
    // operations, so recovery does not introduce a visible flash.
    clearGuards();
    protectedSurfaces = [...document.querySelectorAll(protectedSelector)];
    const failures = protectedSurfaces
      .filter(visible)
      .map(surface => ({ surface, issues: invalidSurface(surface) }))
      .filter(item => item.issues.length > 0);
    if (failures.length > 0) {
      guard(failures);
      return state;
    }

    if (state.status === 'guarded') {
      state.status = 'probing';
      state.reason = null;
      recoveryTimer = setTimeout(runAudit, 180);
      return state;
    }

    const recovered = state.status === 'probing';
    state.status = 'healthy';
    state.reason = null;
    if (recovered) state.recoveredAt = Date.now();
    recoveryPasses = 0;
    recoveryDelay = 350;
    return state;
  };

  const ownershipSelector = '#codex-backdrop-style,#codex-backdrop-layer';
  const affectsAudit = record => {
    const target = record.target instanceof Element ? record.target : record.target.parentElement;
    if (!target) return false;
    if (target.closest(protectedSelector + ',' + ownershipSelector)) return true;
    if (record.type === 'characterData') return false;
    if (record.type === 'attributes') {
      return target === document.documentElement || target === document.body
        || target === context.style || target === context.layer
        || protectedSurfaces.some(surface => target.contains(surface));
    }
    return [...record.addedNodes, ...record.removedNodes].some(node =>
      node instanceof Element && (node.matches(protectedSelector + ',' + ownershipSelector)
        || node.querySelector(protectedSelector + ',' + ownershipSelector)));
  };
  // Codex 26.924 floats its page surface inside a thin window frame that it
  // leaves transparent (Windows paints Mica there). Publish the surface's
  // geometry so the layer paints that frame, and the corners outside the
  // surface's rounding, in chrome color instead of showing raw wallpaper. An
  // adopted sheet keeps the updates out of the observed document.
  const frameSheet = typeof CSSStyleSheet === 'function' && 'adoptedStyleSheets' in document
    && typeof ResizeObserver === 'function' ? new CSSStyleSheet() : null;
  let frameSurface = null;
  let frameRule = null;
  const trackFrame = () => {
    const current = document.querySelector('main[data-app-shell-main-surface]');
    if (current !== frameSurface) {
      if (frameSurface) frameObserver.unobserve(frameSurface);
      frameSurface = current;
      if (frameSurface) frameObserver.observe(frameSurface);
    }
    let rule = '';
    if (frameSurface) {
      const rect = frameSurface.getBoundingClientRect();
      const style = getComputedStyle(frameSurface);
      rule = '#codex-backdrop-layer{'
        + `--codex-backdrop-surface-top:${rect.top}px;`
        + `--codex-backdrop-surface-right:${innerWidth - rect.right}px;`
        + `--codex-backdrop-surface-bottom:${innerHeight - rect.bottom}px;`
        + `--codex-backdrop-surface-left:${rect.left}px;`
        + `--codex-backdrop-surface-radius:${style.borderTopLeftRadius} ${style.borderTopRightRadius} ${style.borderBottomRightRadius} ${style.borderBottomLeftRadius}}`;
    }
    if (rule === frameRule) return;
    frameRule = rule;
    frameSheet.replaceSync(rule);
  };
  const frameObserver = frameSheet ? new ResizeObserver(trackFrame) : null;
  if (frameSheet) {
    document.adoptedStyleSheets = [...document.adoptedStyleSheets, frameSheet];
    addEventListener('resize', trackFrame);
    trackFrame();
  }

  observer = new MutationObserver(records => {
    if (frameSheet && !frameSurface?.isConnected) trackFrame();
    if (records.some(affectsAudit)) schedule();
  });
  observer.observe(document.documentElement, {
    subtree: true,
    childList: true,
    characterData: true,
    attributes: true,
    attributeFilter: ['class', 'style', 'role', 'data-slot', 'data-state', 'aria-hidden', 'aria-modal', 'hidden', 'open', 'id', 'data-radix-popper-content-wrapper'],
  });
  schedule();

  return {
    state,
    auditNow: runAudit,
    disconnect() {
      observer.disconnect();
      if (frameSheet) {
        frameObserver.disconnect();
        removeEventListener('resize', trackFrame);
        document.adoptedStyleSheets = document.adoptedStyleSheets.filter(sheet => sheet !== frameSheet);
      }
      clearTimeout(auditTimer);
      clearTimeout(recoveryTimer);
      clearGuards();
    },
  };
})
""";

    internal static readonly string Fingerprint = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Script)));
}
