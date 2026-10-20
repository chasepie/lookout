import { HttpClient, httpResource } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { type ScheduleForm, scheduleFields } from './schedule';
import { Session } from './session';
import type { Run, Watch } from './watch.model';

@Service()
export class Watches {
  private readonly http = inject(HttpClient);
  private readonly session = inject(Session);
  // Loaded only while logged in, so it is fetched again for the next login.
  readonly list = httpResource<Watch[]>(() => (this.session.userName() ? '/api/watches' : undefined));

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

  runNow(id: string) {
    return firstValueFrom(this.http.post<Run>(`/api/watches/${id}/runs`, {}));
  }
}
