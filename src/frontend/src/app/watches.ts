import { HttpClient, httpResource } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Session } from './session';
import type { Run, Watch } from './watch.model';

@Service()
export class Watches {
  private readonly http = inject(HttpClient);
  private readonly session = inject(Session);
  // Loaded only while logged in, so it is fetched again for the next login.
  readonly list = httpResource<Watch[]>(() => (this.session.userName() ? '/api/watches' : undefined));

  create(name: string, notificationUrl: string, script: File) {
    const data = new FormData();
    data.set('name', name);
    data.set('notificationUrl', notificationUrl);
    data.set('script', script);
    return firstValueFrom(this.http.post<Watch>('/api/watches', data));
  }

  runNow(id: string) {
    return firstValueFrom(this.http.post<Run>(`/api/watches/${id}/runs`, {}));
  }
}
