import { computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter, merge } from 'rxjs';
import { LiveUpdates } from '../live/live-updates';
import { settle } from '../live/reload-on';
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
  /** Whether there's something to show in place of "Loading": the log, or why it couldn't load. */
  const loaded = signal(false);
  /** Whether the newest page has arrived since the log last started over, so later pages merge into it. */
  const started = signal(false);
  const error = signal<unknown>(null);
  const hasOlder = signal(false);
  const loadingOlder = signal(false);
  /** Runs that arrived after the log first loaded, which are highlighted as they appear. */
  const arrived = signal<ReadonlySet<number>>(new Set());

  // Each request is numbered, so a slow response can't overwrite a newer one
  let latestRequest = 0;
  // Each start-over is numbered too, so an older page asked for before it isn't added to the new log
  let generation = 0;

  function startOver() {
    generation++;
    started.set(false);
    loadingOlder.set(false);
  }

  async function refresh(reset: boolean) {
    if (reset) {
      startOver();
    }
    const request = ++latestRequest;
    const id = watchId();
    try {
      const newest = await api.runs(id);
      if (request !== latestRequest) {
        return;
      }
      error.set(null);
      loaded.set(true);
      if (!started()) {
        entries.set(newest);
        hasOlder.set(newest.length === runPageSize);
        arrived.set(new Set());
        started.set(true);
        return;
      }
      // Only runs newer than any shown have arrived; older ones only move up into the page as others are deleted
      const newestShown = entries()[0]?.id ?? 0;
      const added = newest.filter((run) => run.id > newestShown).map((run) => run.id);
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
    if (!oldest || loadingOlder() || !started()) {
      return;
    }
    const pageGeneration = generation;
    loadingOlder.set(true);
    try {
      const page = await api.runs(watchId(), oldest.id);
      if (pageGeneration !== generation) {
        return;
      }
      const known = new Set(entries().map((run) => run.id));
      entries.update((current) => [...current, ...page.filter((run) => !known.has(run.id))]);
      hasOlder.set(page.length === runPageSize);
    } catch (e) {
      if (pageGeneration === generation) {
        error.set(e);
      }
    } finally {
      if (pageGeneration === generation) {
        loadingOlder.set(false);
      }
    }
  }

  // A different watch starts a different log
  effect(() => {
    watchId();
    untracked(() => {
      loaded.set(false);
      entries.set([]);
      error.set(null);
      void refresh(true);
    });
  });

  const isThisWatch = <T extends { watchId: number }>(update: T) => update.watchId === Number(watchId());
  // Deleted runs go at once; the page is fetched again once a burst of changes settles
  updates.runsDeleted.pipe(filter(isThisWatch), takeUntilDestroyed()).subscribe(({ runIds }) => {
    const deleted = new Set(runIds);
    entries.update((current) => current.filter((run) => !deleted.has(run.id)));
  });
  merge(updates.runUpdated.pipe(filter(isThisWatch)), updates.runsDeleted.pipe(filter(isThisWatch)))
    .pipe(settle(), takeUntilDestroyed())
    .subscribe(() => void refresh(false));
  // Messages were missed while disconnected, so start over
  updates.reconnected.pipe(takeUntilDestroyed()).subscribe(() => void refresh(true));

  return {
    entries: entries.asReadonly(),
    loaded: loaded.asReadonly(),
    error: error.asReadonly(),
    hasOlder: hasOlder.asReadonly(),
    loadingOlder: loadingOlder.asReadonly(),
    arrived: arrived.asReadonly(),
    isEmpty: computed(() => started() && entries().length === 0),
    loadOlder,
    reload: () => refresh(false),
  };
}

export type RunLog = ReturnType<typeof injectRunLog>;
