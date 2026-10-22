import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { refreshableHttpResource } from '../../shared/refreshable-http-resource';
import { WatchEvents } from '../../watches/watch-events';
import type { Vpn } from '../vpn.model';

@Component({
  selector: 'app-vpn-status',
  templateUrl: './vpn-status.html',
  styleUrl: './vpn-status.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VpnStatus {
  protected readonly status = refreshableHttpResource<Vpn>(() => ({ url: '/api/vpn' }));

  constructor() {
    // Taken from the event, as reading the status reads gluetun again. Only reconnecting, which may have missed some,
    // reads it afresh.
    inject(WatchEvents).events.pipe(takeUntilDestroyed()).subscribe((event) => {
      if (event.type === 'vpnStatusChanged') {this.status.set(event.vpn);}
      if (event.type === 'connected') {this.status.refresh();}
    });
  }
}
