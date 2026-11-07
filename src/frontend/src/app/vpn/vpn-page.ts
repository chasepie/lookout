import { Component, computed, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { merge } from 'rxjs';
import { LiveUpdates } from '../live/live-updates';
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
    <h1>VPN</h1>

    @if (status.isLoading() && !status.hasValue()) {
      <p class="muted" role="status">Loading the VPN's status…</p>
    } @else if (status.error(); as error) {
      <div class="callout callout-failure" role="alert"><p>Couldn't load the VPN's status: {{ describeError(error) }}</p></div>
    } @else if (status.hasValue()) {
      @if (status.value().settings; as settings) {
        <app-vpn-status-card [status]="status.value()" [settings]="settings" />

        @if (regions.error(); as error) {
          <div class="callout callout-failure" role="alert"><p>Couldn't load the regions: {{ describeError(error) }}</p></div>
        } @else if (regions.hasValue()) {
          @if (regions.value().regions.length === 0) {
            <div class="callout callout-failure" role="alert">
              <p>
                gluetun's servers list has no regions for {{ regions.value().provider }}, so the VPN can't switch
                region.
              </p>
            </div>
          } @else {
            <div class="controls">
              <app-vpn-switch-form [regions]="regions.value().regions" [switching]="!!status.value().switch" />
              <app-vpn-rotation-form [settings]="settings" [regions]="regions.value().regions" />
            </div>
          }
        } @else {
          <p class="muted" role="status">Loading regions…</p>
        }
      } @else {
        <section class="unset" aria-labelledby="unset-title">
          <h2 id="unset-title">The VPN isn't set up</h2>
          <p>
            Watches that use the VPN fail until it is. Set <code>OPENVPN_USER</code>, <code>OPENVPN_PASSWORD</code> and
            <code>LOOKOUT_GLUETUN_DIR</code> in <code>.env</code>, then restart the AppHost.
          </p>
        </section>
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-5);
    }

    .controls {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 2fr);
      gap: var(--space-7);
      align-items: start;
      padding-top: var(--space-5);
    }

    .unset {
      display: grid;
      gap: var(--space-3);
      padding: var(--space-5);
      border-left: 3px solid var(--magenta);
      background: var(--paper-raised);
    }

    @media (max-width: 52rem) {
      .controls {
        grid-template-columns: minmax(0, 1fr);
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
