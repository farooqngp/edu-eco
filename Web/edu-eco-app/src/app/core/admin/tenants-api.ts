import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../config';

export interface TenantDetails {
  id: number;
  code: string;
  name: string;
  isActive: boolean;
  createdAtUtc: string;
}

export interface PagedResult<T> {
  items: T[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
}

export interface CreateTenantRequest {
  code: string;
  name: string;
  adminEmail: string;
  adminDisplayName: string;
}

/** No credential is returned: the administrator sets their own password from the emailed link. */
export interface TenantProvisionedResponse {
  id: number;
  code: string;
  name: string;
  adminUserId: number;
  invitationSent: boolean;
}

@Injectable({ providedIn: 'root' })
export class TenantsApi {
  private readonly http = inject(HttpClient);

  create(request: CreateTenantRequest): Observable<TenantProvisionedResponse> {
    return this.http.post<TenantProvisionedResponse>(`${API_BASE_URL}/api/v1/tenants`, request);
  }

  list(page = 1, pageSize = 25): Observable<PagedResult<TenantDetails>> {
    return this.http.get<PagedResult<TenantDetails>>(`${API_BASE_URL}/api/v1/tenants`, {
      params: { page, pageSize },
    });
  }

  current(): Observable<TenantDetails> {
    return this.http.get<TenantDetails>(`${API_BASE_URL}/api/v1/tenants/current`);
  }
}
