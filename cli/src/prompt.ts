import * as readline from 'node:readline/promises';
import { Writable } from 'node:stream';

/**
 * Refuses to prompt when there is no terminal to prompt on.
 *
 * Without this the failure is silent and looks like success: `rl.question()` never resolves on
 * EOF, so the event loop simply drains and node exits 0 having written nothing. A user running
 * `init` from CI, a pipe, or an editor-embedded shell sees the first prompt, gets their shell back,
 * and has no way to tell that nothing happened — the exit code says it worked.
 */
function assertInteractive(): void {
    if (process.stdin.isTTY) return;
    console.error(
        '\x1b[31mThis command is interactive, but stdin is not a terminal.\x1b[0m\n' +
        'Piped input, CI, and some editor-embedded shells have no TTY, so there is no way to ask you anything.\n\n' +
        'Either run it in a real terminal, or pass every answer as a flag:\n' +
        '  npx -y pointer-feedback init --server <url> --key ptr_... --project <key> --environment local --yes\n\n' +
        "Run 'npx -y pointer-feedback init --help' for the full list of flags."
    );
    process.exit(2);
}

export async function ask(question: string, options: { default?: string, validate?: (val: string) => string | undefined, secret?: boolean } = {}): Promise<string> {
    assertInteractive();
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
        if (options.secret) muted = true;
        const answer = await rl.question('');
        muted = false;
        if (options.secret) process.stdout.write('\n');
        
        const finalAnswer = answer.trim() || options.default || '';
        
        if (options.validate) {
            const error = options.validate(finalAnswer);
            if (error) {
                console.log(`\x1b[31m${error}\x1b[0m`);
                continue;
            }
        }
        
        rl.close();
        return finalAnswer;
    }
}

export async function select(question: string, items: string[], defaultItem?: string): Promise<string> {
    assertInteractive();
    const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout,
        terminal: true
    });

    const defaultLabel = defaultItem ? ` [${defaultItem}]` : '';
    console.log(`${question}${defaultLabel}`);
    items.forEach((item, i) => {
        console.log(`  ${i + 1}) ${item}`);
    });
    
    while (true) {
        const answer = await rl.question('> ');
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
        console.log('\x1b[31mInvalid selection\x1b[0m');
    }
}
