import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';
import { AuthService } from '../../core/auth/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [ReactiveFormsModule, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, TranslocoModule],
  template: `
    <div class="login-wrap">
      <mat-card class="login-card">
        <h1>{{ 'login.title' | transloco }}</h1>
        <form [formGroup]="form" (ngSubmit)="submit()">
          <mat-form-field appearance="outline"><mat-label>{{ 'login.email' | transloco }}</mat-label>
            <input matInput type="email" formControlName="email" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>{{ 'login.password' | transloco }}</mat-label>
            <input matInput type="password" formControlName="password" /></mat-form-field>
          <button mat-flat-button color="primary" [disabled]="form.invalid || loading()">{{ 'login.signIn' | transloco }}</button>
        </form>
      </mat-card>
    </div>`,
  styles: [`.login-wrap{min-height:100vh;display:flex;align-items:center;justify-content:center;background:#f1f5f9}
    .login-card{width:360px;max-width:92vw;padding:24px;display:flex;flex-direction:column;gap:8px}
    form{display:flex;flex-direction:column;gap:8px} button{margin-top:8px}`],
})
export class LoginComponent {
  private fb = inject(FormBuilder);
  private auth = inject(AuthService);
  private router = inject(Router);
  private snack = inject(MatSnackBar);
  private transloco = inject(TranslocoService);
  loading = signal(false);

  constructor() {
    if (this.auth.isAuthenticated() && this.auth.isAdmin()) {
      this.router.navigateByUrl('/overview');
    }
  }
  form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });

  submit() {
    if (this.form.invalid) return;
    this.loading.set(true);
    const { email, password } = this.form.getRawValue();
    this.auth.login(email, password).subscribe({
      next: (user) => {
        this.loading.set(false);
        if (!user.isAdmin) {
          this.snack.open(this.transloco.translate('login.notAdmin'), 'OK', { duration: 4000 });
          this.auth.logout();
          return;
        }
        this.router.navigateByUrl('/overview');
      },
      error: (e) => {
        this.loading.set(false);
        this.snack.open(e.message || this.transloco.translate('login.failed'), 'OK', { duration: 4000 });
      },
    });
  }
}
