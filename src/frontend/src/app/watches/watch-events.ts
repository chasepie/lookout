import { HttpClient, HttpXsrfTokenExtractor } from '@angular/common/http';
import { Service, effect, inject } from '@angular/core';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Subject, firstValueFrom } from 'rxjs';
import { Session } from '../session/session';
import type { SessionStatus } from '../session/session.model';
import type { Vpn } from '../settings/vpn.model';
import type { RunOutcome, WatchStatus } from './watch.model';

export interface RunStarted { watchId: string; startedAt: string }

/** A Run that ended without being recorded, such as one another Run superseded, has no ID or outcome. */
export interface RunFinished {
  watchId: string;
  runId: number | null;
  outcome: RunOutcome | null;
  isChange: boolean;
  /** The Run's content, only when it's a Change. */
  content: string | null;
}

export interface WatchStatusChanged { watchId: string; status: WatchStatus }

/**
 * What Lookout pushes as it happens, and `connected` whenever the connection opens, since events sent before then,
 * such as while the page loaded or the connection was down, were missed.
 */
export type WatchEvent =
  | ({ type: 'runStarted' } & RunStarted)
  | ({ type: 'runFinished' } & RunFinished)
  | ({ type: 'watchStatusChanged' } & WatchStatusChanged)
  | { type: 'vpnStatusChanged'; vpn: Vpn }
  | { type: 'connected' };

const maxRetryDelayMs = 30_000;

/** Listens to Lookout's events while logged in, reconnecting whenever the connection drops. */
@Service()
export class WatchEvents {
  private readonly http = inject(HttpClient);
  private readonly session = inject(Session);
  private readonly requestToken = inject(HttpXsrfTokenExtractor);
  private readonly subject = new Subject<WatchEvent>();
  readonly events = this.subject.asObservable();

  constructor() {
    effect((onCleanup) => {
      if (!this.session.userName()) {return;}
      const stopped = new AbortController();
      void this.listen(stopped.signal);
      onCleanup(() => stopped.abort());
    });
  }

  private async listen(stopped: AbortSignal) {
    for (let failures = 0; !stopped.aborted;) {
      // Built afresh each time, so negotiating sends the current request token.
      const connection = new HubConnectionBuilder()
        .withUrl('/api/events', { headers: { 'X-XSRF-TOKEN': this.requestToken.getToken() ?? '' } })
        .configureLogging(LogLevel.None)
        .build();
      connection.on('RunStarted', (event: RunStarted) => this.subject.next({ type: 'runStarted', ...event }));
      connection.on('RunFinished', (event: RunFinished) => this.subject.next({ type: 'runFinished', ...event }));
      connection.on('WatchStatusChanged', (event: WatchStatusChanged) =>
        this.subject.next({ type: 'watchStatusChanged', ...event }));
      connection.on('VpnStatusChanged', (vpn: Vpn) => this.subject.next({ type: 'vpnStatusChanged', vpn }));
      const closed = new Promise<void>((resolve) => connection.onclose(() => resolve()));
      const stop = () => void connection.stop();
      stopped.addEventListener('abort', stop);
      try {
        await connection.start();
        failures = 0;
        if (!stopped.aborted) {this.subject.next({ type: 'connected' });}
        await closed;
      } catch {
        failures++;
      } finally {
        stopped.removeEventListener('abort', stop);
      }
      if (stopped.aborted) {return;}
      await delay(Math.min(maxRetryDelayMs, 1000 * 2 ** failures), stopped);
      if (!stopped.aborted) {await this.checkSession();}
    }
  }

  /**
   * Checks the session the connection dropped or was refused under, which may have ended, such as on logout
   * elsewhere, and issues a fresh request token for the next attempt. Only a session that has ended reloads the app's
   * own status, which stops this listener; a backend that's restarting mustn't take the page down.
   */
  private async checkSession() {
    try {
      const { userName } = await firstValueFrom(this.http.get<SessionStatus>('/api/session'));
      if (!userName) {this.session.status.reload();}
    } catch {
      // Unreachable for now; the next attempt tries again.
    }
  }
}

function delay(ms: number, stopped: AbortSignal) {
  return new Promise<void>((resolve) => {
    const timer = setTimeout(done, ms);
    stopped.addEventListener('abort', done, { once: true });
    function done() {
      clearTimeout(timer);
      stopped.removeEventListener('abort', done);
      resolve();
    }
  });
}
