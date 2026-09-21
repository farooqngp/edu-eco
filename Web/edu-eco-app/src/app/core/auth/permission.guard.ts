import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionStore } from './session-store';
import { TokenStore } from './token-store';

/**
 * Gates a route on a server-resolved permission (see SessionStore). The API enforces this too — this only keeps the
 * UI honest. Signed out goes to /login; signed in but not permitted goes to /profile rather than a dead end.
 */
export function permissionGuard(permission: string): CanActivateFn {
  return () => {
    const router = inject(Router);

    if (!inject(TokenStore).isAuthenticated()) {
      return router.createUrlTree(['/login']);
    }

    return inject(SessionStore).has(permission) ? true : router.createUrlTree(['/profile']);
  };
}
