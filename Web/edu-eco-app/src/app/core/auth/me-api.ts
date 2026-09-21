import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../config';

export interface CallerResponse {
  subject: string | null;
  userId: number | null;
  clientId: string | null;
  isServiceClient: boolean;
  /** Null for a platform admin acting outside any tenant. */
  tenantId: number | null;
  scopes: string[];
  roles: string[];
  /** Resolved server-side; the only thing the UI should gate on. */
  effectivePermissions: string[];
}

@Injectable({ providedIn: 'root' })
export class MeApi {
  private readonly http = inject(HttpClient);

  get(): Observable<CallerResponse> {
    return this.http.get<CallerResponse>(`${API_BASE_URL}/api/v1/me`);
  }
}
