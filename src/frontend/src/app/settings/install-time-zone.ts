import { HttpClient } from '@angular/common/http';
import { Service, computed, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { InstallSettings } from './install-settings';
import type { LookoutSettings } from './settings.model';

/** The install-wide time zone that daily and cron Schedules use, which the user changes in Settings. */
@Service()
export class InstallTimeZone {
  private readonly http = inject(HttpClient);
  readonly settings = inject(InstallSettings).resource;
  /** The saved time zone, once loaded. */
  readonly current = computed(() => (this.settings.hasValue() ? this.settings.value().timeZone : undefined));
  /** The time zone daily and cron Schedules run in, once loaded: the saved one, unless this host doesn't know it. */
  readonly effective = computed(() => (this.settings.hasValue() ? this.settings.value().effectiveTimeZone : undefined));
  /** Whether Lookout's host knows the saved time zone, once loaded. Its ID can differ in case from the effective one's. */
  readonly known = computed(() => (this.settings.hasValue() ? this.settings.value().timeZoneKnown : undefined));

  /** Saves the time zone, which reschedules daily and cron Watches in it, and returns the settings as saved. */
  async change(timeZone: string) {
    const saved = await firstValueFrom(this.http.put<LookoutSettings>('/api/settings/time-zone', { timeZone }));
    this.settings.set(saved);
    return saved;
  }
}
