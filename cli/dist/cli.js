#!/usr/bin/env node

// src/prompt.ts
import * as readline from "node:readline/promises";
import { Writable } from "node:stream";
async function ask(question, options = {}) {
  let muted = false;
  const mutableStdout = new Writable({
    write: function(chunk, encoding, callback) {
      if (!muted)
        process.stdout.write(chunk, encoding);
      callback();
    }
  });
  const rl = readline.createInterface({
    input: process.stdin,
    output: mutableStdout,
    terminal: true
  });
  const displayQuestion = options.default ? `${question} [${options.default}]: ` : `${question}: `;
  while (true) {
    process.stdout.write(displayQuestion);
    if (options.secret)
      muted = true;
    const answer = await rl.question("");
    muted = false;
    if (options.secret)
      process.stdout.write("\n");
    const finalAnswer = answer.trim() || options.default || "";
    if (options.validate) {
      const error = options.validate(finalAnswer);
      if (error) {
        console.log(`\x1B[31m${error}\x1B[0m`);
        continue;
      }
    }
    rl.close();
    return finalAnswer;
  }
}
async function select(question, items, defaultItem) {
  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
    terminal: true
  });
  const defaultLabel = defaultItem ? ` [${defaultItem}]` : "";
  console.log(`${question}${defaultLabel}`);
  items.forEach((item, i) => {
    console.log(`  ${i + 1}) ${item}`);
  });
  while (true) {
    const answer = await rl.question("> ");
    const finalAnswer = answer.trim() || defaultItem || items[0];
    const asNum = parseInt(finalAnswer, 10);
    if (!isNaN(asNum) && asNum >= 1 && asNum <= items.length) {
      rl.close();
      return items[asNum - 1];
    }
    if (items.includes(finalAnswer)) {
      rl.close();
      return finalAnswer;
    }
    console.log("\x1B[31mInvalid selection\x1B[0m");
  }
}

// src/config.ts
import { promises as fs } from "node:fs";
import { join, dirname } from "node:path";
var CONFIG_FILE = ".pointer/config.json";
var CREDENTIALS_FILE = ".pointer/credentials.env";
async function readConfig(cwd2) {
  try {
    const content = await fs.readFile(join(cwd2, CONFIG_FILE), "utf8");
    return JSON.parse(content);
  } catch (err) {
    if (err.code !== "ENOENT")
      throw err;
    return {};
  }
}
async function writeConfig(cwd2, config) {
  const file = join(cwd2, CONFIG_FILE);
  await fs.mkdir(dirname(file), { recursive: true });
  const existing = await readConfig(cwd2);
  const data = JSON.stringify({ ...existing, ...config }, null, 2) + "\n";
  await fs.writeFile(file, data, "utf8");
}
async function writeCredentials(cwd2, token) {
  const file = join(cwd2, CREDENTIALS_FILE);
  await fs.mkdir(dirname(file), { recursive: true });
  await fs.writeFile(file, `POINTER_API_KEY=${token}
`, { encoding: "utf8", mode: 384 });
  const exampleFile = join(cwd2, ".pointer/credentials.env.example");
  await fs.writeFile(exampleFile, `POINTER_API_KEY=
`, { encoding: "utf8" });
}
async function upsertGitignore(cwd2) {
  const file = join(cwd2, ".gitignore");
  let content = await fs.readFile(file, "utf8").catch(() => "");
  const entry = "\n# Pointer\n.pointer/credentials.env\n";
  if (!content.includes(".pointer/credentials.env")) {
    content += entry;
    await fs.writeFile(file, content, "utf8");
  }
}

