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

// src/build-constants.ts
var BUILD_DEFAULT_SERVER = true ? "https://api.pointer.moamen.work" : "https://api.pointer.moamen.work";
var BUILD_CLI_VERSION = true ? "0.1.0" : "0.0.0-dev";

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
async function upsertGitignore(cwd2, productName = "Feedback tool") {
  const file = join(cwd2, ".gitignore");
  let content = await fs.readFile(file, "utf8").catch(() => "");
  const entry = [
    "",
    `# ${productName}`,
    ".pointer/",
    "!.pointer/credentials.env.example",
    "!.pointer/stack.json",
    "!.pointer/pointer.sh",
    "!.pointer/config.json",
    ""
  ].join("\n");
  if (!content.includes("\n.pointer/\n") && !content.startsWith(".pointer/\n")) {
    content = content.replace(/\n?# [^\n]*\n\.pointer\/credentials\.env\n/, "");
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
  const block = cfg.envGuarded ? `<!-- pointer-feedback:start -->
<script>
  if (
    '%VITE_POINTER_ENABLED%' === 'true' &&
    '%VITE_POINTER_SERVER%'.indexOf('http') === 0
  ) {
    var s = document.createElement('script');
    s.src = '%VITE_POINTER_SERVER%/pointer.js';
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
<!-- pointer-feedback:end -->` : `<!-- pointer-feedback:start -->
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
  const p = await injectStatic(cwd2, htmlPath, { ...cfg, envGuarded: true });
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
var SKILL_FILES = {
  "claude-code": [".claude/skills/pointer-init/SKILL.md", ".claude/skills/pointer-feedback/SKILL.md"],
  cursor: [".cursor/rules/pointer-init.md", ".cursor/rules/pointer-feedback.md"],
  windsurf: [".windsurf/rules/pointer-init.md", ".windsurf/rules/pointer-feedback.md"],
  other: [".agents/pointer-init/SKILL.md", ".agents/pointer-feedback/SKILL.md"]
};
async function installSkills(server, aiTool, cwd2, overrideDir) {
  const files = [];
  server = server.replace(/\/$/, "");
  const pointerSh = join5(cwd2, ".pointer", "pointer.sh");
  await download(`${server}/pointer.sh`, pointerSh, true);
  files.push(".pointer/pointer.sh");
  const agentsDir = join5(cwd2, ".agents");
  async function writeOrLink(primaryPath, skillName, isMd2) {
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
  const layout = SKILL_FILES[aiTool] ?? SKILL_FILES.other;
  const isMd = aiTool === "cursor" || aiTool === "windsurf";
  await writeOrLink(layout[0], "pointer-init", isMd);
  await writeOrLink(layout[1], "pointer-feedback", isMd);
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
  let branding;
  try {
    branding = await api(server, "/api/branding");
  } catch {
    console.error(`Could not reach ${server} \u2014 check the URL.`);
    process.exit(1);
  }
  if (!branding?.productName) {
    console.error(`${server} returned no product name \u2014 cannot continue without branding.`);
    process.exit(1);
  }
  return { productName: branding.productName, urls: { app: branding.urls?.app ?? "" } };
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
import { promises as fs7 } from "node:fs";
import { join as join7 } from "node:path";
import { execFile } from "node:child_process";
import { promisify } from "node:util";

// src/lib/skill-stamp.ts
import { promises as fs6 } from "node:fs";
async function readStamp(path) {
  let content;
  try {
    content = await fs6.readFile(path, "utf8");
  } catch {
    return null;
  }
  const lines = content.split("\n");
  if (path.endsWith(".md")) {
    if (lines[0]?.trim() !== "---")
      return null;
    let close = -1;
    for (let i = 1; i < lines.length; i++) {
      if (lines[i].trim() === "---") {
        close = i;
        break;
      }
    }
    if (close === -1)
      return null;
    for (let i = close + 1; i < lines.length; i++) {
      const match = lines[i].match(/pointer-skill-version:\s*([^\s>-][^>]*?)\s*(?:-->)?\s*$/);
      if (match)
        return match[1].trim();
      if (lines[i].trim() !== "" && !lines[i].trim().startsWith("<!--"))
        break;
    }
    return null;
  }
  if (path.endsWith(".sh")) {
    const match = lines[1]?.match(/^#\s*pointer-skill-version:\s*(.+?)\s*$/);
    return match ? match[1].trim() : null;
  }
  return null;
}

// src/lib/skill-paths.ts
import { join as join6 } from "node:path";
function skillFilesFor(config) {
  const layout = SKILL_FILES[config.aiTool ?? ""] ?? SKILL_FILES.other;
  const skillPaths = config.skillsDir ? ["pointer-init", "pointer-feedback"].map((name) => join6(config.skillsDir, name, "SKILL.md")) : [...layout];
  return [...skillPaths, ".pointer/pointer.sh"];
}

// src/checks.ts
var execFileAsync = promisify(execFile);
function compareSemver(a, b) {
  const parse = (v) => {
    const [core, pre] = String(v ?? "0.0.0").trim().replace(/^v/, "").split("-");
    const nums = core.split(".").map((n) => parseInt(n, 10) || 0);
    return { nums: [nums[0] ?? 0, nums[1] ?? 0, nums[2] ?? 0], pre: pre ?? null };
  };
  const x = parse(a);
  const y = parse(b);
  for (let i = 0; i < 3; i++) {
    if (x.nums[i] !== y.nums[i])
      return x.nums[i] - y.nums[i];
  }
  if (x.pre === y.pre)
    return 0;
  if (x.pre === null)
    return 1;
  if (y.pre === null)
    return -1;
  return x.pre < y.pre ? -1 : 1;
}
async function fetchWithTimeout(url, ms, init) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), ms);
  try {
    return await fetch(url, { ...init, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}
async function readCredentialsKey(cwd2) {
  try {
    const raw = await fs7.readFile(join7(cwd2, ".pointer/credentials.env"), "utf8");
    const match = raw.match(/^POINTER_API_KEY=(.*)$/m);
    return match?.[1]?.trim() || void 0;
  } catch {
    return void 0;
  }
}
async function runInitChecks(cwd2, overrides = {}, cliVersion = "0.0.0") {
  const checks = [];
  const config = await readConfig(cwd2);
  const server = (overrides.server || config.server || "").replace(/\/$/, "");
  const project = overrides.project || config.project || "";
  const environment = config.environment || "local";
  if (server && project) {
    checks.push({ id: "config", status: "ok", message: `${project} @ ${server} (${environment})` });
  } else {
    checks.push({
      id: "config",
      status: "error",
      message: "No .pointer/config.json",
      hint: "Run `npx -y pointer-feedback init`"
    });
    return checks;
  }
  let serverReachable = false;
  try {
    const res = await fetchWithTimeout(`${server}/api/branding`, 3e3);
    serverReachable = res.ok;
    checks.push(
      res.ok ? { id: "server", status: "ok", message: `Reached ${server}` } : { id: "server", status: "error", message: `Cannot reach ${server} (HTTP ${res.status})` }
    );
  } catch {
    checks.push({ id: "server", status: "error", message: `Cannot reach ${server}` });
  }
  let meta = null;
  if (serverReachable) {
    try {
      meta = await api(server, "/api/meta");
      const min = meta?.minCliVersion || "0.0.0";
      if (compareSemver(cliVersion, min) < 0) {
        checks.push({
          id: "meta",
          status: "error",
          message: `CLI ${cliVersion} is older than the server requires (${min})`,
          hint: "Run `npx -y pointer-feedback@latest doctor`"
        });
        return checks;
      }
      checks.push({ id: "meta", status: "ok", message: `Server ${meta?.version ?? "unknown"} (api v${meta?.apiVersion ?? "?"})` });
    } catch (err) {
      checks.push(
        err instanceof ApiError && err.code === 404 ? { id: "meta", status: "warn", message: "Server predates /api/meta" } : { id: "meta", status: "warn", message: `Could not read /api/meta: ${err?.message ?? err}` }
      );
    }
  }
  if (meta?.serverTime) {
    const skewMs = Math.abs(Date.now() - new Date(meta.serverTime).getTime());
    const skewSeconds = Math.round(skewMs / 1e3);
    checks.push(
      skewMs < 5 * 6e4 ? { id: "clock", status: "ok", message: `Clock within ${skewSeconds}s of the server` } : { id: "clock", status: "warn", message: `Clock skew ${skewSeconds}s \u2014 logins may fail` }
    );
  }
  const apiKey = await readCredentialsKey(cwd2);
  let token;
  if (!apiKey) {
    checks.push({
      id: "key",
      status: "error",
      message: "No POINTER_API_KEY in .pointer/credentials.env",
      hint: "Copy it from Profile \u2192 API key"
    });
  } else if (!serverReachable) {
    checks.push({ id: "key", status: "warn", message: "Server unreachable \u2014 key not verified" });
  } else {
    try {
      const login = await api(server, "/api/auth/login-with-key", {
        method: "POST",
        body: { apiKey }
      });
      if (login?.status === "ok" && login.token) {
        token = login.token;
        checks.push({ id: "key", status: "ok", message: "API key accepted" });
      } else {
        checks.push({ id: "key", status: "error", message: "API key rejected", hint: "Regenerate it in Profile \u2192 API key" });
      }
    } catch {
      checks.push({ id: "key", status: "error", message: "API key invalid", hint: "Regenerate it in Profile \u2192 API key" });
    }
  }
  if (token) {
    try {
      const projects = await api(server, "/api/admin/projects", { token });
      const found = projects.find((p) => p.key === project);
      if (!found) {
        checks.push({ id: "project", status: "error", message: `Project ${project} not found in this workspace` });
      } else {
        const activeField = environment === "production" ? "isActiveProduction" : environment === "staging" ? "isActiveStaging" : "isActiveLocal";
        checks.push(
          found[activeField] === false ? { id: "project", status: "warn", message: `Project inactive for ${environment}` } : { id: "project", status: "ok", message: `Project ${project} active for ${environment}` }
        );
      }
    } catch (err) {
      checks.push({ id: "project", status: "warn", message: `Could not list projects: ${err?.message ?? err}` });
    }
  }
  checks.push(await widgetCheck(cwd2));
  if (serverReachable) {
    try {
      const res = await fetchWithTimeout(`${server}/pointer.js`, 3e3);
      const type = res.headers.get("content-type") || "";
      checks.push(
        res.ok && type.includes("javascript") ? { id: "widget-served", status: "ok", message: "Widget script served" } : { id: "widget-served", status: "error", message: `Widget script not served (HTTP ${res.status})` }
      );
    } catch {
      checks.push({ id: "widget-served", status: "error", message: "Widget script not served" });
    }
  }
  checks.push(await skillsCheck(cwd2, config));
  if (meta?.skillVersion) {
    const stale = [];
    for (const rel of skillFilesFor(config)) {
      const abs = join7(cwd2, rel);
      try {
        await fs7.access(abs);
      } catch {
        continue;
      }
      if (await readStamp(abs) !== meta.skillVersion)
        stale.push(rel);
    }
    checks.push(
      stale.length === 0 ? { id: "stale", status: "ok", message: `Skills match the server (${meta.skillVersion})` } : {
        id: "stale",
        status: "warn",
        message: `${stale.length} file${stale.length === 1 ? "" : "s"} behind the server (${meta.skillVersion}): ${stale.join(", ")}`,
        hint: "Run `npx -y pointer-feedback update`"
      }
    );
  }
  checks.push(...await gitignoreChecks(cwd2));
  try {
    await fs7.access(join7(cwd2, ".pointer/stack.json"));
    checks.push({ id: "stack", status: "ok", message: "Stack registered" });
  } catch {
    checks.push({ id: "stack", status: "warn", message: "Stack not registered", fixable: true });
  }
  return checks;
}
async function widgetCheck(cwd2) {
  const detection = await detectStack(cwd2).catch(() => null);
  const candidates = [detection?.htmlPath, "index.html", "public/index.html", "src/index.html"].filter(Boolean);
  for (const rel of candidates) {
    try {
      const html = await fs7.readFile(join7(cwd2, rel), "utf8");
      if (html.includes("<!-- pointer-feedback:start -->") || html.includes("<pointer-feedback")) {
        return { id: "widget", status: "ok", message: `Widget found in ${rel}` };
      }
    } catch {
    }
  }
  for (const envFile of [".env", ".env.local", ".env.development"]) {
    try {
      const env = await fs7.readFile(join7(cwd2, envFile), "utf8");
      if (/^VITE_POINTER_PROJECT=/m.test(env)) {
        return { id: "widget", status: "ok", message: `Widget env configured in ${envFile}` };
      }
    } catch {
    }
  }
  return {
    id: "widget",
    status: "warn",
    message: "Widget not found in this app",
    hint: "Run `init`, or the pointer-init skill for framework installs"
  };
}
async function skillsCheck(cwd2, config) {
  const tool = config.aiTool;
  if (!tool)
    return { id: "skills", status: "warn", message: "No AI tool configured" };
  const expected = SKILL_FILES[tool] ?? SKILL_FILES.other;
  const missing = [];
  for (const rel of expected) {
    try {
      await fs7.access(join7(cwd2, config.skillsDir ?? "", rel));
    } catch {
      missing.push(rel);
    }
  }
  return missing.length === 0 ? { id: "skills", status: "ok", message: `Skills installed for ${tool}` } : { id: "skills", status: "warn", message: `Skills missing for ${tool}: ${missing.join(", ")}`, fixable: true };
}
async function gitignoreChecks(cwd2) {
  const results = [];
  let tracked = false;
  try {
    await execFileAsync("git", ["ls-files", "--error-unmatch", ".pointer/credentials.env"], { cwd: cwd2 });
    tracked = true;
  } catch {
    tracked = false;
  }
  if (tracked) {
    results.push({
      id: "gitignore",
      status: "error",
      message: ".pointer/credentials.env is tracked by git!",
      hint: "Run `git rm --cached .pointer/credentials.env`, then rotate the key \u2014 it is in your history"
    });
    return results;
  }
  try {
    const ignore = await fs7.readFile(join7(cwd2, ".gitignore"), "utf8");
    results.push(
      ignore.includes(".pointer/") ? { id: "gitignore", status: "ok", message: "Credentials ignored by git" } : { id: "gitignore", status: "warn", message: ".gitignore is missing the .pointer/ entries", fixable: true }
    );
  } catch {
    results.push({ id: "gitignore", status: "warn", message: "No .gitignore found", fixable: true });
  }
  return results;
}

// src/commands/init.ts
import { promises as fs8 } from "node:fs";
import { join as join8 } from "node:path";
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
  let server = options["server"] || config.server || process.env.POINTER_SERVER || BUILD_DEFAULT_SERVER;
  if (!isYes && !options["server"] && !config.server) {
    server = await ask("Server URL", { default: server });
  }
  const branding = await getBranding(server);
  const product = branding.productName;
  let key = options["key"];
  let me = null;
  let token;
  if (isYes) {
    try {
      const login = await api(server, "/api/auth/login-with-key", {
        method: "POST",
        body: { apiKey: key }
      });
      if (login?.status !== "ok" || !login?.token)
        throw new Error(login?.status || "invalid");
      token = login.token;
      me = login.user ?? await api(server, "/api/auth/me", { token });
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
        const login = await api(server, "/api/auth/login-with-key", {
          method: "POST",
          body: { apiKey: key }
        });
        if (login?.status !== "ok" || !login?.token)
          throw new Error(login?.status || "invalid");
        token = login.token;
        me = login.user ?? await api(server, "/api/auth/me", { token });
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
  await upsertGitignore(cwd2, product);
  let project = options["project"];
  let create = options["create"];
  let finalProjectKey = project || "";
  let projectName = create || finalProjectKey;
  let created = false;
  if (!isYes && !project && !create) {
    const projects = await api(server, "/api/admin/projects", { token }).catch(() => []);
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
  } else if (isYes && project && !create) {
    const existing = await api(server, "/api/admin/projects", { token }).catch(() => []);
    if (!existing.some((p) => p.key === project)) {
      created = true;
      projectName = project;
    }
  } else if (create) {
    created = true;
    let derivedKey = create.toLowerCase().replace(/[^a-z0-9-]/g, "-").replace(/-+/g, "-").replace(/^-|-$/g, "");
    finalProjectKey = project || derivedKey;
    projectName = create;
  }
  if (created) {
    try {
      await api(server, "/api/admin/projects", { method: "POST", body: { key: finalProjectKey, name: projectName }, token });
    } catch (err) {
      if (err instanceof ApiError && err.code === 409) {
        console.error("Key already exists, choose another.");
        process.exit(3);
      } else if (err instanceof ApiError && err.code === 403) {
        console.error("This account cannot create projects.");
        process.exit(3);
      } else if (err instanceof ApiError && err.code === 400) {
        console.error(err.message);
        process.exit(1);
      } else {
        console.error(`Could not create project: ${err?.message ?? err}`);
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
  const pkgStr = await fs8.readFile(join8(cwd2, "package.json"), "utf8").catch(() => "{}");
  const tokens = extractTokens(JSON.parse(pkgStr));
  const stackMeta = { frontend: tokens.frontend, backend: tokens.backend, aiTool: tool };
  try {
    await api(server, `/api/projects/${finalProjectKey}/stack`, { method: "POST", body: stackMeta, token });
    await fs8.mkdir(join8(cwd2, ".pointer"), { recursive: true });
    await fs8.writeFile(join8(cwd2, ".pointer/stack.json"), JSON.stringify(stackMeta, null, 2), "utf8");
  } catch (e) {
    if (!isJson)
      console.log(`\u26A0 Stack not registered (${e.code || 500})`);
  }
  await writeConfig(cwd2, { server, project: finalProjectKey, environment: env, aiTool: tool, skillsDir: options["skills-dir"], cliVersion: BUILD_CLI_VERSION });
  filesMod.push(".pointer/config.json");
  filesMod.push(".pointer/credentials.env");
  filesMod.push(".gitignore");
  if (!isJson)
    console.log("Verifying...");
  const checks = await runInitChecks(cwd2, { server, project: finalProjectKey }, BUILD_CLI_VERSION);
  if (!isJson) {
    const icon = { ok: "\u2714", warn: "\u26A0", error: "\u2718" };
    for (const c of checks) {
      console.log(`${icon[c.status]} ${c.id}: ${c.message}`);
    }
  }
  await postEvent(server, token, { type: "installed", projectKey: finalProjectKey, meta: { stack: stackMeta, aiTool: tool, injected, cliVersion: BUILD_CLI_VERSION } });
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
      cliVersion: BUILD_CLI_VERSION
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

// src/commands/doctor.ts
import { promises as fs9 } from "node:fs";
import { join as join9 } from "node:path";
var ICON = { ok: "\u2714", warn: "\u26A0", error: "\u2718" };
function exitCodeFor(checks) {
  const failed = checks.filter((c) => c.status === "error");
  if (failed.some((c) => c.id === "meta"))
    return 5;
  if (failed.some((c) => c.id === "key"))
    return 3;
  return failed.length > 0 ? 1 : 0;
}
async function doctorCommand(cwd2, options, cliVersion) {
  let checks = await runInitChecks(cwd2, { server: options.server, project: options.project }, cliVersion);
  if (options.fix) {
    const repaired = await applyFixes(cwd2, checks);
    if (repaired.length > 0) {
      checks = await runInitChecks(cwd2, { server: options.server, project: options.project }, cliVersion);
    }
  }
  const code = exitCodeFor(checks);
  const ok = code === 0;
  if (options.json) {
    console.log(JSON.stringify({ ok, checks }, null, 2));
  } else {
    for (const check of checks) {
      console.log(`${ICON[check.status]} ${check.id.padEnd(14)} ${check.message}`);
      if (check.hint && check.status !== "ok")
        console.log(`  ${" ".repeat(14)} \u2192 ${check.hint}`);
    }
    const failed = checks.filter((c) => c.status === "error").length;
    const warned = checks.filter((c) => c.status === "warn").length;
    console.log(
      ok ? `
All good${warned ? ` (${warned} warning${warned === 1 ? "" : "s"})` : ""}.` : `
${failed} problem${failed === 1 ? "" : "s"} found.`
    );
  }
  await reportRun(cwd2, options, checks, ok);
  return code;
}
async function reportRun(cwd2, options, checks, ok) {
  const keyCheck = checks.find((c) => c.id === "key");
  if (keyCheck?.status !== "ok")
    return;
  try {
    const config = await readConfig(cwd2);
    const server = (options.server || config.server || "").replace(/\/$/, "");
    if (!server)
      return;
    const apiKey = (await fs9.readFile(join9(cwd2, ".pointer/credentials.env"), "utf8")).match(/^POINTER_API_KEY=(.*)$/m)?.[1]?.trim();
    if (!apiKey)
      return;
    const login = await api(server, "/api/auth/login-with-key", { method: "POST", body: { apiKey } });
    if (!login?.token)
      return;
    await postEvent(server, login.token, {
      type: "doctor_run",
      projectKey: options.project || config.project,
      meta: { ok, failed: checks.filter((c) => c.status === "error").map((c) => c.id) }
    });
  } catch {
  }
}
async function applyFixes(cwd2, checks) {
  const repaired = [];
  const config = await readConfig(cwd2);
  const server = (config.server || "").replace(/\/$/, "");
  let token;
  const tokenFor = async () => {
    if (token)
      return token;
    try {
      const apiKey = (await fs9.readFile(join9(cwd2, ".pointer/credentials.env"), "utf8")).match(/^POINTER_API_KEY=(.*)$/m)?.[1]?.trim();
      if (!apiKey || !server)
        return void 0;
      const login = await api(server, "/api/auth/login-with-key", { method: "POST", body: { apiKey } });
      token = login?.token;
    } catch {
      token = void 0;
    }
    return token;
  };
  for (const check of checks.filter((c) => c.fixable && c.status !== "ok")) {
    try {
      if (check.id === "gitignore") {
        const path = join9(cwd2, ".gitignore");
        const existing = await fs9.readFile(path, "utf8").catch(() => "");
        if (!existing.includes(".pointer/")) {
          const block = [
            "",
            "# Local install state. credentials.env holds an API key.",
            ".pointer/",
            "!.pointer/config.json",
            "!.pointer/stack.json",
            ""
          ].join("\n");
          await fs9.writeFile(path, existing + block, "utf8");
          repaired.push(check.id);
        }
      } else if (check.id === "skills" && server && config.aiTool) {
        await installSkills(server, config.aiTool, cwd2, config.skillsDir);
        repaired.push(check.id);
      } else if (check.id === "stack" && server && config.project) {
        const detection = await detectStack(cwd2);
        const stackToken = await tokenFor();
        const stack = stackToken ? await api(server, `/api/projects/${config.project}/stack`, {
          method: "POST",
          token: stackToken,
          body: { kind: detection.kind, evidence: detection.evidence }
        }).catch(() => null) : null;
        if (stack) {
          await fs9.writeFile(join9(cwd2, ".pointer/stack.json"), JSON.stringify(stack, null, 2) + "\n", "utf8");
          repaired.push(check.id);
        }
      }
    } catch {
    }
  }
  return repaired;
}

// src/commands/update.ts
import { promises as fs10 } from "node:fs";
import { dirname as dirname3, join as join10 } from "node:path";
function sourceFor(path) {
  if (path.endsWith("pointer.sh"))
    return "/pointer.sh";
  if (path.includes("pointer-init"))
    return "/pointer-init.md";
  if (path.includes("pointer-feedback"))
    return "/skill.md";
  return null;
}
async function updateCommand(cwd2, options) {
  const config = await readConfig(cwd2);
  const server = (options.server || config.server || "").replace(/\/$/, "");
  if (!server) {
    console.error("No server configured \u2014 run `npx -y pointer-feedback init` first.");
    return 1;
  }
  let served = null;
  try {
    const meta = await api(server, "/api/meta");
    served = meta?.skillVersion ?? null;
  } catch {
    console.error(`Could not reach ${server} \u2014 check the URL.`);
    return 1;
  }
  const files = skillFilesFor(config);
  const stale = [];
  for (const rel of files) {
    const abs = join10(cwd2, rel);
    try {
      await fs10.access(abs);
    } catch {
      continue;
    }
    const installed = await readStamp(abs);
    if (installed !== served)
      stale.push({ path: rel, installed });
  }
  if (stale.length === 0) {
    console.log(`Up to date (skill version ${served ?? "unknown"}).`);
    return 0;
  }
  if (options.check) {
    console.log(`${stale.length} file${stale.length === 1 ? "" : "s"} out of date (server ${served ?? "unknown"}):`);
    for (const f of stale)
      console.log(`  ${f.path} (${f.installed ?? "unstamped"})`);
    return 0;
  }
  let updated = 0;
  const from = stale[0]?.installed ?? "unstamped";
  for (const f of stale) {
    const source = sourceFor(f.path);
    if (!source)
      continue;
    try {
      const res = await fetch(`${server}${source}`);
      if (!res.ok)
        throw new Error(`HTTP ${res.status}`);
      const body = await res.text();
      const abs = join10(cwd2, f.path);
      await fs10.mkdir(dirname3(abs), { recursive: true });
      await fs10.writeFile(abs, body, "utf8");
      if (abs.endsWith(".sh"))
        await fs10.chmod(abs, 493).catch(() => {
        });
      updated++;
    } catch (err) {
      console.error(`  failed to update ${f.path}: ${err?.message ?? err}`);
    }
  }
  console.log(`updated ${updated} file${updated === 1 ? "" : "s"} (skill version ${from} \u2192 ${served ?? "unknown"})`);
  return updated === stale.length ? 0 : 1;
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
      if (key === "no-app-url" || key === "no-inject" || key === "no-skills" || key === "yes" || key === "json" || key === "help" || key === "fix" || key === "check") {
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
  init      Set up the feedback widget in your project
  doctor    Diagnose an install and report what is wrong
  update    Refresh the served skills to the server's current version

Options:
  -h, --help    Show this help message

Run 'pointer doctor --help' for its options.
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
  --server <url>           Feedback server URL
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
    if (parsed["help"]) {
      console.log(`
Usage: pointer doctor [options]

Checks an existing install and prints one line per check.

Options:
  --server <url>     Override the server from .pointer/config.json
  --project <key>    Override the project key
  --json             Emit { ok, checks } as JSON
  --fix              Apply the idempotent repairs (gitignore, skills, stack)
  -h, --help         Show this help

Exit codes:
  0  everything passed (warnings allowed)
  1  a check failed
  3  the API key is missing or rejected
  5  this CLI is older than the server requires
`);
      process.exit(0);
    }
    const code = await doctorCommand(cwd(), {
      server: typeof parsed["server"] === "string" ? parsed["server"] : void 0,
      project: typeof parsed["project"] === "string" ? parsed["project"] : void 0,
      json: parsed["json"] === true,
      fix: parsed["fix"] === true
    }, BUILD_CLI_VERSION);
    process.exit(code);
  } else if (command === "update") {
    if (parsed["help"]) {
      console.log(`
Usage: pointer update [options]

Refreshes the AI skills and pointer.sh from the configured server.

Options:
  --server <url>     Override the server from .pointer/config.json
  --check            Report what is out of date without writing anything
  -h, --help         Show this help
`);
      process.exit(0);
    }
    const code = await updateCommand(cwd(), {
      server: typeof parsed["server"] === "string" ? parsed["server"] : void 0,
      check: parsed["check"] === true
    });
    process.exit(code);
  } else {
    console.error(`Unknown command: ${command}`);
    process.exit(2);
  }
}
main().catch((err) => {
  console.error("Fatal error:", err);
  process.exit(1);
});
