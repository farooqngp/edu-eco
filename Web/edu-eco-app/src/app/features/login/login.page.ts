import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthApi, problemDetail } from '../../core/auth/auth-api';
import { TokenStore } from '../../core/auth/token-store';

@Component({
  selector: 'app-login-page',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <h1>Sign in</h1>

    <form [formGroup]="form" (ngSubmit)="submit()">
      <label>
        Email
        <input formControlName="identifier" type="email" autocomplete="email" />
      </label>

      <label>
        Password
        <input formControlName="password" type="password" autocomplete="current-password" />
      </label>

      @if (error()) {
        <p role="alert">{{ error() }}</p>
      }

      <button type="submit" [disabled]="form.invalid || submitting()">Sign in</button>
    </form>

    <p><a routerLink="/register">Create an account</a></p>
  `,
})
export class LoginPage {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApi);
  private readonly tokenStore = inject(TokenStore);
  private readonly router = inject(Router);

  protected readonly submitting = signal(false);
  protected readonly error = signal<string | null>(null);

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
        this.router.navigateByUrl('/profile');
      },
      error: (err: unknown) => {
        this.error.set(problemDetail(err, 'The username or password is incorrect.'));
        this.submitting.set(false);
      },
    });
  }
}