// src/detect.ts
import { promises as fs2, existsSync } from "node:fs";
import { join as join2 } from "node:path";
async function readFileSafe(path) {
  try {
    return await fs2.readFile(path, "utf8");
  } catch {
    return "";
  }
}
async function detectAppUrl(cwd2, kind, envName) {
  if (envName !== "local")
    return { url: null, source: "environment not local" };
  if (kind === "vite") {
    const exts = ["js", "ts", "mjs", "mts"];
    for (const ext of exts) {
      const cfg = await readFileSafe(join2(cwd2, `vite.config.${ext}`));
      if (cfg) {
        let port = 5173;
        let isHttps = false;
        const portMatch = cfg.match(/port\s*:\s*(\d+)/);
        if (portMatch)
          port = parseInt(portMatch[1], 10);
        if (cfg.match(/https\s*:\s*(true|{)/))
          isHttps = true;
        if (portMatch || isHttps) {
          return { url: `http${isHttps ? "s" : ""}://localhost:${port}`, source: `vite.config.${ext}:server` };
        }
      }
    }
    const pkgStr = await readFileSafe(join2(cwd2, "package.json"));
    if (pkgStr) {
      try {
        const pkg = JSON.parse(pkgStr);
        if (pkg.scripts && pkg.scripts.dev) {
          const pm = pkg.scripts.dev.match(/--port\s+(\d+)/);
          if (pm)
            return { url: `http://localhost:${pm[1]}`, source: "package.json:scripts.dev" };
        }
      } catch {
      }
    }
    return { url: "http://localhost:5173", source: "vite default" };
  }
  if (kind === "next") {
    const pkgStr = await readFileSafe(join2(cwd2, "package.json"));
    if (pkgStr) {
      try {
        const pkg = JSON.parse(pkgStr);
        if (pkg.scripts && pkg.scripts.dev) {
          const pm = pkg.scripts.dev.match(/(?:-p|--port)\s+(\d+)/);
          if (pm)
            return { url: `http://localhost:${pm[1]}`, source: "package.json:scripts.dev" };
        }
      } catch {
      }
    }
    const envs = [".env", ".env.local", ".env.development"];
    for (const e of envs) {
      const env = await readFileSafe(join2(cwd2, e));
      const m = env.match(/^PORT=(\d+)/m);
      if (m)
        return { url: `http://localhost:${m[1]}`, source: `${e}:PORT` };
    }
    return { url: "http://localhost:3000", source: "next default" };
  }
  if (kind === "angular") {
    const angularJsonStr = await readFileSafe(join2(cwd2, "angular.json"));
    if (angularJsonStr) {
      try {
        const angularJson = JSON.parse(angularJsonStr);
        const projects = angularJson.projects || {};
        for (const proj of Object.values(projects)) {
          if (proj.architect?.serve?.options) {
            const opts = proj.architect.serve.options;
            const port = opts.port || 4200;
            const isSsl = opts.ssl === true;
            return { url: `http${isSsl ? "s" : ""}://localhost:${port}`, source: "angular.json:serve.options" };
          }
        }
      } catch {
      }
    }
    const pkgStr = await readFileSafe(join2(cwd2, "package.json"));
    if (pkgStr) {
      try {
        const pkg = JSON.parse(pkgStr);
        if (pkg.scripts && pkg.scripts.start) {
          const pm = pkg.scripts.start.match(/--port\s+(\d+)/);
          if (pm)
            return { url: `http://localhost:${pm[1]}`, source: "package.json:scripts.start" };
        }
      } catch {
      }
    }
    return { url: "http://localhost:4200", source: "angular default" };
  }
  if (kind === "cra") {
    const pkgStr = await readFileSafe(join2(cwd2, "package.json"));
    if (pkgStr) {
      try {
        const pkg = JSON.parse(pkgStr);
        if (pkg.scripts && pkg.scripts.start) {
          const pm = pkg.scripts.start.match(/PORT=(\d+)/);
          if (pm)
            return { url: `http://localhost:${pm[1]}`, source: "package.json:scripts.start" };
        }
      } catch {
      }
    }
    const envs = [".env", ".env.local", ".env.development"];
    for (const e of envs) {
      const env = await readFileSafe(join2(cwd2, e));
      const m = env.match(/^PORT=(\d+)/m);
      if (m)
        return { url: `http://localhost:${m[1]}`, source: `${e}:PORT` };
    }
    return { url: "http://localhost:3000", source: "cra default" };
  }
  return { url: null, source: "no default for stack" };
}
async function detectStack(cwd2) {
  const pkgStr = await readFileSafe(join2(cwd2, "package.json"));
  let pkg = {};
  if (pkgStr) {
    try {
      pkg = JSON.parse(pkgStr);
    } catch {
    }
  }
  const deps = { ...pkg.dependencies, ...pkg.devDependencies };
  const hasIndexHtml = existsSync(join2(cwd2, "index.html"));
  const evidence = [];
  if (deps.vite) {
    evidence.push("package.json (vite)");
    for (const ext of ["js", "ts", "mjs", "mts"]) {
      if (existsSync(join2(cwd2, `vite.config.${ext}`)))
        evidence.push(`vite.config.${ext}`);
    }
    if (hasIndexHtml)
      evidence.push("index.html");
    return { kind: "vite", evidence, htmlPath: hasIndexHtml ? join2(cwd2, "index.html") : void 0 };
  }
  if (deps.next) {
    evidence.push("package.json (next)");
    for (const ext of ["js", "mjs", "ts"]) {
      if (existsSync(join2(cwd2, `next.config.${ext}`)))
        evidence.push(`next.config.${ext}`);
    }
    if (existsSync(join2(cwd2, "app")))
      evidence.push("app/");
    return { kind: "next", evidence };
  }
  if (existsSync(join2(cwd2, "angular.json"))) {
    evidence.push("angular.json");
    return { kind: "angular", evidence };
  }
  if (deps["react-scripts"]) {
    evidence.push("package.json (react-scripts)");
    return { kind: "cra", evidence };
  }
  for (const wcfg of ["pnpm-workspace.yaml", "lerna.json", "nx.json", "turbo.json"]) {
    if (existsSync(join2(cwd2, wcfg))) {
      evidence.push(wcfg);
      if (!hasIndexHtml)
        return { kind: "monorepo", evidence };
    }
  }
  if (pkg.workspaces && !hasIndexHtml) {
    evidence.push("package.json (workspaces)");
    return { kind: "monorepo", evidence };
  }
  if (hasIndexHtml && !pkgStr) {
    evidence.push("index.html");
    return { kind: "static", evidence, htmlPath: join2(cwd2, "index.html") };
  }
  return { kind: "unknown", evidence };
}
function extractTokens(pkg) {
  const deps = { ...pkg.dependencies, ...pkg.devDependencies };
  const frontend = [];
  const backend = [];
  if (deps.react)
    frontend.push("react");
  if (deps.vue)
    frontend.push("vue");
  if (deps.svelte)
    frontend.push("svelte");
  if (deps["solid-js"])
    frontend.push("solid");
  if (deps["@angular/core"])
    frontend.push("angular");
  if (deps.next)
    frontend.push("next");
  if (deps.tailwindcss)
    frontend.push("tailwind");
  if (deps.vite)
    frontend.push("vite");
  if (deps.express || deps.fastify || deps.nest)
    backend.push("node");
  return { frontend, backend };
}

// src/inject/static.ts
import { promises as fs3 } from "node:fs";
import { join as join3 } from "node:path";
async function injectStatic(cwd2, htmlPath, cfg) {
  const p = htmlPath || join3(cwd2, "index.html");
  let content = await fs3.readFile(p, "utf8").catch(() => "");
  if (!content)
    throw new Error(`HTML file not found at ${p}`);
  const block = `<!-- pointer-feedback:start -->
<script src="${cfg.server}/pointer.js" defer></script>
<pointer-feedback project="${cfg.key}" server="${cfg.server}" environment="${cfg.environment}" source-attr="data-component-source"></pointer-feedback>
<!-- pointer-feedback:end -->`;
  const re = /<!-- pointer-feedback:start -->[\s\S]*?<!-- pointer-feedback:end -->/;
  if (re.test(content)) {
    content = content.replace(re, block);
  } else if (content.toLowerCase().includes("</body>")) {
    content = content.replace(/(<\/body>)/i, `${block}
$1`);
  } else {
    content += `
${block}`;
  }
  await fs3.writeFile(p, content, "utf8");
  return p;
}

// src/inject/vite.ts
import { promises as fs4 } from "node:fs";
import { join as join4 } from "node:path";
async function injectVite(cwd2, cfg, htmlPath) {
  const modified = [];
  const p = await injectStatic(cwd2, htmlPath, cfg);
  modified.push("index.html");
  const envPath = join4(cwd2, ".env");
  let envContent = await fs4.readFile(envPath, "utf8").catch(() => "");
  const envVars = {
    VITE_POINTER_ENABLED: "true",
    VITE_POINTER_SERVER: cfg.server,
    VITE_POINTER_PROJECT: cfg.key,
    VITE_POINTER_ENV: cfg.environment
  };
  for (const [k, v] of Object.entries(envVars)) {
    const re = new RegExp(`^${k}=.*$`, "m");
    if (re.test(envContent)) {
      envContent = envContent.replace(re, `${k}=${v}`);
    } else {
      envContent += `${envContent.endsWith("\n") || envContent === "" ? "" : "\n"}${k}=${v}
`;
    }
  }
  await fs4.writeFile(envPath, envContent, "utf8");
  modified.push(".env");
  for (const example of [".env.example", ".env.sample"]) {
    const exPath = join4(cwd2, example);
    let exContent = await fs4.readFile(exPath, "utf8").catch(() => null);
    if (exContent !== null) {
      const exVars = { ...envVars, VITE_POINTER_ENABLED: "false" };
      for (const k of ["VITE_POINTER_SERVER", "VITE_POINTER_PROJECT", "VITE_POINTER_ENV"]) {
        exVars[k] = "";
      }
      for (const [k, v] of Object.entries(exVars)) {
        const re = new RegExp(`^${k}=.*$`, "m");
        if (re.test(exContent)) {
          exContent = exContent.replace(re, `${k}=${v}`);
        } else {
          exContent += `${exContent.endsWith("\n") || exContent === "" ? "" : "\n"}${k}=${v}
`;
        }
      }
      await fs4.writeFile(exPath, exContent, "utf8");
      modified.push(example);
    }
  }
  return modified;
}

// src/skills.ts
import { promises as fs5 } from "node:fs";
import { join as join5, dirname as dirname2 } from "node:path";
async function download(url, dest, chmod = false) {
  const res = await fetch(url);
  if (!res.ok)
    throw new Error(`Failed to fetch ${url}: ${res.status}`);
  const txt = await res.text();
  await fs5.mkdir(dirname2(dest), { recursive: true });
  await fs5.writeFile(dest, txt, "utf8");
  if (chmod) {
    await fs5.chmod(dest, 493).catch(() => {
    });
  }
}
async function makeSymlink(target, path) {
  await fs5.mkdir(dirname2(path), { recursive: true });
  try {
    await fs5.symlink(target, path);
  } catch (err) {
    if (err.code === "EPERM" && process.platform === "win32") {
      await fs5.copyFile(target, path);
    } else if (err.code !== "EEXIST") {
      throw err;
    }
  }
}
async function installSkills(server, aiTool, cwd2, overrideDir) {
  const files = [];
  server = server.replace(/\/$/, "");
  const pointerSh = join5(cwd2, ".pointer", "pointer.sh");
  await download(`${server}/pointer.sh`, pointerSh, true);
  files.push(".pointer/pointer.sh");
  const agentsDir = join5(cwd2, ".agents");
  async function writeOrLink(primaryPath, skillName, isMd) {
    const url = skillName === "pointer-init" ? `${server}/pointer-init.md` : `${server}/skill.md`;
    const finalPath = overrideDir ? join5(cwd2, overrideDir, skillName, "SKILL.md") : join5(cwd2, primaryPath);
    await download(url, finalPath);
    files.push(overrideDir ? join5(overrideDir, skillName, "SKILL.md") : primaryPath);
    if (!overrideDir && (aiTool === "claude-code" || aiTool === "cursor" || aiTool === "windsurf")) {
      const symDest = join5(cwd2, ".agents", skillName, "SKILL.md");
      await makeSymlink(join5("..", "..", primaryPath), symDest);
      files.push(`.agents/${skillName}/SKILL.md`);
    }
  }
  if (aiTool === "claude-code") {
    await writeOrLink(".claude/skills/pointer-init/SKILL.md", "pointer-init", false);
    await writeOrLink(".claude/skills/pointer-feedback/SKILL.md", "pointer-feedback", false);
  } else if (aiTool === "cursor") {
    await writeOrLink(".cursor/rules/pointer-init.md", "pointer-init", true);
    await writeOrLink(".cursor/rules/pointer-feedback.md", "pointer-feedback", true);
  } else if (aiTool === "windsurf") {
    await writeOrLink(".windsurf/rules/pointer-init.md", "pointer-init", true);
    await writeOrLink(".windsurf/rules/pointer-feedback.md", "pointer-feedback", true);
  } else {
    await writeOrLink(".agents/pointer-init/SKILL.md", "pointer-init", false);
    await writeOrLink(".agents/pointer-feedback/SKILL.md", "pointer-feedback", false);
  }
  return files;
}

// src/api.ts
var ApiError = class extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
};
async function api(server, path, options = {}) {
  const url = `${server.replace(/\/$/, "")}${path}`;
  const headers = { "Accept": "application/json" };
  if (options.token)
    headers["Authorization"] = `Bearer ${options.token}`;
  if (options.body)
    headers["Content-Type"] = "application/json";
  const res = await fetch(url, {
    method: options.method || "GET",
    headers,
    body: options.body ? JSON.stringify(options.body) : void 0
  });
  if (!res.ok) {
    let msg = res.statusText;
    try {
      const body2 = await res.json();
      if (body2.message)
        msg = body2.message;
    } catch {
    }
    throw new ApiError(res.status, msg);
  }
  if (res.status === 204)
    return {};
  const body = await res.json();
  return body.data !== void 0 ? body.data : body;
}

