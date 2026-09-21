import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { permissionGuard } from './core/auth/permission.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'login' },
  { path: 'login', loadComponent: () => import('./features/login/login.page').then((m) => m.LoginPage) },
  { path: 'register', loadComponent: () => import('./features/register/register.page').then((m) => m.RegisterPage) },
  {
    path: 'forgot-password',
    loadComponent: () =>
      import('./features/forgot-password/forgot-password.page').then((m) => m.ForgotPasswordPage),
  },
  {
    path: 'profile',
    canActivate: [authGuard],
    loadComponent: () => import('./features/profile/profile.page').then((m) => m.ProfilePage),
  },
  {
    path: 'admin',
    canActivate: [permissionGuard('users.manage')],
    loadComponent: () => import('./features/admin/dashboard.page').then((m) => m.AdminDashboardPage),
  },
  {
    path: 'admin/tenants',
    canActivate: [permissionGuard('tenants.manage')],
    loadComponent: () => import('./features/admin/tenant-provisioning.page').then((m) => m.TenantProvisioningPage),
  },
  { path: '**', redirectTo: 'login' },
];
