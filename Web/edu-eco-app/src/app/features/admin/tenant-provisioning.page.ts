import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { TenantDetails, TenantsApi } from '../../core/admin/tenants-api';
import { problemDetail } from '../../core/auth/auth-api';
import { SessionStore } from '../../core/auth/session-store';
import { TokenStore } from '../../core/auth/token-store';

@Component({
  selector: 'app-tenant-provisioning-page',
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
            <div class="brand-tag">Platform administration</div>
          </div>
        </div>
        <button type="button" class="signout-btn" (click)="signOut()">
          Sign out
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/>
            <path d="M16 17l5-5-5-5"/><path d="M21 12H9"/>
          </svg>
        </button>
      </header>

      <main class="content">
        <div class="card">
          <div class="card-header">
            <div class="card-icon">
              <svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M6 22V4a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v18Z"/>
                <path d="M6 12H4a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h2"/>
                <path d="M18 9h2a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-2"/>
                <path d="M10 6h4"/><path d="M10 10h4"/><path d="M10 14h4"/><path d="M10 18h4"/>
              </svg>
            </div>
            <div>
              <h1>Register a school</h1>
              <p class="subtitle">Creates the tenant and its administrator, who is emailed a link to set their own password.</p>
            </div>
          </div>

          @if (provisioned(); as result) {
            <div class="status" role="status">
              {{ result.name }} created. An invitation was sent to {{ lastAdminEmail() }}.
            </div>
          }

          <form [formGroup]="form" (ngSubmit)="submit()">
            <div class="field-row">
              <div class="field">
                <label for="code">School code</label>
                <input id="code" formControlName="code" type="text" autocomplete="off" placeholder="northwood-high" />
              </div>
              <div class="field">
                <label for="name">School name</label>
                <input id="name" formControlName="name" type="text" autocomplete="off" placeholder="Northwood High School" />
              </div>
            </div>

            <div class="field">
              <label for="adminEmail">Administrator email</label>
              <input id="adminEmail" formControlName="adminEmail" type="email" autocomplete="off" placeholder="principal@northwood.example" />
            </div>

            <div class="field">
              <label for="adminDisplayName">Administrator name</label>
              <input id="adminDisplayName" formControlName="adminDisplayName" type="text" autocomplete="off" placeholder="Northwood Principal" />
            </div>

            @if (error()) {
              <p role="alert" class="alert">{{ error() }}</p>
            }

            <button type="submit" class="submit-btn" [disabled]="form.invalid || saving()">Register school</button>
          </form>
        </div>

        <div class="card">
          <div class="card-header">
            <div class="card-icon">
              <svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M3 3v16a2 2 0 0 0 2 2h16"/>
                <rect x="7" y="8" width="4" height="9"/>
                <rect x="15" y="5" width="4" height="12"/>
              </svg>
            </div>
            <div>
              <h1>Schools</h1>
              <p class="subtitle">{{ tenants().length }} registered</p>
            </div>
          </div>

          @if (tenants().length === 0) {
            <p class="subtitle">No schools yet.</p>
          } @else {
            <ul class="tenant-list">
              @for (tenant of tenants(); track tenant.id) {
                <li>
                  <span class="tenant-name">{{ tenant.name }}</span>
                  <span class="tenant-code">{{ tenant.code }}</span>
                </li>
              }
            </ul>
          }
        </div>
      </main>
    </div>
  `,
  styles: [`
    .tenant-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.5rem; }
    .tenant-list li {
      display: flex; align-items: center; justify-content: space-between; gap: 1rem;
      border: 1px solid var(--slate-200); border-radius: 0.625rem; padding: 0.75rem 1rem;
    }
    .tenant-name { font-size: 0.875rem; font-weight: 600; color: var(--slate-900); }
    .tenant-code { font-size: 0.75rem; color: var(--slate-500); }
  `],
})
export class TenantProvisioningPage implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly tenantsApi = inject(TenantsApi);
  private readonly tokenStore = inject(TokenStore);
  private readonly sessionStore = inject(SessionStore);
  private readonly router = inject(Router);

  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly provisioned = signal<{ name: string } | null>(null);
  protected readonly lastAdminEmail = signal('');
  protected readonly tenants = signal<TenantDetails[]>([]);

  protected readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(50)]],
    name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(200)]],
    adminEmail: ['', [Validators.required, Validators.email]],
    adminDisplayName: ['', [Validators.required, Validators.minLength(2)]],
  });

  ngOnInit(): void {
    this.loadTenants();
  }

  protected submit(): void {
    if (this.form.invalid || this.saving()) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.provisioned.set(null);
    const value = this.form.getRawValue();

    this.tenantsApi.create(value).subscribe({
      next: (result) => {
        this.lastAdminEmail.set(value.adminEmail);
        this.provisioned.set({ name: result.name });
        this.form.reset();
        this.saving.set(false);
        this.loadTenants();
      },
      error: (err: unknown) => {
        this.error.set(problemDetail(err, 'Could not register the school.'));
        this.saving.set(false);
      },
    });
  }

  protected signOut(): void {
    this.tokenStore.clear();
    this.sessionStore.clear();
    this.router.navigateByUrl('/login');
  }

  private loadTenants(): void {
    this.tenantsApi.list().subscribe({
      next: (page) => this.tenants.set(page.items),
      error: () => this.tenants.set([]),
    });
  }
}
