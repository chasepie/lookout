import { HttpClient, httpResource } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { VpnRegions, VpnSettings, VpnSettingsDto, VpnStatus } from './vpn.model';

const baseUrl = '/api/Vpn';

/**
 * Client for the Backend's `/Vpn` endpoints.
 * The `*Resource` methods must be called in an injection context (e.g. a component field initializer).
 */
@Service()
export class VpnApi {
  private readonly http = inject(HttpClient);

  statusResource() {
    return httpResource<VpnStatus>(() => baseUrl);
  }

  /** Makes no request while `enabled` returns `false`: the endpoint fails when the VPN isn't set up. */
  regionsResource(enabled: () => boolean) {
    return httpResource<VpnRegions>(() => (enabled() ? `${baseUrl}/regions` : undefined));
  }

  updateSettings(settings: VpnSettingsDto) {
    return firstValueFrom(this.http.put<VpnSettings>(`${baseUrl}/settings`, settings));
  }

  /**
   * Asks for a switch to `region`, or to a random region that isn't excluded when `null`.
   * Resolves before the switch happens; `LiveUpdates.vpnChanged` reports its progress.
   */
  switchRegion(region: string | null) {
    return firstValueFrom(this.http.post(`${baseUrl}/switch`, { region }, { responseType: 'text' }));
  }
}
