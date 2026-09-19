import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'login' },
  { path: 'login', loadComponent: () => import('./features/login/login.page').then((m) => m.LoginPage) },
  { path: 'register', loadComponent: () => import('./features/register/register.page').then((m) => m.RegisterPage) },
  {
    path: 'profile',
    canActivate: [authGuard],
    loadComponent: () => import('./features/profile/profile.page').then((m) => m.ProfilePage),
  },
  { path: '**', redirectTo: 'login' },
];
