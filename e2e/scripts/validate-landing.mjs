// Validate landing pages: HTML validation + offline link check
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..', '..');
const landingDir = join(repoRoot, 'landing');

// 1. html-validate landing/data.html landing/privacy.html
console.log('Running html-validate...');
execFileSync('npx', ['html-validate', 'landing/data.html', 'landing/privacy.html'], {
  cwd: repoRoot,
  stdio: 'inherit',
});

// 2. Link check across landing pages
const filesToCheck = [
  'landing/index.html',
  'landing/privacy.html',
  'landing/data.html',
  'landing/v2/index.html',
];

let totalLinks = 0;
let internalLinksCount = 0;
const externalLinks = new Set();
const brokenLinks = [];

for (const relFile of filesToCheck) {
  const filePath = join(repoRoot, relFile);
  const content = readFileSync(filePath, 'utf8');
  const fileDir = dirname(filePath);

  // Match href="..."
  const matches = content.matchAll(/href="([^"]*)"/g);
  for (const match of matches) {
    const rawHref = match[1].trim();
    if (!rawHref) continue;
    totalLinks++;

    if (rawHref.startsWith('mailto:') || rawHref.startsWith('tel:')) {
      continue;
    }

    if (rawHref.startsWith('http://') || rawHref.startsWith('https://')) {
      externalLinks.add(rawHref);
      continue;
    }

    // Internal link
    internalLinksCount++;
    // Strip query string and fragment
    let clean = rawHref.split('#')[0].split('?')[0];
    if (!clean) {
      // It was just an anchor (#...) on the same page
      continue;
    }

    let targetPath;
    if (clean.startsWith('/')) {
      // Relative to landing root
      targetPath = join(landingDir, clean);
    } else {
      // Relative to current file
      targetPath = join(fileDir, clean);
    }

    // Directory hrefs -> index.html
    if (existsSync(targetPath)) {
      try {
        const stat = statSync(targetPath);
        if (stat.isDirectory()) {
          targetPath = join(targetPath, 'index.html');
        }
      } catch {}
    } else if (clean.endsWith('/') || !clean.includes('.')) {
      targetPath = join(targetPath, 'index.html');
    }

    if (!existsSync(targetPath)) {
      brokenLinks.push(`${relFile} -> ${rawHref} (resolved to ${targetPath})`);
    }
  }
}

if (brokenLinks.length > 0) {
  console.error('Broken internal links found:');
  for (const b of brokenLinks) {
    console.error(`  - ${b}`);
  }
  process.exit(1);
}

console.log(`links=${totalLinks} internal-ok external-listed=${externalLinks.size}`);
process.exit(0);
