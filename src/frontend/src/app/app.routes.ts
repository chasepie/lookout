import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', loadComponent: () => import('./watches/watch-list').then((m) => m.WatchList) },
  { path: 'watches/new', loadComponent: () => import('./watches/watch-form').then((m) => m.WatchForm) },
  { path: 'watches/:id', loadComponent: () => import('./watches/watch-detail').then((m) => m.WatchDetail) },
  { path: 'watches/:id/edit', loadComponent: () => import('./watches/watch-form').then((m) => m.WatchForm) },
  {
    path: 'watches/:id/runs/:runId',
    loadComponent: () => import('./watches/run-detail').then((m) => m.RunDetail),
  },
  { path: '**', redirectTo: '' },
];