// src/branding.ts
async function getBranding(server) {
  try {
    const controllerPath = server.includes("localhost") ? "/api/meta/branding" : "/api/branding";
    return await api(server, "/api/branding");
  } catch (e) {
    console.error(`Could not reach ${server} \u2014 check the URL.`);
    process.exit(1);
  }
}

// src/events.ts
async function postEvent(server, token, payload) {
  if (!token)
    return;
  try {
    await api(server, "/api/events", { method: "POST", body: payload, token });
  } catch (e) {
  }
}

// src/checks.ts
async function runInitChecks(server, projectKey, env, token) {
  return [];
}

// src/commands/init.ts
import { promises as fs6 } from "node:fs";
import { join as join6 } from "node:path";
async function initCommand(cwd2, options = {}) {
  const isYes = options["yes"] || options["json"];
  const isJson = options["json"];
  if (isYes) {
    if (!options["key"]) {
      console.error("Missing flag: --key is required with --yes");
      process.exit(2);
    }
    if (!options["project"] && !options["create"]) {
      console.error("Missing flag: --project or --create is required with --yes");
      process.exit(2);
    }
  }
  const config = await readConfig(cwd2).catch(() => ({}));
  let server = options["server"] || config.server || process.env.POINTER_SERVER || globalThis.DEFAULT_SERVER || "https://api.pointer.moamen.work";
  if (!isYes && !options["server"] && !config.server) {
    server = await ask("Server URL", { default: server });
  }
  const branding = await getBranding(server);
  const product = branding.productName || "Pointer";
  let key = options["key"];
  let me = null;
  if (isYes) {
    try {
      me = await api(server, "/api/auth/me", { token: key });
    } catch (err) {
      if (isJson) {
        console.log(JSON.stringify({ ok: false, error: { code: 3, message: "Invalid API key" } }));
      } else {
        console.error("Invalid API key.");
      }
      process.exit(3);
    }
  } else {
    let attempts = 0;
    while (!me) {
      if (!key) {
        key = await ask(`API key (from ${product} -> profile -> API key; input hidden)`, { secret: true });
      }
      try {
        const res = await api(server, "/api/auth/login-with-key", { method: "POST", body: { apiKey: key } }).catch(() => api(server, "/api/auth/me", { token: key }));
        me = res;
        if (!me.displayName && res.token) {
          me = await api(server, "/api/auth/me", { token: res.token || key });
        }
      } catch (err) {
        attempts++;
        if (attempts >= 3) {
          console.error("Invalid API key.");
          process.exit(3);
        }
        console.error("Invalid API key.");
        key = "";
      }
    }
    console.log(`\u2714 Signed in as ${me.displayName} (${me.roleName || "User"})`);
  }
  await writeCredentials(cwd2, key);
  await upsertGitignore(cwd2);
  let project = options["project"];
  let create = options["create"];
  let finalProjectKey = project || "";
  let projectName = create || finalProjectKey;
  let created = false;
  if (!isYes && !project && !create) {
    const projects = await api(server, "/api/admin/projects", { token: key }).catch(() => []);
    const createOpt = "\uFF0B Create a new project\u2026";
    const choices = projects.map((p) => `${p.name}  (${p.key})`).concat(createOpt);
    let choice = createOpt;
    if (projects.length > 0) {
      choice = await select("Which project is this app?", choices);
    }
    if (choice === createOpt) {
      const name = await ask("Project name");
      let derivedKey = name.toLowerCase().replace(/[^a-z0-9-]/g, "-").replace(/-+/g, "-").replace(/^-|-$/g, "");
      finalProjectKey = await ask("Project key", { default: derivedKey, validate: (v) => /^[a-z0-9-]+$/.test(v) ? void 0 : "Must match ^[a-z0-9-]+$" });
      projectName = name;
      created = true;
    } else {
      const match = choice.match(/\((.*?)\)$/);
      if (match)
        finalProjectKey = match[1];
    }
  } else if (create) {
    created = true;
    let derivedKey = create.toLowerCase().replace(/[^a-z0-9-]/g, "-").replace(/-+/g, "-").replace(/^-|-$/g, "");
    finalProjectKey = project || derivedKey;
    projectName = create;
  }
  if (created) {
    try {
      await api(server, "/api/admin/projects", { method: "POST", body: { key: finalProjectKey, name: projectName }, token: key });
    } catch (err) {
      if (err instanceof ApiError && err.code === 409) {
        console.error("Key already exists, choose another.");
        process.exit(1);
      } else if (err instanceof ApiError && err.code === 403) {
        console.error("This account cannot create projects.");
        process.exit(3);
      } else if (err instanceof ApiError && err.code === 400) {
        console.error(err.message);
        process.exit(1);
      }
    }
  }
  let env = options["environment"] || "local";
  if (!isYes && !options["environment"]) {
    env = await select("Environment", ["local", "staging", "production"], env);
  }
  const appInfo = await detectStack(cwd2);
  let appUrl = options["app-url"];
  let noAppUrl = options["no-app-url"];
  let source = "";
  if (!noAppUrl && !appUrl) {
    const detected = await detectAppUrl(cwd2, appInfo.kind, env);
    source = detected.source;
    if (!isYes) {
      const displayDefault = detected.url ? detected.url : "";
      const ans = await ask(`Where does this app run in ${env}?`, { default: displayDefault });
      appUrl = ans || void 0;
    } else {
      appUrl = detected.url || void 0;
    }
  }
  if (appUrl && env !== "local") {
  }
  let tool = options["tool"];
  if (!tool) {
    if (process.env.CLAUDECODE || process.env.CLAUDE_CODE_ENTRYPOINT)
      tool = "claude-code";
    else if (process.env.ANTIGRAVITY_AGENT || process.env.GEMINI_CLI)
      tool = "antigravity";
    else if (process.env.TERM_PROGRAM && process.env.TERM_PROGRAM.includes("Cursor"))
      tool = "cursor";
    else if (process.env.WINDSURF)
      tool = "windsurf";
    else if (process.env.OPENCODE)
      tool = "opencode";
    else
      tool = isYes ? "other" : "claude-code";
    if (!isYes && !options["tool"]) {
      tool = await select("AI tool", ["claude-code", "cursor", "windsurf", "opencode", "antigravity", "other"], tool);
    }
  }
  if (!isJson)
    console.log(`Detecting your stack... -> ${appInfo.kind} (${appInfo.evidence.join(", ")})`);
  let injected = false;
  let routedToSkill = false;
  let filesMod = [];
  if (!options["no-inject"]) {
    if (appInfo.kind === "vite") {
      filesMod = await injectVite(cwd2, { server, key: finalProjectKey, environment: env }, options["html"]);
      injected = true;
      if (!isJson)
        console.log(`Injected widget into ${filesMod.join(", ")}`);
    } else if (appInfo.kind === "static") {
      const htmlPath = await injectStatic(cwd2, options["html"], { server, key: finalProjectKey, environment: env });
      filesMod = [htmlPath];
      injected = true;
      if (!isJson)
        console.log(`Injected widget into ${htmlPath}`);
    } else if (appInfo.kind !== "unknown") {
      routedToSkill = true;
      if (!isJson) {
        console.log(`\u2139 ${appInfo.kind} detected \u2014 automatic injection isn't supported for this stack yet.
  The pointer-init skill was installed for ${tool}. Run it and it will mount the widget for you:
    claude -> /pointer-init (or @pointer-init for cursor)
  Config is already saved in .pointer/config.json, so the skill won't ask for the key or project again.`);
      }
    }
  }
  if (!options["no-skills"]) {
    if (!isJson)
      console.log("Installing AI skills");
    const skillsDir = options["skills-dir"];
    const installed = await installSkills(server, tool, cwd2, skillsDir);
    filesMod.push(...installed);
  }
  const pkgStr = await fs6.readFile(join6(cwd2, "package.json"), "utf8").catch(() => "{}");
  const tokens = extractTokens(JSON.parse(pkgStr));
  const stackMeta = { frontend: tokens.frontend, backend: tokens.backend, aiTool: tool };
  try {
    await api(server, `/api/projects/${finalProjectKey}/stack`, { method: "POST", body: stackMeta, token: key });
    await fs6.mkdir(join6(cwd2, ".pointer"), { recursive: true });
    await fs6.writeFile(join6(cwd2, ".pointer/stack.json"), JSON.stringify(stackMeta, null, 2), "utf8");
  } catch (e) {
    if (!isJson)
      console.log(`\u26A0 Stack not registered (${e.code || 500})`);
  }
  await writeConfig(cwd2, { server, project: finalProjectKey, environment: env, aiTool: tool, skillsDir: options["skills-dir"], cliVersion: "0.1.0" });
  filesMod.push(".pointer/config.json");
  if (!isJson)
    console.log("Verifying...");
  const checks = await runInitChecks(server, finalProjectKey, env, key);
  if (!isJson) {
    for (const c of checks) {
      console.log(`${c.status === "ok" ? "\u2714" : "\u2718"} ${c.id}: ${c.message}`);
    }
  }
  await postEvent(server, key, { type: "installed", projectKey: finalProjectKey, meta: { stack: stackMeta, aiTool: tool, injected, cliVersion: "0.1.0" } });
  if (isJson) {
    console.log(JSON.stringify({
      ok: true,
      product,
      server,
      project: { key: finalProjectKey, name: projectName, created },
      environment: env,
      appUrl: appUrl || null,
      appUrlSource: source,
      aiTool: tool,
      stack: { kind: appInfo.kind, evidence: appInfo.evidence },
      injected,
      routedToSkill,
      files: filesMod,
      checks,
      cliVersion: "0.1.0"
    }));
    process.exit(0);
  }
  console.log(`Summary:
\u2714 ${product} is set up for project "${projectName}" (${finalProjectKey})
  \u2022 Widget: ${injected ? `injected into index.html (${appInfo.kind})` : `run the pointer-init skill in ${tool} (${appInfo.kind})`}
  \u2022 Key: .pointer/credentials.env (gitignored)
  \u2022 Skills: installed

Next: start your dev server, open the app, click the ${product} button and sign in.
      Dashboard: ${branding.urls?.app || server}`);
  process.exit(0);
}

