import { Component, computed, inject } from '@angular/core';
import { LiveUpdates } from '../live/live-updates';
import { reloadOn } from '../live/reload-on';
import { describeError } from '../shared/describe-error';
import { lastValue } from '../shared/last-value';
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

    @if (shownStatus(); as vpn) {
      @if (status.error(); as error) {
        <div class="callout callout-failure callout-retry" role="alert">
          <p>Couldn't refresh the VPN's status: {{ describeError(error) }}</p>
          <button type="button" class="button" (click)="status.reload()">Try again</button>
        </div>
      }
      @if (vpn.settings; as settings) {
        <app-vpn-status-card [status]="vpn" [settings]="settings" />

        @if (shownRegions(); as available) {
          @if (regions.error(); as error) {
            <div class="callout callout-failure callout-retry" role="alert">
              <p>Couldn't refresh the regions: {{ describeError(error) }}</p>
              <button type="button" class="button" (click)="regions.reload()">Try again</button>
            </div>
          }
          @if (available.regions.length === 0) {
            <div class="callout callout-failure" role="alert">
              <p>
                gluetun's servers list has no regions for {{ available.provider }}, so the VPN can't switch region.
              </p>
            </div>
          } @else {
            <div class="controls">
              <app-vpn-switch-form [regions]="available.regions" [switching]="!!vpn.switch" />
              <app-vpn-rotation-form [settings]="settings" [regions]="available.regions" />
            </div>
          }
        } @else if (regions.error(); as error) {
          <div class="callout callout-failure callout-retry" role="alert">
            <p>Couldn't load the regions: {{ describeError(error) }}</p>
            <button type="button" class="button" (click)="regions.reload()">Try again</button>
          </div>
        } @else {
          <p class="muted" role="status">Loading regions…</p>
        }
      } @else {
        <section class="unset" aria-labelledby="unset-title">
          <h2 id="unset-title">The VPN isn't set up</h2>
          <p>
            Watches that use the VPN fail until it is. Set <code>GLUETUN_OPENVPN_USER</code> and <code>GLUETUN_OPENVPN_PASSWORD</code>
            in <code>.env</code>, then restart the AppHost.
          </p>
        </section>
      }
    } @else if (status.error(); as error) {
      <div class="callout callout-failure callout-retry" role="alert">
        <p>Couldn't load the VPN's status: {{ describeError(error) }}</p>
        <button type="button" class="button" (click)="status.reload()">Try again</button>
      </div>
    } @else {
      <p class="muted" role="status">Loading the VPN's status…</p>
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
  /** The status as last loaded, still shown if a refresh fails. */
  protected readonly shownStatus = lastValue(this.status);
  private readonly configured = computed(() => this.shownStatus()?.configured ?? false);
  protected readonly regions = this.api.regionsResource(this.configured);
  protected readonly shownRegions = lastValue(this.regions);

  protected readonly describeError = describeError;

  constructor() {
    const updates = inject(LiveUpdates);
    reloadOn(this.status, updates.vpnChanged, updates.reconnected);
    reloadOn(this.regions, updates.reconnected);
  }
}
