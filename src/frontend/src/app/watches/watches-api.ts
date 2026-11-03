import { HttpClient, httpResource } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CreateWatchDto, Watch, WatchRun } from './watch.model';

const baseUrl = '/api/Watches';

/**
 * Client for the Backend's `/Watches` endpoints.
 * The `*Resource` methods must be called in an injection context (e.g. a component field initializer).
 */
@Service()
export class WatchesApi {
  private readonly http = inject(HttpClient);

  watchesResource() {
    return httpResource<Watch[]>(() => baseUrl);
  }

  /** Makes no request while `id` returns `undefined`. */
  watchResource(id: () => string | undefined) {
    return httpResource<Watch>(() => {
      const watchId = id();
      return watchId === undefined ? undefined : `${baseUrl}/${watchId}`;
    });
  }

  runsResource(watchId: () => string) {
    return httpResource<WatchRun[]>(() => `${baseUrl}/${watchId()}/runs`);
  }

  /** `runId` may be `"latest"`. */
  runResource(watchId: () => string, runId: () => string) {
    return httpResource<WatchRun>(() => `${baseUrl}/${watchId()}/runs/${runId()}`);
  }

  create(watch: CreateWatchDto) {
    return firstValueFrom(this.http.post<Watch>(baseUrl, watch));
  }

  update(id: string, watch: CreateWatchDto) {
    return firstValueFrom(this.http.put<Watch>(`${baseUrl}/${id}`, watch));
  }

  delete(id: string) {
    return firstValueFrom(this.http.delete<Watch>(`${baseUrl}/${id}`));
  }

  uploadScript(id: string, script: File) {
    const body = new FormData();
    body.append('script', script);
    return firstValueFrom(this.http.post(`${baseUrl}/${id}/script`, body, { responseType: 'text' }));
  }

  run(id: string) {
    return firstValueFrom(this.http.post<WatchRun>(`${baseUrl}/${id}/run`, null));
  }
}

/** Turns an HTTP error into something readable. */
export function describeError(error: unknown): string {
  if (error && typeof error === 'object' && 'error' in error) {
    const body = error.error;
    if (typeof body === 'string' && body) {
      return body;
    }
    if (body && typeof body === 'object') {
      return JSON.stringify(body, null, 2);
    }
  }
  if (error instanceof Error) {
    return error.message;
  }
  return String(error);
}
