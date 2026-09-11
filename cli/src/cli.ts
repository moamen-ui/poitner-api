import { initCommand } from './commands/init.js';
import { argv, cwd } from 'node:process';

function parseArgs(args: string[]) {
    const parsed: Record<string, string | boolean> = {};
    const positionals: string[] = [];
    for (let i = 0; i < args.length; i++) {
        const arg = args[i];
        if (arg.startsWith('--')) {
            const key = arg.slice(2);
            if (key === 'no-app-url' || key === 'no-inject' || key === 'no-skills' || key === 'yes' || key === 'json' || key === 'help') {
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
  init      Initialize Pointer in your project
  doctor    Run health checks (stub)

Options:
  -h, --help    Show this help message
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
    } else if (command === 'doctor') {
        console.log('Doctor command is currently stubbed.');
    } else {
        console.error(`Unknown command: ${command}`);
        process.exit(2);
    }
}

main().catch(err => {
    console.error('Fatal error:', err);
    process.exit(1);
});
