import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { LiveUpdates } from '../live/live-updates';
import { settleMs } from '../live/reload-on';
import { RunLog, injectRunLog } from './run-log';
import { WatchRunStatus, WatchRunSummary, WatchRunUpdate } from './watch.model';
import { WatchesApi, runPageSize } from './watches-api';

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

describe('injectRunLog', () => {
  let runs: ReturnType<typeof vi.fn>;
  let updates: {
    runUpdated: Subject<WatchRunUpdate>;
    runsDeleted: Subject<{ watchId: number; runIds: number[] }>;
    reconnected: Subject<void>;
  };
  let log: RunLog;

  beforeEach(() => {
    runs = vi.fn();
    updates = { runUpdated: new Subject(), runsDeleted: new Subject(), reconnected: new Subject() };
    TestBed.configureTestingModule({
      providers: [
        { provide: WatchesApi, useValue: { runs } },
        { provide: LiveUpdates, useValue: updates },
      ],
    });
  });

  async function start(firstPage: WatchRunSummary[]) {
    runs.mockResolvedValueOnce(firstPage);
    log = TestBed.runInInjectionContext(() => injectRunLog(() => '1'));
    TestBed.tick();
    await vi.waitFor(() => expect(log.loaded()).toBe(true));
  }

  const ids = () => log.entries().map((r) => r.id);

  it('loads the newest page, and offers older runs when the page is full', async () => {
    await start(page(runPageSize, 1));

    expect(runs).toHaveBeenCalledWith('1');
    expect(log.entries()).toHaveLength(runPageSize);
    expect(log.hasOlder()).toBe(true);
  });

  it('pages back from the oldest run loaded', async () => {
    await start(page(60, 11));
    runs.mockResolvedValueOnce(page(10, 1));

    await log.loadOlder();

    expect(runs).toHaveBeenLastCalledWith('1', 11);
    expect(ids().at(-1)).toBe(1);
    expect(log.hasOlder()).toBe(false);
  });

  it('adds a new run at the top, keeping older pages, and marks it as arrived', async () => {
    await start(page(60, 11));
    runs.mockResolvedValueOnce(page(10, 1));
    await log.loadOlder();

    runs.mockResolvedValueOnce(page(61, 12));
    updates.runUpdated.next({ ...run(61), status: WatchRunStatus.Created });
    await vi.waitFor(() => expect(ids()[0]).toBe(61));

    expect(ids()).toHaveLength(61);
    expect(ids().at(-1)).toBe(1);
    expect(log.arrived().has(61)).toBe(true);
    expect(log.arrived().has(60)).toBe(false);
  });

  it('drops deleted runs straight away', async () => {
    await start(page(3, 1));
    runs.mockResolvedValueOnce([run(3), run(1)]);

    updates.runsDeleted.next({ watchId: 1, runIds: [2] });

    expect(ids()).toEqual([3, 1]);
  });

  it("ignores another watch's runs", async () => {
    await start(page(3, 1));

    updates.runUpdated.next({ ...run(9), watchId: 2 });
    await new Promise((resolve) => setTimeout(resolve, settleMs + 50));

    expect(runs).toHaveBeenCalledTimes(1);
  });

  it('fetches once for a burst of updates', async () => {
    await start(page(3, 1));
    runs.mockResolvedValue(page(4, 1));

    updates.runUpdated.next({ ...run(4), status: WatchRunStatus.Created });
    updates.runUpdated.next({ ...run(4), status: WatchRunStatus.Running });
    updates.runUpdated.next(run(4));
    await vi.waitFor(() => expect(ids()[0]).toBe(4));

    expect(runs).toHaveBeenCalledTimes(2);
  });

  it('starts properly once a failed first load is tried again', async () => {
    runs.mockRejectedValueOnce(new Error('offline'));
    log = TestBed.runInInjectionContext(() => injectRunLog(() => '1'));
    TestBed.tick();
    await vi.waitFor(() => expect(log.error()).toBeTruthy());
    expect(log.isEmpty()).toBe(false);
    runs.mockResolvedValueOnce(page(60, 11));

    await log.reload();

    expect(log.hasOlder()).toBe(true);
    expect(log.arrived().size).toBe(0);
  });

  it('drops an older page asked for before the log started over', async () => {
    await start(page(60, 11));
    let answerOlder: (value: WatchRunSummary[]) => void = () => undefined;
    runs.mockReturnValueOnce(new Promise((resolve) => (answerOlder = resolve)));
    const older = log.loadOlder();
    runs.mockResolvedValueOnce(page(70, 21));

    updates.reconnected.next();
    await vi.waitFor(() => expect(ids()[0]).toBe(70));
    answerOlder(page(10, 1));
    await older;

    expect(ids().at(-1)).toBe(21);
    expect(log.loadingOlder()).toBe(false);
  });

  it("doesn't mark an old run as arrived when a deletion brings it into the page", async () => {
    await start(page(60, 11));
    runs.mockResolvedValueOnce(page(59, 10));

    updates.runsDeleted.next({ watchId: 1, runIds: [60] });
    await vi.waitFor(() => expect(ids().at(-1)).toBe(10));

    expect(log.arrived().size).toBe(0);
  });

  it('keeps the newest answer when responses arrive out of order', async () => {
    await start(page(3, 1));
    let answerSlow: (value: WatchRunSummary[]) => void = () => undefined;
    runs.mockReturnValueOnce(new Promise((resolve) => (answerSlow = resolve)));
    runs.mockResolvedValueOnce(page(5, 1));

    updates.runUpdated.next(run(4));
    await vi.waitFor(() => expect(runs).toHaveBeenCalledTimes(2));
    updates.runUpdated.next(run(5));
    await vi.waitFor(() => expect(ids()[0]).toBe(5));
    answerSlow(page(4, 1));
    await Promise.resolve();

    expect(ids()[0]).toBe(5);
  });
});
