import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthApi, problemDetail } from '../../core/auth/auth-api';

@Component({
  selector: 'app-register-page',
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
                <p class="subtitle">We sent a confirmation link to your inbox. Confirm your address, then sign in.</p>
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
                    <path d="M2 21a8 8 0 0 1 13.292-6"/>
                    <circle cx="10" cy="8" r="5"/>
                    <path d="M19 16v6"/><path d="M22 19h-6"/>
                  </svg>
                </div>
                <p class="eyebrow">Join your campus</p>
                <h2>Create an account</h2>
                <p class="subtitle">Set up your Edu-Ecosystem profile and connect with your school.</p>
              </div>

              <form [formGroup]="form" (ngSubmit)="submit()">
                <div class="field">
                  <label for="inviteCode">Invite code</label>
                  <div class="input-shell">
                    <svg class="input-icon" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                      <path d="M2 9a3 3 0 0 1 0 6v2a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-2a3 3 0 0 1 0-6V7a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2Z"/>
                      <path d="M13 5v2"/><path d="M13 17v2"/><path d="M13 11v2"/>
                    </svg>
                    <input id="inviteCode" formControlName="inviteCode" type="text" autocomplete="off" placeholder="Enter your invite code" />
                  </div>
                </div>

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

                <div class="field">
                  <label for="phoneNumber">Phone <span class="optional">(optional)</span></label>
                  <div class="input-shell">
                    <svg class="input-icon" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                      <path d="M13.832 16.568a1 1 0 0 0 1.213-.303l.355-.465A2 2 0 0 1 17 15h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2A18 18 0 0 1 2 4a2 2 0 0 1 2-2h3a2 2 0 0 1 2 2v3a2 2 0 0 1-.8 1.6l-.468.351a1 1 0 0 0-.292 1.233 14 14 0 0 0 6.392 6.384"/>
                    </svg>
                    <input id="phoneNumber" formControlName="phoneNumber" type="tel" autocomplete="tel" placeholder="Enter your phone number" />
                  </div>
                </div>

                <div class="field">
                  <label for="displayName">Display name</label>
                  <div class="input-shell">
                    <svg class="input-icon" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                      <rect x="2" y="5" width="20" height="14" rx="2"/>
                      <circle cx="9" cy="10" r="2"/>
                      <path d="M15 8h2"/><path d="M15 12h2"/><path d="M7 16h10"/>
                    </svg>
                    <input id="displayName" formControlName="displayName" type="text" autocomplete="name" placeholder="Enter your display name" />
                  </div>
                </div>

                <div class="field">
                  <label for="password">Password</label>
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
                      autocomplete="new-password"
                      placeholder="Create a secure password"
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
                  Create account
                  <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <path d="M5 12h14"/><path d="m12 5 7 7-7 7"/>
                  </svg>
                </button>
              </form>

              <p class="signin-hint">Already have an account? <a routerLink="/login" class="forgot-link">Back to sign in</a></p>
            }
          </div>
        </section>
      </main>
    </div>
  `,
})
export class RegisterPage {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApi);

  protected readonly submitting = signal(false);
  protected readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly passwordVisible = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    inviteCode: ['', Validators.required],
    email: ['', [Validators.required, Validators.email]],
    phoneNumber: [''],
    displayName: ['', [Validators.required, Validators.minLength(2)]],
    password: ['', [Validators.required, Validators.minLength(12)]],
  });

  protected submit(): void {
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    this.error.set(null);
    const value = this.form.getRawValue();

    this.authApi
      .register({ ...value, phoneNumber: value.phoneNumber || null })
      .subscribe({
        next: () => this.submitted.set(true),
        error: (err: unknown) => {
          this.error.set(problemDetail(err, 'Registration failed. Check your invite code and try again.'));
          this.submitting.set(false);
        },
      });
  }
}
