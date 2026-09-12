import { initCommand } from './commands/init.js';
import { doctorCommand } from './commands/doctor.js';
import { updateCommand } from './commands/update.js';
import { applyCommand } from './commands/apply.js';
import { listCommand, getCommand, statusCommand, replyCommand } from './commands/comments.js';
import { mcpCommand } from './commands/mcp.js';
import { argv, cwd } from 'node:process';
import { BUILD_CLI_VERSION } from './build-constants.js';

function parseArgs(args: string[]) {
    const parsed: Record<string, string | boolean> = {};
    const positionals: string[] = [];
    const booleanFlags = new Set([
        'no-app-url',
        'no-inject',
        'no-skills',
        'yes',
        'json',
        'help',
        'fix',
        'check',
        'plan',
        'dry-run',
        'no-commit'
    ]);

    for (let i = 0; i < args.length; i++) {
        const arg = args[i];
        if (arg.startsWith('--')) {
            const key = arg.slice(2);
            if (booleanFlags.has(key)) {
                parsed[key] = true;
            } else if (i + 1 < args.length && !args[i+1].startsWith('-')) {
                parsed[key] = args[i+1];
                i++;
            } else {
                parsed[key] = true;
            }
        } else if (arg.startsWith('-')) {
            const key = arg.slice(1);
            if (key === 'y') parsed['yes'] = true;
            if (key === 'h') parsed['help'] = true;
        } else {
            positionals.push(arg);
        }
    }
    return { parsed, positionals };
}

const HELP = `
Usage: pointer <command> [options]

Commands:
  init      Set up the feedback widget in your project
  doctor    Diagnose an install and report what is wrong
  update    Refresh the served skills to the server's current version
  apply     Turn pending feedback into an AI apply prompt and mark applied
  list      List feedback comments (summary view)
  get       View comment details (whitelisted projection)
  status    Update comment status
  reply     Add a reply to a comment
  mcp       Start the Model Context Protocol (MCP) server

Options:
  -h, --help    Show this help message

Run 'pointer <command> --help' for command-specific options.
`;

async function main() {
    const { parsed, positionals } = parseArgs(argv.slice(2));
    const command = positionals[0];
    
    if (parsed['help'] || command === '--help' || !command) {
        if (!command || command === '--help') {
            console.log(HELP);
            process.exit(0);
        }
    }
    
    if (command === 'init') {
        if (parsed['help']) {
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
    } else if (command === 'doctor') {
        if (parsed['help']) {
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
            server: typeof parsed['server'] === 'string' ? parsed['server'] : undefined,
            project: typeof parsed['project'] === 'string' ? parsed['project'] : undefined,
            json: parsed['json'] === true,
            fix: parsed['fix'] === true,
        }, BUILD_CLI_VERSION);
        process.exit(code);
    } else if (command === 'update') {
        if (parsed['help']) {
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
            server: typeof parsed['server'] === 'string' ? parsed['server'] : undefined,
            check: parsed['check'] === true,
        });
        process.exit(code);
    } else if (command === 'apply') {
        if (parsed['help']) {
            console.log(`
Usage: pointer apply [options]

Turn pending feedback comments into a self-contained AI apply prompt.

Options:
  --plan             Plan only: list files without making edits
  --tool <name>      Hand off prompt to claude, opencode, cursor, or clipboard
  --mark <id>|all    Commit staged changes and mark comment(s) applied
  --reply <text>     Reply text for applied comment (required with --mark)
  --no-commit        Skip git commit during --mark (PATCH only)
  --dry-run          Print what --mark would do without making git/API changes
  --fail <id>        Mark apply failed with a reply
  --reason <text>    Failure reason (required with --fail)
  --status <status>  Filter queue by status (open, ready, applied, archived)
  --env <env>        Filter queue by environment (local, staging, production)
  --json             Emit output as JSON
  -h, --help         Show this help
`);
            process.exit(0);
        }
        await applyCommand(cwd(), parsed, positionals);
    } else if (command === 'list' || command === 'comments') {
        if (parsed['help']) {
            console.log(`
Usage: pointer list [status] [environment] [options]

List feedback comments in a summary view.

Options:
  --status <status>  Filter by status (open, ready, applied, archived)
  --env <env>        Filter by environment (local, staging, production)
  --json             Emit comments as JSON
  -h, --help         Show this help
`);
            process.exit(0);
        }
        await listCommand(cwd(), parsed, positionals);
    } else if (command === 'get') {
        if (parsed['help']) {
            console.log(`
Usage: pointer get <id> [options]

View whitelisted comment projection for AI agents.

Options:
  --json             Emit whitelisted AiCommentView JSON
  -h, --help         Show this help
`);
            process.exit(0);
        }
        await getCommand(cwd(), parsed, positionals);
    } else if (command === 'status') {
        if (parsed['help']) {
            console.log(`
Usage: pointer status <id> <open|ready|applied|archived>

Update comment status.
`);
            process.exit(0);
        }
        await statusCommand(cwd(), parsed, positionals);
    } else if (command === 'reply') {
        if (parsed['help']) {
            console.log(`
Usage: pointer reply <id> "<text>"

Add a reply to a comment.
`);
            process.exit(0);
        }
        await replyCommand(cwd(), parsed, positionals);
    } else if (command === 'mcp') {
        if (parsed['help']) {
            console.log(`
Usage: pointer mcp [options]

Start the Pointer stdio MCP server for AI tools.

Options:
  --log <file>       Log MCP server traffic to file
  --server <url>     Feedback server URL
  --project <key>    Project key
  --key <key>        API key
  -h, --help         Show this help
`);
            process.exit(0);
        }
        await mcpCommand(cwd(), parsed);
    } else {
        console.error(`Unknown command: ${command}`);
        process.exit(2);
    }
}

main().catch(err => {
    console.error('Fatal error:', err);
    process.exit(1);
});
