import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { InviteResponse, InvitesApi } from '../../core/admin/invites-api';
import { TenantsApi } from '../../core/admin/tenants-api';
import { problemDetail } from '../../core/auth/auth-api';
import { SessionStore } from '../../core/auth/session-store';
import { TokenStore } from '../../core/auth/token-store';

@Component({
  selector: 'app-admin-dashboard-page',
  imports: [ReactiveFormsModule, RouterLink],
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
            <div class="brand-name">{{ tenantName() || 'Edu-Ecosystem' }}</div>
            <div class="brand-tag">School administration</div>
          </div>
        </div>
        <div class="topbar-actions">
          <a routerLink="/profile" class="topbar-link">My profile</a>
          <button type="button" class="signout-btn" (click)="signOut()">
            Sign out
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/>
              <path d="M16 17l5-5-5-5"/><path d="M21 12H9"/>
            </svg>
          </button>
        </div>
      </header>

      <main class="content">
        <div class="card">
          <div class="card-header">
            <div class="card-icon">
              <svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M2 21a8 8 0 0 1 13.292-6"/>
                <circle cx="10" cy="8" r="5"/>
                <path d="M19 16v6"/><path d="M22 19h-6"/>
              </svg>
            </div>
            <div>
              <h1>Invite a member</h1>
              <p class="subtitle">The invite code is emailed to them and shown once below.</p>
            </div>
          </div>

          @if (issued(); as invite) {
            <div class="invite-result" role="status">
              <p class="status">Invite emailed to {{ lastEmail() }}.</p>
              <code>{{ invite.code }}</code>
            </div>
          }

          <form [formGroup]="form" (ngSubmit)="submit()">
            <div class="field">
              <label for="email">Invitee email</label>
              <input id="email" formControlName="email" type="email" autocomplete="off" placeholder="new.teacher@example.com" />
            </div>

            <div class="field-row">
              <div class="field">
                <label for="roleName">Role</label>
                <select id="roleName" formControlName="roleName">
                  @for (role of roles; track role) {
                    <option [value]="role">{{ role }}</option>
                  }
                </select>
              </div>
              <div class="field">
                <label for="expiresOn">Expires</label>
                <input id="expiresOn" formControlName="expiresOn" type="date" />
              </div>
            </div>

            @if (error()) {
              <p role="alert" class="alert">{{ error() }}</p>
            }

            <button type="submit" class="submit-btn" [disabled]="form.invalid || saving()">Send invite</button>
          </form>
        </div>
      </main>
    </div>
  `,
  styles: [`
    .invite-result { margin-bottom: 1.25rem; }
    .invite-result code {
      display: block; margin-top: 0.5rem; padding: 0.75rem 1rem;
      border: 1px dashed var(--slate-300); border-radius: 0.625rem; background: var(--slate-50);
      font-size: 1rem; letter-spacing: 0.08em; color: var(--slate-900);
    }
  `],
})
export class AdminDashboardPage implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly invitesApi = inject(InvitesApi);
  private readonly tenantsApi = inject(TenantsApi);
  private readonly tokenStore = inject(TokenStore);
  private readonly sessionStore = inject(SessionStore);
  private readonly router = inject(Router);

  protected readonly roles = ['Student', 'Teacher', 'Parent', 'TenantAdmin'];
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly issued = signal<InviteResponse | null>(null);
  protected readonly lastEmail = signal('');
  protected readonly tenantName = signal('');

  protected readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    roleName: ['Student', Validators.required],
    expiresOn: [this.defaultExpiry(), Validators.required],
  });

  ngOnInit(): void {
    this.tenantsApi.current().subscribe({
      next: (tenant) => this.tenantName.set(tenant.name),
      error: () => this.tenantName.set(''),
    });
  }

  protected submit(): void {
    if (this.form.invalid || this.saving()) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.issued.set(null);
    const value = this.form.getRawValue();

    this.invitesApi
      .issue({
        email: value.email,
        roleName: value.roleName,
        // The API takes an instant; a date input gives a day, so send end of that day in UTC.
        expiresAtUtc: new Date(`${value.expiresOn}T23:59:59Z`).toISOString(),
      })
      .subscribe({
        next: (invite) => {
          this.lastEmail.set(value.email);
          this.issued.set(invite);
          this.form.patchValue({ email: '' });
          this.saving.set(false);
        },
        error: (err: unknown) => {
          this.error.set(problemDetail(err, 'Could not issue the invite.'));
          this.saving.set(false);
        },
      });
  }

  protected signOut(): void {
    this.tokenStore.clear();
    this.sessionStore.clear();
    this.router.navigateByUrl('/login');
  }

  private defaultExpiry(): string {
    const date = new Date();
    date.setDate(date.getDate() + 7);
    return date.toISOString().slice(0, 10);
  }
}
