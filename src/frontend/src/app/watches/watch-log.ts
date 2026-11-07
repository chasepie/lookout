import { Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideImage } from '@lucide/angular';
import { Clock } from '../shared/clock';
import { describeError } from '../shared/describe-error';
import { clockTime, dayHeading, dayKey, duration, fullTime } from '../shared/time';
import { RunLog } from './run-log';
import { RunOutcome } from './run-outcome';
import { outcomeOf, sameAsAbove, timingsOf } from './runs';
import { WatchRunSummary } from './watch.model';

interface Entry {
  run: WatchRunSummary;
  time: string;
  exactTime: string;
  ran: string | null;
  ditto: boolean;
  failed: boolean;
}

interface Day {
  key: number;
  heading: string;
  entries: Entry[];
  failures: number;
}

/**
 * A watch's runs as a logbook: grouped by day, newest first, with a ditto mark wherever a run saw the same as the
 * one above it, so the eye goes to what changed.
 */
@Component({
  selector: 'app-watch-log',
  imports: [RouterLink, RunOutcome, LucideImage],
  template: `
    @if (!log().loaded()) {
      <p class="muted" role="status">Loading the log…</p>
    } @else if (log().isEmpty()) {
      <p class="muted">No runs yet. Run the watch to see what it finds.</p>
    }

    @for (day of days(); track day.key) {
      <section class="day" [attr.aria-labelledby]="'day-' + day.key">
        <h3 class="day-head" [id]="'day-' + day.key">
          <span>{{ day.heading }}</span>
          <span class="day-count"
            >{{ day.entries.length }} {{ day.entries.length === 1 ? 'run' : 'runs' }}@if (day.failures > 0) {<span
                class="failure"
                >, {{ day.failures }} failed</span
              >}</span
          >
        </h3>
        <ol class="entries">
          @for (entry of day.entries; track entry.run.id) {
            <li>
              <a
                class="entry"
                [class.entry-arrived]="log().arrived().has(entry.run.id)"
                [class.entry-failed]="entry.failed"
                [routerLink]="['/watches', watchId(), 'runs', entry.run.id]"
                [attr.aria-current]="selectedId() === entry.run.id ? 'true' : null"
              >
                <time class="time" [attr.datetime]="entry.run.createdTime" [title]="entry.exactTime">{{ entry.time }}</time>
                <app-run-outcome class="outcome" [run]="entry.run" [repeat]="entry.ditto" />
                <span class="meta">
                  @if (entry.run.hasScreenshot) {
                    <svg lucideImage aria-hidden="true"></svg>
                    <span class="visually-hidden">Has a screenshot.</span>
                  }
                  @if (entry.ran) {
                    <span><span class="visually-hidden">Took </span>{{ entry.ran }}</span>
                  }
                </span>
              </a>
            </li>
          }
        </ol>
      </section>
    }

    @if (log().error(); as error) {
      <div class="callout callout-failure" role="alert">
        <p>Couldn't load the log: {{ describeError(error) }}</p>
        <button type="button" class="button" (click)="log().reload()">Try again</button>
      </div>
    }

    @if (log().hasOlder()) {
      <button type="button" class="button older" [disabled]="log().loadingOlder()" (click)="log().loadOlder()">
        {{ log().loadingOlder() ? 'Loading older runs…' : 'Show older runs' }}
      </button>
    }
  `,
  styleUrl: './watch-log.scss',
})
export class WatchLog {
  readonly log = input.required<RunLog>();
  readonly watchId = input.required<string>();
  /** The run open beside the log, highlighted in it. */
  readonly selectedId = input<number | null>(null);

  private readonly clock = inject(Clock);
  protected readonly describeError = describeError;

  protected readonly days = computed<Day[]>(() => {
    const runs = this.log().entries();
    const ditto = sameAsAbove(runs);
    const now = this.clock.now();
    const days: Day[] = [];
    runs.forEach((run, i) => {
      const created = Date.parse(run.createdTime);
      const key = dayKey(created);
      let day = days.at(-1);
      if (day?.key !== key) {
        day = { key, heading: dayHeading(created, now), entries: [], failures: 0 };
        days.push(day);
      }
      const { ran } = timingsOf(run);
      const failed = outcomeOf(run) === 'failed';
      day.failures += failed ? 1 : 0;
      day.entries.push({
        run,
        time: clockTime(created),
        exactTime: fullTime(created),
        ran: ran === null ? null : duration(ran),
        // A day starts a fresh page, so its first entry is always written out
        ditto: ditto[i] && day.entries.length > 0,
        failed,
      });
    });
    return days;
  });
}
