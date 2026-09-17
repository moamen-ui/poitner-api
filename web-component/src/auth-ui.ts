import { TPL } from './templates';
import { escapeHtml } from './dom';
import { t } from './i18n';
import type { PointerHost, User } from './types';

// One modal shell, two swappable bodies (sign-in / sign-up). The shell owns the
// Skip control (deferred-login dismissal); the views fill #fbk-auth-body and wire
// their own events. Decoupled from the element via the PointerHost interface.

export function showLoginModal(host: PointerHost, afterLogin?: () => void): void {
  host.afterLogin = afterLogin || null;
  host.root.innerHTML = TPL.loginModal(host.project);

  // Skip → dismiss without logging in; restore the toolbar so the user can come
  // back to it later by clicking the tool again.
  const skipBtn = host.root.querySelector('#fbk-login-skip');
  if (skipBtn) skipBtn.addEventListener('click', () => { host.afterLogin = null; host.renderChrome(); });

  renderLoginView(host);
}

// Populate a <select> with [{id,name}] roles fetched anonymously. Disables the
// element while loading and on failure (shows a placeholder option).
async function populateRoles(host: PointerHost, selectEl: HTMLSelectElement | null, errEl: HTMLElement | null): Promise<void> {
  if (!selectEl) return;
  selectEl.disabled = true;
  selectEl.innerHTML = `<option value="">${t('auth.loadingRoles')}</option>`;
  try {
    const roles = await host.apiRoles();
    if (!roles.length) {
      selectEl.innerHTML = `<option value="">${t('auth.noRolesAvailable')}</option>`;
      return;
    }
    selectEl.innerHTML = roles.map((r) =>
      `<option value="${escapeHtml(r.id)}">${escapeHtml(r.name)}</option>`).join('');
    selectEl.disabled = false;
  } catch (e) {
    selectEl.innerHTML = `<option value="">${t('auth.couldNotLoadRoles')}</option>`;
    if (errEl) errEl.textContent = (e as Error).message || t('auth.couldNotLoadRoles');
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
  const body = host.root.querySelector('#fbk-auth-body') as HTMLElement | null;
  if (!body) return;
  body.innerHTML = TPL.loginBody(!!opts.rejected);

  const emailEl = body.querySelector('#fbk-email') as HTMLInputElement;
  const passEl = body.querySelector('#fbk-password') as HTMLInputElement;
  const errEl = body.querySelector('#fbk-login-error') as HTMLElement;
  const submitBtn = body.querySelector('#fbk-login-submit') as HTMLButtonElement;

  const doLogin = async () => {
    const email = emailEl.value.trim();
    const password = passEl.value;
    if (!email) { errEl.textContent = t('auth.pleaseEnterEmail'); return; }
    if (!password) { errEl.textContent = t('auth.pleaseEnterPassword'); return; }
    errEl.textContent = '';
    submitBtn.disabled = true;
    submitBtn.textContent = t('auth.signingIn');
    const restore = () => { submitBtn.disabled = false; submitBtn.textContent = t('auth.signIn'); };
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
        errEl.textContent = envelope.message || t('auth.pendingApproval');
        restore();
        return;
      }
      if (status === 'disabled') {
        errEl.textContent = envelope.message || t('auth.accountDisabled');
        restore();
        return;
      }
      if (status === 'rejected') {
        // Re-render with the re-apply block, preserving the typed credentials.
        renderLoginView(host, { rejected: true });
        const re = host.root.querySelector('#fbk-auth-body') as HTMLElement;
        (re.querySelector('#fbk-email') as HTMLInputElement).value = email;
        (re.querySelector('#fbk-password') as HTMLInputElement).value = password;
        (re.querySelector('#fbk-login-error') as HTMLElement).textContent =
          envelope.message || t('auth.requestRejected');
        return;
      }
      // Missing/unknown status with failure → generic message.
      errEl.textContent = envelope.message || t('auth.invalidCredentials');
      restore();
    } catch (e) {
      errEl.textContent = t('auth.networkError');
      restore();
    }
  };

  submitBtn.addEventListener('click', doLogin);
  passEl.addEventListener('keydown', (e) => { if (e.key === 'Enter') doLogin(); });

  (body.querySelector('#fbk-show-signup') as HTMLElement).addEventListener('click', () => renderSignupView(host));

  // Rejected re-apply block: populate roles and wire "Request again".
  if (opts.rejected) {
    const roleEl = body.querySelector('#fbk-reapply-role') as HTMLSelectElement;
    const reBtn = body.querySelector('#fbk-reapply-submit') as HTMLButtonElement;
    populateRoles(host, roleEl, errEl);
    reBtn.addEventListener('click', async () => {
      const email = emailEl.value.trim();
      const password = passEl.value;
      const roleId = roleEl.value;
      if (!roleId) { errEl.textContent = t('auth.pleaseChooseRole'); return; }
      if (!email || !password) { errEl.textContent = t('auth.enterEmailPasswordToRequestAgain'); return; }
      errEl.textContent = '';
      reBtn.disabled = true;
      reBtn.textContent = t('auth.submitting');
      try {
        const r = await host.apiRegister({ email, password, displayName: '', roleId });
        const envelope = await r.json();
        if (!r.ok || !envelope.isSuccess) {
          errEl.textContent = envelope.message || t('auth.couldNotSubmitRequest');
          reBtn.disabled = false;
          reBtn.textContent = t('auth.requestAgain');
          return;
        }
        // Success → collapse the re-apply block; show the submitted message.
        renderLoginView(host);
        const reBody = host.root.querySelector('#fbk-auth-body') as HTMLElement;
        (reBody.querySelector('#fbk-email') as HTMLInputElement).value = email;
        (reBody.querySelector('#fbk-login-error') as HTMLElement).textContent =
          envelope.message || t('auth.requestSubmittedMsg');
      } catch (e) {
        errEl.textContent = t('auth.networkError');
        reBtn.disabled = false;
        reBtn.textContent = t('auth.requestAgain');
      }
    });
  }
}

