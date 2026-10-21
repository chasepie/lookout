import type { Routes } from '@angular/router';
import { WatchDetail } from './watches/watch-detail/watch-detail';
import { WatchList } from './watches/watch-list/watch-list';

export const routes: Routes = [
  { path: '', component: WatchList, title: 'Watches · Lookout' },
  { path: 'watches/:id', component: WatchDetail, title: 'Watch · Lookout' },
  { path: '**', redirectTo: '' },
];
