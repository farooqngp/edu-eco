import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { problemDetail } from '../../core/auth/auth-api';
import { SessionStore } from '../../core/auth/session-store';
import { TokenStore } from '../../core/auth/token-store';
import { ProfileApi } from '../../core/profile/profile-api';

@Component({
  selector: 'app-profile-page',
  imports: [ReactiveFormsModule],
  template: `
    <div class="page">
      <header class="topbar">
        <div class="brand">
          <div class="brand-mark">
            <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
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
        <button type="button" class="signout-btn" (click)="signOut()">
          Sign out
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/>
            <path d="M16 17l5-5-5-5"/>
            <path d="M21 12H9"/>
          </svg>
        </button>
      </header>

      <main class="content">
        <div class="card">
          <div class="card-header">
            <div class="card-icon">
              <svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <circle cx="12" cy="8" r="5"/>
                <path d="M20 21a8 8 0 0 0-16 0"/>
              </svg>
            </div>
            <div>
              <h1>Your profile</h1>
              <p class="subtitle">Keep your personal details up to date.</p>
            </div>
          </div>

          <form [formGroup]="form" (ngSubmit)="submit()">
            <div class="field">
              <label for="dateOfBirth">Date of birth</label>
              <input id="dateOfBirth" formControlName="dateOfBirth" type="date" />
            </div>

            <div class="field">
              <label for="address">Address</label>
              <input id="address" formControlName="address" type="text" autocomplete="street-address" placeholder="Street address" />
            </div>

            <div class="field-row">
              <div class="field">
                <label for="city">City</label>
                <input id="city" formControlName="city" type="text" autocomplete="address-level2" placeholder="City" />
              </div>
              <div class="field">
                <label for="postalCode">Postal code</label>
                <input id="postalCode" formControlName="postalCode" type="text" autocomplete="postal-code" placeholder="Postal code" />
              </div>
            </div>

            <div class="field">
              <label for="country">Country</label>
              <input id="country" formControlName="country" type="text" autocomplete="country-name" placeholder="Country" />
            </div>

            @if (error()) {
              <p role="alert" class="alert">{{ error() }}</p>
            }
            @if (saved()) {
              <p role="status" class="status">Saved.</p>
            }

            <button type="submit" class="submit-btn" [disabled]="saving()">Save changes</button>
          </form>
        </div>
      </main>
    </div>
  `,
})
export class ProfilePage implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly profileApi = inject(ProfileApi);
  private readonly tokenStore = inject(TokenStore);
  private readonly sessionStore = inject(SessionStore);
  private readonly router = inject(Router);

  protected readonly saving = signal(false);
  protected readonly saved = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    dateOfBirth: [''],
    address: [''],
    city: [''],
    postalCode: [''],
    country: [''],
  });

  ngOnInit(): void {
    this.profileApi.get().subscribe({
      next: (profile) =>
        this.form.setValue({
          dateOfBirth: profile.dateOfBirth ?? '',
          address: profile.address ?? '',
          city: profile.city ?? '',
          postalCode: profile.postalCode ?? '',
          country: profile.country ?? '',
        }),
      error: (err: unknown) => {
        // No profile saved yet is expected right after registration — leave the form blank, not an error.
        if (!(err instanceof HttpErrorResponse) || err.status !== 404) {
          this.error.set(problemDetail(err, 'Could not load your profile.'));
        }
      },
    });
  }

  protected submit(): void {
    if (this.saving()) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.saved.set(false);
    const value = this.form.getRawValue();

    this.profileApi
      .update({
        dateOfBirth: value.dateOfBirth || null,
        address: value.address || null,
        city: value.city || null,
        postalCode: value.postalCode || null,
        country: value.country || null,
      })
      .subscribe({
        next: () => {
          this.saved.set(true);
          this.saving.set(false);
        },
        error: (err: unknown) => {
          this.error.set(problemDetail(err, 'Could not save your profile.'));
          this.saving.set(false);
        },
      });
  }

  protected signOut(): void {
    this.tokenStore.clear();
    this.sessionStore.clear();
    this.router.navigateByUrl('/login');
  }
}
