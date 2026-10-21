import type { HttpResourceRef } from '@angular/common/http';
import type { WritableSignal } from '@angular/core';

/**
 * Reads a resource again. reload() declines while the resource is still loading its first value, which may predate
 * the change, so then this restarts the read by bumping the counter its request reads.
 */
export function refresh(resource: HttpResourceRef<unknown>, reads: WritableSignal<number>) {
  if (!resource.reload()) {
    reads.update((count) => count + 1);
  }
}
