import { HttpClient, httpResource } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CreateWatchDto, DeleteRunsResult, Watch, WatchRun, WatchRunSummary, WatchSummary } from './watch.model';

const baseUrl = '/api/Watches';

/** How many runs a page of a watch's log holds. */
export const runPageSize = 50;

/** How many of each watch's latest runs the list shows. Mirrors `WatchSummary.RecentRunCount` in the Backend. */
export const recentRunCount = 24;

/**
 * Client for the Backend's `/Watches` endpoints.
 * The `*Resource` methods must be called in an injection context (e.g. a component field initializer).
 */
@Service()
export class WatchesApi {
  private readonly http = inject(HttpClient);

  watchesResource() {
    return httpResource<WatchSummary[]>(() => baseUrl);
  }

  /** Makes no request while `id` returns `undefined`. */
  watchResource(id: () => string | undefined) {
    return httpResource<WatchSummary>(() => {
      const watchId = id();
      return watchId === undefined ? undefined : `${baseUrl}/${watchId}`;
    });
  }

  /** One watch as the list shows it, with its latest runs. */
  watchSummary(id: number) {
    return firstValueFrom(
      this.http.get<WatchSummary>(`${baseUrl}/${id}`, { params: { recentRuns: recentRunCount } }),
    );
  }

  /** The newest page of a watch's runs, newest first. */
  newestRunsResource(watchId: () => string) {
    return httpResource<WatchRunSummary[]>(() => ({
      url: `${baseUrl}/${watchId()}/runs`,
      params: { limit: runPageSize },
    }));
  }

  /** `runId` may be `"latest"`. */
  runResource(watchId: () => string, runId: () => string) {
    return httpResource<WatchRun>(() => `${baseUrl}/${watchId()}/runs/${runId()}`);
  }

  /** A page of the runs older than the run `before`, newest first. */
  olderRuns(watchId: string, before: number) {
    return firstValueFrom(
      this.http.get<WatchRunSummary[]>(`${baseUrl}/${watchId}/runs`, { params: { limit: runPageSize, before } }),
    );
  }

  /** Creates a watch with its script, sent together as multipart form data. */
  create(watch: CreateWatchDto, script: File) {
    const body = new FormData();
    body.append('name', watch.name);
    body.append('scheduleEnabled', String(watch.scheduleEnabled));
    if (watch.scheduleKind !== null) {
      body.append('scheduleKind', String(watch.scheduleKind));
    }
    if (watch.scheduleInterval !== null) {
      body.append('scheduleInterval', watch.scheduleInterval);
    }
    body.append('useVpn', String(watch.useVpn));
    body.append('script', script);
    return firstValueFrom(this.http.post<Watch>(baseUrl, body));
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

  /** Fails with 409 if the run is in progress. */
  deleteRun(watchId: string, runId: number) {
    return firstValueFrom(this.http.delete<WatchRun>(`${baseUrl}/${watchId}/runs/${runId}`));
  }

  /** Keeps any run that is in progress. */
  deleteRuns(watchId: string) {
    return firstValueFrom(this.http.delete<DeleteRunsResult>(`${baseUrl}/${watchId}/runs`));
  }

  run(id: string) {
    return firstValueFrom(this.http.post<WatchRun>(`${baseUrl}/${id}/runs`, null));
  }
}
