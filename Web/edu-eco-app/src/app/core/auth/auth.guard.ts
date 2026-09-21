import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { TokenStore } from './token-store';

export const authGuard: CanActivateFn = () => {
  const tokenStore = inject(TokenStore);
  if (tokenStore.isAuthenticated()) {
    return true;
  }

  return inject(Router).createUrlTree(['/login']);
};
