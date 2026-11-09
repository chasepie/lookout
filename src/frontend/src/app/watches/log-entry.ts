import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideImage } from '@lucide/angular';
import { RunOutcome } from './run-outcome';
import { WatchRunSummary } from './watch.model';

/** A run as one line of the log reads it. */
export interface LogEntry {
  run: WatchRunSummary;
  /** The time of day it ran. */
  time: string;
  /** The full date and time, for its tooltip. */
  exactTime: string;
  /** How long it took, once it has finished. */
  ran: string | null;
  /** Whether it saw the same as the run above it, which shows a ditto mark. */
  ditto: boolean;
  /** Whether it crashed, which the log rules in the margin. */
  crashed: boolean;
}

/** One run in the log: when, what it saw, and how long it took. Opens the run. */
@Component({
  selector: 'app-log-entry',
  imports: [RouterLink, RunOutcome, LucideImage],
  template: `
    <a
      class="entry"
      [class.entry-arrived]="arrived()"
      [class.entry-crashed]="entry().crashed"
      [routerLink]="['/watches', watchId(), 'runs', entry().run.id]"
      [attr.aria-current]="selected() ? 'true' : null"
    >
      <time class="time" [attr.datetime]="entry().run.createdTime" [title]="entry().exactTime">{{ entry().time }}</time>
      <app-run-outcome class="outcome" [run]="entry().run" [repeat]="entry().ditto" />
      <span class="meta">
        @if (entry().run.hasScreenshot) {
          <svg lucideImage aria-hidden="true" />
          <span class="visually-hidden">Has a screenshot.</span>
        }
        @if (entry().ran; as ran) {
          <span><span class="visually-hidden">Took </span>{{ ran }}</span>
        }
      </span>
    </a>
  `,
  styleUrl: './log-entry.scss',
})
export class LogEntryLink {
  readonly entry = input.required<LogEntry>();
  readonly watchId = input.required<string>();
  /** Whether it's the run open beside the log. */
  readonly selected = input(false);
  /** Whether it arrived while the log was open, which washes it in. */
  readonly arrived = input(false);
}
