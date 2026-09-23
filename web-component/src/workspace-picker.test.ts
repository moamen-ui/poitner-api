import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { t, setLang } from './i18n';
import { TPL } from './templates';
import { PointerFeedback } from './element';
import { apiSwitchWorkspace, renderLoginView, renderWorkspacePicker } from './auth-ui';
import type { PointerHost, WorkspaceChoice } from './types';

describe('DB-11b workspace picker & login projectKey', () => {
  beforeEach(() => {
    setLang('en');
  });

  describe('i18n strings', () => {
    it('resolves the five auth keys in English', () => {
      setLang('en');
      expect(t('auth.chooseWorkspace')).toBe('Choose which workspace to open.');
      expect(t('auth.unnamedWorkspace')).toBe('Unnamed workspace');
      expect(t('auth.homeWorkspaceBadge')).toBe('Home');
      expect(t('auth.noWorkspace')).toBe('Your account is not a member of any workspace.');
      expect(t('auth.switchingWorkspace')).toBe('Opening workspace…');
    });

    it('resolves the five auth keys in Arabic', () => {
      setLang('ar');
      expect(t('auth.chooseWorkspace')).toBe('اختر مساحة العمل التي تريد فتحها.');
      expect(t('auth.unnamedWorkspace')).toBe('مساحة عمل بدون اسم');
      expect(t('auth.homeWorkspaceBadge')).toBe('الرئيسية');
      expect(t('auth.noWorkspace')).toBe('حسابك ليس عضوًا في أي مساحة عمل.');
      expect(t('auth.switchingWorkspace')).toBe('جارٍ فتح مساحة العمل…');
    });
  });

  describe('apiLogin posts projectKey', () => {
    let lastUrl = '';
    let lastOpts: RequestInit | undefined;

    beforeEach(() => {
      lastUrl = '';
      lastOpts = undefined;
      (window as any).__POINTER_FETCH__ = vi.fn(async (url: string, opts?: RequestInit) => {
        lastUrl = url;
        lastOpts = opts;
        return {
          ok: true,
          status: 200,
          json: async () => ({ success: true, data: { status: 'ok', token: 'jwt-tok', user: null } }),
        } as Response;
      });
    });

    afterEach(() => {
      delete (window as any).__POINTER_FETCH__;
      vi.restoreAllMocks();
    });

    function createFeedbackElement(): PointerFeedback {
      if (!customElements.get('pointer-feedback')) {
        customElements.define('pointer-feedback', PointerFeedback);
      }
      return document.createElement('pointer-feedback') as PointerFeedback;
    }

    it('posts explicit projectKey from PointerFeedback.apiLogin', async () => {
      const el = createFeedbackElement();
      el.server = 'https://api.example.com';
      el.project = 'default-project';

      await el.apiLogin('test@example.com', 'secret123', 'override-project');

      expect(lastUrl).toBe('https://api.example.com/api/auth/login');
      expect(lastOpts?.method).toBe('POST');
      const body = JSON.parse(lastOpts?.body as string);
      expect(body).toEqual({
        email: 'test@example.com',
        password: 'secret123',
        projectKey: 'override-project',
      });
    });

    it('defaults projectKey to el.project when not explicitly passed', async () => {
      const el = createFeedbackElement();
      el.server = 'https://api.example.com';
      el.project = 'host-project';

      await el.apiLogin('test@example.com', 'secret123');

      expect(lastUrl).toBe('https://api.example.com/api/auth/login');
      const body = JSON.parse(lastOpts?.body as string);
      expect(body).toEqual({
        email: 'test@example.com',
        password: 'secret123',
        projectKey: 'host-project',
      });
    });

    it('omits projectKey when el.project is empty and no projectKey passed', async () => {
      const el = createFeedbackElement();
      el.server = 'https://api.example.com';
      el.project = '';

      await el.apiLogin('test@example.com', 'secret123');

      const body = JSON.parse(lastOpts?.body as string);
      expect(body).toEqual({
        email: 'test@example.com',
        password: 'secret123',
      });
    });
  });

  describe('apiSwitchWorkspace', () => {
    let lastUrl = '';
    let lastOpts: RequestInit | undefined;

    beforeEach(() => {
      lastUrl = '';
      lastOpts = undefined;
      (window as any).__POINTER_FETCH__ = vi.fn(async (url: string, opts?: RequestInit) => {
        lastUrl = url;
        lastOpts = opts;
        return {
          ok: true,
          status: 200,
          json: async () => ({ success: true, data: { status: 'ok', token: 'switched-token', user: { id: 'u1' } } }),
        } as Response;
      });
    });

    afterEach(() => {
      delete (window as any).__POINTER_FETCH__;
      vi.restoreAllMocks();
    });

    it('posts Authorization Bearer and workspaceId body', async () => {
      const res = await apiSwitchWorkspace('https://api.example.com', 'ws-42', 'selection-jwt-5m');

      expect(lastUrl).toBe('https://api.example.com/api/auth/switch-workspace');
      expect(lastOpts?.method).toBe('POST');
      expect((lastOpts?.headers as Record<string, string>)?.['Authorization']).toBe('Bearer selection-jwt-5m');
      expect((lastOpts?.headers as Record<string, string>)?.['Content-Type']).toBe('application/json');
      expect(JSON.parse(lastOpts?.body as string)).toEqual({ workspaceId: 'ws-42' });
      expect(res.ok).toBe(true);
    });
  });

  describe('TPL.workspacePicker', () => {
    it('renders workspace list with home badge and replaces placeholder name with Unnamed workspace', () => {
      setLang('en');
      const workspaces: WorkspaceChoice[] = [
        {
          workspaceId: 'ws-1',
          name: 'Acme Corp',
          roleName: 'Developer',
          isAdmin: false,
          isHome: true,
        },
        {
          workspaceId: 'ws-2',
          name: 'Workspace',
          roleName: 'Admin',
          isAdmin: true,
          isHome: false,
        },
      ];

      const html = TPL.workspacePicker(workspaces);

      expect(html).toContain('Choose which workspace to open.');
      expect(html).toContain('data-workspace-id="ws-1"');
      expect(html).toContain('Acme Corp');
      expect(html).toContain('Developer');
      expect(html).toContain('<span class="fbk-badge">Home</span>');

      expect(html).toContain('data-workspace-id="ws-2"');
      expect(html).toContain('Unnamed workspace');
      expect(html).toContain('Admin');
    });

    it('renders localized names and home badge in Arabic', () => {
      setLang('ar');
      const workspaces: WorkspaceChoice[] = [
        {
          workspaceId: 'ws-1',
          name: 'Workspace',
          roleName: 'مدير',
          isAdmin: true,
          isHome: true,
        },
      ];

      const html = TPL.workspacePicker(workspaces);
      expect(html).toContain('اختر مساحة العمل التي تريد فتحها.');
      expect(html).toContain('مساحة عمل بدون اسم');
      expect(html).toContain('<span class="fbk-badge">الرئيسية</span>');
    });
  });

  describe('auth-ui integration', () => {
    let host: PointerHost;
    let container: HTMLElement;

    beforeEach(() => {
      setLang('en');
      container = document.createElement('div');
      container.innerHTML = '<div id="fbk-auth-body"></div>';
      document.body.appendChild(container);

      host = {
        project: 'my-project',
        server: 'https://api.example.com',
        root: container,
        apiLogin: vi.fn(),
        apiRoles: vi.fn().mockResolvedValue([]),
        apiRegister: vi.fn(),
        saveAuth: vi.fn(),
        init: vi.fn(),
        renderChrome: vi.fn(),
        afterLogin: null,
      };
    });

    afterEach(() => {
      container.remove();
      delete (window as any).__POINTER_FETCH__;
      vi.restoreAllMocks();
    });

    it('renders workspace picker on choose-workspace status and switches workspace on click', async () => {
      const selectionToken = 'sel-token-5m';
      const workspaces: WorkspaceChoice[] = [
        { workspaceId: 'ws-10', name: 'Design Studio', roleName: 'Reviewer', isAdmin: false, isHome: true },
        { workspaceId: 'ws-20', name: 'Workspace', roleName: 'Member', isAdmin: false, isHome: false },
      ];

      (host.apiLogin as any).mockResolvedValue({
        ok: true,
        json: async () => ({
          success: true,
          data: {
            status: 'choose-workspace',
            token: selectionToken,
            workspaces,
          },
        }),
      });

      (window as any).__POINTER_FETCH__ = vi.fn(async (url: string, opts?: RequestInit) => {
        if (url.includes('/api/auth/switch-workspace')) {
          return {
            ok: true,
            status: 200,
            json: async () => ({
              success: true,
              data: {
                status: 'ok',
                token: 'switched-session-jwt',
                user: { id: 'u-1', email: 'test@example.com' },
              },
            }),
          } as Response;
        }
        return { ok: false, status: 404, json: async () => ({}) } as Response;
      });

      renderLoginView(host);

      const emailEl = container.querySelector('#fbk-email') as HTMLInputElement;
      const passEl = container.querySelector('#fbk-password') as HTMLInputElement;
      const submitBtn = container.querySelector('#fbk-login-submit') as HTMLButtonElement;

      emailEl.value = 'user@example.com';
      passEl.value = 'password123';
      submitBtn.click();

      // Wait for async doLogin
      await new Promise((r) => setTimeout(r, 10));

      expect(host.apiLogin).toHaveBeenCalledWith('user@example.com', 'password123', 'my-project');

      // Workspace picker should now be rendered inside #fbk-auth-body
      const pickerItems = container.querySelectorAll('.fbk-workspace-item');
      expect(pickerItems.length).toBe(2);
      expect(container.textContent).toContain('Design Studio');
      expect(container.textContent).toContain('Unnamed workspace');

      // Click the first workspace
      (pickerItems[0] as HTMLButtonElement).click();

      await new Promise((r) => setTimeout(r, 10));

      expect((window as any).__POINTER_FETCH__).toHaveBeenCalledWith(
        'https://api.example.com/api/auth/switch-workspace',
        expect.objectContaining({
          method: 'POST',
          headers: expect.objectContaining({
            Authorization: `Bearer ${selectionToken}`,
          }),
        })
      );
      expect(host.saveAuth).toHaveBeenCalledWith('switched-session-jwt', { id: 'u-1', email: 'test@example.com' });
      expect(host.init).toHaveBeenCalled();
    });

    it('shows error on no-workspace status', async () => {
      (host.apiLogin as any).mockResolvedValue({
        ok: true,
        json: async () => ({
          success: false,
          message: 'No active workspaces found for account.',
          data: {
            status: 'no-workspace',
          },
        }),
      });

      renderLoginView(host);

      const emailEl = container.querySelector('#fbk-email') as HTMLInputElement;
      const passEl = container.querySelector('#fbk-password') as HTMLInputElement;
      const submitBtn = container.querySelector('#fbk-login-submit') as HTMLButtonElement;
      const errEl = container.querySelector('#fbk-login-error') as HTMLElement;

      emailEl.value = 'orphan@example.com';
      passEl.value = 'password123';
      submitBtn.click();

      await new Promise((r) => setTimeout(r, 10));

      expect(errEl.textContent).toBe('No active workspaces found for account.');
    });

    it('shows i18n fallback when no-workspace response message is empty', async () => {
      (host.apiLogin as any).mockResolvedValue({
        ok: true,
        json: async () => ({
          success: false,
          message: '',
          data: {
            status: 'no-workspace',
          },
        }),
      });

      renderLoginView(host);

      const emailEl = container.querySelector('#fbk-email') as HTMLInputElement;
      const passEl = container.querySelector('#fbk-password') as HTMLInputElement;
      const submitBtn = container.querySelector('#fbk-login-submit') as HTMLButtonElement;
      const errEl = container.querySelector('#fbk-login-error') as HTMLElement;

      emailEl.value = 'orphan@example.com';
      passEl.value = 'password123';
      submitBtn.click();

      await new Promise((r) => setTimeout(r, 10));

      expect(errEl.textContent).toBe('Your account is not a member of any workspace.');
    });

    it('renderWorkspacePicker handles back to sign in button', () => {
      const workspaces: WorkspaceChoice[] = [
        { workspaceId: 'ws-1', name: 'W1', roleName: 'R1', isAdmin: false, isHome: true },
      ];
      renderWorkspacePicker(host, workspaces, 'tok');

      const backBtn = container.querySelector('#fbk-show-login') as HTMLElement;
      expect(backBtn).toBeTruthy();

      backBtn.click();

      // Back to sign in view
      expect(container.querySelector('#fbk-email')).toBeTruthy();
      expect(container.querySelector('#fbk-password')).toBeTruthy();
    });
  });
});
