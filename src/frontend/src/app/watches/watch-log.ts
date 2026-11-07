import { Component, ElementRef, Injector, afterNextRender, computed, inject, input, signal } from '@angular/core';
import { LucideChevronDown } from '@lucide/angular';
import { Clock } from '../shared/clock';
import { describeError } from '../shared/describe-error';
import { clockTime, dayHeading, dayKey, duration, fullTime } from '../shared/time';
import { LogEntry, LogEntryLink } from './log-entry';
import { WatchRunSummary } from './watch.model';
import { RunLog } from './run-log';
import { outcomeOf, sameAsAbove, timingsOf } from './runs';

/** A streak of runs that each saw the same as the one above, folded into one line until opened. */
interface Fold {
  kind: 'fold';
  id: string;
  /** The oldest run in the streak, which stays the same as newer runs join it at the top. */
  key: number;
  entries: LogEntry[];
  /** When the oldest run in the streak ran. */
  earliest: string;
  open: boolean;
}

/** A line of the log: one run, or a folded streak of them. */
type Item = { kind: 'entry'; id: string; entry: LogEntry } | Fold;

const entryItem = (entry: LogEntry): Item => ({ kind: 'entry', id: `run-${entry.run.id}`, entry });

interface Day {
  /** The start of the day, local time. */
  key: number;
  items: Item[];
  runs: number;
  failures: number;
}

/** A streak this long or longer folds; shorter ones are quicker to read than to open. */
const foldFrom = 3;

/** Turns a day's entries into lines, folding each long streak of repeats. */
function fold(entries: LogEntry[], isOpen: (key: number, entries: LogEntry[]) => boolean): Item[] {
  const items: Item[] = [];
  let streak: LogEntry[] = [];
  const endStreak = () => {
    const oldest = streak.at(-1);
    if (oldest && streak.length >= foldFrom) {
      const key = oldest.run.id;
      items.push({
        kind: 'fold',
        id: `fold-${key}`,
        key,
        entries: streak,
        earliest: oldest.time,
        open: isOpen(key, streak),
      });
    } else {
      items.push(...streak.map(entryItem));
    }
    streak = [];
  };
  for (const entry of entries) {
    if (entry.ditto) {
      streak.push(entry);
    } else {
      endStreak();
      items.push(entryItem(entry));
    }
  }
  endStreak();
  return items;
}

/**
 * A watch's runs as a logbook: grouped by day, newest first, with a ditto mark wherever a run saw the same as the
 * one above it, so the eye goes to what changed. A long streak of repeats folds into one line.
 */
@Component({
  selector: 'app-watch-log',
  imports: [LogEntryLink, LucideChevronDown],
  templateUrl: './watch-log.html',
  styleUrl: './watch-log.scss',
})
export class WatchLog {
  readonly log = input.required<RunLog>();
  readonly watchId = input.required<string>();
  /** The run open beside the log, highlighted in it; a fold holding it opens unless the Owner closed it. */
  readonly selectedId = input<number | null>(null);

  private readonly clock = inject(Clock);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  /** The folds the Owner opened or closed, by key, which win over opening for the selected run. */
  private readonly folded = signal<ReadonlyMap<number, boolean>>(new Map());
  protected readonly describeError = describeError;

  protected readonly days = computed<Day[]>(() => {
    const folded = this.folded();
    const selected = this.selectedId();
    const isOpen = (key: number, entries: LogEntry[]) =>
      folded.get(key) ?? entries.some((entry) => entry.run.id === selected);

    const days: { key: number; runs: WatchRunSummary[] }[] = [];
    for (const run of this.log().entries()) {
      const key = dayKey(Date.parse(run.createdTime));
      let day = days.at(-1);
      if (day?.key !== key) {
        day = { key, runs: [] };
        days.push(day);
      }
      day.runs.push(run);
    }
    return days.map(({ key, runs }) => {
      // A day starts a fresh page, so ditto marks look no further than the day
      const ditto = sameAsAbove(runs);
      const entries = runs.map((run, i): LogEntry => {
        const created = Date.parse(run.createdTime);
        const { ran } = timingsOf(run);
        return {
          run,
          time: clockTime(created),
          exactTime: fullTime(created),
          ran: ran === null ? null : duration(ran),
          ditto: ditto[i],
          failed: outcomeOf(run) === 'failed',
        };
      });
      return {
        key,
        items: fold(entries, isOpen),
        runs: entries.length,
        failures: entries.filter((entry) => entry.failed).length,
      };
    });
  });

  /** Each day's heading, apart from the days themselves so the clock's tick only touches these. */
  protected readonly headings = computed(() => {
    const now = this.clock.now();
    return new Map(this.days().map((day) => [day.key, dayHeading(day.key, now)]));
  });

  protected toggle(key: number, open: boolean) {
    this.folded.update((folds) => new Map(folds).set(key, !open));
  }

  /** Loads an older page, then moves focus to its first line, as the button may go once there's nothing older. */
  protected async showOlder() {
    const shown = this.log().entries().length;
    await this.log().loadOlder();
    const first = this.log().entries()[shown];
    if (!first) {
      return;
    }
    const line = this.days()
      .flatMap((day) => day.items)
      .find((item) => lineHolds(item, first.id));
    afterNextRender(
      () => {
        const target = line && this.host.nativeElement.querySelector(`[data-line="${line.id}"]`);
        target?.querySelector<HTMLElement>('a, button')?.focus();
      },
      { injector: this.injector },
    );
  }
}

/** Whether a line of the log is the run, or a fold holding it. */
function lineHolds(item: Item, runId: number) {
  return item.kind === 'entry' ? item.entry.run.id === runId : item.entries.some((entry) => entry.run.id === runId);
}
