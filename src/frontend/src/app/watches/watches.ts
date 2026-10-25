import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { firstValueFrom, fromEvent, takeUntil } from 'rxjs';
import { refreshableHttpResource } from '../shared/refreshable-http-resource';
import { type ScheduleForm, scheduleFields } from '../schedules/schedule';
import { Session } from '../session/session';
import type { Run, ScriptVersion, ScriptVersionSource, TestRun, Watch, WatchSettings } from './watch.model';

@Service()
export class Watches {
  private readonly http = inject(HttpClient);
  private readonly session = inject(Session);
  // Loaded only while logged in, so it is fetched again for the next login. Refreshing keeps the Watches on screen
  // until the new list arrives.
  readonly list = refreshableHttpResource<Watch[]>(() => (this.session.userName() ? { url: '/api/watches' } : undefined));

  create(name: string, notificationUrl: string, usesVpn: boolean, schedule: ScheduleForm, script: File,
    notificationChannelIds: Iterable<string>) {
    const data = new FormData();
    data.set('name', name);
    data.set('notificationUrl', notificationUrl);
    data.set('usesVpn', String(usesVpn));
    for (const [field, value] of Object.entries(scheduleFields(schedule))) {
      data.set(field, String(value));
    }
    for (const id of notificationChannelIds) {
      data.append('notificationChannelIds', id);
    }
    data.set('script', script);
    return firstValueFrom(this.http.post<Watch>('/api/watches', data));
  }

  changeSchedule(id: string, schedule: ScheduleForm) {
    return firstValueFrom(this.http.put<Watch>(`/api/watches/${id}/schedule`, scheduleFields(schedule)));
  }

  edit(id: string, settings: WatchSettings) {
    return firstValueFrom(this.http.put<void>(`/api/watches/${id}`, settings));
  }

  changeNotificationChannels(id: string, notificationChannelIds: string[]) {
    return firstValueFrom(this.http.put<void>(`/api/watches/${id}/notification-channels`, { notificationChannelIds }));
  }

  pause(id: string) {
    return firstValueFrom(this.http.post<void>(`/api/watches/${id}/pause`, {}));
  }

  resume(id: string) {
    return firstValueFrom(this.http.post<void>(`/api/watches/${id}/resume`, {}));
  }

  async delete(id: string) {
    await firstValueFrom(this.http.delete<void>(`/api/watches/${id}`));
    // Removed at once, so the list never shows the deleted Watch while it reloads.
    if (this.list.hasValue()) {
      this.list.update((watches) => watches?.filter((watch) => watch.id !== id));
    }
  }

  runNow(id: string) {
    return firstValueFrom(this.http.post<Run>(`/api/watches/${id}/runs`, {}));
  }

  /** Runs a Test run, which `stop` cancels: the backend then stops the Script and frees its run slot. */
  testRun(id: string, script: File, stop: AbortSignal) {
    const data = new FormData();
    data.set('script', script);
    return firstValueFrom(this.http.post<TestRun>(`/api/watches/${id}/test-runs`, data)
      .pipe(takeUntil(fromEvent(stop, 'abort'))));
  }

  uploadScript(id: string, script: File, keepBaseline: boolean) {
    const data = new FormData();
    data.set('script', script);
    data.set('keepBaseline', String(keepBaseline));
    return firstValueFrom(this.http.post<ScriptVersion>(`/api/watches/${id}/script-versions`, data));
  }

  scriptVersion(id: string, versionId: string) {
    return firstValueFrom(this.http.get<ScriptVersionSource>(`/api/watches/${id}/script-versions/${versionId}`));
  }

  history(id: string, before: number, limit: number) {
    return firstValueFrom(this.http.get<Run[]>(`/api/watches/${id}/runs`, { params: { before, limit } }));
  }
}
