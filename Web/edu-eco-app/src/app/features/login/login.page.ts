import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthApi, problemDetail } from '../../core/auth/auth-api';
import { MeApi } from '../../core/auth/me-api';
import { SessionStore } from '../../core/auth/session-store';
import { TokenStore } from '../../core/auth/token-store';

@Component({
  selector: 'app-login-page',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <div class="login-screen">
      <main class="login-card">
        <section class="hero">
          <div class="hero-grid"></div>
          <div class="hero-ring hero-ring-1"></div>
          <div class="hero-ring hero-ring-2"></div>

          <div class="brand">
            <div class="brand-mark">
              <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M21.42 10.922a1 1 0 0 0-.019-1.838L12.83 5.18a2 2 0 0 0-1.66 0L2.6 9.08a1 1 0 0 0 0 1.832l8.57 3.908a2 2 0 0 0 1.66 0z"/>
                <path d="M22 10v6"/>
                <path d="M6 12.5V16a6 3 0 0 0 12 0v-3.5"/>
              </svg>
            </div>
            <div>
              <div class="brand-name">Edu-Ecosystem</div>
              <div class="brand-tag">School Management</div>
            </div>
          </div>

          <div class="hero-copy">
            <div class="hero-pill"><span class="hero-dot"></span> One connected campus</div>
            <h1>Everything your school needs, in one place.</h1>
            <p>Simplify daily operations, empower educators, and keep your entire school community moving forward.</p>
          </div>

          <div class="hero-foot">
            <span>Trusted by forward-thinking schools</span>
            <span class="hero-secure">
              <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/>
                <path d="m9 12 2 2 4-4"/>
              </svg>
              Secure platform
            </span>
          </div>
        </section>

        <section class="form-panel">
          <div class="form-inner">
            <div class="form-header">
              <div class="form-icon">
                <svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                  <path d="M6 22V4a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v18Z"/>
                  <path d="M6 12H4a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h2"/>
                  <path d="M18 9h2a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-2"/>
                  <path d="M10 6h4"/><path d="M10 10h4"/><path d="M10 14h4"/><path d="M10 18h4"/>
                </svg>
              </div>
              <p class="eyebrow">Welcome back</p>
              <h2>Sign in to your portal</h2>
              <p class="subtitle">Access your school workspace and stay connected to what matters.</p>
            </div>

            <form [formGroup]="form" (ngSubmit)="submit()">
              <div class="field">
                <label for="identifier">Email or phone number</label>
                <div class="input-shell">
                  <svg class="input-icon" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <circle cx="12" cy="8" r="5"/>
                    <path d="M20 21a8 8 0 0 0-16 0"/>
                  </svg>
                  <input
                    id="identifier"
                    formControlName="identifier"
                    type="text"
                    autocomplete="username"
                    placeholder="Enter your email or phone"
                  />
                </div>
              </div>

              <div class="field">
                <div class="field-label-row">
                  <label for="password">Password</label>
                  <a routerLink="/forgot-password" class="forgot-link">Forgot password?</a>
                </div>
                <div class="input-shell">
                  <svg class="input-icon" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <circle cx="12" cy="16" r="1"/>
                    <rect x="3" y="10" width="18" height="12" rx="2"/>
                    <path d="M7 10V7a5 5 0 0 1 10 0v3"/>
                  </svg>
                  <input
                    id="password"
                    formControlName="password"
                    [type]="passwordVisible() ? 'text' : 'password'"
                    autocomplete="current-password"
                    placeholder="Enter your password"
                  />
                  <button type="button" class="toggle-visibility" (click)="passwordVisible.set(!passwordVisible())" [attr.aria-label]="passwordVisible() ? 'Hide password' : 'Show password'">
                    @if (passwordVisible()) {
                      <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                        <path d="M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49"/>
                        <path d="M14.084 14.158a3 3 0 0 1-4.242-4.242"/>
                        <path d="M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143"/>
                        <path d="m2 2 20 20"/>
                      </svg>
                    } @else {
                      <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                        <path d="M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0"/>
                        <circle cx="12" cy="12" r="3"/>
                      </svg>
                    }
                  </button>
                </div>
              </div>

              @if (error()) {
                <p role="alert" class="alert">{{ error() }}</p>
              }

              <button type="submit" class="submit-btn" [disabled]="form.invalid || submitting()">
                Sign in to portal
                <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                  <path d="M5 12h14"/><path d="m12 5 7 7-7 7"/>
                </svg>
              </button>
            </form>

            <div class="divider"><span></span>or<span></span></div>

            <a routerLink="/register" class="create-account-btn">
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M2 21a8 8 0 0 1 13.292-6"/>
                <circle cx="10" cy="8" r="5"/>
                <path d="M19 16v6"/><path d="M22 19h-6"/>
              </svg>
              Create an account
            </a>
          </div>
        </section>
      </main>
    </div>
  `,
})
export class LoginPage {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApi);
  private readonly meApi = inject(MeApi);
  private readonly tokenStore = inject(TokenStore);
  private readonly sessionStore = inject(SessionStore);
  private readonly router = inject(Router);

  protected readonly submitting = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly passwordVisible = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    identifier: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });

  protected submit(): void {
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    this.error.set(null);

    this.authApi.login(this.form.getRawValue()).subscribe({
      next: (tokens) => {
        this.tokenStore.set({ accessToken: tokens.accessToken, refreshToken: tokens.refreshToken });

        // Where to land depends on server-resolved permissions, so ask before routing. A failure here is not worth
        // blocking sign-in over: the token is valid, so fall back to the page every signed-in user can use.
        this.meApi.get().subscribe({
          next: (caller) => {
            this.sessionStore.set(caller);
            this.router.navigateByUrl(this.landingUrl());
          },
          error: () => this.router.navigateByUrl('/profile'),
        });
      },
      error: (err: unknown) => {
        this.error.set(problemDetail(err, 'The username or password is incorrect.'));
        this.submitting.set(false);
      },
    });
  }

  private landingUrl(): string {
    if (this.sessionStore.has('tenants.manage')) {
      return '/admin/tenants';
    }

    return this.sessionStore.has('users.manage') ? '/admin' : '/profile';
  }
}
