import { Component, computed, inject } from '@angular/core';
import { LiveUpdates } from './live-updates';

/** Whether pages are keeping up with changes as they happen, or may be out of date. */
@Component({
  selector: 'app-live-indicator',
  template: `
    <span class="dot" aria-hidden="true"></span>
    <span class="label" [title]="detail()">{{ label() }}</span>
  `,
  host: {
    role: 'status',
    '[class]': 'live.state()',
  },
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      color: var(--ink-muted);
      font-size: var(--step--1);
    }

    .dot {
      width: 0.5rem;
      height: 0.5rem;
      border: 1.5px solid currentColor;
      border-radius: 50%;
    }

    :host(.live) .dot {
      border-color: var(--deep);
      background: var(--deep);
    }

    :host(.reconnecting) {
      color: var(--magenta);
    }

    :host(.reconnecting) .dot {
      animation: blink 1.2s steps(2, jump-none) infinite;
    }

    // On a phone the dot alone says it; the label is still read out
    @media (max-width: 40rem) {
      .label {
        position: absolute;
        width: 1px;
        height: 1px;
        overflow: hidden;
        clip-path: inset(50%);
        white-space: nowrap;
      }
    }

    @keyframes blink {
      50% {
        opacity: 0;
      }
    }
  `,
})
export class LiveIndicator {
  protected readonly live = inject(LiveUpdates);
  protected readonly label = computed(
    () => ({ connecting: 'Connecting', live: 'Live', reconnecting: 'Reconnecting' })[this.live.state()],
  );
  protected readonly detail = computed(
    () =>
      ({
        connecting: 'Connecting to Lookout for live updates.',
        live: 'Runs and changes show up here as they happen.',
        reconnecting: "Lost the connection to Lookout. Pages may be out of date until it's back.",
      })[this.live.state()],
  );
}
