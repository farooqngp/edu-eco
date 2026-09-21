import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../config';

export interface IssueInviteRequest {
  roleName: string;
  expiresAtUtc: string;
  /** The code is emailed here as well as returned below. */
  email: string;
}

export interface InviteResponse {
  inviteId: number;
  code: string;
  expiresAtUtc: string;
}

@Injectable({ providedIn: 'root' })
export class InvitesApi {
  private readonly http = inject(HttpClient);

  issue(request: IssueInviteRequest): Observable<InviteResponse> {
    return this.http.post<InviteResponse>(`${API_BASE_URL}/api/v1/invites`, request);
  }
}
