import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, merge } from 'rxjs';

/**
 * Reloads `target`, such as an `httpResource`, whenever any of `sources` emits, until the component is destroyed.
 * Must be called in an injection context, such as a field initializer or constructor.
 */
export function reloadOn(target: { reload(): unknown }, ...sources: Observable<unknown>[]) {
  merge(...sources)
    .pipe(takeUntilDestroyed())
    .subscribe(() => target.reload());
}
