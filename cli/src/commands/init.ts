import * as p from '@clack/prompts';
import pc from 'picocolors';
import { readConfig, writeConfig, PointerConfig } from '../config.js';
import { detectApp } from '../detect.js';
import { injectWidget } from '../inject/index.js';
import { injectSkill } from '../skills.js';
import { checkAuth } from '../api.js';
import { recordEvent } from '../events.js';
import { runInitChecks } from '../checks.js';

export async function initCommand(cwd: string) {
  p.intro(pc.bgBlue(pc.white(' Pointer Initialization ')));

  const config = await readConfig(cwd);

  const server = await p.text({
    message: 'Pointer server URL:',
    initialValue: config.server || (globalThis as any).DEFAULT_SERVER,
    validate: (val) => (val as string).length === 0 ? 'Server URL is required' : void 0
  });
  if (p.isCancel(server)) { p.cancel('Operation cancelled.'); process.exit(0); }

  const projectKey = await p.text({
    message: 'Project Key:',
    initialValue: config.projectKey || 'my-project',
    validate: (val) => (val as string).length === 0 ? 'Project Key is required' : void 0
  });
  if (p.isCancel(projectKey)) { p.cancel('Operation cancelled.'); process.exit(0); }

  const env = await p.text({
    message: 'Environment (e.g., local, staging, production):',
    initialValue: config.environment || 'local',
    validate: (val) => (val as string).length === 0 ? 'Environment is required' : void 0
  });
  if (p.isCancel(env)) { p.cancel('Operation cancelled.'); process.exit(0); }

  const hasPat = await p.confirm({
    message: 'Do you have a Personal Access Token (PAT) for admin features?',
    initialValue: false
  });
  if (p.isCancel(hasPat)) { p.cancel('Operation cancelled.'); process.exit(0); }

  let token = '';
  if (hasPat) {
    const pat = await p.text({
      message: 'Enter your PAT (hidden):',
      // No mask option in text, so we just use text.
    });
    if (p.isCancel(pat)) return p.cancel('Operation cancelled.');
    token = pat as string;

    const s = p.spinner();
    s.start('Verifying token...');
    const isValid = await checkAuth(server as string, token);
    s.stop(isValid ? 'Token verified' : 'Token invalid');
    if (!isValid) {
      p.log.error('Invalid token. Aborting.');
      return;
    }
  }

  // Detect App
  const appInfo = await detectApp(cwd);
  p.log.info(`Detected app type: ${pc.cyan(appInfo.type)} (port: ${appInfo.port})`);

  let injected = false;
  if (appInfo.type === 'nextjs' || appInfo.type === 'vite' || appInfo.type === 'static') {
    const s = p.spinner();
    s.start(`Injecting widget into ${appInfo.type} app...`);
    injected = await injectWidget(cwd, appInfo.type, server as string, projectKey as string, env as string);
    if (injected) {
      s.stop('Widget injected automatically.');
    } else {
      s.stop('Automatic injection failed or skipped.');
    }
  }

  if (!injected) {
    p.log.warn('Could not inject automatically. Please add the following script manually:');
    p.log.message(pc.dim(`<script src="${server as string}/embed.js?project=${encodeURIComponent(projectKey as string)}&environment=${encodeURIComponent(env as string)}"></script>`));
  }

  // Config
  await writeConfig(cwd, { server: server as string, projectKey: projectKey as string, environment: env as string });
  p.log.success(`Saved config to .pointerrc.json`);

  // Skills
  const sSkills = p.spinner();
  sSkills.start('Installing Antigravity skills...');
  try {
    await injectSkill(cwd, server as string);
    sSkills.stop('Skills installed successfully.');
  } catch (err: any) {
    sSkills.stop('Failed to install skills.');
    p.log.warn(`Could not install skills: ${err.message}`);
  }

  // Events
  await recordEvent({ server: server as string, token }, 'cli_init', 'cli', projectKey as string, {
    appType: appInfo.type,
    injected
  });

  // Checks
  const sChecks = p.spinner();
  sChecks.start('Running health checks...');
  const checks = await runInitChecks(server as string, projectKey as string, env as string, token);
  sChecks.stop('Health checks complete.');

  for (const check of checks) {
    if (check.status === 'pass') p.log.success(`${check.name}: ${check.message}`);
    else if (check.status === 'warn') p.log.warn(`${check.name}: ${check.message}`);
    else p.log.error(`${check.name}: ${check.message}`);
  }

  // App URL /check URL
  const checkUrl = `${(server as string).replace(/\/$/, '')}/check?project=${encodeURIComponent(projectKey as string)}&environment=${encodeURIComponent(env as string)}`;
  
  p.note(
    `1. Start your dev server (e.g., npm run dev)\n` +
    `2. Open your app (e.g., http://localhost:${appInfo.port})\n` +
    `3. If you don't see the widget, verify here: ${checkUrl}`,
    'Next Steps'
  );

  p.outro(pc.green('Pointer setup complete! 🎉'));
}
