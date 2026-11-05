import { Service, effect, inject } from '@angular/core';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { Session, csrfHeader } from '../auth/session';
import { WatchRunUpdate } from './watch.model';

/** The Backend's `UpdatesHub`, on the page's own origin like the rest of the API. */
const hubUrl = '/api/hubs/updates';

/** Waits 1s, 2s, 4s… between connection attempts, capped at 30s, and never gives up. */
function retryDelayMs(previousAttempts: number) {
  return Math.min(30_000, 1_000 * 2 ** previousAttempts);
}

/**
 * Real-time watch, run and VPN changes pushed by the Backend over SignalR.
 * Holds one connection while the Owner is signed in, and reconnects whenever it drops until the session ends.
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

  private readonly session = inject(Session);

  private readonly connection = new HubConnectionBuilder()
    // The CSRF header covers the negotiate request, a POST. Browsers can't add headers to the WebSocket handshake, so
    // the Backend checks that one's origin instead.
    .withUrl(hubUrl, { headers: { [csrfHeader]: '1' } })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (context) => {
        // Reconnecting after the session ended would only be refused
        if (!this.session.signedIn()) {
          return null;
        }
        // The Backend closes the connection when the session expires, and refuses it after the credentials change,
        // but SignalR doesn't say why an attempt failed. So ask about the session before each attempt: checking only
        // once can miss it, such as when the check reaches a Backend that is still shutting down.
        void this.session.recheck();
        return retryDelayMs(context.previousRetryCount);
      },
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
    effect(() => {
      if (this.session.signedIn()) {
        void this.start(0);
      } else {
        void this.connection.stop();
      }
    });
  }

  /** Automatic reconnect only covers drops after the first connection, so retry that here. */
  private async start(failedAttempts: number) {
    if (!this.session.signedIn() || this.connection.state !== HubConnectionState.Disconnected) {
      return;
    }
    try {
      await this.connection.start();
      if (failedAttempts > 0) {
        this.reconnected.next();
      }
    } catch {
      await this.session.recheck();
      setTimeout(() => void this.start(failedAttempts + 1), retryDelayMs(failedAttempts));
    }
  }
}
