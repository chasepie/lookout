import { HttpClient } from '@angular/common/http';
import { Service, computed, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Session } from '../session/session';
import { refreshableHttpResource } from '../shared/refreshable-http-resource';
import type { LookoutSettings } from './settings.model';

/** The install-wide time zone that daily and cron schedules use, which the user changes in Settings. */
@Service()
export class InstallTimeZone {
  private readonly http = inject(HttpClient);
  private readonly session = inject(Session);
  // Loaded only while logged in, so it is fetched again for the next login.
  readonly settings = refreshableHttpResource<LookoutSettings>(() => (this.session.userName() ? { url: '/api/settings' } : undefined));
  /** The time zone, once loaded. */
  readonly current = computed(() => (this.settings.hasValue() ? this.settings.value().timeZone : undefined));

  /** Saves the time zone, which reschedules daily and cron Watches in it, and returns the settings as saved. */
  async change(timeZone: string) {
    const saved = await firstValueFrom(this.http.put<LookoutSettings>('/api/settings/time-zone', { timeZone }));
    this.settings.set(saved);
    return saved;
  }
}
