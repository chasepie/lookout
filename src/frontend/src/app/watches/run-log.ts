import { computed, inject, linkedSignal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { LiveUpdates } from '../live/live-updates';
import { reloadOn, reloader } from '../live/reload-on';
import { WatchRunSummary } from './watch.model';
import { WatchesApi, runPageSize } from './watches-api';

interface LogState {
  watchId: string;
  /** Counts the times the log has started over, so a page asked for before then is dropped. */
  generation: number;
  /** Whether the newest page has arrived yet. */
  started: boolean;
  /** Whether the next newest page replaces the log rather than merging into it, after missed updates. */
  replaceNext: boolean;
  /** Newest first, as far back as the Owner has paged. */
  entries: WatchRunSummary[];
  hasOlder: boolean;
  /** Runs that arrived while the log was open, which are highlighted as they appear. */
  arrived: ReadonlySet<number>;
}

function emptyLog(watchId: string, generation: number): LogState {
  return { watchId, generation, started: false, replaceNext: false, entries: [], hasOlder: false, arrived: new Set() };
}

/** The log once the newest page of runs has arrived: merged in on top, or in place of it after starting over. */
function withNewest(log: LogState, page: WatchRunSummary[]): LogState {
  const full = page.length === runPageSize;
  if (!log.started || log.replaceNext) {
    return { ...log, started: true, replaceNext: false, entries: page, hasOlder: full, arrived: new Set() };
  }
  // Only runs newer than any shown have arrived; older ones only move up into the page as others are deleted
  const newestShown = log.entries[0]?.id ?? 0;
  const added = page.filter((run) => run.id > newestShown).map((run) => run.id);
  // A page that isn't full holds every run there is, so nothing older than it is left to keep
  const oldestInPage = page.at(-1)?.id ?? 0;
  const older = full ? log.entries.filter((run) => run.id < oldestInPage) : [];
  return {
    ...log,
    entries: [...page, ...older],
    hasOlder: full && (log.hasOlder || older.length === 0),
    arrived: added.length > 0 ? new Set([...log.arrived, ...added]) : log.arrived,
  };
}

/** The log with an older page added at the bottom. */
function withOlder(log: LogState, page: WatchRunSummary[]): LogState {
  const shown = new Set(log.entries.map((run) => run.id));
  return {
    ...log,
    entries: [...log.entries, ...page.filter((run) => !shown.has(run.id))],
    hasOlder: page.length === runPageSize,
  };
}

/**
 * A watch's runs, newest first, as far back as the Owner has paged. Stays current as runs are created, change
 * status or are deleted, keeping the older pages already loaded.
 * Must be called in an injection context, such as a component field initializer.
 */
export function injectRunLog(watchId: () => string) {
  const api = inject(WatchesApi);
  const updates = inject(LiveUpdates);

  const newest = api.newestRunsResource(watchId);
  const reloadNewest = reloader(newest);

  // A different watch starts a different log; each newest page that arrives merges into the current one
  const log = linkedSignal<{ watchId: string; page: WatchRunSummary[] | undefined }, LogState>({
    source: () => ({ watchId: watchId(), page: newest.hasValue() ? newest.value() : undefined }),
    computation: ({ watchId, page }, previous) => {
      const current =
        previous?.value.watchId === watchId ? previous.value : emptyLog(watchId, (previous?.value.generation ?? 0) + 1);
      return page ? withNewest(current, page) : current;
    },
  });

  /** Identifies the log as it is now; paging back that began under another one is ignored. */
  const logKey = computed(() => `${log().watchId}:${log().generation}`);
  const loadingOlder = linkedSignal({ source: logKey, computation: () => false });
  const olderError = linkedSignal<string, unknown>({ source: logKey, computation: () => undefined });

  async function loadOlder() {
    const { started, entries, watchId: id } = log();
    const oldest = entries.at(-1);
    if (!started || !oldest || loadingOlder()) {
      return;
    }
    const key = logKey();
    loadingOlder.set(true);
    olderError.set(undefined);
    try {
      const page = await api.olderRuns(id, oldest.id);
      if (logKey() === key) {
        log.update((current) => withOlder(current, page));
      }
    } catch (e) {
      if (logKey() === key) {
        olderError.set(e);
      }
    } finally {
      if (logKey() === key) {
        loadingOlder.set(false);
      }
    }
  }

  /** Replaces the log with the newest page when it arrives, as updates may have been missed. */
  function startOver() {
    log.update((current) => ({ ...current, generation: current.generation + 1, replaceNext: true }));
    reloadNewest();
  }

  const isThisWatch = <T extends { watchId: number }>(update: T) => update.watchId === Number(watchId());
  // Deleted runs go at once; the newest page is fetched again once a burst of changes settles
  updates.runsDeleted.pipe(filter(isThisWatch), takeUntilDestroyed()).subscribe(({ runIds }) => {
    const deleted = new Set(runIds);
    log.update((current) => ({ ...current, entries: current.entries.filter((run) => !deleted.has(run.id)) }));
  });
  reloadOn(newest, updates.runUpdated.pipe(filter(isThisWatch)), updates.runsDeleted.pipe(filter(isThisWatch)));
  // Messages were missed while disconnected
  updates.reconnected.pipe(takeUntilDestroyed()).subscribe(() => startOver());

  return {
    entries: computed(() => log().entries),
    /** Whether there's something to show in place of "Loading": the log, or why it couldn't load. */
    loaded: computed(() => log().started || newest.error() !== undefined),
    error: computed(() => newest.error() ?? olderError()),
    hasOlder: computed(() => log().hasOlder),
    loadingOlder: loadingOlder.asReadonly(),
    arrived: computed(() => log().arrived),
    isEmpty: computed(() => log().started && log().entries.length === 0),
    loadOlder,
    reload: reloadNewest,
  };
}

export type RunLog = ReturnType<typeof injectRunLog>;