// --- Sign-up view --------------------------------------------------------
export function renderSignupView(host: PointerHost): void {
  const body = host.root.querySelector('#fbk-auth-body') as HTMLElement | null;
  if (!body) return;
  body.innerHTML = TPL.signupBody();

  const nameEl = body.querySelector('#fbk-su-name') as HTMLInputElement;
  const emailEl = body.querySelector('#fbk-su-email') as HTMLInputElement;
  const passEl = body.querySelector('#fbk-su-password') as HTMLInputElement;
  const roleEl = body.querySelector('#fbk-su-role') as HTMLSelectElement;
  const errEl = body.querySelector('#fbk-signup-error') as HTMLElement;
  const okEl = body.querySelector('#fbk-signup-success') as HTMLElement;
  const submitBtn = body.querySelector('#fbk-signup-submit') as HTMLButtonElement;

  // Populate the role <select> from /api/roles when the form opens.
  populateRoles(host, roleEl, errEl);

  (body.querySelector('#fbk-show-login') as HTMLElement).addEventListener('click', () => renderLoginView(host));

  const doSignup = async () => {
    const displayName = nameEl.value.trim();
    const email = emailEl.value.trim();
    const password = passEl.value;
    const roleId = roleEl.value;
    errEl.textContent = '';
    okEl.textContent = '';
    if (!displayName) { errEl.textContent = t('auth.pleaseEnterName'); return; }
    if (!email) { errEl.textContent = t('auth.pleaseEnterEmail'); return; }
    if (!password) { errEl.textContent = t('auth.pleaseChoosePassword'); return; }
    if (!roleId) { errEl.textContent = t('auth.pleaseChooseRole'); return; }
    submitBtn.disabled = true;
    submitBtn.textContent = t('auth.submitting');
    const restore = () => { submitBtn.disabled = false; submitBtn.textContent = t('auth.createAccount'); };
    try {
      const r = await host.apiRegister({ email, password, displayName, roleId });
      const envelope = await r.json();
      if (!r.ok || !envelope.isSuccess) {
        errEl.textContent = envelope.message || t('auth.couldNotCreateAccount');
        restore();
        return;
      }
      // Success: lock the form, show the inline message + a way back to sign in.
      okEl.textContent = envelope.message || t('auth.requestSubmittedMsg');
      submitBtn.textContent = t('auth.requestSubmittedBtn');
      submitBtn.disabled = true;
      [nameEl, emailEl, passEl, roleEl].forEach((el) => { el.disabled = true; });
    } catch (e) {
      errEl.textContent = t('auth.networkError');
      restore();
    }
  };

  submitBtn.addEventListener('click', doSignup);
  passEl.addEventListener('keydown', (e) => { if (e.key === 'Enter') doSignup(); });
}
