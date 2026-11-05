import { Routes } from '@angular/router';
import { ownerGuard, signedOutGuard } from './auth/auth-guards';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [signedOutGuard],
    loadComponent: () => import('./auth/login-page').then((m) => m.LoginPage),
  },
  {
    // Every other page needs the Owner to be signed in
    path: '',
    canActivateChild: [ownerGuard],
    children: [
      { path: '', loadComponent: () => import('./watches/watch-list').then((m) => m.WatchList) },
      { path: 'watches/new', loadComponent: () => import('./watches/watch-form').then((m) => m.WatchForm) },
      { path: 'watches/:id', loadComponent: () => import('./watches/watch-detail').then((m) => m.WatchDetail) },
      { path: 'watches/:id/edit', loadComponent: () => import('./watches/watch-form').then((m) => m.WatchForm) },
      {
        path: 'watches/:id/runs/:runId',
        loadComponent: () => import('./watches/run-detail').then((m) => m.RunDetail),
      },
      { path: 'vpn', loadComponent: () => import('./vpn/vpn-page').then((m) => m.VpnPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
