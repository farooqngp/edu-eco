import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthApi } from './auth-api';
import { TokenStore } from './token-store';

/**
 * Attaches the access token to every request, and on a 401 tries exactly one silent refresh
 * before giving up (never for the refresh call itself, to avoid a retry loop on a dead refresh token).
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const tokenStore = inject(TokenStore);
  const authApi = inject(AuthApi);

  const withToken = (request: HttpRequest<unknown>, token: string | null) =>
    token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;

  const isRefreshCall = req.url.includes('/api/v1/auth/refresh');

  return next(withToken(req, tokenStore.accessToken)).pipe(
    catchError((error: unknown) => {
      const refreshToken = tokenStore.refreshToken;
      if (isRefreshCall || !(error instanceof HttpErrorResponse) || error.status !== 401 || !refreshToken) {
        return throwError(() => error);
      }

      return authApi.refresh(refreshToken).pipe(
        switchMap((tokens) => {
          tokenStore.set({ accessToken: tokens.accessToken, refreshToken: tokens.refreshToken });
          return next(withToken(req, tokens.accessToken));
        }),
        catchError((refreshError: unknown) => {
          tokenStore.clear();
          return throwError(() => refreshError);
        }),
      );
    }),
  );
};
