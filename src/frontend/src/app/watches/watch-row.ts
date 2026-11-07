import { Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucidePlay, LucideShieldCheck } from '@lucide/angular';
import { Notices } from '../shared/notices';
import { RelativeTime } from '../shared/relative-time';
import { describeError } from '../shared/describe-error';
import { RunOutcome } from './run-outcome';
import { RunStrip } from './run-strip';
import { latestFinished, outcomeOf } from './runs';
import { describeSchedule } from './schedule';
import { WatchSummary } from './watch.model';
import { WatchesApi } from './watches-api';

/**
 * One watch in the list: its name and schedule, what it last saw, its recent runs, and a button to run it now.
 * The whole row opens the watch.
 */
@Component({
  selector: 'app-watch-row',
  imports: [RouterLink, RunOutcome, RunStrip, RelativeTime, LucidePlay, LucideShieldCheck],
  template: `
    <div class="identity">
      <h2 class="name">
        <a class="row-link" [routerLink]="['/watches', watch().id]">{{ watch().name }}</a>
      </h2>
      <p class="schedule">
        <span>{{ schedule() }}</span>
        @if (watch().useVpn) {
          <span class="vpn"><svg lucideShieldCheck aria-hidden="true" />VPN</span>
        }
      </p>
    </div>

    <div class="seen">
      @if (latest(); as run) {
        <app-run-outcome [run]="run" />
      } @else {
        <span class="muted">Hasn't run yet</span>
      }
    </div>

    <div class="when">
      @if (inProgress(); as label) {
        <span class="progress">{{ label }}</span>
      } @else if (latest(); as run) {
        <app-relative-time [value]="run.createdTime" />
      }
      @if (watch().recentRuns.length > 0) {
        <app-run-strip [runs]="watch().recentRuns" />
      }
    </div>

    <button
      type="button"
      class="button button-quiet button-icon run"
      [disabled]="starting() || !watch().scriptFileName"
      [title]="watch().scriptFileName ? 'Run ' + watch().name + ' now' : 'Upload a script to run this watch'"
      (click)="runNow()"
    >
      <svg lucidePlay aria-hidden="true" />
      <span class="visually-hidden">Run {{ watch().name }} now</span>
    </button>
  `,
  styles: `
    :host {
      position: relative;
      display: grid;
      grid-template-columns: minmax(10rem, 15rem) minmax(0, 1fr) auto auto;
      grid-template-areas: 'identity seen when run';
      align-items: center;
      gap: var(--space-2) var(--space-5);
      padding: var(--space-4) var(--space-2);
      transition: background-color 120ms;
    }

    :host:hover {
      background: var(--paper-sunk);
    }

    .identity {
      grid-area: identity;
      min-width: 0;
    }

    .name {
      font-size: var(--step-1);
    }

    // The link covers the whole row; the run button sits above it
    .row-link {
      color: var(--ink);
      text-decoration: none;

      &::after {
        content: '';
        position: absolute;
        inset: 0;
      }

      &:focus-visible {
        outline: none;

        &::after {
          outline: 2px solid var(--deep);
          outline-offset: -2px;
        }
      }
    }

    :host:hover .row-link {
      text-decoration: underline;
      text-underline-offset: 0.18em;
    }

    .schedule {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      color: var(--ink-muted);
      font-size: var(--step--1);
    }

    .vpn {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);

      svg {
        width: 0.875rem;
        height: 0.875rem;
      }
    }

    .seen {
      grid-area: seen;
      font-size: var(--step-2);
    }

    .when {
      grid-area: when;
      display: grid;
      justify-items: end;
      gap: var(--space-1);
      color: var(--ink-muted);
      font-size: var(--step--1);
    }

    .progress {
      color: var(--deep);
      font-weight: 500;
    }

    .run {
      grid-area: run;
      position: relative;
      z-index: 1;
    }

    @media (max-width: 48rem) {
      :host {
        grid-template-columns: minmax(0, 1fr) auto;
        grid-template-areas:
          'identity run'
          'seen seen'
          'when when';
      }

      .when {
        justify-items: start;
        grid-auto-flow: column;
        justify-content: space-between;
        align-items: center;
      }
    }
  `,
})
export class WatchRow {
  readonly watch = input.required<WatchSummary>();

  private readonly api = inject(WatchesApi);
  private readonly notices = inject(Notices);

  protected readonly starting = signal(false);
  protected readonly schedule = computed(() => describeSchedule(this.watch()));
  protected readonly latest = computed(() => latestFinished(this.watch().recentRuns));
  /** Says so while a run is under way, since the result shown is from the run before. */
  protected readonly inProgress = computed(() => {
    const outcomes = this.watch().recentRuns.map(outcomeOf);
    return outcomes.includes('running') ? 'Running now' : outcomes.includes('queued') ? 'Starting soon' : null;
  });

  protected async runNow() {
    this.starting.set(true);
    try {
      const run = await this.api.run(String(this.watch().id));
      this.notices.say(`Started run #${run.id} of ${this.watch().name}.`);
    } catch (e) {
      this.notices.fail(`Couldn't run ${this.watch().name}: ${describeError(e)}`);
    } finally {
      this.starting.set(false);
    }
  }
}
