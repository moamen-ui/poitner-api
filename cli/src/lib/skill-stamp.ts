import { promises as fs } from 'node:fs';

/**
 * Reads the `pointer-skill-version` stamp the server writes into a served skill or script.
 *
 * Placement differs by file type, and the markdown rule is the one that matters:
 *
 *   .md — an HTML comment on the first line AFTER the closing `---` of the YAML frontmatter.
 *         AI tools parse that frontmatter block, so nothing may precede it. A stamp found on
 *         line 1 of a .md file is therefore NOT valid and is deliberately rejected: treating it
 *         as valid would bless a file whose frontmatter a tool can no longer read.
 *   .sh — a `#` comment on line 2, after the shebang.
 *
 * Returns null when the file has no stamp (an older installed copy), cannot be read, or is
 * neither type. Never throws: this runs inside `doctor`, which must survive a broken install.
 */
export async function readStamp(path: string): Promise<string | null> {
  let content: string;
  try {
    content = await fs.readFile(path, 'utf8');
  } catch {
    return null;
  }

  const lines = content.split('\n');

  if (path.endsWith('.md')) {
    if (lines[0]?.trim() !== '---') return null;

    // Scan for the closing delimiter rather than assuming a line number — the frontmatter grows.
    let close = -1;
    for (let i = 1; i < lines.length; i++) {
      if (lines[i].trim() === '---') { close = i; break; }
    }
    if (close === -1) return null;

    for (let i = close + 1; i < lines.length; i++) {
      const match = lines[i].match(/pointer-skill-version:\s*([^\s>-][^>]*?)\s*(?:-->)?\s*$/);
      if (match) return match[1].trim();
      // Only the block immediately after the frontmatter is the stamp's home; stop at real
      // content so a mention further down the prose is never mistaken for it.
      if (lines[i].trim() !== '' && !lines[i].trim().startsWith('<!--')) break;
    }
    return null;
  }

  if (path.endsWith('.sh')) {
    const match = lines[1]?.match(/^#\s*pointer-skill-version:\s*(.+?)\s*$/);
    return match ? match[1].trim() : null;
  }

  return null;
}
