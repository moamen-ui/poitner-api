import { TPL } from './templates';
import { escapeHtml } from './dom';
import type { PointerHost, User } from './types';

// One modal shell, two swappable bodies (sign-in / sign-up). The shell owns the
// Skip control (deferred-login dismissal); the views fill #pf-auth-body and wire
// their own events. Decoupled from the element via the PointerHost interface.

export function showLoginModal(host: PointerHost, afterLogin?: () => void): void {
  host.afterLogin = afterLogin || null;
  host.root.innerHTML = TPL.loginModal(host.project);

  // Skip → dismiss without logging in; restore the toolbar so the user can come
  // back to it later by clicking the tool again.
  const skipBtn = host.root.querySelector('#pf-login-skip');
  if (skipBtn) skipBtn.addEventListener('click', () => { host.afterLogin = null; host.renderChrome(); });

  renderLoginView(host);
}

// Populate a <select> with [{id,name}] roles fetched anonymously. Disables the
// element while loading and on failure (shows a placeholder option).
async function populateRoles(host: PointerHost, selectEl: HTMLSelectElement | null, errEl: HTMLElement | null): Promise<void> {
  if (!selectEl) return;
  selectEl.disabled = true;
  selectEl.innerHTML = '<option value="">Loading roles…</option>';
  try {
    const roles = await host.apiRoles();
    if (!roles.length) {
      selectEl.innerHTML = '<option value="">No roles available</option>';
      return;
    }
    selectEl.innerHTML = roles.map((r) =>
      `<option value="${escapeHtml(r.id)}">${escapeHtml(r.name)}</option>`).join('');
    selectEl.disabled = false;
  } catch (e) {
    selectEl.innerHTML = '<option value="">Could not load roles</option>';
    if (errEl) errEl.textContent = (e as Error).message || 'Could not load roles.';
  }
}

// Finish a successful login: persist auth, clear the modal, run the deferred
// callback (or init the full UI).
function afterAuthOk(host: PointerHost, token: string, user: User | null): void {
  host.saveAuth(token, user);
  host.root.innerHTML = '';
  if (host.afterLogin) {
    const cb = host.afterLogin;
    host.afterLogin = null;
    cb();
  } else {
    host.init();
  }
}

// --- Sign-in view --------------------------------------------------------
export function renderLoginView(host: PointerHost, opts: { rejected?: boolean } = {}): void {
  const body = host.root.querySelector('#pf-auth-body') as HTMLElement | null;
  if (!body) return;
  body.innerHTML = TPL.loginBody(!!opts.rejected);

  const emailEl = body.querySelector('#pf-email') as HTMLInputElement;
  const passEl = body.querySelector('#pf-password') as HTMLInputElement;
  const errEl = body.querySelector('#pf-login-error') as HTMLElement;
  const submitBtn = body.querySelector('#pf-login-submit') as HTMLButtonElement;

  const doLogin = async () => {
    const email = emailEl.value.trim();
    const password = passEl.value;
    if (!email) { errEl.textContent = 'Please enter your email.'; return; }
    if (!password) { errEl.textContent = 'Please enter your password.'; return; }
    errEl.textContent = '';
    submitBtn.disabled = true;
    submitBtn.textContent = 'Signing in…';
    const restore = () => { submitBtn.disabled = false; submitBtn.textContent = 'Sign in'; };
    try {
      const r = await host.apiLogin(email, password);
      const envelope = await r.json();
      const data = envelope.data || null;
      const status = data && data.status;
      if (status === 'ok' && data.token) {
        afterAuthOk(host, data.token, data.user);
        return;
      }
      if (status === 'pending') {
        errEl.textContent = envelope.message || 'Your request is awaiting admin approval.';
        restore();
        return;
      }
      if (status === 'disabled') {
        errEl.textContent = envelope.message || 'Your account is disabled.';
        restore();
        return;
      }
      if (status === 'rejected') {
        // Re-render with the re-apply block, preserving the typed credentials.
        renderLoginView(host, { rejected: true });
        const re = host.root.querySelector('#pf-auth-body') as HTMLElement;
        (re.querySelector('#pf-email') as HTMLInputElement).value = email;
        (re.querySelector('#pf-password') as HTMLInputElement).value = password;
        (re.querySelector('#pf-login-error') as HTMLElement).textContent =
          envelope.message || 'Your request was rejected.';
        return;
      }
      // Missing/unknown status with failure → generic message.
      errEl.textContent = envelope.message || 'Invalid email or password.';
      restore();
    } catch (e) {
      errEl.textContent = 'Network error. Please try again.';
      restore();
    }
  };

  submitBtn.addEventListener('click', doLogin);
  passEl.addEventListener('keydown', (e) => { if (e.key === 'Enter') doLogin(); });

  (body.querySelector('#pf-show-signup') as HTMLElement).addEventListener('click', () => renderSignupView(host));

  // Rejected re-apply block: populate roles and wire "Request again".
  if (opts.rejected) {
    const roleEl = body.querySelector('#pf-reapply-role') as HTMLSelectElement;
    const reBtn = body.querySelector('#pf-reapply-submit') as HTMLButtonElement;
    populateRoles(host, roleEl, errEl);
    reBtn.addEventListener('click', async () => {
      const email = emailEl.value.trim();
      const password = passEl.value;
      const roleId = roleEl.value;
      if (!roleId) { errEl.textContent = 'Please choose a role.'; return; }
      if (!email || !password) { errEl.textContent = 'Enter your email and password to request again.'; return; }
      errEl.textContent = '';
      reBtn.disabled = true;
      reBtn.textContent = 'Submitting…';
      try {
        const r = await host.apiRegister({ email, password, displayName: '', roleId });
        const envelope = await r.json();
        if (!r.ok || !envelope.isSuccess) {
          errEl.textContent = envelope.message || 'Could not submit your request.';
          reBtn.disabled = false;
          reBtn.textContent = 'Request again';
          return;
        }
        // Success → collapse the re-apply block; show the submitted message.
        renderLoginView(host);
        const reBody = host.root.querySelector('#pf-auth-body') as HTMLElement;
        (reBody.querySelector('#pf-email') as HTMLInputElement).value = email;
        (reBody.querySelector('#pf-login-error') as HTMLElement).textContent =
          envelope.message || 'Request submitted — an admin will review it.';
      } catch (e) {
        errEl.textContent = 'Network error. Please try again.';
        reBtn.disabled = false;
        reBtn.textContent = 'Request again';
      }
    });
  }
}

