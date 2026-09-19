import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthApi, problemDetail } from '../../core/auth/auth-api';

@Component({
  selector: 'app-register-page',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <h1>Create an account</h1>

    @if (submitted()) {
      <p role="status">Check your email to confirm your address, then <a routerLink="/login">sign in</a>.</p>
    } @else {
      <form [formGroup]="form" (ngSubmit)="submit()">
        <label>
          Invite code
          <input formControlName="inviteCode" type="text" autocomplete="off" />
        </label>

        <label>
          Email
          <input formControlName="email" type="email" autocomplete="email" />
        </label>

        <label>
          Phone (optional)
          <input formControlName="phoneNumber" type="tel" autocomplete="tel" />
        </label>

        <label>
          Display name
          <input formControlName="displayName" type="text" autocomplete="name" />
        </label>

        <label>
          Password
          <input formControlName="password" type="password" autocomplete="new-password" />
        </label>

        @if (error()) {
          <p role="alert">{{ error() }}</p>
        }

        <button type="submit" [disabled]="form.invalid || submitting()">Create account</button>
      </form>
    }

    <p><a routerLink="/login">Back to sign in</a></p>
  `,
})
export class RegisterPage {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApi);

  protected readonly submitting = signal(false);
  protected readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);

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
