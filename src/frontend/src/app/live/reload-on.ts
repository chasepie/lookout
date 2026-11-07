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

/**
 * Reloads `target`, such as an `httpResource`, once any of `sources` emits and the burst has settled, until the
 * component is destroyed. Must be called in an injection context, such as a field initializer or constructor.
 */
export function reloadOn(target: { reload(): unknown }, ...sources: Observable<unknown>[]) {
  merge(...sources)
    .pipe(settle(), takeUntilDestroyed())
    .subscribe(() => target.reload());
}
