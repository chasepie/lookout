import { Service, effect, inject } from '@angular/core';
import { DefaultHttpClient, HubConnectionBuilder, HubConnectionState, LogLevel, NullLogger } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { csrfHeaders } from '../auth/csrf';
import { Session } from '../auth/session';
import { SessionAwareHttpClient } from './session-aware-http-client';
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
    .withUrl(hubUrl, {
      // For the negotiate request; the WebSocket handshake can't carry headers
      headers: csrfHeaders,
      // A refused session ends it here, which stops reconnecting
      httpClient: new SessionAwareHttpClient(new DefaultHttpClient(NullLogger.instance), () => this.session.expired()),
    })
    .withAutomaticReconnect({
      // Reconnecting after the session ended would only be refused
      nextRetryDelayInMilliseconds: (context) =>
        this.session.signedIn() ? retryDelayMs(context.previousRetryCount) : null,
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
    // Covers signing in again while the previous connection was still stopping, when start() had to skip
    this.connection.onclose(() => {
      if (this.session.signedIn()) {
        void this.start(0);
      }
    });
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
      // A refused session already ended through the HTTP client, so the next start() returns early
      setTimeout(() => void this.start(failedAttempts + 1), retryDelayMs(failedAttempts));
    }
  }
}
