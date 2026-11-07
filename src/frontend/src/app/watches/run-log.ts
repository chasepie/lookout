import { computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { LiveUpdates } from '../live/live-updates';
import { WatchRunSummary } from './watch.model';
import { WatchesApi, runPageSize } from './watches-api';

/**
 * A watch's runs, newest first, as far back as the Owner has paged. Stays current as runs are created, change
 * status or are deleted, keeping the older pages already loaded.
 * Must be called in an injection context, such as a component field initializer.
 */
export function injectRunLog(watchId: () => string) {
  const api = inject(WatchesApi);
  const updates = inject(LiveUpdates);

  const entries = signal<WatchRunSummary[]>([]);
  const loaded = signal(false);
  const error = signal<unknown>(null);
  const hasOlder = signal(false);
  const loadingOlder = signal(false);
  /** Runs that arrived after the log first loaded, which are highlighted as they appear. */
  const arrived = signal<ReadonlySet<number>>(new Set());

  // Each request is numbered, so a slow response can't overwrite a newer one
  let latestRequest = 0;

  async function refresh(reset: boolean) {
    const request = ++latestRequest;
    const id = watchId();
    try {
      const newest = await api.runs(id);
      if (request !== latestRequest) {
        return;
      }
      error.set(null);
      if (reset || !loaded()) {
        entries.set(newest);
        hasOlder.set(newest.length === runPageSize);
        arrived.set(new Set());
        loaded.set(true);
        return;
      }
      const known = new Set(entries().map((run) => run.id));
      const added = newest.filter((run) => !known.has(run.id)).map((run) => run.id);
      if (added.length > 0) {
        arrived.update((ids) => new Set([...ids, ...added]));
      }
      // Older runs already loaded are kept, unless the newest page reaches back past them
      const oldestFetched = newest.at(-1)?.id ?? Infinity;
      entries.update((current) => [...newest, ...current.filter((run) => run.id < oldestFetched)]);
    } catch (e) {
      if (request === latestRequest) {
        error.set(e);
        loaded.set(true);
      }
    }
  }

  async function loadOlder() {
    const oldest = entries().at(-1);
    if (!oldest || loadingOlder()) {
      return;
    }
    loadingOlder.set(true);
    try {
      const page = await api.runs(watchId(), oldest.id);
      const known = new Set(entries().map((run) => run.id));
      entries.update((current) => [...current, ...page.filter((run) => !known.has(run.id))]);
      hasOlder.set(page.length === runPageSize);
    } catch (e) {
      error.set(e);
    } finally {
      loadingOlder.set(false);
    }
  }

  // A different watch starts a different log
  effect(() => {
    watchId();
    untracked(() => {
      loaded.set(false);
      entries.set([]);
      void refresh(true);
    });
  });

  const isThisWatch = <T extends { watchId: number }>(update: T) => update.watchId === Number(watchId());
  updates.runsDeleted.pipe(filter(isThisWatch), takeUntilDestroyed()).subscribe(({ runIds }) => {
    const deleted = new Set(runIds);
    entries.update((current) => current.filter((run) => !deleted.has(run.id)));
    void refresh(false);
  });
  updates.runUpdated.pipe(filter(isThisWatch), takeUntilDestroyed()).subscribe(() => void refresh(false));
  // Messages were missed while disconnected, so start over
  updates.reconnected.pipe(takeUntilDestroyed()).subscribe(() => void refresh(true));

  return {
    entries: entries.asReadonly(),
    loaded: loaded.asReadonly(),
    error: error.asReadonly(),
    hasOlder: hasOlder.asReadonly(),
    loadingOlder: loadingOlder.asReadonly(),
    arrived: arrived.asReadonly(),
    isEmpty: computed(() => loaded() && entries().length === 0),
    loadOlder,
    reload: () => refresh(false),
  };
}

export type RunLog = ReturnType<typeof injectRunLog>;
