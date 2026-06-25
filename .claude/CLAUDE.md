# CLAUDE.md

> Instructions for Claude Code when working in this repository.

## Essential Reading

- **[Orval Code Generation Skill](../docs/skills/orval-codegen/SKILL.md)** — read before changing API endpoints/DTOs.
- **[Integrate Pointer Skill](../API/wwwroot/pointer-init.md)** — the consumer-facing init skill, served at
  `/pointer-init.md` (same as the apply skill `skill.md`). Follow when asked to add/init the
  `<pointer-feedback>` widget in a host app (ask for variables → detect stack → inject loader → verify).

More on the Orval skill:

This skill documents:
- How Angular services/models are auto-generated from the .NET API Swagger spec
- How to regenerate after backend API changes (`npm run generate-services`)
- How to consume generated code correctly (imports, resources, services)
- The envelope unwrapping interceptor pattern
- **Never edit files in `admin-web/src/app/core/api/generated/`** — they are auto-generated

## Quick Reference

See [../AGENTS.md](../AGENTS.md) for project overview, commands, and directory structure.

## Key Rules

1. If you add/change/remove an API endpoint or DTO on the backend, you MUST:
   a. Add `[ProducesResponseType(typeof(InnerType), 200)]` to the controller action
   b. Regenerate frontend services: `cd admin-web && npm run generate-services` (requires API running)
2. Never edit generated files under `admin-web/src/app/core/api/generated/`
3. Use `@api/*` imports in Angular code, not relative paths to generated files
4. GET endpoints produce signal-first `httpResource` functions; POST/PATCH/DELETE produce `HttpClient` service methods
5. After mutations, call `resource.reload()` to refresh data
