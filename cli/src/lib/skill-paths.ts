import { join } from 'node:path';
import { SKILL_FILES } from '../skills.js';
import type { PointerConfig } from '../config.js';

/**
 * Every file on disk that the server serves and `update` can refresh, for this install.
 *
 * config.json records the AI tool's NAME, not a directory — the mapping from one to the other
 * lives in SKILL_FILES, which `installSkills` also uses, so `doctor` and `update` check exactly
 * the paths `init` wrote. When `init` recorded a `skillsDir` override, that wins for the skill
 * files.
 *
 * `.pointer/pointer.sh` is always included: it is served and stamped like the skills, and it is
 * the file an AI agent actually executes.
 */
export function skillFilesFor(config: PointerConfig): string[] {
  const layout = SKILL_FILES[config.aiTool ?? ''] ?? SKILL_FILES.other;

  const skillPaths = config.skillsDir
    ? ['pointer-init', 'pointer-feedback'].map((name) => join(config.skillsDir!, name, 'SKILL.md'))
    : [...layout];

  return [...skillPaths, '.pointer/pointer.sh'];
}
