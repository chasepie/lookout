import { ChangeDetectionStrategy, Component } from '@angular/core';
import { refreshableHttpResource } from '../../shared/refreshable-http-resource';
import type { Vpn } from '../vpn.model';

@Component({
  selector: 'app-vpn-status',
  templateUrl: './vpn-status.html',
  styleUrl: './vpn-status.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VpnStatus {
  protected readonly status = refreshableHttpResource<Vpn>(() => ({ url: '/api/vpn' }));
}