// --- Sign-up view --------------------------------------------------------
export function renderSignupView(host: PointerHost): void {
  const body = host.root.querySelector('#pf-auth-body') as HTMLElement | null;
  if (!body) return;
  body.innerHTML = TPL.signupBody();

  const nameEl = body.querySelector('#pf-su-name') as HTMLInputElement;
  const emailEl = body.querySelector('#pf-su-email') as HTMLInputElement;
  const passEl = body.querySelector('#pf-su-password') as HTMLInputElement;
  const roleEl = body.querySelector('#pf-su-role') as HTMLSelectElement;
  const errEl = body.querySelector('#pf-signup-error') as HTMLElement;
  const okEl = body.querySelector('#pf-signup-success') as HTMLElement;
  const submitBtn = body.querySelector('#pf-signup-submit') as HTMLButtonElement;

  // Populate the role <select> from /api/roles when the form opens.
  populateRoles(host, roleEl, errEl);

  (body.querySelector('#pf-show-login') as HTMLElement).addEventListener('click', () => renderLoginView(host));

  const doSignup = async () => {
    const displayName = nameEl.value.trim();
    const email = emailEl.value.trim();
    const password = passEl.value;
    const roleId = roleEl.value;
    errEl.textContent = '';
    okEl.textContent = '';
    if (!displayName) { errEl.textContent = 'Please enter your name.'; return; }
    if (!email) { errEl.textContent = 'Please enter your email.'; return; }
    if (!password) { errEl.textContent = 'Please choose a password.'; return; }
    if (!roleId) { errEl.textContent = 'Please choose a role.'; return; }
    submitBtn.disabled = true;
    submitBtn.textContent = 'Submitting…';
    const restore = () => { submitBtn.disabled = false; submitBtn.textContent = 'Create account'; };
    try {
      const r = await host.apiRegister({ email, password, displayName, roleId });
      const envelope = await r.json();
      if (!r.ok || !envelope.isSuccess) {
        errEl.textContent = envelope.message || 'Could not create your account.';
        restore();
        return;
      }
      // Success: lock the form, show the inline message + a way back to sign in.
      okEl.textContent = envelope.message || 'Request submitted — an admin will review it.';
      submitBtn.textContent = 'Request submitted';
      submitBtn.disabled = true;
      [nameEl, emailEl, passEl, roleEl].forEach((el) => { el.disabled = true; });
    } catch (e) {
      errEl.textContent = 'Network error. Please try again.';
      restore();
    }
  };

  submitBtn.addEventListener('click', doSignup);
  passEl.addEventListener('keydown', (e) => { if (e.key === 'Enter') doSignup(); });
}
