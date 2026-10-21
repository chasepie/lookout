import { type HttpResourceRef, type HttpResourceRequest, httpResource } from '@angular/common/http';
import { effect, linkedSignal, untracked } from '@angular/core';

/** An httpResource that can be read afresh at any time. */
export interface RefreshableHttpResource<T> extends HttpResourceRef<T> {
  refresh(): void;
  // Narrows to this type, not a plain HttpResourceRef, so refresh() stays available alongside the narrowed value.
  hasValue(): this is RefreshableHttpResource<Exclude<T, undefined>>;
}

/**
 * Creates an httpResource whose refresh() reads it again. reload() declines while the resource is loading, and the
 * read under way may predate the change, so refresh() then reads again once that read lands. Restarting the read
 * instead would cancel the request under way, and drop the value on screen during a reload.
 */
export function refreshableHttpResource<T>(request: () => HttpResourceRequest | undefined): RefreshableHttpResource<T | undefined> {
  const resource = httpResource<T>(request);
  // Forgotten when the request changes, as the new request's read is fresh anyway.
  const readAgain = linkedSignal({
    source: () => JSON.stringify(request() ?? null),
    computation: () => false
  });
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
      if (!resource.reload()) {
        readAgain.set(true);
      }
    },
  }) as RefreshableHttpResource<T | undefined>;
}
