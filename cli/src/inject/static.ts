import { promises as fs } from 'node:fs';
import { join } from 'node:path';

/**
 * Writes the widget block into an HTML file.
 *
 * `envGuarded` picks the Vite form from pointer-init.md: values come from %VITE_POINTER_*%
 * placeholders instead of being baked in, so one index.html serves dev, staging and production and
 * the widget simply does not mount when VITE_POINTER_ENABLED is false. Hardcoding the server (the
 * plain form) would pin every build to whichever server happened to run `init`.
 */
export async function injectStatic(
    cwd: string,
    htmlPath: string | undefined,
    cfg: {
        server: string;
        key: string;
        environment: string;
        envGuarded?: boolean;
        /**
         * Pin the widget to one immutable build, with Subresource Integrity.
         *
         * Unpinned, `/pointer.js` is whatever the server is serving today — convenient, and the
         * right default for most installs. Pinned, the page loads exactly the bytes it was tested
         * against and the browser refuses anything else, which is what a site needs when a
         * third-party script sits on a page handling real users.
         */
        pin?: { version: string; integrity: string } | null;
        /**
         * Origin → environment name, from the URLs registered against the project.
         *
         * Present (or more than one environment selected) switches the injected block to the form
         * that resolves its environment at runtime instead of baking one in.
         */
        envMap?: Record<string, string>;
        /** Every environment this install covers. More than one implies the runtime-resolving form. */
        environments?: string[];
    },
): Promise<string> {
    const p = htmlPath || join(cwd, 'index.html');
    let content = await fs.readFile(p, 'utf8').catch(() => '');
    if (!content) throw new Error(`HTML file not found at ${p}`);
    
    // A pinned loader sets integrity/crossOrigin as PROPERTIES on the created element rather than
    // writing an attribute string. Same effect, and it keeps the snippet working on a host page
    // that runs a strict Content-Security-Policy.
    const pinnedSrc = cfg.pin ? `?v=${cfg.pin.version}` : '';
    const pinnedProps = cfg.pin
        ? `
    s.integrity = '${cfg.pin.integrity}';
    s.crossOrigin = 'anonymous';`
        : '';
    /** The same pin, as attributes, for the forms that write a <script> tag rather than build one. */
    const pinnedAttrs = cfg.pin
        ? ` integrity="${cfg.pin.integrity}" crossorigin="anonymous"`
        : '';
    /** More than one environment, or a known origin map, needs the runtime-resolving block. */
    const multiEnv = (cfg.environments?.length ?? 0) > 1 || Object.keys(cfg.envMap ?? {}).length > 0;

    const block = cfg.envGuarded
        ? `<!-- pointer-feedback:start -->
<script>
  if (
    '%VITE_POINTER_ENABLED%' === 'true' &&
    '%VITE_POINTER_SERVER%'.indexOf('http') === 0
  ) {
    var s = document.createElement('script');
    s.src = '%VITE_POINTER_SERVER%/pointer.js${pinnedSrc}';${pinnedProps}
    s.defer = true;
    document.head.appendChild(s);
    // Deferred until the body exists. Injected just above </body> this is already true, but the
    // block gets copied into other files by hand, and inside <head> document.body is null —
    // "Cannot read properties of null (reading 'appendChild')", and nothing mounts.
    var mount = function () {
      if (document.querySelector('pointer-feedback')) return;
      var el = document.createElement('pointer-feedback');
      el.setAttribute('project', '%VITE_POINTER_PROJECT%');
      el.setAttribute('server', '%VITE_POINTER_SERVER%');
      el.setAttribute('environment', '%VITE_POINTER_ENV%');
      el.setAttribute('source-attr', 'data-component-source');
      document.body.appendChild(el);
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount);
    else mount();
  }
</script>
<!-- pointer-feedback:end -->`
        : multiEnv
        ? // One file, several environments.
          //
          // The single-environment form writes environment="local" into the markup, which is right
          // until the same index.html is built for staging and production too — then every comment
          // from every deployment is tagged `local` and nothing can tell them apart. This form
          // resolves the environment from the page's own origin at runtime, using the URLs already
          // registered against the project, so one committed file is correct everywhere.
          `<!-- pointer-feedback:start -->
<script${pinnedAttrs} src="${cfg.server}/pointer.js${pinnedSrc}" defer></script>
<script>
  (function () {
    var ORIGINS = ${JSON.stringify(cfg.envMap ?? {})};
    var FALLBACK = '${cfg.environment}';
    function pointerEnv() {
      if (ORIGINS[location.origin]) return ORIGINS[location.origin];
      // A dev server's port changes more often than anyone updates a URL list, so localhost is
      // recognised by host rather than by exact origin.
      if (/^(localhost|127\\.0\\.0\\.1|\\[::1\\])$/.test(location.hostname)) return 'local';
      return FALLBACK;
    }
    function mount() {
      if (document.querySelector('pointer-feedback')) return;
      var el = document.createElement('pointer-feedback');
      el.setAttribute('project', '${cfg.key}');
      el.setAttribute('server', '${cfg.server}');
      el.setAttribute('environment', pointerEnv());
      el.setAttribute('source-attr', 'data-component-source');
      document.body.appendChild(el);
    }
    // document.body is null while the parser is still in <head>. Waiting for DOMContentLoaded
    // makes the snippet work wherever it is pasted, instead of only just above </body>.
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount);
    else mount();
  })();
</script>
<!-- pointer-feedback:end -->`
        : `<!-- pointer-feedback:start -->
<script${pinnedAttrs} src="${cfg.server}/pointer.js${pinnedSrc}" defer></script>
<pointer-feedback project="${cfg.key}" server="${cfg.server}" environment="${cfg.environment}" source-attr="data-component-source"></pointer-feedback>
<!-- pointer-feedback:end -->`;

    const re = /<!-- pointer-feedback:start -->[\s\S]*?<!-- pointer-feedback:end -->/;
    if (re.test(content)) {
        content = content.replace(re, block);
    } else if (content.toLowerCase().includes('</body>')) {
        content = content.replace(/(<\/body>)/i, `${block}\n$1`);
    } else {
        content += `\n${block}`;
    }
    
    await fs.writeFile(p, content, 'utf8');
    return p;
}
