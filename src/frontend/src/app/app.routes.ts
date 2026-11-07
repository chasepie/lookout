import { Routes } from '@angular/router';
import { ownerGuard, signedOutGuard } from './auth/auth-guards';

export const routes: Routes = [
  {
    path: 'login',
    title: 'Sign in – Lookout',
    canActivate: [signedOutGuard],
    loadComponent: () => import('./auth/login-page').then((m) => m.LoginPage),
  },
  {
    // Every other page needs the Owner to be signed in
    path: '',
    canActivateChild: [ownerGuard],
    children: [
      {
        path: '',
        title: 'Watches – Lookout',
        loadComponent: () => import('./watches/watch-list').then((m) => m.WatchList),
      },
      {
        path: 'watches/new',
        title: 'New watch – Lookout',
        loadComponent: () => import('./watches/watch-form').then((m) => m.WatchForm),
      },
      {
        path: 'watches/:id/edit',
        title: 'Edit watch – Lookout',
        loadComponent: () => import('./watches/watch-form').then((m) => m.WatchForm),
      },
      {
        path: 'watches/:id',
        // No title here: the router would put it back each time a run opens or closes; the page titles itself
        loadComponent: () => import('./watches/watch-detail').then((m) => m.WatchDetail),
        // A run opens beside the watch's log, so its route is the watch page's child
        children: [
          {
            path: 'runs/:runId',
            loadComponent: () => import('./watches/run-panel').then((m) => m.RunPanel),
          },
        ],
      },
      { path: 'vpn', title: 'VPN – Lookout', loadComponent: () => import('./vpn/vpn-page').then((m) => m.VpnPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
