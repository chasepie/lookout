import { type HttpResourceRef, type HttpResourceRequest, httpResource } from '@angular/common/http';
import { signal } from '@angular/core';

/** An httpResource that can be read afresh at any time. */
export interface RefreshableHttpResource<T> extends HttpResourceRef<T> {
  refresh(): void;
  // Narrows to this type, not a plain HttpResourceRef, so refresh() stays available alongside the narrowed value.
  hasValue(): this is RefreshableHttpResource<Exclude<T, undefined>>;
}

/**
 * Creates an httpResource whose refresh() reads it again. reload() declines while the resource is still loading its
 * first value, which may predate the change, so then refresh() restarts the read with a new request object.
 */
export function refreshableHttpResource<T>(request: () => HttpResourceRequest | undefined): RefreshableHttpResource<T | undefined> {
  const reads = signal(0);
  const resource = httpResource<T>(() => {
    reads();
    const current = request();
    return current && { ...current };
  });
  // The same object once narrowed still has refresh(), which TypeScript can't see through hasValue()'s predicate.
  return Object.assign(resource, {
    refresh() {
      if (!resource.reload()) {
        reads.update((count) => count + 1);
      }
    },
  }) as RefreshableHttpResource<T | undefined>;
}
