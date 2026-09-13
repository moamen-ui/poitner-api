// R3-03-06 — the widget's cache-header matrix, on the API hop.
//
// Three different caching promises are made from ONE path, chosen entirely by the `v` query:
//
//   /pointer.js              → no-cache               (the floating latest build)
//   /pointer.js?v=stable     → public, max-age=3600   (opt into an hour of staleness)
//   /pointer.js?v=<hash>     → immutable, one year    (pinned: the bytes can never change)
//
// Getting one of those wrong is not a visible bug — it is a silent one. Serving the floating
// build as immutable pins every visitor to a build that will never update again; serving a pinned
// build as no-cache quietly deletes the caching the pin exists to buy. Neither shows up in any
// functional test, which is why the headers themselves are the assertion here.
//
// Pure `fetch` against :8090, under a second, so it gates every PR. The Caddy hop is R3-03-06b.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import { BASE_URL } from '../scripts/lib/api.mjs';
import { record } from '../scripts/lib/report.mjs';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const wwwroot = join(repoRoot, 'API', 'wwwroot');

const IMMUTABLE = 'public, max-age=31536000, immutable';
const STABLE = 'public, max-age=3600';
const NO_CACHE = 'no-cache';

/** Fetches a widget asset and returns status, the headers this matrix cares about, and the bytes. */
async function head(path) {
  const res = await fetch(`${BASE_URL}${path}`);
  const body = Buffer.from(await res.arrayBuffer());
  return {
    status: res.status,
    cacheControl: res.headers.get('cache-control'),
    mismatch: res.headers.get('x-pointer-widget-version-mismatch'),
    acao: res.headers.get('access-control-allow-origin'),
    body,
  };
}

const shortSha = (buf) => createHash('sha256').update(buf).digest('hex').slice(0, 12);

let manifest;
let H; // the current build's hash
let H0; // a genuinely retained OLDER build, when the server has one

test.beforeAll(async () => {
  const res = await fetch(`${BASE_URL}/pointer.version.json`);
  expect(res.status, '/pointer.version.json must be served').toBe(200);
  manifest = await res.json();
  H = manifest.hash;
  expect(H, 'the manifest must name the current build hash').toMatch(/^[0-9a-f]{12}$/);

  // The contract expected to FABRICATE an older build and to SKIP that row on the PR tier. The
  // server retains previous builds, so when a real one is present it is used instead: a retained
  // build is the actual thing a pinned page depends on, and a fabricated one only proves the route
  // works on bytes nobody will ever request.
  H0 = (manifest.retained || []).map((r) => r.hash).find((h) => h !== H);
});

test('R3-03-06 — cache-header matrix (API hop)', async () => {
  const start = Date.now();

  // 1. The manifest itself is the thing a client polls to discover a new build, so it must never
  //    be cached — a stale manifest pins everyone to yesterday's hash.
  const version = await head('/pointer.version.json');
  expect(version.status).toBe(200);
  expect(version.cacheControl, 'the manifest must not be cached').toBe(NO_CACHE);

  // 2. Bare /pointer.js is the floating latest build: no-cache, and byte-identical to what is on
  //    disk (a stale copy served from memory would be invisible here otherwise).
  const bare = await head('/pointer.js');
  expect(bare.status).toBe(200);
  expect(bare.cacheControl, 'the floating build must not be cached').toBe(NO_CACHE);
  expect(bare.body.equals(readFileSync(join(wwwroot, 'pointer.js'))), 'bare bytes must match wwwroot').toBe(true);

  // 3. ?v=stable buys an hour of staleness — deliberately NOT immutable, because the bytes behind
  //    it change on the next deploy.
  const stable = await head('/pointer.js?v=stable');
  expect(stable.status).toBe(200);
  expect(stable.cacheControl).toBe(STABLE);
  expect(stable.body.equals(bare.body), 'stable must serve the current bytes').toBe(true);

  // 4. A pinned hash is immutable for a year. The bytes must hash to the pin — that identity is
  //    the entire licence to cache them forever, and `crossorigin` on the script tag means the
  //    response also has to carry CORS or a pinned page fails to load it.
  const pinned = await head(`/pointer.js?v=${H}`);
  expect(pinned.status).toBe(200);
  expect(pinned.cacheControl).toBe(IMMUTABLE);
  expect(shortSha(pinned.body), 'pinned bytes must hash to the pin').toBe(H);
  expect(
    pinned.body.equals(readFileSync(join(wwwroot, 'widget', H, 'pointer.js'))),
    'pinned bytes must match the retained build on disk',
  ).toBe(true);
  expect(pinned.acao, 'a crossorigin <script> needs CORS on the pinned asset').toBe('*');

  // 5. One build is one `v`: the CSS pins on the same hash as the JS.
  const css = await head(`/pointer.css?v=${H}`);
  expect(css.status).toBe(200);
  expect(css.cacheControl).toBe(IMMUTABLE);

  // 6. A previous build stays served, immutably, and is genuinely different bytes. This is what
  //    makes a pin durable: a page pinned before the last deploy must keep working unchanged.
  let olderDetail = 'no retained older build on this server';
  if (H0) {
    const older = await head(`/pointer.js?v=${H0}`);
    expect(older.status, `retained build ${H0} must still be served`).toBe(200);
    expect(older.cacheControl).toBe(IMMUTABLE);
    expect(shortSha(older.body), 'the retained build must hash to its own pin').toBe(H0);
    expect(older.body.equals(pinned.body), 'a retained build must not be the current bytes').toBe(false);
    olderDetail = `retained ${H0} serves its own bytes`;
  }

  // 7. An unknown hash is a 404 — never a silent fallback to the current build, which would defeat
  //    the pin by serving different bytes under a hash that promised they could not change. The
  //    mismatch header tells the client which build the server actually has, so it can recover.
  //    The 404 body must not leak the widget source banner.
  const unknown = await head('/pointer.js?v=000000000000');
  expect(unknown.status).toBe(404);
  expect(unknown.mismatch, 'the 404 must name the version the server has').toBe(H);
  expect(unknown.body.toString('utf8')).not.toContain('GENERATED from web-component/src');

  // 8. Malformed pins are 404s, never 500s and never a path escape.
  for (const v of ['..%2F..%2Fetc%2Fpasswd', '%00', 'a'.repeat(200)]) {
    const bad = await head(`/pointer.js?v=${v}`);
    expect(bad.status, `?v=${v.slice(0, 24)} must be rejected, not error`).toBe(404);
  }

  // 8b. An EMPTY value is an unknown hash, not "no pin". This one is load-bearing beyond
  //     completeness: Caddy's `not query v=*` matcher DOES match an empty value, so `?v=` slips
  //     past the proxy's no-cache rule and lands here. If this returned the current build with a
  //     cacheable header, `?v=` would be a way to get the floating build cached.
  const empty = await head('/pointer.js?v=');
  expect(empty.status, 'an empty ?v= is an unknown hash').toBe(404);
  expect(empty.mismatch).toBe(H);

  record({
    id: 'R3-03-06', tier: 'PR', layer: 'api', role: '—', result: 'PASS', ms: Date.now() - start,
    detail: `H=${H}; no-cache/stable/immutable exact; unknown+malformed+empty → 404; ${olderDetail}`,
  });
});

