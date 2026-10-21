import { HttpClient, httpResource } from '@angular/common/http';
import { Service, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { refresh } from './refresh';
import { type ScheduleForm, scheduleFields } from './schedule';
import { Session } from './session';
import type { Run, Watch, WatchSettings } from './watch.model';

@Service()
export class Watches {
  private readonly http = inject(HttpClient);
  private readonly session = inject(Session);
  // Each read's request is a new object, so bumping this restarts a read that reload() won't.
  private readonly listReads = signal(0);
  // Loaded only while logged in, so it is fetched again for the next login.
  readonly list = httpResource<Watch[]>(() => {
    this.listReads();
    return this.session.userName() ? { url: '/api/watches' } : undefined;
  });

  /** Reads the list afresh, keeping the Watches on screen until the new list arrives. */
  refreshList() {
    refresh(this.list, this.listReads);
  }

  create(name: string, notificationUrl: string, schedule: ScheduleForm, script: File) {
    const data = new FormData();
    data.set('name', name);
    data.set('notificationUrl', notificationUrl);
    for (const [field, value] of Object.entries(scheduleFields(schedule))) {
      data.set(field, String(value));
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

  history(id: string, before: number, limit: number) {
    return firstValueFrom(this.http.get<Run[]>(`/api/watches/${id}/runs`, { params: { before, limit } }));
  }
}
