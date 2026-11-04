import { Service } from '@angular/core';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { WatchRunUpdate } from './watch.model';

/** The Backend's `UpdatesHub`, reached through the dev server's `/api` proxy. */
const hubUrl = '/api/hubs/updates';

/** Waits 1s, 2s, 4s… between connection attempts, capped at 30s, and never gives up. */
function retryDelayMs(previousAttempts: number) {
  return Math.min(30_000, 1_000 * 2 ** previousAttempts);
}

/**
 * Real-time watch, run and VPN changes pushed by the Backend over SignalR.
 * Holds one connection for the app's lifetime and reconnects whenever it drops.
 */
@Service()
export class LiveUpdates {
  /** A run was created or changed status. */
  readonly runUpdated = new Subject<WatchRunUpdate>();
  /** Runs of a watch were deleted. */
  readonly runsDeleted = new Subject<{ watchId: number; runIds: number[] }>();
  /** A watch was created, edited, deleted, or had its script replaced. Emits the watch ID. */
  readonly watchChanged = new Subject<number>();
  /** The VPN settings changed, or a region switch started, progressed or finished. */
  readonly vpnChanged = new Subject<void>();
  /** The connection is back after being down. Messages sent meanwhile were missed, so reload. */
  readonly reconnected = new Subject<void>();

  private readonly connection = new HubConnectionBuilder()
    .withUrl(hubUrl)
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (context) => retryDelayMs(context.previousRetryCount),
    })
    .configureLogging(LogLevel.Warning)
    .build();

  constructor() {
    this.connection.on('RunUpdated', (update: WatchRunUpdate) => this.runUpdated.next(update));
    this.connection.on('RunsDeleted', (watchId: number, runIds: number[]) =>
      this.runsDeleted.next({ watchId, runIds }),
    );
    this.connection.on('WatchChanged', (watchId: number) => this.watchChanged.next(watchId));
    this.connection.on('VpnChanged', () => this.vpnChanged.next());
    this.connection.onreconnected(() => this.reconnected.next());
    void this.start(0);
  }

  /** Automatic reconnect only covers drops after the first connection, so retry that here. */
  private async start(failedAttempts: number) {
    try {
      await this.connection.start();
      if (failedAttempts > 0) {
        this.reconnected.next();
      }
    } catch {
      setTimeout(() => void this.start(failedAttempts + 1), retryDelayMs(failedAttempts));
    }
  }
}
