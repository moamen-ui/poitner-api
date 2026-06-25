# Pointer API — Implementation Tasks (living tracker)

> **This file is updated as work proceeds.** When starting a task set its status to 🟡, when done
> set it to ✅. Keep the "Last updated" line current. Statuses: ⬜ Not started · 🟡 In progress ·
> ✅ Done · ⛔ Blocked. Task IDs match [`PLAN.md`](PLAN.md) 1:1.

**Last updated:** 2026-06-23 — _all phases (0–9) implemented & verified locally; full API + browser e2e pass. Work uncommitted (no git commits by tooling)._

| Task | Description | Status |
|------|-------------|--------|
| **Phase 0** | **Scaffold & DX** | |
| 0.1 | Solution + four projects + refs + packages | ✅ |
| 0.2 | Docker, just, CSharpier, `.env.example` | ✅ |
| **Phase 1** | **Domain** | |
| 1.1 | BaseEntity + Role/CommentStatus/EnvironmentTag enums | ✅ |
| 1.2 | User/Project/Comment/Reply entities + ElementCapture (+ User.PublicId) | ✅ |
| **Phase 2** | **Application foundations** | |
| 2.1 | Result / Result<T> / PagedData / Pagination | ✅ |
| 2.2 | ICurrentUser / ITokenService / IPasswordHasher seams + MessageKeys | ✅ |
| **Phase 3** | **Infrastructure** | |
| 3.1 | AppDbContext + snake_case mappings + jsonb element + audit | ✅ |
| 3.2 | Generic Repository + UnitOfWork | ✅ |
| 3.3 | BCrypt password hasher (+ unit test) | ✅ |
| 3.4 | JWT token service + HttpCurrentUser (+ unit test) | ✅ |
| 3.5 | Infrastructure DI registration | ✅ |
| **Phase 4** | **Application services & DTOs** | |
| 4.1 | Auth DTOs + AuthService + LoginValidator | ✅ |
| 4.2 | User (admin) DTOs + UserService + validator | ✅ |
| 4.3 | Project DTOs + ProjectService + validator | ✅ |
| 4.4 | Comment/Reply DTOs + CommentService + validator (+ test) | ✅ |
| 4.5 | Application DI (Scrutor + FluentValidation) | ✅ |
| **Phase 5** | **API** | |
| 5.1 | Program.cs pipeline, JWT bearer, CORS, Swagger, static files | ✅ |
| 5.2 | AuthController | ✅ |
| 5.3 | Admin Users + Projects controllers | ✅ |
| 5.4 | Comments + Replies controllers | ✅ |
| 5.5 | Admin seeder + initial migration + e2e smoke | ✅ |
| **Phase 6** | **Static assets** | |
| 6.1 | Port pointer.js + login + bearer + new element payload | ✅ |
| 6.2 | Updated skill.md (auth + new endpoints) | ✅ |
| **Phase 7** | **Admin UI (build-free)** | |
| 7.1 | Login + Users + Projects pages | ✅ |
| **Phase 8** | **Host-app cutover + skill install** | |
| 8.1 | Point the host app `.env` at new API + full e2e | ✅ |
| 8.2 | Install AI skill in consuming repos | ✅ |
| **Phase 9** | **Docs sync** | |
| 9.1 | Finalize README; keep DESIGN/TASKS current | ✅ |

### Phase 6–9 verification notes (this session)

- **6.1 fix:** `CommentListItemDto` now includes `Element` + `Replies`, and `CommentService.ListAsync`
  projects them (with `.Include(Replies)`). Without this the list/AI-queue returned no element capture,
  breaking pins **and** the AI apply flow (DESIGN §7 requires a self-contained queue). Build clean.
- **6.2:** `API/wwwroot/skill.md` rewritten for login (`POINTER_EMAIL`/`POINTER_PASSWORD`), the
  `?status=2` queue, the nested camelCase `element` (stringified sub-fields), and PATCH apply.
- **7.1:** `API/wwwroot/admin/{index.html,app.js,style.css}` — admin login + Users (add/role/disable)
  + Projects (add/disable). Browser-verified: login, panels render, data loads.
- **8.1:** `apps/my-app/.env` → `VITE_POINTER_SERVER=http://localhost:8090` (loader unchanged).
- **e2e (curl + Playwright):** admin login (role 1) · project list · comment create w/ element ·
  list & `?status=2` queue carry `element.sourcePath` · ReadyToApply → Applied + reply + `appliedByLabel`
  + `appliedAt` · authz: Tester (role 4) → **403** on admin, can comment (author bound to account) ·
  overlay login as Tester + pick element + submit comment → persisted (author = account, source captured).