// src/cli.ts
import { argv, cwd } from "node:process";
function parseArgs(args) {
  const parsed = {};
  const positionals = [];
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg.startsWith("--")) {
      const key = arg.slice(2);
      if (key === "no-app-url" || key === "no-inject" || key === "no-skills" || key === "yes" || key === "json" || key === "help") {
        parsed[key] = true;
      } else if (i + 1 < args.length && !args[i + 1].startsWith("-")) {
        parsed[key] = args[i + 1];
        i++;
      } else {
        parsed[key] = true;
      }
    } else if (arg.startsWith("-")) {
      const key = arg.slice(1);
      if (key === "y")
        parsed["yes"] = true;
      if (key === "h")
        parsed["help"] = true;
    } else {
      positionals.push(arg);
    }
  }
  return { parsed, positionals };
}
var HELP = `
Usage: pointer <command> [options]

Commands:
  init      Initialize Pointer in your project
  doctor    Run health checks (stub)

Options:
  -h, --help    Show this help message
`;
async function main() {
  const { parsed, positionals } = parseArgs(argv.slice(2));
  const command = positionals[0];
  if (parsed["help"] || command === "--help" || !command) {
    if (!command || command === "--help") {
      console.log(HELP);
      process.exit(0);
    }
  }
  if (command === "init") {
    if (parsed["help"]) {
      console.log(`
Usage: pointer init [options]

Options:
  --server <url>           Pointer server URL
  --key <key>              API key
  --project <key>          Project key
  --create <name>          Create project with name
  --environment <env>      Environment (default: local)
  --tool <tool>            AI tool
  --skills-dir <path>      Skills directory
  --app-url <url>          App URL
  --no-app-url             Skip App URL
  --html <path>            HTML file to inject into
  --no-inject              Skip injection
  --no-skills              Skip skills installation
  -y, --yes                Non-interactive
  --json                   JSON output (implies --yes)
  -h, --help               Show help
`);
      process.exit(0);
    }
    await initCommand(cwd(), parsed);
  } else if (command === "doctor") {
    console.log("Doctor command is currently stubbed.");
  } else {
    console.error(`Unknown command: ${command}`);
    process.exit(2);
  }
}
main().catch((err) => {
  console.error("Fatal error:", err);
  process.exit(1);
});
