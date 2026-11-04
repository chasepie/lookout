import { DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { VpnSettings, VpnStatus } from './vpn.model';

/** The VPN's region, exit IP and rotation timing, plus any switch in progress or failure. */
@Component({
  selector: 'app-vpn-status-card',
  imports: [DatePipe],
  template: `
    <h3>Status</h3>
    <dl>
      <dt>Region</dt>
      <dd>{{ settings().currentRegion ?? 'Any (not switched yet, so gluetun picked one)' }}</dd>
      <dt>Exit IP</dt>
      <dd>
        @if (status().publicIp; as ip) {
          {{ ip.publicIp }}
          @if (location(); as location) {
            ({{ location }})
          }
        } @else if (status().switch) {
          Reconnecting…
        } @else {
          Unknown
        }
      </dd>
      <dt>Last switch</dt>
      <dd>{{ (settings().lastSwitchTime | date: 'medium') ?? 'Never' }}</dd>
      <dt>Next rotation</dt>
      <dd>
        @if (settings().rotationEnabled) {
          {{ (settings().nextRotationTime | date: 'medium') ?? 'Soon' }}
        } @else {
          Rotation is off
        }
      </dd>
    </dl>

    <p role="status">
      @if (status().switch; as progress) {
        @if (progress.waitingForRun) {
          Switching to {{ progress.region }} once the VPN watch run in progress finishes…
        } @else {
          Switching to {{ progress.region }}…
        }
      }
    </p>
    @if (settings().lastError; as error) {
      <p role="alert">The last switch failed: {{ error }}</p>
    }
    @if (status().gluetunError; as error) {
      <p role="alert">{{ error }}</p>
    }
  `,
})
export class VpnStatusCard {
  readonly status = input.required<VpnStatus>();
  /** The status's settings, which the parent only passes on when they're set. */
  readonly settings = input.required<VpnSettings>();

  /** Where the exit IP geolocates, which can be named differently from the VPN provider's region. */
  protected readonly location = computed(() => {
    const ip = this.status().publicIp;
    return ip ? [ip.city, ip.country].filter(Boolean).join(', ') : '';
  });
}