test('R3-03-06b — cache-header matrix (Caddy hop)', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — needs the caddy container');
  const start = Date.now();

  // Cleartext sibling of the TLS mock-domain port. A certificate would add nothing here: what is
  // under test is which headers survive the proxy.
  const PROXY = process.env.E2E_CADDY_URL || 'http://localhost:8444';

  const reachable = await fetch(`${PROXY}/pointer.version.json`).then((r) => r.ok).catch(() => false);
  test.skip(
    !reachable,
    `caddy is not serving ${PROXY} — start it with ` +
      `docker compose -f docker-compose.yaml -f e2e/compose.caddy.yaml up -d caddy`,
  );

  const through = async (path) => {
    const res = await fetch(`${PROXY}${path}`);
    return {
      status: res.status,
      cacheControl: res.headers.get('cache-control'),
      mismatch: res.headers.get('x-pointer-widget-version-mismatch'),
    };
  };

  // The proxy sets no-cache on the unhashed widget files so a deploy actually reaches browsers.
  // Exact, not "contains": Caddy appends by default, and a response carrying
  // "no-cache, must-revalidate, no-cache" would pass a substring check while proving the file's
  // directive is not the one clients see.
  const bare = await through('/pointer.js');
  expect(bare.status).toBe(200);
  expect(bare.cacheControl).toBe('no-cache, must-revalidate');

  // …and it must NOT touch a pinned request. This is the row that matters: the @widget matcher is
  // query-blind by default, so without `not query v=*` the proxy overwrites the API's immutable
  // header and silently destroys the year of caching a pin exists to buy. Nothing about the
  // response would look wrong — the bytes are still correct.
  const pinned = await through(`/pointer.js?v=${H}`);
  expect(pinned.status).toBe(200);
  expect(pinned.cacheControl, 'the proxy must not overwrite an immutable pin').toBe(IMMUTABLE);

  const stable = await through('/pointer.js?v=stable');
  expect(stable.status).toBe(200);
  expect(stable.cacheControl, 'the proxy must not overwrite the stable channel either').toBe(STABLE);

  // An unknown pin's 404 and its recovery header pass through untouched.
  const unknown = await through('/pointer.js?v=000000000000');
  expect(unknown.status).toBe(404);
  expect(unknown.mismatch).toBe(H);

  // An EMPTY ?v= matches `query v=*`, so it is excluded from @widget and falls through to the API,
  // which treats it as an unknown hash. The alternative — the proxy claiming it as an unpinned
  // request and answering no-cache — would make `?v=` a way to get the floating build cached under
  // a URL that looks pinned.
  const empty = await through('/pointer.js?v=');
  expect(empty.status, 'an empty pin must reach the API, not be claimed by the proxy').toBe(404);
  expect(empty.mismatch).toBe(H);

  record({
    id: 'R3-03-06b', tier: 'nightly', layer: 'api', role: '—', result: 'PASS', ms: Date.now() - start,
    detail: `via ${PROXY}: no-cache exact on bare; immutable and stable preserved; 404+mismatch for unknown and empty`,
  });
});
