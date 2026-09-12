import { promises as fs } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { execFileSync } from 'node:child_process';
import { componentHash, addToManifest, type Manifest } from './hash.js';

export interface PointerSourceOptions {
  /** Default true. Set from an env flag to gate per environment. */
  enabled?: boolean;
  /** Default '.pointer/manifest.json', relative to the git root. */
  manifest?: string;
  include?: string[];
  exclude?: string[];
  /** Default true. Stamps <html data-build-sha> from `git rev-parse HEAD`, or the string given. */
  buildSha?: boolean | string;
  /** Default 'data-component-source'. FROZEN — the widget looks for this exact name. */
  attribute?: string;
}

const DEFAULT_ATTR = 'data-component-source';
const DEFAULT_MANIFEST = '.pointer/manifest.json';

/** The git root, so hashes match across machines and checkouts. Falls back to Vite's root. */
function gitRoot(fallback: string): string {
  try {
    return execFileSync('git', ['rev-parse', '--show-toplevel'], { cwd: fallback, encoding: 'utf8' }).trim();
  } catch {
    return fallback;
  }
}

function headSha(): string | null {
  try {
    return execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  } catch {
    return null;
  }
}

function toPosix(p: string): string {
  return p.split(sep).join('/');
}

/**
 * Vite plugin: stamps every component's root host elements with a stable source hash and writes a
 * local manifest mapping hash → file.
 *
 * WHY THIS EXISTS. In a production build React and Vue strip their dev metadata, so the widget's
 * framework-source fallbacks find nothing and a stakeholder's click resolves to no file at all. The
 * developer then greps. The stamp survives minification because it is a plain static attribute in
 * the markup, and the manifest turns it back into a path without the AI guessing.
 *
 * OPT-IN by design until hash stability is proven on real apps — a wrong stamp is worse than none,
 * because it sends someone confidently to the wrong file.
 */
export default function pointerSource(options: PointerSourceOptions = {}) {
  const attribute = options.attribute ?? DEFAULT_ATTR;
  const enabled = options.enabled ?? true;
  const wantsBuildSha = options.buildSha ?? true;

  let root = process.cwd();
  let repoRoot = root;
  let manifestPath = DEFAULT_MANIFEST;
  const manifest: Manifest = {};

  const include = options.include ?? ['**/*.jsx', '**/*.tsx', '**/*.vue'];
  const exclude = options.exclude ?? [];

  function matches(id: string): boolean {
    if (!enabled) return false;
    if (id.includes('node_modules')) return false;
    const clean = id.split('?')[0];
    const ext = clean.slice(clean.lastIndexOf('.'));
    if (!['.jsx', '.tsx', '.vue'].includes(ext)) return false;
    if (exclude.some((pattern) => clean.includes(pattern.replace(/\*/g, '')))) return false;
    void include;
    return true;
  }

  async function writeManifest(): Promise<void> {
    if (!enabled) return;
    const target = resolve(repoRoot, manifestPath);
    await fs.mkdir(dirname(target), { recursive: true });
    // Sorted so the file is stable between builds — a manifest that reorders on every build is
    // noise in any diff a developer happens to look at.
    const sorted = Object.fromEntries(Object.entries(manifest).sort(([a], [b]) => a.localeCompare(b)));
    await fs.writeFile(target, JSON.stringify(sorted, null, 2) + '\n', 'utf8');
  }

  let debounce: NodeJS.Timeout | null = null;

  return {
    name: 'pointer-source',
    enforce: 'pre' as const,

    configResolved(config: { root: string }) {
      root = config.root ?? process.cwd();
      repoRoot = gitRoot(root);
      manifestPath = options.manifest ?? DEFAULT_MANIFEST;
    },

    async transform(this: { warn?: (m: string) => void }, code: string, id: string) {
      if (!matches(id)) return null;

      const relPath = toPosix(relative(repoRoot, id.split('?')[0]));
      const { stampSource } = await import('./transform.js');

      try {
        const result = await stampSource(code, relPath, attribute);
        for (const [hash, entry] of Object.entries(result.components)) {
          addToManifest(manifest, hash, entry);
        }
        if (debounce) clearTimeout(debounce);
        debounce = setTimeout(() => void writeManifest(), 250);
        return result.changed ? { code: result.code, map: null } : null;
      } catch (err: any) {
        // A stamping failure must never break the host app's build. The developer loses exact
        // source resolution for that file and keeps a working application.
        this.warn?.(`pointer: could not stamp ${relPath}: ${err?.message ?? err}`);
        return null;
      }
    },

    transformIndexHtml(html: string) {
      if (!enabled || wantsBuildSha === false) return html;
      const sha = typeof wantsBuildSha === 'string' ? wantsBuildSha : headSha();
      if (!sha) return html;
      if (html.includes('data-build-sha')) return html;
      // Lets a comment's status move from "applied" to "deployed" once its commit is actually live.
      return html.replace(/<html(\s|>)/, `<html data-build-sha="${sha}"$1`);
    },

    async buildEnd() {
      if (debounce) clearTimeout(debounce);
      await writeManifest();
    },
  };
}

export { componentHash } from './hash.js';
export type { Manifest, ManifestEntry } from './hash.js';
