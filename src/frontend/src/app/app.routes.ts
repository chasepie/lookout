import type { Routes } from '@angular/router';
import { Settings } from './settings/settings';
import { WatchDetail } from './watches/watch-detail/watch-detail';
import { ScriptUpload } from './watches/script-upload/script-upload';
import { WatchList } from './watches/watch-list/watch-list';

export const routes: Routes = [
  { path: '', component: WatchList, title: 'Watches · Lookout' },
  { path: 'watches/:id', component: WatchDetail, title: 'Watch · Lookout' },
  { path: 'watches/:id/script', component: ScriptUpload, title: 'Upload Script · Lookout' },
  { path: 'settings', component: Settings, title: 'Settings · Lookout' },
  { path: '**', redirectTo: '' },
];
