import { Service, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Session } from '../session/session';
import { refreshableHttpResource } from '../shared/refreshable-http-resource';
import { WatchEvents } from '../watches/watch-events';
import type { LookoutSettings } from './settings.model';

/**
 * The install-wide settings at /api/settings, shared by everything that shows them. They're read afresh when the time
 * zone changes, such as in another tab, so schedule inputs already open name the new one, and whenever the events
 * connection opens, in case that was missed. The Settings page reads them afresh too, as retention isn't pushed.
 */
@Service()
export class InstallSettings {
  private readonly session = inject(Session);
  // Loaded only while logged in, so it is fetched again for the next login.
  readonly resource = refreshableHttpResource<LookoutSettings>(() => (this.session.userName() ? { url: '/api/settings' } : undefined));

  constructor() {
    inject(WatchEvents).events.pipe(takeUntilDestroyed()).subscribe((event) => {
      if (event.type === 'timeZoneChanged' || event.type === 'connected') {this.resource.refresh();}
    });
  }
}
