import { Injectable, signal } from '@angular/core';
import { CallerResponse } from './me-api';

/**
 * Who the signed-in caller is, from `GET api/v1/me` — never decoded from the JWT, because permissions are resolved
 * server-side and are deliberately not in the token. Lives alongside the in-memory TokenStore, so it is lost on a
 * hard reload exactly as the tokens are; guards must treat "nothing loaded" as "not allowed".
 */
@Injectable({ providedIn: 'root' })
export class SessionStore {
  readonly roles = signal<string[]>([]);
  readonly permissions = signal<string[]>([]);
  readonly tenantId = signal<number | null>(null);
  readonly loaded = signal(false);

  set(caller: CallerResponse): void {
    this.roles.set(caller.roles ?? []);
    this.permissions.set(caller.effectivePermissions ?? []);
    this.tenantId.set(caller.tenantId);
    this.loaded.set(true);
  }

  has(permission: string): boolean {
    return this.permissions().includes(permission);
  }

  clear(): void {
    this.roles.set([]);
    this.permissions.set([]);
    this.tenantId.set(null);
    this.loaded.set(false);
  }
}
