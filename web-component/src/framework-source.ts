// Zero-config source-location detection via framework dev-mode internals — an alternative to the
// `data-component-source` attribute, which requires the host app to add a custom build plugin.
// These hooks are already present in a normal `npm run dev`/`vite dev` build with NO extra setup:
// React/Vue dev tooling attaches this metadata to the live component tree as a side effect of
// normal compilation. Verified live (this session, real Playwright-driven dev servers) against
// React 19.2 + @vitejs/plugin-react 6.1, and Vue 3.5 + @vitejs/plugin-vue 6.0 — see the framework
// version caveats on each detector below. Every detector is defensive: it never throws, and
// returns null (falling through to the next detector, and ultimately to the existing
// attribute-based/text-search fallback in skill.md) rather than risk breaking capture.
//
// Angular's public `ng.getComponent(el)` API was considered but deliberately excluded: it yields
// a component class name, never a file path, which doesn't fit `sourcePath`'s file:line contract.
// Svelte was considered but not implemented — no live-verified zero-config hook was confirmed.

type AnyRecord = Record<string, unknown>;

// A React/Vite dev-server URL (e.g. http://localhost:5173/src/Button.tsx) or a Vue dev-mode
// absolute filesystem path (e.g. /Users/jamie/app/src/components/Button.vue) both need
// stripping down to a portable, repo-relative-looking path before use as `sourcePath`.
function toPortablePath(raw: string): string {
  if (/^https?:\/\//.test(raw)) {
    try {
      return new URL(raw).pathname.replace(/^\//, '');
    } catch {
      return raw;
    }
  }
  // Absolute filesystem path (Vue's __file, or any future detector that yields one): the widget
  // has no way to know the actual repo root from browser JS, so anchor on the near-universal
  // `src/` convention rather than sending a dev-machine-specific absolute path.
  const srcIdx = raw.lastIndexOf('/src/');
  if (srcIdx >= 0) return raw.slice(srcIdx + 1);
  return raw;
}

// React 19+: fiber._debugStack is a real Error whose stack's first non-framework frame names the
// exact file:line:column where this fiber's JSX was written. (React <19 instead attached a plain
// {fileName, lineNumber, columnNumber} object as fiber._debugSource — handled as a fallback below
// in case an older React is present; not independently live-verified this session.)
function reactStackToSourcePath(stack: string): string | null {
  const lines = stack.split('\n').slice(1); // drop the "Error: react-stack-top-frame" message line
  for (const line of lines) {
    if (/node_modules|react-dom|react_jsx|react-jsx/.test(line)) continue;
    const m = line.match(/\((https?:\/\/[^\s)]+):(\d+):(\d+)\)/) || line.match(/at (https?:\/\/[^\s)]+):(\d+):(\d+)/);
    if (m) return `${toPortablePath(m[1])}:${m[2]}`;
  }
  return null;
}

export function detectReactSource(el: Element): string | null {
  const key = Object.getOwnPropertyNames(el).find((k) => k.startsWith('__reactFiber'));
  if (!key) return null;
  const fiber = (el as unknown as AnyRecord)[key] as AnyRecord | null;
  if (!fiber) return null;

  const legacy = fiber._debugSource as { fileName?: string; lineNumber?: number } | undefined;
  if (legacy && legacy.fileName) {
    return `${toPortablePath(legacy.fileName)}:${legacy.lineNumber || 0}`;
  }

  const debugStack = fiber._debugStack as { stack?: string } | undefined;
  if (debugStack && typeof debugStack.stack === 'string') {
    const found = reactStackToSourcePath(debugStack.stack);
    if (found) return found;
  }

  return null;
}

// Vue 3 dev mode (@vitejs/plugin-vue): the component instance reachable via
// el.__vueParentComponent carries __file on its `type` (or, for some component definition
// styles, directly on the instance) — an absolute filesystem path to the .vue file. No
// line/column is available this way (unlike React), only the file.
export function detectVueSource(el: Element): string | null {
  let instance = (el as unknown as AnyRecord).__vueParentComponent as AnyRecord | null;
  let depth = 0;
  while (instance && depth < 6) {
    const type = instance.type as AnyRecord | undefined;
    const file = (type && (type.__file as string | undefined)) || (instance.__file as string | undefined);
    if (file) return toPortablePath(file);
    instance = instance.parent as AnyRecord | null;
    depth++;
  }
  return null;
}

export function detectFrameworkSourcePath(el: Element): string | null {
  try {
    const react = detectReactSource(el);
    if (react) return react;
  } catch {
    /* never let detection break capture */
  }

  try {
    const vue = detectVueSource(el);
    if (vue) return vue;
  } catch {
    /* ignore */
  }

  return null;
}
