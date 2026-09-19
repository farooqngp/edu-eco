import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { problemDetail } from '../../core/auth/auth-api';
import { ProfileApi } from '../../core/profile/profile-api';

@Component({
  selector: 'app-profile-page',
  imports: [ReactiveFormsModule],
  template: `
    <h1>Your profile</h1>

    <form [formGroup]="form" (ngSubmit)="submit()">
      <label>
        Date of birth
        <input formControlName="dateOfBirth" type="date" />
      </label>

      <label>
        Address
        <input formControlName="address" type="text" autocomplete="street-address" />
      </label>

      <label>
        City
        <input formControlName="city" type="text" autocomplete="address-level2" />
      </label>

      <label>
        Postal code
        <input formControlName="postalCode" type="text" autocomplete="postal-code" />
      </label>

      <label>
        Country
        <input formControlName="country" type="text" autocomplete="country-name" />
      </label>

      @if (error()) {
        <p role="alert">{{ error() }}</p>
      }
      @if (saved()) {
        <p role="status">Saved.</p>
      }

      <button type="submit" [disabled]="saving()">Save</button>
    </form>
  `,
})
export class ProfilePage implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly profileApi = inject(ProfileApi);

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
}
