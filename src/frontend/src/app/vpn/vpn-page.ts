import { Component, computed, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { merge } from 'rxjs';
import { LiveUpdates } from '../watches/live-updates';
import { describeError } from '../shared/describe-error';
import { VpnApi } from './vpn-api';
import { VpnRotationForm } from './vpn-rotation-form';
import { VpnStatusCard } from './vpn-status-card';
import { VpnSwitchForm } from './vpn-switch-form';

/** The VPN's status, switching region on demand, and the rotation settings. */
@Component({
  selector: 'app-vpn-page',
  imports: [VpnStatusCard, VpnSwitchForm, VpnRotationForm],
  template: `
    <h2>VPN</h2>

    @if (status.isLoading() && !status.hasValue()) {
      <p>Loading…</p>
    } @else if (status.error(); as error) {
      <pre role="alert">{{ describeError(error) }}</pre>
    } @else if (status.hasValue()) {
      @if (status.value().settings; as settings) {
        <app-vpn-status-card [status]="status.value()" [settings]="settings" />

        @if (regions.error(); as error) {
          <pre role="alert">Couldn't load the regions: {{ describeError(error) }}</pre>
        } @else if (regions.hasValue()) {
          @if (regions.value().regions.length === 0) {
            <p role="alert">
              gluetun's servers list has no regions for {{ regions.value().provider }}, so the VPN can't switch region.
            </p>
          } @else {
            <app-vpn-switch-form [regions]="regions.value().regions" [switching]="!!status.value().switch" />
            <app-vpn-rotation-form [settings]="settings" [regions]="regions.value().regions" />
          }
        } @else {
          <p>Loading regions…</p>
        }
      } @else {
        <p>
          The VPN isn't set up. Set <code>OPENVPN_USER</code>, <code>OPENVPN_PASSWORD</code> and
          <code>LOOKOUT_GLUETUN_DIR</code> in <code>.env</code>, then restart the AppHost.
        </p>
      }
    }
  `,
})
export class VpnPage {
  private readonly api = inject(VpnApi);

  protected readonly status = this.api.statusResource();
  private readonly configured = computed(() => this.status.hasValue() && this.status.value().configured);
  protected readonly regions = this.api.regionsResource(this.configured);

  protected readonly describeError = describeError;

  constructor() {
    const updates = inject(LiveUpdates);
    merge(updates.vpnChanged, updates.reconnected)
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.status.reload());
    updates.reconnected.pipe(takeUntilDestroyed()).subscribe(() => this.regions.reload());
  }
}
