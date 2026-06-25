import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { MeService } from '@api/me/me.service';
import { UpdatePreferencesRequest } from '@api/model';

type Lang = 'ar' | 'en';
type Theme = 'light' | 'dark';
const LANG_KEY = 'pointer_admin_lang';
const THEME_KEY = 'pointer_admin_theme';
const USER_KEY = 'pointer_admin_user';

@Injectable({ providedIn: 'root' })
export class PreferencesService {
  private transloco = inject(TranslocoService);
  private meService = inject(MeService);
  language = signal<Lang>('en');
  theme = signal<Theme>('dark');

  private storage(): Storage | null {
    try { return typeof localStorage !== 'undefined' ? localStorage : null; } catch { return null; }
  }

  init(saved?: { language?: string | null; theme?: string | null }): void {
    const store = this.storage();
    const lang = (saved?.language as Lang) || (store?.getItem(LANG_KEY) as Lang) || this.browserLang();
    const theme = (saved?.theme as Theme) || (store?.getItem(THEME_KEY) as Theme) || this.systemTheme();
    this.language.set(lang === 'ar' ? 'ar' : 'en');
    this.theme.set(theme === 'light' ? 'light' : 'dark');
    this.apply();
  }

  private browserLang(): Lang {
    const lang = (typeof navigator !== 'undefined' ? navigator.language : '') || '';
    return lang.toLowerCase().startsWith('ar') ? 'ar' : 'en';
  }

  private systemTheme(): Theme {
    return (typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches) ? 'dark' : 'light';
  }

  apply(): void {
    const l = this.language();
    const t = this.theme();
    this.transloco.setActiveLang(l);
    if (typeof document !== 'undefined') {
      const html = document.documentElement;
      html.setAttribute('lang', l);
      html.setAttribute('dir', l === 'ar' ? 'rtl' : 'ltr');
      html.classList.toggle('dark', t === 'dark');
    }
    const store = this.storage();
    store?.setItem(LANG_KEY, l);
    store?.setItem(THEME_KEY, t);
    const raw = store?.getItem(USER_KEY);
    if (raw) {
      try {
        const u = JSON.parse(raw);
        u.language = l;
        u.theme = t;
        store?.setItem(USER_KEY, JSON.stringify(u));
      } catch { /* ignore */ }
    }
  }

  setLanguage(l: Lang): void {
    this.language.set(l);
    this.apply();
    this.persist({ language: l });
  }

  setTheme(t: Theme): void {
    this.theme.set(t);
    this.apply();
    this.persist({ theme: t });
  }

  private persist(p: UpdatePreferencesRequest): void {
    if (!this.storage()?.getItem('pointer_admin_token')) return;
    this.meService.patchApiMePreferences(p).subscribe({ next: () => {}, error: () => {} });
  }
}
