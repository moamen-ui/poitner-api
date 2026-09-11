import { initCommand } from './commands/init.js';
import { env, argv, cwd } from 'node:process';

async function main() {
  const args = argv.slice(2);
  const command = args[0];

  if (!command || command === 'init') {
    await initCommand(cwd());
  } else if (command === 'doctor') {
    // R1-04 owns full doctor, but we can do a basic one using checks.ts
    console.log('Doctor command is currently stubbed.');
  } else {
    console.error(`Unknown command: ${command}`);
  }
}

main().catch(err => {
  console.error('Fatal error:', err);
  process.exit(1);
});
