import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../config';

export interface RegisterRequest {
  inviteCode: string;
  email: string;
  phoneNumber?: string | null;
  displayName: string;
  password: string;
}

export interface RegisterResponse {
  requiresEmailConfirmation: boolean;
}

export interface LoginRequest {
  identifier: string;
  password: string;
}

export interface TokenResponse {
  accessToken: string;
  refreshToken: string | null;
  expiresIn: number;
  tokenType: string;
}

/** Extracts the RFC 9457 `detail` field the API returns on 4xx/5xx, falling back to a generic message. */
export function problemDetail(error: unknown, fallback: string): string {
  if (error instanceof HttpErrorResponse && typeof error.error?.detail === 'string') {
    return error.error.detail;
  }
  return fallback;
}

@Injectable({ providedIn: 'root' })
export class AuthApi {
  private readonly http = inject(HttpClient);

  register(request: RegisterRequest): Observable<RegisterResponse> {
    return this.http.post<RegisterResponse>(`${API_BASE_URL}/api/v1/auth/register`, request);
  }

  login(request: LoginRequest): Observable<TokenResponse> {
    return this.http.post<TokenResponse>(`${API_BASE_URL}/api/v1/auth/login`, request);
  }

  refresh(refreshToken: string): Observable<TokenResponse> {
    return this.http.post<TokenResponse>(`${API_BASE_URL}/api/v1/auth/refresh`, { refreshToken });
  }

  forgotPassword(email: string): Observable<object> {
    return this.http.post(`${API_BASE_URL}/api/v1/auth/forgot-password`, { email });
  }
}
