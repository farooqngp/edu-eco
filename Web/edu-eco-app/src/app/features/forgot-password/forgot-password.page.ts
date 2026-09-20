import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthApi } from '../../core/auth/auth-api';

@Component({
  selector: 'app-forgot-password-page',
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
            @if (submitted()) {
              <div class="success">
                <div class="success-icon">
                  <svg viewBox="0 0 24 24" width="28" height="28" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <path d="M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2Z"/>
                    <path d="m22 6-10 7L2 6"/>
                  </svg>
                </div>
                <h2>Check your email</h2>
                <p class="subtitle">If an account exists for that address, we've sent a link to reset your password.</p>
                <a routerLink="/login" class="submit-btn success-link">
                  Back to sign in
                  <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <path d="M5 12h14"/><path d="m12 5 7 7-7 7"/>
                  </svg>
                </a>
              </div>
            } @else {
              <div class="form-header">
                <div class="form-icon">
                  <svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <circle cx="12" cy="16" r="1"/>
                    <rect x="3" y="10" width="18" height="12" rx="2"/>
                    <path d="M7 10V7a5 5 0 0 1 10 0v3"/>
                  </svg>
                </div>
                <p class="eyebrow">Reset access</p>
                <h2>Forgot your password?</h2>
                <p class="subtitle">Enter the email on your account and we'll send you a reset link.</p>
              </div>

              <form [formGroup]="form" (ngSubmit)="submit()">
                <div class="field">
                  <label for="email">Email</label>
                  <div class="input-shell">
                    <svg class="input-icon" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                      <rect x="2" y="4" width="20" height="16" rx="2"/>
                      <path d="m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7"/>
                    </svg>
                    <input id="email" formControlName="email" type="email" autocomplete="email" placeholder="Enter your email address" />
                  </div>
                </div>

                <button type="submit" class="submit-btn" [disabled]="form.invalid || submitting()">
                  Send reset link
                  <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <path d="M5 12h14"/><path d="m12 5 7 7-7 7"/>
                  </svg>
                </button>
              </form>

              <p class="signin-hint">Remembered your password? <a routerLink="/login" class="forgot-link">Back to sign in</a></p>
            }
          </div>
        </section>
      </main>
    </div>
  `,
})
export class ForgotPasswordPage {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApi);

  protected readonly submitting = signal(false);
  protected readonly submitted = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
  });

  protected submit(): void {
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    const { email } = this.form.getRawValue();

    // 202 regardless of whether the email exists (no enumeration) — always show the same success state.
    this.authApi.forgotPassword(email).subscribe({
      next: () => this.submitted.set(true),
      error: () => this.submitted.set(true),
    });
  }
}
