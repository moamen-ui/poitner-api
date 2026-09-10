Here is the verified code-based evaluation of Claude’s claims, answers to the open questions, security design, Vite plugin recommendation, and the updated priority list.

### 1. VERIFY CLAUDE (Section 1)
1. **Plan limits exist/enforced**: CONFIRMED. `Domain/ValueObjects/PlanEntitlements.cs:18-37` lists all caps. `Application/Services/Implementation/CommentService.cs:89-100` actively enforces `MaxCommentsPerMonth` via `IEntitlementService`.
2. **Email budget modeled**: CONFIRMED. `PlanEntitlements.cs:30` (`EmailsPerMonth`).
3. **Retention modeled**: CONFIRMED. `PlanEntitlements.cs:27` (`RetentionDays`).
4. **Environments are two concepts**: CONFIRMED. Named stages exist in `Domain/Entity/AppEnvironment.cs` and link to URLs in `Domain/Entity/ProjectAppUrl.cs`. The Staging/Prod fixed enum is separate.
5. **ExtensionSite origins**: CONFIRMED. `ProjectAppUrl.cs:19` documents origin matching for the extension.
6. **API keys one-per-user**: CONFIRMED. `Domain/Entity/User.cs:40` (`public string? ApiKey { get; set; }`).
7. **E2E suite exists**: CONFIRMED. The `e2e/` folder contains `playwright.config.ts` and `run-e2e.sh`.
8. **AiRule, Statuses, etc. exist**: CONFIRMED. Models like `AiRule.cs` and `StatusPresentation.cs` exist in `Domain/Entity/`.

### 2. ANSWER CLAUDE'S 4 QUESTIONS
1. **AppEnvironment view on §35/§41**: Yes. Since `ProjectAppUrl` exists, §35 (Preview envs) is just a webhook wiring task (insert/delete URL rows on PR open/close). The effort drops from weeks to days.
2. **Business phase new work**: No new DB schema is needed for entitlements. The "business" phase is almost entirely wiring existing catalog limits to application code. 
3. **Widget failure behavior (from `web-component/src/element.ts`)**:
   - **Init (Network/5xx)**: `_checkWidgetActive` (`L285`) silently aborts rendering. `fetchComments` (`L634`) shows a visible error toast. 
   - **Init (401)**: `api()` throws (`L609`), calling `handle401()` which opens the visible login modal (`L451`). The toast is suppressed (`L633`).
   - **Submit (Network/5xx)**: Caught at `L1182`, shows a visible error toast ("Failed to save comment") at `L1184`.
   - **Submit (401)**: `api()` calls `handle401()` (`L609`) to open the visible login modal. The error toast is explicitly suppressed (`L1183`).
   - **Submit (409/404 - Project Disabled)**: Silently tears down the widget via `disableSilently()` (`L1160`).
4. **MCP same npm package**: There is no reason to split it. Since MCP shares the `apply` core and configuration logic, shipping it inside the CLI (`npx pointer mcp`) reduces maintenance overhead.

### 3. API KEY SECURITY DESIGN
**Design**: 
Drop the 1:1 plaintext `User.ApiKey`. Create a 1:N `ApiKeys` table containing: `UserId`, `Prefix` (e.g. `ptr_ABCD` plaintext for fast indexed lookup), `Hash` (SHA-256 of the remaining key), and `Scopes` (enum/flags: e.g. `Read`, `Apply`, `MCP`). `login-with-key` will query by `Prefix` and verify the `Hash`.
**Migration**: 
For existing plaintext `User.ApiKey` rows, split them into a prefix and suffix, hash the suffix, insert into the new `ApiKeys` table with full legacy scopes, and drop the `ApiKey` column from `User`. 
**Order Placement**: 
This must happen **BEFORE §1 (init)**. §1 writes the key into local `.pointer/credentials.env` files; it is critical to distribute scoped, hashable keys from the start rather than migrating scattered plaintext `.env` files later. It naturally precedes §24 (MCP).

### 4. PHASE 4 (VITE PLUGIN) RECOMMENDATION
**Recommendation**: 
Do **not** write a custom DOM-stamping Babel plugin that fails on Fragments/HOCs. Instead, rely on or fork the approach used by tools like `LocatorJS` or Vite's `react-babel` plugin (`@babel/plugin-transform-react-jsx-source`), which natively injects `__source` props (file, line) into the React **Fiber node** rather than the DOM element. 
**Tradeoff**: 
This requires traversing the React Fiber tree (`__reactFiber$*`) at runtime instead of reading a simple HTML attribute, making it React-specific (requires adaptors for Vue/Angular) and strictly bound to Dev Mode. However, it perfectly solves the Fragment/HOC problem without breaking the host app's render tree or polluting CSS child selectors.

### 5. UPDATED TOP 15
1. NEW-1: On-disk contract freeze *(Unchanged)*
2. **§25: Secure & Scoped API Keys** *(MOVED UP: Fix the plaintext/root key storage before `init` distributes keys to disk).*
3. §1: `npx pointer init` *(Unchanged)*
4. §3 + §4: `doctor` + `/api/meta` *(Unchanged)*
5. NEW-2: Served-file version stamp *(Unchanged)*
6. §7: `apply` core as shared lib *(Unchanged)*
7. §24: MCP server *(Unchanged)*
8. **§35: Preview environments** *(MOVED UP: Days of effort, not weeks, since `ProjectAppUrl` schema already exists).*
9. **§20: Plan limits enforcement** *(MOVED UP: DB modeling exists; this is just wiring).*
10. §45: Design tokens into `stack.json` *(Unchanged)*
11. §41: Allowed origins + comment rate limiting *(Unchanged)*
12. NEW-3: Widget release engineering *(Unchanged)*
13. §33-lite: DOM snapshot privacy *(Unchanged)*
14. NEW-4: Continuous E2E *(Unchanged)*
15. §18: Per-project scoping rules *(Unchanged)*
