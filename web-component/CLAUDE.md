# web-component (`<pointer-feedback>`)

- `npm run build` writes the served bundle: `../API/wwwroot/widget.js`, `widget.css`, the pinned
  `widget/<hash>/` copy and `pointer.version.json`. Commit all of them after a component change — the Docker
  image bakes in `wwwroot`, so a deploy only ships what is committed. Never hand-edit them.
- `API/wwwroot/pointer.js` / `pointer.css` are stale leftovers, not build outputs — never commit changes to them.
- Keep the gzipped bundle inside the build's size budget (it fails the build when exceeded; headroom is small).
- **Theming:** styles use `var(--fbk-*, default)` tokens (defaults in `src/styles/_variables.scss`); consumers
  override per project from their own CSS, e.g. `pointer-feedback { --fbk-primary: #0aa36e; }`.
