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
    var el = document.createElement('pointer-feedback');
    el.setAttribute('project', '%VITE_POINTER_PROJECT%');
    el.setAttribute('server', '%VITE_POINTER_SERVER%');
    el.setAttribute('environment', '%VITE_POINTER_ENV%');
    el.setAttribute('source-attr', 'data-component-source');
    document.body.appendChild(el);
  }
</script>
<!-- pointer-feedback:end -->`
        : cfg.pin
        ? `<!-- pointer-feedback:start -->
<script src="${cfg.server}/pointer.js?v=${cfg.pin.version}" integrity="${cfg.pin.integrity}" crossorigin="anonymous" defer></script>
<pointer-feedback project="${cfg.key}" server="${cfg.server}" environment="${cfg.environment}" source-attr="data-component-source"></pointer-feedback>
<!-- pointer-feedback:end -->`
        : `<!-- pointer-feedback:start -->
<script src="${cfg.server}/pointer.js" defer></script>
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
