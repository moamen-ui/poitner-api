// MCP stdio client for the pointer CLI's `pointer mcp` server (R2-02-tests Preconditions pin
// this surface; R2-06-03 step 6 consumes it). Zero LLM — the SDK client speaks JSON-RPC over the
// child's stdio, nothing more.
//
// Pinned surface (docs/roadmap/testing/R2-02-tests.md):
//   connectMcp({ cwd, env }) → {
//     listTools(): { name, description, inputSchema }[],
//     callTool(name, args) → { isError, code?, result },   // result = parsed JSON content
//     listPrompts(), getPrompt(name, args), readResource(uri),
//     pid: number,
//     exited: Promise<number>,     // resolves with the child's exit code
//     close()  // kills the child; await `exited` to assert a clean exit
//   }
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { CLI_ENTRY } from './cli.mjs';

/**
 * Spawns `node $CLI_ENTRY mcp` in `cwd` and performs the initialize handshake before returning.
 *
 * `env` is merged over the FULL parent environment on purpose: SDK 1.6.0's StdioClientTransport
 * inherits only a sudo-style minimal env (HOME/PATH/…) when none is given, which would strip the
 * very POINTER_* variables a scenario may be relying on.
 */
export async function connectMcp({ cwd, env } = {}) {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [CLI_ENTRY, 'mcp'],
    cwd,
    env: { ...process.env, ...env },
    stderr: 'pipe',
  });

  const client = new Client({ name: 'pointer-e2e', version: '0.0.0' });
  await client.connect(transport);

  // SDK 1.6.0 has no public pid getter and drops the child's exit code inside its own close
  // handler, so both handles are captured off the (private but stable) child process right after
  // the handshake: pid once, and a close listener that resolves `exited` with the real code.
  let pid = null;
  let exitedResolve;
  const exited = new Promise((resolve) => {
    exitedResolve = resolve;
  });
  const child = transport._process;
  if (child) {
    pid = child.pid;
    child.on('close', (code) => exitedResolve(code ?? 0));
  }
  // Drain stderr so a chatty server can never fill the pipe and deadlock the child.
  transport.stderr?.on('data', () => {});

  return {
    pid,
    exited,

    async listTools() {
      const res = await client.listTools();
      return res.tools;
    },

    async callTool(name, args = {}) {
      const res = await client.callTool({ name, arguments: args });
      const text = (res.content || []).find((c) => c.type === 'text')?.text;
      let parsed;
      try {
        parsed = text ? JSON.parse(text) : undefined;
      } catch {
        parsed = text;
      }
      // On isError the server's payload is { code, message }; surface `code` one level up so
      // scenarios can assert `code === 'not_found'` without re-parsing content.
      return { isError: res.isError === true, code: parsed?.code, result: parsed };
    },

    async listPrompts() {
      const res = await client.listPrompts();
      return res.prompts;
    },

    async getPrompt(name, args = {}) {
      return client.getPrompt({ name, arguments: args });
    },

    async readResource(uri) {
      const res = await client.readResource({ uri });
      const text = res.contents?.[0]?.text;
      try {
        return text ? JSON.parse(text) : undefined;
      } catch {
        return text;
      }
    },

    async close() {
      await client.close();
    },
  };
}
