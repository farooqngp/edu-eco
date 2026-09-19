import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../config';

export interface ProfileResponse {
  userId: number;
  dateOfBirth: string | null;
  address: string | null;
  city: string | null;
  postalCode: string | null;
  country: string | null;
  createdAtUtc: string;
  updatedAtUtc: string | null;
}

export interface UpdateProfileRequest {
  dateOfBirth?: string | null;
  address?: string | null;
  city?: string | null;
  postalCode?: string | null;
  country?: string | null;
}

@Injectable({ providedIn: 'root' })
export class ProfileApi {
  private readonly http = inject(HttpClient);

  get(): Observable<ProfileResponse> {
    return this.http.get<ProfileResponse>(`${API_BASE_URL}/api/v1/profile`);
  }

  update(request: UpdateProfileRequest): Observable<ProfileResponse> {
    return this.http.put<ProfileResponse>(`${API_BASE_URL}/api/v1/profile`, request);
  }
}
