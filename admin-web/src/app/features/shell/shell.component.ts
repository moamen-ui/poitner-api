import { Component, inject } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { BidiModule } from '@angular/cdk/bidi';
import { TranslocoModule } from '@jsverse/transloco';
import { AuthService } from '../../core/auth/auth.service';
import { PreferencesService } from '../../core/prefs/preferences.service';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatToolbarModule,
    MatSidenavModule,
    MatListModule,
    MatIconModule,
    MatButtonModule,
    BidiModule,
    TranslocoModule,
  ],
  template: `
    <mat-toolbar class="toolbar">
      <span class="brand">
        <mat-icon class="brand-icon">push_pin</mat-icon>
        {{ 'header.brand' | transloco }}
      </span>
      <span class="spacer"></span>
      @if (auth.user()) {
        <span class="user-info">
          <mat-icon class="user-icon">account_circle</mat-icon>
          {{ auth.user()!.displayName }} · {{ auth.user()!.roleName }}
        </span>
      }
      <button mat-icon-button (click)="toggleTheme()" class="toolbar-icon-btn">
        <mat-icon>{{ prefs.theme() === 'dark' ? 'light_mode' : 'dark_mode' }}</mat-icon>
      </button>
      <button mat-button (click)="togglePrefsLang()" class="lang-btn">{{ prefs.language() === 'ar' ? 'EN' : 'ع' }}</button>
      <button mat-stroked-button class="signout" (click)="auth.logout()">
        <mat-icon>logout</mat-icon> {{ 'header.signOut' | transloco }}
      </button>
    </mat-toolbar>

    <mat-sidenav-container class="sidenav-container" [dir]="prefs.language() === 'ar' ? 'rtl' : 'ltr'">
      <mat-sidenav mode="side" opened class="sidenav">
        <mat-nav-list>
          <a mat-list-item routerLink="/overview" routerLinkActive="active-link">
            <mat-icon matListItemIcon>dashboard</mat-icon>
            <span matListItemTitle>{{ 'nav.overview' | transloco }}</span>
          </a>
          <a mat-list-item routerLink="/roles" routerLinkActive="active-link">
            <mat-icon matListItemIcon>manage_accounts</mat-icon>
            <span matListItemTitle>{{ 'nav.roles' | transloco }}</span>
          </a>
          <a mat-list-item routerLink="/users" routerLinkActive="active-link">
            <mat-icon matListItemIcon>people</mat-icon>
            <span matListItemTitle>{{ 'nav.users' | transloco }}</span>
          </a>
          <a mat-list-item routerLink="/projects" routerLinkActive="active-link">
            <mat-icon matListItemIcon>folder</mat-icon>
            <span matListItemTitle>{{ 'nav.projects' | transloco }}</span>
          </a>
        </mat-nav-list>
      </mat-sidenav>

      <mat-sidenav-content class="content">
        <router-outlet />
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; height: 100vh; }

    /* Header — white, distinct from the sidebar, with a subtle divider/shadow */
    .toolbar {
      flex-shrink: 0;
      background: var(--header-bg);
      color: var(--ink);
      border-bottom: 1px solid var(--border);
      box-shadow: 0 1px 3px rgba(15, 23, 42, 0.05);
      z-index: 2;
    }
    .brand { display: flex; align-items: center; gap: 8px; font-weight: 700; font-size: 1.1rem; }
    .brand-icon { color: var(--brand); transform: rotate(45deg); }
    .brand-light { color: var(--muted); font-weight: 600; }
    .spacer { flex: 1; }
    .user-info { display: inline-flex; align-items: center; gap: 6px; margin-inline-end: 14px; font-size: 0.9rem; color: var(--muted); }
    .user-icon { color: var(--muted); }
    .signout { color: var(--ink); border-color: var(--border); }

    .sidenav-container { flex: 1; overflow: hidden; background: var(--app-bg); }

    /* Sidebar — light tinted panel, a different bg than the white header/content */
    .sidenav {
      width: 232px;
      background: var(--sidebar-bg);
      border-inline-end: 1px solid var(--border);
      padding-top: 8px;
    }
    .sidenav a.mat-mdc-list-item {
      margin: 2px 10px;
      border-radius: 10px;
      color: var(--muted);
    }
    .sidenav a.mat-mdc-list-item mat-icon { color: var(--muted); }
    .sidenav a.mat-mdc-list-item:hover { background: rgba(15, 23, 42, 0.04); }
    .active-link.mat-mdc-list-item { background: var(--brand-tint); color: var(--brand); font-weight: 600; }
    .active-link.mat-mdc-list-item mat-icon { color: var(--brand); }

    .content { padding: 24px; overflow-y: auto; height: 100%; background: var(--app-bg); }
  `],
})
export class ShellComponent {
  auth = inject(AuthService);
  prefs = inject(PreferencesService);

  togglePrefsLang(): void {
    this.prefs.setLanguage(this.prefs.language() === 'ar' ? 'en' : 'ar');
  }

  toggleTheme(): void {
    this.prefs.setTheme(this.prefs.theme() === 'dark' ? 'light' : 'dark');
  }
}
