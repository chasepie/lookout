import { Component, computed, input } from '@angular/core';
import { RelativeTime } from '../shared/relative-time';
import { VpnSettings, VpnStatus } from './vpn.model';

/** Where the VPN comes out now: its region, exit IP and rotation timing, plus any switch in progress or failure. */
@Component({
  selector: 'app-vpn-status-card',
  imports: [RelativeTime],
  template: `
    <section class="position" aria-labelledby="position-title">
      <h2 id="position-title" class="visually-hidden">Current position</h2>
      <div class="region">
        @if (settings().currentRegion; as region) {
          <p class="region-name">{{ region }}</p>
        } @else {
          <p class="region-name">Any region</p>
          <p class="region-note">Not switched yet, so gluetun picked one.</p>
        }
      </div>

      <dl class="facts">
        <div>
          <dt>Exit IP</dt>
          <dd>
            @if (status().publicIp; as ip) {
              {{ ip.publicIp }}
            } @else if (status().switch) {
              Reconnecting…
            } @else {
              Unknown
            }
          </dd>
        </div>
        @if (location(); as location) {
          <div>
            <dt>Appears to be in</dt>
            <dd>{{ location }}</dd>
          </div>
        }
        <div>
          <dt>Last switch</dt>
          <dd>
            @if (settings().lastSwitchTime; as time) {
              <app-relative-time [value]="time" />
            } @else {
              Never
            }
          </dd>
        </div>
        <div>
          <dt>Next rotation</dt>
          <dd>
            @if (!settings().rotationEnabled) {
              Rotation is off
            } @else if (settings().nextRotationTime; as time) {
              <app-relative-time [value]="time" />
            } @else {
              Soon
            }
          </dd>
        </div>
      </dl>
    </section>

    <div role="status">
      @if (status().switch; as progress) {
        <p class="callout switching">
          @if (progress.waitingForRun) {
            Switching to {{ progress.region }} once the VPN watch runs in progress finish…
          } @else {
            Switching to {{ progress.region }}…
          }
        </p>
      }
    </div>
    @if (settings().lastError; as error) {
      <div class="callout callout-failure" role="alert"><p>The last switch failed: {{ error }}</p></div>
    }
    @if (status().gluetunError; as error) {
      <div class="callout callout-failure" role="alert"><p>{{ error }}</p></div>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }

    .position {
      display: grid;
      overflow: hidden;
      border: 1px solid var(--rule);
      border-radius: var(--radius-sheet);
      background: var(--paper-raised);
    }

    // The current region is land on the chart
    .region {
      padding: var(--space-5) var(--space-5) var(--space-4);
      background: var(--buff);
      color: var(--on-buff);
    }

    .region-name {
      font-family: var(--font-condensed);
      font-size: var(--step-5);
      font-weight: 600;
      line-height: 1;
      overflow-wrap: anywhere;
    }

    .region-note {
      margin-top: var(--space-2);
    }

    .facts {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3) var(--space-7);
      padding: var(--space-4) var(--space-5);

      dt {
        color: var(--ink-muted);
        font-size: var(--step--1);
      }

      dd {
        font-size: var(--step-1);
        font-weight: 500;
      }
    }

    .switching::before {
      content: '';
      display: inline-block;
      width: 0.5rem;
      height: 0.5rem;
      margin-right: var(--space-2);
      border-radius: 50%;
      background: var(--deep);
      animation: pulse 1.6s ease-in-out infinite;
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
    return ip ? [ip.city, ip.region, ip.country].filter(Boolean).join(', ') : '';
  });
}
