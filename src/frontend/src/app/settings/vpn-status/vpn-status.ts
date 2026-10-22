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
    // Read afresh rather than taken from the event, as an event may overtake a read already under way.
    inject(WatchEvents).events.pipe(takeUntilDestroyed()).subscribe((event) => {
      if (event.type === 'vpnStatusChanged' || event.type === 'connected') {this.status.refresh();}
    });
  }
}
