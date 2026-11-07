import { Component, computed, inject, input, signal } from '@angular/core';
import { LucideChevronDown } from '@lucide/angular';
import { Clock } from '../shared/clock';
import { describeError } from '../shared/describe-error';
import { clockTime, dayHeading, dayKey, duration, fullTime } from '../shared/time';
import { LogEntry, LogEntryLink } from './log-entry';
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
  key: number;
  heading: string;
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
  /** The run open beside the log, highlighted in it; a fold holding it opens. */
  readonly selectedId = input<number | null>(null);

  private readonly clock = inject(Clock);
  /** The folds the Owner opened, by key. */
  private readonly opened = signal<ReadonlySet<number>>(new Set());
  protected readonly describeError = describeError;

  protected readonly days = computed<Day[]>(() => {
    const runs = this.log().entries();
    const ditto = sameAsAbove(runs);
    const now = this.clock.now();
    const opened = this.opened();
    const selected = this.selectedId();
    const isOpen = (key: number, entries: LogEntry[]) =>
      opened.has(key) || entries.some((entry) => entry.run.id === selected);

    const days: { key: number; heading: string; entries: LogEntry[] }[] = [];
    runs.forEach((run, i) => {
      const created = Date.parse(run.createdTime);
      const key = dayKey(created);
      let day = days.at(-1);
      if (day?.key !== key) {
        day = { key, heading: dayHeading(created, now), entries: [] };
        days.push(day);
      }
      const { ran } = timingsOf(run);
      day.entries.push({
        run,
        time: clockTime(created),
        exactTime: fullTime(created),
        ran: ran === null ? null : duration(ran),
        // A day starts a fresh page, so its first entry is always written out
        ditto: ditto[i] && day.entries.length > 0,
        failed: outcomeOf(run) === 'failed',
      });
    });
    return days.map(({ key, heading, entries }) => ({
      key,
      heading,
      items: fold(entries, isOpen),
      runs: entries.length,
      failures: entries.filter((entry) => entry.failed).length,
    }));
  });

  protected toggle(key: number) {
    this.opened.update((keys) => {
      const next = new Set(keys);
      if (!next.delete(key)) {
        next.add(key);
      }
      return next;
    });
  }
}
