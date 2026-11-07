import { DestroyRef, Service, inject, signal } from '@angular/core';

/** How often relative times such as "4 min ago" move on. */
const tickMs = 15_000;

/** The current time as a signal, so anything showing how long ago something happened stays current. */
@Service()
export class Clock {
  private readonly current = signal(Date.now());
  /** Milliseconds since the epoch, updated every few seconds. */
  readonly now = this.current.asReadonly();

  constructor() {
    const timer = setInterval(() => this.current.set(Date.now()), tickMs);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }
}
