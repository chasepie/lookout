import { ResourceStatus, Signal, linkedSignal } from '@angular/core';

/** A resource, such as an `httpResource`, as far as its value goes. */
interface Valued<T> {
  readonly status: Signal<ResourceStatus>;
  hasValue(): boolean;
  readonly value: Signal<T>;
}

/**
 * The resource's value, kept through a reload that fails, so a page goes on showing what it had beside the error
 * instead of losing it all. Starts over, `undefined`, when the resource loads something else, such as another watch.
 */
export function lastValue<T>(resource: Valued<T>): Signal<T | undefined> {
  return linkedSignal<{ status: ResourceStatus; value: T | undefined }, T | undefined>({
    source: () => ({ status: resource.status(), value: resource.hasValue() ? resource.value() : undefined }),
    computation: ({ status, value }, previous) => (status === 'loading' ? value : (value ?? previous?.value)),
  });
}
