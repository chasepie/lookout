import { Signal, effect, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MonoTypeOperatorFunction, Observable, auditTime, merge } from 'rxjs';

/** How long to let a burst of live events settle before reloading. A run sends three: created, started, finished. */
export const settleMs = 250;

/**
 * Lets a burst of events settle into one, emitted at most every {@link settleMs}, so one reload covers them all
 * and the page still updates while events keep coming.
 */
export function settle<T>(): MonoTypeOperatorFunction<T> {
  return auditTime(settleMs);
}

/** A resource, such as an `httpResource`, as far as reloading it goes. */
interface Reloadable {
  readonly isLoading: Signal<boolean>;
  /** Returns `false`, doing nothing, while the resource is already loading. */
  reload(): boolean;
}

/**
 * Returns a function that reloads `target`, or, if it's loading already, reloads it again once that load ends:
 * a change that happened during a request may not be in its answer. Must be called in an injection context.
 */
export function reloader(target: Reloadable): () => void {
  let pending = false;
  effect(() => {
    if (!target.isLoading() && pending) {
      pending = false;
      untracked(() => target.reload());
    }
  });
  return () => {
    if (!target.reload()) {
      pending = true;
    }
  };
}

/**
 * Reloads `target` once any of `sources` emits and the burst has settled, until the component is destroyed.
 * Must be called in an injection context, such as a field initializer or constructor.
 */
export function reloadOn(target: Reloadable, ...sources: Observable<unknown>[]) {
  const reload = reloader(target);
  merge(...sources)
    .pipe(settle(), takeUntilDestroyed())
    .subscribe(() => reload());
}
