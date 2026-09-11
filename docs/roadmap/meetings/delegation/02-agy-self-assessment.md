1. **SELF-ASSESSMENT**
1. **Mechanical .NET CRUD:** HIGH. Strong training density for EF Core/C# boilerplate. Risk: Plausible-but-wrong EF relationship mapping if I guess navigation properties instead of strictly reading the schema.
2. **Security-sensitive .NET:** MEDIUM. Risk: I might confidently implement a naive cryptographic fallback or place middleware in the wrong order, causing silent auth bypasses.
3. **Node/TypeScript CLI:** MEDIUM. Risk: AST manipulation/regex for injecting code into index.html is highly brittle; I am likely to corrupt the host file if I guess its formatting.
4. **Vite plugin:** LOW. Build lifecycle hooks are complex with undocumented edge cases. Risk: I will break source maps or misalign the transform timing.
5. **Vanilla-TS Web Component:** HIGH. Shadow DOM is predictable. Risk: Forgetting to handle specific lifecycle teardowns (`disconnectedCallback`).
6. **Frontend UI (3 frameworks):** MEDIUM. Risk: I will mix up state management paradigms across the three frameworks when context-switching, leading to subtle reactivity bugs.
7. **Test authoring:** HIGH. Playwright is heavily represented in my data. Risk: I will invent generic CSS selectors instead of using the harness's specific data attributes.
8. **Review:** HIGH. I have a massive context window to hold the spec and the diff simultaneously. Risk: I am prone to "rubber-stamping" code that looks structurally correct but misses subtle logical flaws.
9. **Orchestration:** MEDIUM. Risk: Without a strict state machine, I will hallucinate that a previously discussed task was actually implemented, dropping items from the queue.

2. **YOUR PLAN'S ECONOMICS**
- **Limits:** My reliable output length per turn (~8k tokens) and context degradation. If pushed too hard, I will truncate files or output placeholder comments (`// ... rest of code`).
- **Batching:** One vertical slice per job (e.g., 1 entity + 1 controller + 1 test file). Do not batch across domains.
- **Largest unit:** 3-5 tightly coupled files.

3. **WHAT YOU SHOULD NOT BE GIVEN**
- **Category 4 (Vite plugin).** Too fragile for my zero-shot generation; requires iterative execution and deep reasoning.
- **Category 2 (Security-sensitive cryptography).** The stakes are too high for my probability-based generation without a human in the loop.

4. **ORCHESTRATOR + REVIEWER**
- **Orchestrator:** Me (Gemini 3.1 Pro). I have a 1M+ token context window, perfect for holding the 24-item roadmap, API inventory, and project state without forgetting.
- **Reviewer:** GLM-5.2 or Claude. *Must be a different model.* If I review my own code, I will suffer from model confirmation bias and approve my own hallucinations.

5. **YOUR PROPOSED ASSIGNMENT**

| Item ID | Model | Why | Verification on my output |
|---|---|---|---|
| R1-01 (.NET CRUD) | Gemini | Fast, cheap boilerplate generation. | Strict xUnit pass + GLM review of EF migrations. |
| R1-02-cli-init | Gemini | Good at TS arg parsing and scaffolding. | Claude review of the file injection logic. |
| R1-06-api-key-hardening | Claude | Security-sensitive; needs Claude's superior cross-cutting reasoning. | N/A |
| R2-02-mcp-server | Gemini | Stdio servers are well-represented in my data. | GLM adversarial review. |
| R3-01-vite-plugin-manifest | Claude | Hardest item; Claude is best at complex build tooling. | N/A |
| R3-02 (Web/UI/Tests) | GLM-5.2 | Capable of isolated repetitive work; saves Gemini/Claude quota. | N/A |

6. **THE HANDOFF CONTRACT**
- **Prompt must contain:** The explicit `00-API-INVENTORY.md` context, the exact DTO spec, and the test harness rules. No implicit assumptions.
- **My output must include:** A machine-readable checklist of completed acceptance criteria, the exact file paths modified, and a `<NEEDS_REVIEW>` tag for any complex logic I struggled with.

7. **RISK**
- **Biggest failure:** Context drift—I will confidently report an item is "done" when I only wrote the scaffolding, and the orchestrator will move on.
- **Cheapest mitigation:** Enforce a strict rule: An item is not "done" until the test suite outputs a green CI log, and the orchestrator is forced to read that log before updating state.
