1. VERIFY GLM
- **Factual Issue 1 (§19 assumes workspaces don't exist)**: CONFIRMED. `Application/Services/Implementation/TenantService.cs:12-445` already handles workspaces, billing, and roles natively.
- **Factual Issue 2 (Project list is admin-gated)**: WRONG. `API/Controllers/Admin/ProjectsController.cs:11-17` explicitly notes authorization was broadened to plain `[Authorize]` so stakeholders can list/create their own projects. GLM misread the route path `/api/admin/projects` as an authorization gate.
- **Factual Issue 3 (pointer-init.md contradicts code)**: CONFIRMED. `API/wwwroot/pointer-init.md:11-12` says projects self-register, but line 25 says they must already exist.
- **Factual Issue 4 (attr-name split in Phase 4)**: CONFIRMED. `API/Program.cs:309` and `web-component/src/capture.ts:246-249` hardcode `data-component-source`, while Phase 4 proposes `data-pf`. 
- **Factual Issue 5 (§41 threat model overstated)**: CONFIRMED. Comments require JWT auth, so anonymous web users cannot flood the endpoint.
- **Factual Issue 6 (§13 misnames IsQuickAccess)**: CONFIRMED. It is the `QuickAccess` boolean flag on the `Role` entity (`Domain/Entity/Role.cs:28`).
- **On-disk contract freeze table**: CONFIRMED. Correctly identifies what is baked into customer repos and necessitates strict preservation.

2. WHERE I DISAGREE WITH GLM'S FINAL ORDER
- **Move §41 (Allowed origins) out of slot 1**: GLM prioritized this due to an overstated threat model. Since comments require JWTs, an attacker must be authenticated. §1 (`npx pointer init`) must be #1 to fix the immediate funnel issue.
- **Hold Phase 4 (Vite plugin)**: GLM kept this at #9. Rewriting component roots via a build plugin is highly invasive and breaks easily on React Fragments (`<>...</>`) or Higher Order Components. Delay it until heuristic grep is proven insufficient.
- **§25 (Scoped API keys) must precede MCP (§24)**: GLM moved MCP to #7 but held §25. Handing a root API key to an autonomous AI agent (Claude Code/Cursor) via `.mcp.json` is a severe security hazard. Scoped keys must ship first.
- **Claude's §18 (Scoping rules) was sidelined**: Both reviewers let scoping rules slide to the hold list, but this is a critical trust/safety feature to safely execute the agency model (§44).

3. WHAT BOTH MISSED
1. **Dashboard Repo (Angular/Orval) Blast Radius**: The dashboard is a completely separate repo. Every API DTO/enum change requires cross-repo PR coordination, doubling the frontend effort and increasing deployment complexity.
2. **EF Core Migrations in Self-Hosting**: Adding columns or enums requires EF migrations. In a self-hosted Docker Compose setup, these migrations must be strictly backward-compatible to avoid locking tables and breaking instances on `docker-compose up`.
3. **API Key Storage Security**: The plan relies heavily on personal API keys for the CLI/MCP but doesn't specify storage. If hashed securely, the `login-with-key` endpoint needs a prefix/ID index to avoid full table scans.
4. **CSP Nonces for Shadow DOM**: The widget injects styles (`pointer.css`) and scripts. Strict host CSPs will block this unless the widget dynamically inherits and passes `nonce` attributes from the host `index.html`.
5. **Widget Bundle Size Bloat**: `web-component/src/capture.ts:10-11` vendors Snapdom (~123KB). Adding client heuristics (§46) risks bloating the payload that loads continuously on every customer page.
6. **Tenant Query Filter Leaks**: EF Global Query Filters enforce data isolation. Future cross-tenant features (like agency clients in §44) risk catastrophic data leaks if `IgnoreQueryFilters` is used carelessly to fulfill those features.
7. **Caddy / Docker Compose Limits**: Self-hosting relies on a single VM. Adding heavy webhook dispatchers (§32) or cloud apply runners (§43) will easily saturate a small VM's memory.

4. YOUR TOP 15
1. NEW-1: On-disk contract freeze (Decide env vars, `stack.json` schema, tag names).
2. §1: `npx pointer init` (Server → key → project → env → Vite/static inject).
3. §3 + §4: `doctor` + `/api/meta` (Shipped inside the CLI release).
4. NEW-2: Served-file version stamp.
5. §25: Scoped API keys (Read-only or apply-only scopes before handing to AI tools).
6. §7: `apply` core as shared lib + CLI entry.
7. §24: MCP server (Built on the apply core, using scoped keys).
8. §45: Design tokens into `stack.json` (Do this early so the file schema is stable).
9. §10: In-app author notification with commit link.
10. §41: Allowed origins + comment rate limiting.
11. NEW-3: Widget release engineering (Immutable versions, SRI, budget).
12. §33-lite: DOM snapshot privacy.
13. §13: Quick-access invites.
14. NEW-4: Continuous E2E (Init → comment → apply on fresh Vite/Angular app).
15. §18: Per-project scoping rules (Crucial for agency trust).

5. QUESTIONS FOR THE OTHER TWO
1. How are personal API keys stored securely in Postgres, and how does `login-with-key` query them quickly without a plaintext index?
2. How will Phase 4's Vite plugin reliably stamp `data-component-source` on React Fragments (`<>...</>`) or Higher Order Components without breaking the host app's render tree?
3. Since the dashboard is a separate Angular repo, how will we deploy breaking API schema changes without breaking existing self-hosted dashboards?
4. How do we pass CSP nonces to the widget's injected `<link>` and `<script>` tags so it doesn't break in apps with strict `default-src 'none'` policies?
5. How does the cloud apply runner (§43) fit into the single-VM Docker Compose architecture for self-hosted users in terms of memory overhead?
