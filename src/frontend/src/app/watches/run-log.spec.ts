import { ApplicationRef, signal } from '@angular/core';
import { HttpRequest, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { LiveUpdates } from '../live/live-updates';
import { settleMs } from '../live/reload-on';
import { RunLog, injectRunLog } from './run-log';
import { WatchRunStatus, WatchRunSummary, WatchRunUpdate } from './watch.model';
import { runPageSize } from './watches-api';

function run(id: number, content = `run ${id}`): WatchRunSummary {
  return {
    id,
    watchId: 1,
    createdTime: '2026-11-07T12:00:00Z',
    startTime: null,
    endTime: null,
    status: WatchRunStatus.Done,
    content,
    error: null,
    hasScreenshot: false,
  };
}

/** Runs `from` down to `to`, newest first, as a page from the API. */
function page(from: number, to: number) {
  const runs: WatchRunSummary[] = [];
  for (let id = from; id >= to; id--) {
    runs.push(run(id));
  }
  return runs;
}

type RequestMatch = (request: HttpRequest<unknown>) => boolean;

const newestOf =
  (watchId: string): RequestMatch =>
  (request) =>
    request.url === `/api/Watches/${watchId}/runs` && !request.params.has('before');
const olderThan =
  (before: number): RequestMatch =>
  (request) =>
    request.url === '/api/Watches/1/runs' && request.params.get('before') === String(before);

describe('injectRunLog', () => {
  let backend: HttpTestingController;
  let updates: {
    runUpdated: Subject<WatchRunUpdate>;
    runsDeleted: Subject<{ watchId: number; runIds: number[] }>;
    reconnected: Subject<void>;
  };
  const watchId = signal('1');
  let log: RunLog;

  beforeEach(() => {
    updates = { runUpdated: new Subject(), runsDeleted: new Subject(), reconnected: new Subject() };
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: LiveUpdates, useValue: updates }],
    });
    backend = TestBed.inject(HttpTestingController);
    watchId.set('1');
    log = TestBed.runInInjectionContext(() => injectRunLog(watchId));
  });

  afterEach(() => backend.verify());

  const stable = () => TestBed.inject(ApplicationRef).whenStable();

  /** Waits for the request the log is about to send, which may be after a burst of updates settles. */
  function nextRequest(matches: RequestMatch): Promise<TestRequest> {
    return vi.waitFor(() => {
      TestBed.tick();
      return backend.expectOne(matches);
    });
  }

  async function answer(matches: RequestMatch, runs: WatchRunSummary[]) {
    (await nextRequest(matches)).flush(runs);
    await stable();
  }

  const ids = () => log.entries().map((r) => r.id);

  it('loads the newest page, and offers older runs when the page is full', async () => {
    expect(log.loaded()).toBe(false);

    await answer(newestOf('1'), page(runPageSize, 1));

    expect(log.loaded()).toBe(true);
    expect(log.entries()).toHaveLength(runPageSize);
    expect(log.hasOlder()).toBe(true);
  });

  it('pages back from the oldest run loaded', async () => {
    await answer(newestOf('1'), page(60, 11));

    const older = log.loadOlder();
    (await nextRequest(olderThan(11))).flush(page(10, 1));
    await older;

    expect(ids().at(-1)).toBe(1);
    expect(log.hasOlder()).toBe(false);
  });

  it('adds a new run at the top, keeping older pages, and marks it as arrived', async () => {
    await answer(newestOf('1'), page(60, 11));
    const older = log.loadOlder();
    (await nextRequest(olderThan(11))).flush(page(10, 1));
    await older;

    updates.runUpdated.next({ ...run(61), status: WatchRunStatus.Created });
    await answer(newestOf('1'), page(61, 12));

    expect(ids()).toHaveLength(61);
    expect(ids().at(-1)).toBe(1);
    expect(log.arrived().has(61)).toBe(true);
    expect(log.arrived().has(60)).toBe(false);
  });

  it('fetches once for a burst of updates', async () => {
    await answer(newestOf('1'), page(3, 1));

    updates.runUpdated.next({ ...run(4), status: WatchRunStatus.Created });
    updates.runUpdated.next({ ...run(4), status: WatchRunStatus.Running });
    updates.runUpdated.next(run(4));

    // expectOne fails if the burst sent more than one request
    await answer(newestOf('1'), page(4, 1));
    expect(ids()[0]).toBe(4);
  });

  it("ignores another watch's runs", async () => {
    await answer(newestOf('1'), page(3, 1));

    updates.runUpdated.next({ ...run(9), watchId: 2 });
    await new Promise((resolve) => setTimeout(resolve, settleMs + 50));
    TestBed.tick();

    backend.expectNone(newestOf('1'));
  });

  it('drops deleted runs straight away', async () => {
    await answer(newestOf('1'), page(3, 1));

    updates.runsDeleted.next({ watchId: 1, runIds: [2] });

    expect(ids()).toEqual([3, 1]);
    await answer(newestOf('1'), [run(3), run(1)]);
  });

  it('empties when every run is deleted', async () => {
    await answer(newestOf('1'), page(3, 1));

    updates.runsDeleted.next({ watchId: 1, runIds: [1, 2, 3] });
    await answer(newestOf('1'), []);

    expect(log.isEmpty()).toBe(true);
  });

  it('fetches again when an update arrives while a request is on its way', async () => {
    await answer(newestOf('1'), page(3, 1));
    updates.runUpdated.next({ ...run(4), status: WatchRunStatus.Running });
    const slow = await nextRequest(newestOf('1'));

    // The run finishes while the request that will say it's running is still out
    updates.runUpdated.next(run(4));
    await new Promise((resolve) => setTimeout(resolve, settleMs + 50));
    // Answering it starts the follow-up straight away
    slow.flush([{ ...run(4), status: WatchRunStatus.Running }, ...page(3, 1)]);
    await answer(newestOf('1'), page(4, 1));

    expect(log.entries()[0].status).toBe(WatchRunStatus.Done);
  });

  it('starts properly once a failed first load is tried again', async () => {
    (await nextRequest(newestOf('1'))).flush('', { status: 502, statusText: 'Bad Gateway' });
    await stable();
    expect(log.error()).toBeTruthy();
    expect(log.isEmpty()).toBe(false);

    log.reload();
    await answer(newestOf('1'), page(60, 11));

    expect(log.error()).toBeUndefined();
    expect(log.hasOlder()).toBe(true);
    expect(log.arrived().size).toBe(0);
  });

  it('drops an older page asked for before the log started over', async () => {
    await answer(newestOf('1'), page(60, 11));
    const older = log.loadOlder();
    const olderRequest = await nextRequest(olderThan(11));

    updates.reconnected.next();
    await answer(newestOf('1'), page(70, 21));
    olderRequest.flush(page(10, 1));
    await older;

    expect(ids().at(-1)).toBe(21);
    expect(log.loadingOlder()).toBe(false);
  });

  it('starts a new log for another watch', async () => {
    await answer(newestOf('1'), page(3, 1));

    watchId.set('2');
    await answer(newestOf('2'), [{ ...run(9), watchId: 2 }]);

    expect(ids()).toEqual([9]);
  });

  it('starts again from the newest page when more runs than a page arrived at once, rather than leave a gap', async () => {
    await answer(newestOf('1'), page(60, 11));
    const older = log.loadOlder();
    (await nextRequest(olderThan(11))).flush(page(10, 1));
    await older;

    updates.runUpdated.next(run(130));
    await answer(newestOf('1'), page(130, 81));

    expect(ids()[0]).toBe(130);
    expect(ids().at(-1)).toBe(81);
    expect(log.hasOlder()).toBe(true);
  });

  it("doesn't mark an old run as arrived when a deletion brings it into the page", async () => {
    await answer(newestOf('1'), page(60, 11));

    updates.runsDeleted.next({ watchId: 1, runIds: [60] });
    await answer(newestOf('1'), page(59, 10));

    expect(ids().at(-1)).toBe(10);
    expect(log.arrived().size).toBe(0);
  });
});
