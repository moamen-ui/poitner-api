import { initCommand } from './commands/init.js';
import { doctorCommand } from './commands/doctor.js';
import { updateCommand } from './commands/update.js';
import { argv, cwd } from 'node:process';
import { BUILD_CLI_VERSION } from './build-constants.js';

function parseArgs(args: string[]) {
    const parsed: Record<string, string | boolean> = {};
    const positionals: string[] = [];
    for (let i = 0; i < args.length; i++) {
        const arg = args[i];
        if (arg.startsWith('--')) {
            const key = arg.slice(2);
            if (key === 'no-app-url' || key === 'no-inject' || key === 'no-skills' || key === 'yes' || key === 'json' || key === 'help' || key === 'fix' || key === 'check') {
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

Options:
  -h, --help    Show this help message

Run 'pointer doctor --help' for its options.
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
    } else {
        console.error(`Unknown command: ${command}`);
        process.exit(2);
    }
}

main().catch(err => {
    console.error('Fatal error:', err);
    process.exit(1);
});
