import { type HttpResourceRef, type HttpResourceRequest, httpResource } from '@angular/common/http';
import { effect, linkedSignal, signal, untracked } from '@angular/core';

/** An httpResource that can be read afresh at any time. */
export interface RefreshableHttpResource<T> extends HttpResourceRef<T> {
  refresh(): void;
  // Narrows to this type, not a plain HttpResourceRef, so refresh() stays available alongside the narrowed value.
  hasValue(): this is RefreshableHttpResource<Exclude<T, undefined>>;
}

/**
 * Creates an httpResource whose refresh() reads it again. reload() declines while the resource is loading, and the
 * read under way may predate the change. While it loads its first value, refresh() restarts the read with a new
 * request object; while it reloads, restarting would drop the value on screen, so it reads again once the reload lands.
 */
export function refreshableHttpResource<T>(request: () => HttpResourceRequest | undefined): RefreshableHttpResource<T | undefined> {
  const reads = signal(0);
  const resource = httpResource<T>(() => {
    reads();
    const current = request();
    return current && { ...current };
  });
  // Forgotten when the request changes, as the new request's read is fresh anyway.
  const readAgain = linkedSignal({ source: () => JSON.stringify(request() ?? null), computation: () => false });
  effect(() => {
    if (readAgain() && !resource.isLoading()) {
      untracked(() => {
        readAgain.set(false);
        resource.reload();
      });
    }
  });
  // The same object once narrowed still has refresh(), which TypeScript can't see through hasValue()'s predicate.
  return Object.assign(resource, {
    refresh() {
      if (resource.reload()) {return;}
      if (resource.hasValue()) {
        readAgain.set(true);
      } else {
        reads.update((count) => count + 1);
      }
    },
  }) as RefreshableHttpResource<T | undefined>;
}
