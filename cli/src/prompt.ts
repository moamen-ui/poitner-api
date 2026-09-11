import * as readline from 'node:readline/promises';
import { Writable } from 'node:stream';

export async function ask(question: string, options: { default?: string, validate?: (val: string) => string | undefined, secret?: boolean } = {}): Promise<string> {
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
