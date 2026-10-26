import { Service, inject } from '@angular/core';
import { Session } from '../session/session';
import { refreshableHttpResource } from '../shared/refreshable-http-resource';
import type { LookoutSettings } from './settings.model';

/**
 * The install-wide settings at /api/settings, read once and shared by everything that shows them. The Settings page
 * reads them afresh, as they may have changed in another tab.
 */
@Service()
export class InstallSettings {
  private readonly session = inject(Session);
  // Loaded only while logged in, so it is fetched again for the next login.
  readonly resource = refreshableHttpResource<LookoutSettings>(() => (this.session.userName() ? { url: '/api/settings' } : undefined));
}
