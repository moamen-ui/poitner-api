1. **S1–S14**: ACCEPT.
**D1–D3**: ACCEPT. (Verified D2: `framework-source.ts` relies on `__reactFiber`, `_debugStack`, and `__vueParentComponent` which are all dev-mode-only internals stripped in production; the Phase 4 Vite plugin is strictly necessary).
**C1–C15**: ACCEPT.

2. **Effort sanity**: C4 (`doctor` + `GET /api/meta`) at 2–3d is >2x off. It's a static version endpoint and a basic CLI ping. My estimate: **≤ 1 day**.

3. **Release 1 cut line**: C7 (NEW-4 continuous verification). It falls to Release 2. If 3 weeks are tight, shipping the public CLI/auth contract (§1, §2, NEW-5) takes priority over internal white-label CI jobs.

4. **One thing you'd still add**: Project soft/hard deletion. Since S4 enables self-serve project creation and D1 opens tenant-wide access, users will inevitably create test projects and need a way to clean them up without admin DB intervention.
