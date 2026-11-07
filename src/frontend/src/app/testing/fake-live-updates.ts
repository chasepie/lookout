import { Provider, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { LiveState, LiveUpdates } from '../live/live-updates';
import { WatchRunUpdate } from '../watches/watch.model';

/** Stands in for the SignalR connection, which tests have no hub for. Push messages through its subjects. */
export class FakeLiveUpdates {
  readonly runUpdated = new Subject<WatchRunUpdate>();
  readonly runsDeleted = new Subject<{ watchId: number; runIds: number[] }>();
  readonly watchChanged = new Subject<number>();
  readonly vpnChanged = new Subject<void>();
  readonly reconnected = new Subject<void>();
  readonly state = signal<LiveState>('live');
}

export function provideFakeLiveUpdates(): Provider {
  return { provide: LiveUpdates, useClass: FakeLiveUpdates };
}

/** The fake provided by {@link provideFakeLiveUpdates}. */
export function liveUpdates(): FakeLiveUpdates {
  return TestBed.inject(LiveUpdates) as unknown as FakeLiveUpdates;
}
